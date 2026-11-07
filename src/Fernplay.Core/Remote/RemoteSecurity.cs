using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Fernplay.Core.Remote;

/// <summary>
/// The phone remote's secrets. The PC has a self-signed certificate (TLS); a phone pairs once by proving it knows the
/// 6-digit code shown on the PC for this very certificate, and gets a random key. Later connections answer a fresh
/// challenge with that key, bound to the certificate the phone pinned, so a copied message or another server is refused.
/// </summary>
public static class RemoteSecurity
{
    /// <summary>A 6-digit code, every digit equally likely.</summary>
    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>SHA-256 of the certificate (lower-case hex): what the phone pins and shows.</summary>
    public static string Fingerprint(X509Certificate2 cert) => Convert.ToHexStringLower(SHA256.HashData(cert.RawData));

    /// <summary>What the phone sends to pair: HMAC-SHA256 keyed with the code over the certificate's fingerprint (hex).</summary>
    public static string PairProof(string code, string fingerprint) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(code), Encoding.UTF8.GetBytes(fingerprint)));

    /// <summary>The answer to a challenge: HMAC-SHA256 keyed with the phone's key over nonce + fingerprint (hex).</summary>
    public static string AuthProof(byte[] key, byte[] nonce, string fingerprint) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, nonce.Concat(Encoding.UTF8.GetBytes(fingerprint)).ToArray()));

    /// <summary>Compares two hex proofs in constant time; false for anything that is not the same length.</summary>
    public static bool SameProof(string? a, string? b)
    {
        if (a is null || b is null || a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a.ToLowerInvariant()), Encoding.ASCII.GetBytes(b.ToLowerInvariant()));
    }

    /// <summary>
    /// Loads the remote's certificate from <paramref name="path"/> (kept protected for this Windows user), or makes a new
    /// one there when it is missing or cannot be read (another PC or user: phones then pair again).
    /// </summary>
    public static X509Certificate2 LoadOrCreateCertificate(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var pfx = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                var loaded = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
                if (loaded.HasPrivateKey && loaded.NotAfter > DateTime.Now.AddDays(30)) return loaded;
                loaded.Dispose();
            }
            catch (Exception ex) when (ex is CryptographicException or IOException) { }
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Fernplay phone remote", key, HashAlgorithmName.SHA256);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        var bytes = made.Export(X509ContentType.Pkcs12);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
        // Loaded back from PKCS#12: Windows' TLS needs a key it can find again, not the in-memory one.
        return X509CertificateLoader.LoadPkcs12(bytes, null, X509KeyStorageFlags.UserKeySet);
    }

    internal static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Fernplay phone remote");
}

/// <summary>A paired phone: its id, the name it gave, its key, when it paired.</summary>
public sealed record PairedPhone(string Id, string Name, byte[] Key, DateTime Paired);

/// <summary>
/// The paired phones, kept in one file protected for this Windows user. Thread-safe; saves on every change.
/// </summary>
public sealed class PairedPhones
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private List<PairedPhone> _phones;

    public PairedPhones(string path)
    {
        _path = path;
        _phones = Load(path);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<PairedPhone> All { get { lock (_gate) return [.. _phones]; } }

    public PairedPhone? Find(string? id)
    {
        if (id is null) return null;
        lock (_gate) return _phones.FirstOrDefault(p => p.Id == id);
    }

    public PairedPhone Add(string name)
    {
        var phone = new PairedPhone(Guid.NewGuid().ToString("N"), Clean(name), RandomNumberGenerator.GetBytes(32), DateTime.UtcNow);
        lock (_gate) { _phones.Add(phone); Save(); }
        Changed?.Invoke(this, EventArgs.Empty);
        return phone;
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_gate) { removed = _phones.RemoveAll(p => p.Id == id) > 0; if (removed) Save(); }
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    /// <summary>A phone's name as shown in Settings: printable, at most 40 characters, "Phone" when empty.</summary>
    internal static string Clean(string? name)
    {
        var s = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (s.Length > 40) s = s[..40];
        return s.Length == 0 ? "Phone" : s;
    }

    private sealed record Stored(string Id, string Name, string Key, DateTime Paired);

    private static List<PairedPhone> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var json = ProtectedData.Unprotect(File.ReadAllBytes(path), RemoteSecurity.Entropy, DataProtectionScope.CurrentUser);
            return (JsonSerializer.Deserialize<List<Stored>>(json) ?? [])
                .Where(s => s.Id is { Length: > 0 } && s.Key is { Length: > 0 })
                .Select(s => new PairedPhone(s.Id, Clean(s.Name), Convert.FromBase64String(s.Key), s.Paired)).ToList();
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or JsonException or FormatException) { return []; }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(_phones.Select(p => new Stored(p.Id, p.Name, Convert.ToBase64String(p.Key), p.Paired)).ToList());
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, ProtectedData.Protect(json, RemoteSecurity.Entropy, DataProtectionScope.CurrentUser));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// The pairing window opened from Settings: one code, valid for a short time, at most five wrong tries, used once.
/// </summary>
public sealed class PairingWindow
{
    public const int MaxTries = 5;
    private readonly Lock _gate = new();
    private readonly Func<DateTime> _now;
    private string? _code;
    private DateTime _until;
    private int _tries;

    public PairingWindow(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    /// <summary>Raised when the window closes (paired, expired on a try, too many wrong codes, or Close).</summary>
    public event EventHandler<PairingResult>? Closed;

    public bool IsOpen { get { lock (_gate) return _code is not null && _now() < _until; } }

    /// <summary>Opens a new window and returns its code (any earlier code stops working).</summary>
    public string Open(TimeSpan valid)
    {
        lock (_gate)
        {
            _code = RemoteSecurity.NewCode();
            _until = _now() + valid;
            _tries = 0;
            return _code;
        }
    }

    public void Close() => Finish(PairingResult.Cancelled);

    /// <summary>Checks a phone's proof for this certificate. True once: the window then closes.</summary>
    public PairingResult Try(string? proof, string fingerprint)
    {
        lock (_gate)
        {
            if (_code is null) return PairingResult.NotOpen;
            if (_now() >= _until) { _code = null; }
            else if (RemoteSecurity.SameProof(proof, RemoteSecurity.PairProof(_code, fingerprint))) { _code = null; return Raise(PairingResult.Paired); }
            else if (++_tries < MaxTries) return PairingResult.WrongCode;
            else { _code = null; return Raise(PairingResult.TooManyTries); }
        }
        return Raise(PairingResult.Expired);
    }

    private void Finish(PairingResult result)
    {
        lock (_gate) { if (_code is null) return; _code = null; }
        Closed?.Invoke(this, result);
    }

    private PairingResult Raise(PairingResult r)
    {
        // Called under the lock; the handler runs after it on the thread pool so it can take its time.
        _ = Task.Run(() => Closed?.Invoke(this, r));
        return r;
    }
}

public enum PairingResult { Paired, WrongCode, TooManyTries, Expired, NotOpen, Cancelled }
