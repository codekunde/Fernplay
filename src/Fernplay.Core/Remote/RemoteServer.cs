using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fernplay.Core.Remote;

/// <summary>What the phone shows: the player's state. Times in seconds; Repeat is "off", "all" or "one".</summary>
public sealed record RemoteState(string Play, string? Title, string? Artist, bool Station, double Position, double Length,
    bool CanSeek, double Volume, bool Shuffle, string Repeat, bool Crossfade, int Index, int ListVersion);

/// <summary>One playlist row for the phone.</summary>
public sealed record RemoteTrack(string Title, double? Length, bool Station);

/// <summary>A command from the phone. Do: play, pause, toggle, stop, next, previous, seek, volume, shuffle, repeat, xfade, playIndex.</summary>
public sealed record RemoteCommand(string Do, double? Number, bool? Flag, string? Text);

/// <summary>The player as the remote sees it. Called from the server's threads; the app hands the calls to its UI thread.</summary>
public interface IRemoteHost
{
    string PcName { get; }
    Task<RemoteState> GetStateAsync();
    Task<IReadOnlyList<RemoteTrack>> GetPlaylistAsync();
    Task ExecuteAsync(RemoteCommand command);
    /// <summary>Something the phone shows changed (track, state, volume, modes, playlist).</summary>
    event EventHandler? Changed;
}

/// <summary>
/// The phone remote's server: TLS on one TCP port, one JSON object per line. A phone either pairs (code proof, gets a
/// key, connection ends) or says hello, answers a challenge with its key, and then sends commands and receives the
/// player's state and playlist as they change. Only addresses of the local network are served.
/// </summary>
public sealed class RemoteServer : IAsyncDisposable
{
    public const int DefaultPort = 47800;
    public const int MaxLine = 64 * 1024;
    public const int MaxSessions = 4;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(90); // the phone pings every 30 s

    private readonly IRemoteHost _host;
    private readonly X509Certificate2 _cert;
    private readonly PairedPhones _phones;
    private readonly PairingWindow _pairing;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly List<Session> _sessions = [];
    private TcpListener? _listener;
    private Task? _accepting;
    private TaskCompletionSource _changed = NewSignal();

    public RemoteServer(IRemoteHost host, X509Certificate2 cert, PairedPhones phones, PairingWindow pairing)
    {
        _host = host;
        _cert = cert;
        _phones = phones;
        _pairing = pairing;
        Fingerprint = RemoteSecurity.Fingerprint(cert);
        _host.Changed += OnHostChanged;
        _phones.Changed += (_, _) => DropRemovedPhones();
    }

    public string Fingerprint { get; }
    public int Port { get; private set; }

    /// <summary>Names of the phones connected now.</summary>
    public IReadOnlyList<string> Connected { get { lock (_gate) return _sessions.Where(s => s.Phone is not null).Select(s => s.Phone!.Name).ToList(); } }

    /// <summary>A phone connected, paired, or went away (the Settings list refreshes).</summary>
    public event EventHandler? PhonesChanged;

    /// <summary>Starts listening on <paramref name="port"/> (0 = any free port, for tests). Throws SocketException when the port is taken.</summary>
    public void Start(int port, IPAddress? address = null)
    {
        _listener = new TcpListener(address ?? IPAddress.Any, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = AcceptLoopAsync(_stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _host.Changed -= OnHostChanged;
        _stop.Cancel();
        _listener?.Stop();
        Session[] open;
        lock (_gate) open = [.. _sessions];
        foreach (var s in open) s.Close();
        if (_accepting is not null) { try { await _accepting; } catch (Exception) { } }
    }

    /// <summary>True for loopback, private (10/8, 172.16/12, 192.168/16), link-local and IPv6 unique-local addresses.</summary>
    public static bool IsLocalNetwork(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }
        return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (SocketException) { continue; }
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            bool full;
            lock (_gate) full = _sessions.Count >= MaxSessions;
            if (remote is null || !IsLocalNetwork(remote) || full) { client.Dispose(); continue; }
            _ = Task.Run(() => RunAsync(client, ct), ct);
        }
    }

