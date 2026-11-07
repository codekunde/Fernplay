using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Fernplay.Core.Remote;

namespace Fernplay.Tests;

/// <summary>One certificate for all remote tests (making one writes a key into the user's profile).</summary>
public sealed class RemoteCertFixture : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "fernplay-remote-cert-" + Guid.NewGuid().ToString("N"));
    public X509Certificate2 Cert { get; }
    public RemoteCertFixture() => Cert = RemoteSecurity.LoadOrCreateCertificate(Path.Combine(Folder, "remote-cert.bin"));
    public void Dispose()
    {
        Cert.Dispose();
        try { Directory.Delete(Folder, true); } catch (IOException) { }
    }
}

internal sealed class FakeRemoteHost : IRemoteHost
{
    public string PcName => "TESTPC";
    public RemoteState State = new("paused", "Song", "Artist", false, 12, 200, true, 0.5, false, "off", false, 0, 1);
    public List<RemoteTrack> List = [new("01 - Artist - Song", 200, false), new("Radio", null, true)];
    public readonly List<RemoteCommand> Commands = [];
    public event EventHandler? Changed;
    public Task<RemoteState> GetStateAsync() => Task.FromResult(State);
    public Task<IReadOnlyList<RemoteTrack>> GetPlaylistAsync() => Task.FromResult<IReadOnlyList<RemoteTrack>>(List.ToList());
    public Task ExecuteAsync(RemoteCommand command) { lock (Commands) Commands.Add(command); return Task.CompletedTask; }
    public void Change(RemoteState s) { State = s; Changed?.Invoke(this, EventArgs.Empty); }
}

/// <summary>A minimal phone: TLS with the pinned fingerprint, JSON lines.</summary>
internal sealed class TestPhone : IAsyncDisposable
{
    private readonly TcpClient _tcp = new();
    private SslStream? _ssl;
    private LineReader? _reader;

    public static async Task<TestPhone> ConnectAsync(int port, string fingerprint)
    {
        var p = new TestPhone();
        await p._tcp.ConnectAsync(IPAddress.Loopback, port);
        p._ssl = new SslStream(p._tcp.GetStream(), false);
        await p._ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "fernplay",
            RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && RemoteSecurity.Fingerprint(new X509Certificate2(cert)) == fingerprint,
        });
        p._reader = new LineReader(p._ssl);
        return p;
    }

    public async Task SendAsync(JsonObject m) => await SendRawAsync(m.ToJsonString() + "\n");

    public async Task SendRawAsync(string text)
    {
        await _ssl!.WriteAsync(Encoding.UTF8.GetBytes(text));
        await _ssl.FlushAsync();
    }

    /// <summary>The next message, or null when the PC closed the connection; times out after 5 s.</summary>
    public async Task<JsonObject?> ReadAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { return await _reader!.ReadAsync(cts.Token); }
        catch (IOException) { return null; }
    }

    /// <summary>Reads until a message of this type arrives (skipping others).</summary>
    public async Task<JsonObject?> ReadTypeAsync(string type)
    {
        for (int i = 0; i < 20; i++)
        {
            var m = await ReadAsync();
            if (m is null || (string?)m["t"] == type) return m;
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ssl is not null) await _ssl.DisposeAsync();
        _tcp.Dispose();
    }
}

public sealed class RemoteTests : IClassFixture<RemoteCertFixture>, IAsyncLifetime
{
    private readonly RemoteCertFixture _fx;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fernplay-remote-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRemoteHost _host = new();
    private readonly PairingWindow _pairing = new();
    private readonly PairedPhones _phones;
    private readonly RemoteServer _server;

    public RemoteTests(RemoteCertFixture fx)
    {
        _fx = fx;
        _phones = new PairedPhones(Path.Combine(_folder, "phones.bin"));
        _server = new RemoteServer(_host, fx.Cert, _phones, _pairing);
    }