    private async Task RunAsync(TcpClient client, CancellationToken serverCt)
    {
        var session = new Session(client, serverCt);
        lock (_gate) _sessions.Add(session);
        try
        {
            var ct = session.Token;
            using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            session.Stream = ssl;
            using (var hs = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                hs.CancelAfter(HandshakeTimeout);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _cert,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, hs.Token);
                var reader = new LineReader(ssl);
                session.Reader = reader;
                var first = await reader.ReadAsync(hs.Token);
                switch (Type(first))
                {
                    case "pair":
                        await PairAsync(session, first!);
                        return;
                    case "hello":
                        if (!await AuthenticateAsync(session, first!, hs.Token)) return;
                        break;
                    default:
                        await session.SendAsync(Error("expected-hello"));
                        return;
                }
            }
            PhonesChanged?.Invoke(this, EventArgs.Empty);
            await session.SendAsync(new JsonObject { ["t"] = "welcome", ["pc"] = _host.PcName });
            await Task.WhenAll(ReadCommandsAsync(session), PushAsync(session));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or AuthenticationException or SocketException or ObjectDisposedException or InvalidDataException) { }
        finally
        {
            bool wasPhone = session.Phone is not null;
            lock (_gate) _sessions.Remove(session);
            session.Close();
            client.Dispose();
            if (wasPhone) PhonesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task PairAsync(Session session, JsonObject message)
    {
        var result = _pairing.Try(Str(message, "proof"), Fingerprint);
        if (result != PairingResult.Paired)
        {
            await session.SendAsync(Error(result switch
            {
                PairingResult.WrongCode => "wrong-code",
                PairingResult.TooManyTries => "too-many-tries",
                PairingResult.Expired => "expired",
                _ => "not-pairing",
            }));
            return;
        }
        var phone = _phones.Add(Str(message, "name") ?? "Phone");
        await session.SendAsync(new JsonObject { ["t"] = "paired", ["id"] = phone.Id, ["key"] = Convert.ToBase64String(phone.Key), ["pc"] = _host.PcName });
        PhonesChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> AuthenticateAsync(Session session, JsonObject hello, CancellationToken ct)
    {
        var phone = _phones.Find(Str(hello, "id"));
        // An unknown id still gets a challenge, so a guesser learns nothing from the answer's timing or shape.
        var nonce = RandomNumberGenerator.GetBytes(32);
        await session.SendAsync(new JsonObject { ["t"] = "challenge", ["nonce"] = Convert.ToBase64String(nonce) });
        var auth = await session.Reader!.ReadAsync(ct);
        if (Type(auth) != "auth" || phone is null || !RemoteSecurity.SameProof(Str(auth!, "proof"), RemoteSecurity.AuthProof(phone.Key, nonce, Fingerprint)))
        {
            await session.SendAsync(Error(phone is null ? "unknown-phone" : "bad-proof"));
            return false;
        }
        session.Phone = phone;
        return true;
    }

    private async Task ReadCommandsAsync(Session session)
    {
        var ct = session.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(IdleTimeout);
                var message = await session.Reader!.ReadAsync(idle.Token);
                if (message is null) return; // the phone hung up
                switch (Type(message))
                {
                    case "ping": await session.SendAsync(new JsonObject { ["t"] = "pong" }); break;
                    case "playlist": session.ListVersion = -1; Signal(); break; // send it again
                    case "cmd" when ParseCommand(message) is { } command: await _host.ExecuteAsync(command); break;
                    default: await session.SendAsync(Error("unknown-message")); break;
                }
            }
        }
        finally { session.Close(); }
    }

    private async Task PushAsync(Session session)
    {
        var ct = session.Token;
        string? last = null;
        while (!ct.IsCancellationRequested)
        {
            var changed = _changed.Task;
            var state = await _host.GetStateAsync();
            var json = ToJson(state).ToJsonString();
            if (json != last) { await session.SendRawAsync(json); last = json; }
            if (state.ListVersion != session.ListVersion)
            {
                var list = await _host.GetPlaylistAsync();
                await session.SendAsync(new JsonObject
                {
                    ["t"] = "playlist",
                    ["version"] = state.ListVersion,
                    ["items"] = new JsonArray(list.Select(t => (JsonNode)new JsonObject { ["title"] = t.Title, ["len"] = t.Length, ["station"] = t.Station }).ToArray()),
                });
                session.ListVersion = state.ListVersion;
            }
            // Position moves while playing: once a second; otherwise only on a change.
            var wait = state.Play == "playing" ? Task.Delay(1000, ct) : Task.Delay(Timeout.Infinite, ct);
            await Task.WhenAny(changed, wait);
            ct.ThrowIfCancellationRequested();
        }
    }

    internal static JsonObject ToJson(RemoteState s) => new()
    {
        ["t"] = "state", ["play"] = s.Play, ["title"] = s.Title, ["artist"] = s.Artist, ["station"] = s.Station,
        ["pos"] = Math.Round(s.Position, 1), ["len"] = Math.Round(s.Length, 1), ["canSeek"] = s.CanSeek, ["vol"] = Math.Round(s.Volume, 3),
        ["shuffle"] = s.Shuffle, ["repeat"] = s.Repeat, ["xfade"] = s.Crossfade, ["index"] = s.Index, ["list"] = s.ListVersion,
    };

    /// <summary>A command message, or null when it is malformed or unknown.</summary>
    internal static RemoteCommand? ParseCommand(JsonObject m)
    {
        var action = Str(m, "do");
        if (action is not ("play" or "pause" or "toggle" or "stop" or "next" or "previous" or "seek" or "volume" or "shuffle" or "repeat" or "xfade" or "playIndex")) return null;
        double? number = null; bool? flag = null; string? text = null;
        if (m["value"] is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d) && double.IsFinite(d)) number = d;
            else if (v.TryGetValue<bool>(out var b)) flag = b;
            else if (v.TryGetValue<string>(out var s)) text = s;
        }
        bool ok = action switch
        {
            "seek" => number >= 0,
            "volume" => number is >= 0 and <= 1,
            "shuffle" or "xfade" => flag is not null,
            "repeat" => text is "off" or "all" or "one",
            "playIndex" => number is >= 0 && number == Math.Floor(number.Value),
            _ => true,
        };
        return ok ? new RemoteCommand(action, number, flag, text) : null;
    }

    private void OnHostChanged(object? sender, EventArgs e) => Signal();

    private void Signal() => Interlocked.Exchange(ref _changed, NewSignal()).TrySetResult();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void DropRemovedPhones()
    {
        Session[] gone;
        lock (_gate) gone = _sessions.Where(s => s.Phone is not null && _phones.Find(s.Phone.Id) is null).ToArray();
        foreach (var s in gone) s.Close();
    }

    private static string? Type(JsonObject? m) => m is null ? null : Str(m, "t");

    private static string? Str(JsonObject m, string name) =>
        m[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static JsonObject Error(string code) => new() { ["t"] = "error", ["error"] = code };

    /// <summary>One connection: its stream, reader, the phone once known, a lock so pushes and replies never interleave.</summary>
    private sealed class Session(TcpClient client, CancellationToken serverCt)
    {
        private readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        private readonly SemaphoreSlim _write = new(1, 1);
        public CancellationToken Token => _cts.Token;
        public SslStream? Stream { get; set; }
        public LineReader? Reader { get; set; }
        public PairedPhone? Phone { get; set; }
        public int ListVersion { get; set; } = -1;

        public Task SendAsync(JsonObject message) => SendRawAsync(message.ToJsonString());

        public async Task SendRawAsync(string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            await _write.WaitAsync(Token);
            try { await Stream!.WriteAsync(bytes, Token); await Stream.FlushAsync(Token); }
            finally { _write.Release(); }
        }

        public void Close()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            try { client.Client.Shutdown(SocketShutdown.Both); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        }
    }
}

/// <summary>Reads one JSON object per line (UTF-8), refusing lines longer than <see cref="RemoteServer.MaxLine"/>.</summary>
public sealed class LineReader(Stream stream)
{
    private readonly byte[] _buffer = new byte[8192];
    private readonly MemoryStream _line = new();
    private int _start, _end;

    /// <summary>The next line as a JSON object; null at the end of the stream. Throws InvalidDataException on a bad line.</summary>
    public async Task<JsonObject?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            int nl = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (nl >= 0)
            {
                _line.Write(_buffer, _start, nl - _start);
                _start = nl + 1;
                var bytes = _line.ToArray();
                _line.SetLength(0);
                if (bytes.Length == 0 || bytes.All(b => b is (byte)'\r' or (byte)' ')) continue;
                try { return JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException("not an object"); }
                catch (JsonException) { throw new InvalidDataException("not JSON"); }
            }
            _line.Write(_buffer, _start, _end - _start);
            if (_line.Length > RemoteServer.MaxLine) throw new InvalidDataException("line too long");
            _start = 0;
            _end = await stream.ReadAsync(_buffer, ct);
            if (_end == 0) return null;
        }
    }
}