    public Task InitializeAsync() { _server.Start(0, IPAddress.Loopback); return Task.CompletedTask; }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private string Fp => _server.Fingerprint;

    private async Task<JsonObject?> PairAsync(string code, string name = "Test phone")
    {
        await using var phone = await TestPhone.ConnectAsync(_server.Port, Fp);
        await phone.SendAsync(new JsonObject { ["t"] = "pair", ["name"] = name, ["proof"] = RemoteSecurity.PairProof(code, Fp) });
        return await phone.ReadAsync();
    }

    private async Task<(TestPhone Phone, JsonObject? Answer)> LogInAsync(string id, byte[] key, string? proofOverride = null)
    {
        var phone = await TestPhone.ConnectAsync(_server.Port, Fp);
        await phone.SendAsync(new JsonObject { ["t"] = "hello", ["id"] = id });
        var challenge = await phone.ReadAsync();
        Assert.Equal("challenge", (string?)challenge!["t"]);
        var nonce = Convert.FromBase64String((string)challenge["nonce"]!);
        await phone.SendAsync(new JsonObject { ["t"] = "auth", ["proof"] = proofOverride ?? RemoteSecurity.AuthProof(key, nonce, Fp) });
        return (phone, await phone.ReadAsync());
    }

    private async Task<PairedPhone> PairedAsync()
    {
        var code = _pairing.Open(TimeSpan.FromMinutes(2));
        var answer = await PairAsync(code);
        Assert.Equal("paired", (string?)answer!["t"]);
        return _phones.Find((string?)answer["id"])!;
    }

    [Fact]
    public async Task Pairing_with_the_right_code_gives_a_key_once()
    {
        var code = _pairing.Open(TimeSpan.FromMinutes(2));
        var answer = await PairAsync(code, "Pixel 8");
        Assert.Equal("paired", (string?)answer!["t"]);
        Assert.Equal("TESTPC", (string?)answer["pc"]);
        var phone = Assert.Single(_phones.All);
        Assert.Equal("Pixel 8", phone.Name);
        Assert.Equal(Convert.ToBase64String(phone.Key), (string?)answer["key"]);
        Assert.Equal(32, phone.Key.Length);
        Assert.False(_pairing.IsOpen);
        Assert.Equal("not-pairing", (string?)(await PairAsync(code))!["error"]); // used once
    }

    [Fact]
    public async Task Five_wrong_codes_close_the_window()
    {
        var code = _pairing.Open(TimeSpan.FromMinutes(2));
        var wrong = code == "000000" ? "000001" : "000000";
        for (int i = 1; i < PairingWindow.MaxTries; i++)
            Assert.Equal("wrong-code", (string?)(await PairAsync(wrong))!["error"]);
        Assert.Equal("too-many-tries", (string?)(await PairAsync(wrong))!["error"]);
        Assert.Equal("not-pairing", (string?)(await PairAsync(code))!["error"]);
        Assert.Empty(_phones.All);
    }

    [Fact]
    public void An_expired_code_is_refused()
    {
        var now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        var window = new PairingWindow(() => now);
        var code = window.Open(TimeSpan.FromMinutes(2));
        now = now.AddMinutes(2).AddSeconds(1);
        Assert.Equal(PairingResult.Expired, window.Try(RemoteSecurity.PairProof(code, "fp"), "fp"));
        Assert.False(window.IsOpen);
    }

    [Fact]
    public void A_proof_for_another_certificate_does_not_pair()
    {
        // A server in the middle has another certificate: the phone's proof (made for the fingerprint it saw) fails.
        var window = new PairingWindow();
        var code = window.Open(TimeSpan.FromMinutes(2));
        Assert.Equal(PairingResult.WrongCode, window.Try(RemoteSecurity.PairProof(code, "other-certificate"), "real-certificate"));
    }

    [Fact]
    public async Task A_paired_phone_logs_in_and_gets_state_and_playlist()
    {
        var p = await PairedAsync();
        var (phone, answer) = await LogInAsync(p.Id, p.Key);
        await using var _ = phone;
        Assert.Equal("welcome", (string?)answer!["t"]);
        var state = await phone.ReadTypeAsync("state");
        Assert.Equal("Song", (string?)state!["title"]);
        Assert.Equal("paused", (string?)state["play"]);
        var list = await phone.ReadTypeAsync("playlist");
        var items = list!["items"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.True((bool)items[1]!["station"]!);
        Assert.Equal(["Test phone"], _server.Connected);
    }

    [Fact]
    public async Task Wrong_key_unknown_phone_and_a_replayed_answer_are_refused()
    {
        var p = await PairedAsync();
        var (a, bad) = await LogInAsync(p.Id, new byte[32]);
        await using (a) Assert.Equal("bad-proof", (string?)bad!["error"]);
        var (b, unknown) = await LogInAsync("nobody", p.Key);
        await using (b) Assert.Equal("unknown-phone", (string?)unknown!["error"]);

        // Someone who recorded a good answer cannot use it again: every login has a new nonce.
        var phone = await TestPhone.ConnectAsync(_server.Port, Fp);
        await phone.SendAsync(new JsonObject { ["t"] = "hello", ["id"] = p.Id });
        var nonce = Convert.FromBase64String((string)(await phone.ReadAsync())!["nonce"]!);
        var recorded = RemoteSecurity.AuthProof(p.Key, nonce, Fp);
        await phone.SendAsync(new JsonObject { ["t"] = "auth", ["proof"] = recorded });
        Assert.Equal("welcome", (string?)(await phone.ReadAsync())!["t"]);
        await phone.DisposeAsync();
        var (c, replay) = await LogInAsync(p.Id, p.Key, proofOverride: recorded);
        await using (c) Assert.Equal("bad-proof", (string?)replay!["error"]);
    }

    [Fact]
    public async Task Commands_reach_the_player_and_bad_ones_are_refused()
    {
        var p = await PairedAsync();
        var (phone, _) = await LogInAsync(p.Id, p.Key);
        await using var __ = phone;
        await phone.SendAsync(new JsonObject { ["t"] = "cmd", ["do"] = "toggle" });
        await phone.SendAsync(new JsonObject { ["t"] = "cmd", ["do"] = "volume", ["value"] = 0.25 });
        await phone.SendAsync(new JsonObject { ["t"] = "cmd", ["do"] = "repeat", ["value"] = "one" });
        await phone.SendAsync(new JsonObject { ["t"] = "cmd", ["do"] = "playIndex", ["value"] = 1 });
        await phone.SendAsync(new JsonObject { ["t"] = "cmd", ["do"] = "volume", ["value"] = 7 });      // out of range
        await phone.SendAsync(new JsonObject { ["t"] = "cmd", ["do"] = "format-disk" });                // unknown
        Assert.Equal("unknown-message", (string?)(await phone.ReadTypeAsync("error"))!["error"]);
        Assert.Equal("unknown-message", (string?)(await phone.ReadTypeAsync("error"))!["error"]);
        await phone.SendAsync(new JsonObject { ["t"] = "ping" });
        Assert.NotNull(await phone.ReadTypeAsync("pong"));
        lock (_host.Commands)
            Assert.Equal(["toggle", "volume", "repeat", "playIndex"], _host.Commands.Select(c => c.Do));
        Assert.Equal(0.25, _host.Commands[1].Number);
        Assert.Equal("one", _host.Commands[2].Text);
        Assert.Equal(1, _host.Commands[3].Number);
    }

    [Fact]
    public async Task Changes_are_pushed_and_a_new_playlist_follows_its_version()
    {
        var p = await PairedAsync();
        var (phone, _) = await LogInAsync(p.Id, p.Key);
        await using var __ = phone;
        await phone.ReadTypeAsync("playlist");
        _host.List.Add(new RemoteTrack("New", 61, false));
        _host.Change(_host.State with { Title = "Next song", Play = "playing", ListVersion = 2 });
        var state = await phone.ReadTypeAsync("state");
        Assert.Equal("Next song", (string?)state!["title"]);
        var list = await phone.ReadTypeAsync("playlist");
        Assert.Equal(3, list!["items"]!.AsArray().Count);
        Assert.Equal(2, (int)list["version"]!);
    }

    [Fact]
    public async Task Removing_a_phone_drops_its_connection_and_it_cannot_log_in_again()
    {
        var p = await PairedAsync();
        var (phone, _) = await LogInAsync(p.Id, p.Key);
        await using var __ = phone;
        await phone.ReadTypeAsync("playlist");
        _phones.Remove(p.Id);
        Assert.Null(await phone.ReadTypeAsync("never")); // connection closed
        var (again, answer) = await LogInAsync(p.Id, p.Key);
        await using (again) Assert.Equal("unknown-phone", (string?)answer!["error"]);
    }

    [Fact]
    public async Task A_line_that_is_too_long_ends_the_connection()
    {
        await using var phone = await TestPhone.ConnectAsync(_server.Port, Fp);
        await phone.SendRawAsync("{\"t\":\"" + new string('x', RemoteServer.MaxLine + 10));
        Assert.Null(await phone.ReadAsync());
    }

    [Fact]
    public async Task A_message_other_than_pair_or_hello_first_is_refused()
    {
        await using var phone = await TestPhone.ConnectAsync(_server.Port, Fp);
        await phone.SendAsync(new JsonObject { ["t"] = "cmd", ["do"] = "play" });
        Assert.Equal("expected-hello", (string?)(await phone.ReadAsync())!["error"]);
        Assert.Empty(_host.Commands);
    }

    [Fact]
    public void Paired_phones_are_kept_protected_and_read_back()
    {
        var path = Path.Combine(_folder, "kept.bin");
        var a = new PairedPhones(path);
        var added = a.Add("Phone\u0007 with a very long name that goes on and on and on");
        var raw = File.ReadAllBytes(path);
        Assert.DoesNotContain("Phone", Encoding.UTF8.GetString(raw)); // not readable as text
        var b = new PairedPhones(path);
        var back = Assert.Single(b.All);
        Assert.Equal(added.Key, back.Key);
        Assert.Equal(40, back.Name.Length);
        Assert.DoesNotContain('\u0007', back.Name);
    }

    [Fact]
    public void The_certificate_is_kept_and_reloaded_with_the_same_fingerprint()
    {
        var path = Path.Combine(_fx.Folder, "remote-cert.bin");
        using var again = RemoteSecurity.LoadOrCreateCertificate(path);
        Assert.Equal(RemoteSecurity.Fingerprint(_fx.Cert), RemoteSecurity.Fingerprint(again));
        Assert.True(again.HasPrivateKey);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.168.20", true)]
    [InlineData("169.254.3.4", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("2001:4860::8888", false)]
    [InlineData("::ffff:192.168.1.5", true)]
    public void Only_local_network_addresses_are_served(string address, bool local) =>
        Assert.Equal(local, RemoteServer.IsLocalNetwork(IPAddress.Parse(address)));

    [Fact]
    public async Task Discovery_answers_the_question_with_name_port_and_fingerprint()
    {
        using var discovery = new RemoteDiscovery(0, "TESTPC", _server.Port, Fp);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        await udp.SendAsync(Encoding.ASCII.GetBytes(RemoteDiscovery.Question), new IPEndPoint(IPAddress.Loopback, discovery.Port));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var got = await udp.ReceiveAsync(cts.Token);
        var answer = JsonNode.Parse(got.Buffer)!.AsObject();
        Assert.Equal("TESTPC", (string?)answer["name"]);
        Assert.Equal(_server.Port, (int)answer["port"]!);
        Assert.Equal(Fp, (string?)answer["fp"]);
    }
}
