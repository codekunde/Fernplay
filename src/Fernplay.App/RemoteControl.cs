using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Threading;
using Fernplay.Core;
using Fernplay.Core.Remote;

namespace Fernplay.App;

/// <summary>
/// The phone remote on the PC: off until switched on in Settings. Then a TLS server and the discovery answer run, and
/// paired phones control the player through the same calls the buttons use (on the UI thread).
/// </summary>
public sealed class RemoteControl : IRemoteHost, IDisposable
{
    public static readonly TimeSpan PairingTime = TimeSpan.FromMinutes(2);

    private readonly PlayerController _c;
    private readonly MainWindow _main;
    private readonly UserSettings _settings;
    private readonly Dispatcher _ui;
    private readonly string _folder;
    private readonly DispatcherTimer _listSettle;
    private X509Certificate2? _cert;
    private RemoteServer? _server;
    private RemoteDiscovery? _discovery;
    private PairedPhones? _phones;
    private int _listVersion = 1;

    public RemoteControl(PlayerController c, MainWindow main, UserSettings settings)
    {
        _c = c;
        _main = main;
        _settings = settings;
        _ui = main.Dispatcher;
        _folder = Path.GetDirectoryName(UserSettings.FilePath)!;
        // Tags read in the background update rows one by one: the phone gets the list again once they settle.
        _listSettle = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => { _listSettle!.Stop(); BumpList(); }, _ui);
        _listSettle.Stop();
        c.StateChanged += (_, _) => Raise();
        c.NowPlayingChanged += (_, _) => Raise();
        c.Playlist.CurrentChanged += (_, _) => Raise();
        c.Playlist.ModeChanged += (_, _) => Raise();
        c.Playlist.Changed += (_, _) => BumpList();
        c.Playlist.ItemUpdated += (_, _) => { _listSettle.Stop(); _listSettle.Start(); };
        main.ControlsChanged += (_, _) => Raise();
    }

    public PairingWindow Pairing { get; } = new();
    public bool IsOn => _server is not null;
    /// <summary>Why the remote could not start, or null.</summary>
    public string? Problem { get; private set; }
    public int Port => _server?.Port ?? _settings.RemotePort;
    public IReadOnlyList<PairedPhone> Phones => PhonesStore.All;
    public IReadOnlyList<string> Connected => _server?.Connected ?? [];

    /// <summary>On/off, a phone paired, connected or went away: Settings refreshes. Raised on the UI thread.</summary>
    public event EventHandler? StatusChanged;

    private PairedPhones PhonesStore
    {
        get
        {
            if (_phones is null)
            {
                _phones = new PairedPhones(Path.Combine(_folder, "remote-phones.bin"));
                _phones.Changed += (_, _) => _ui.BeginInvoke(() => StatusChanged?.Invoke(this, EventArgs.Empty));
            }
            return _phones;
        }
    }

    /// <summary>Starts the server and the discovery answer. False (with <see cref="Problem"/>) when it cannot.</summary>
    public bool TurnOn()
    {
        if (IsOn) return true;
        Problem = null;
        try
        {
            _cert ??= RemoteSecurity.LoadOrCreateCertificate(Path.Combine(_folder, "remote-cert.bin"));
            var server = new RemoteServer(this, _cert, PhonesStore, Pairing);
            server.PhonesChanged += (_, _) => _ui.BeginInvoke(() => StatusChanged?.Invoke(this, EventArgs.Empty));
            server.Start(_settings.RemotePort, LoopbackOnly ? IPAddress.Loopback : IPAddress.Any);
            _server = server;
            try { _discovery = new RemoteDiscovery(LoopbackOnly ? 0 : RemoteDiscovery.DefaultPort, PcName, server.Port, server.Fingerprint); }
            catch (SocketException) { } // another program holds the discovery port: typing the address still works
            DiagnosticLog.Write($"remote on, port {server.Port}{(LoopbackOnly ? " (this PC only)" : "")}");
        }
        catch (SocketException ex)
        {
            Problem = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"Port {_settings.RemotePort} is in use by another program."
                : $"Could not start the remote: {ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            Problem = $"Could not start the remote: {ex.Message}";
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return IsOn;
    }

    public void TurnOff()
    {
        Pairing.Close();
        _discovery?.Dispose();
        _discovery = null;
        var server = _server;
        _server = null;
        if (server is not null)
        {
            _ = server.DisposeAsync().AsTask();
            DiagnosticLog.Write("remote off");
        }
        Problem = null;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemovePhone(string id) => PhonesStore.Remove(id);

    /// <summary>
    /// The PC's IPv4 addresses on its real networks, for typing into the phone. Networks without a gateway (WSL,
    /// Hyper-V and other virtual switches) are left out: no phone is on them.
    /// </summary>
    public static IReadOnlyList<string> Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => n.GetIPProperties())
            .Where(ip => ip.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .SelectMany(ip => ip.UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && RemoteServer.IsLocalNetwork(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Select(a => a.ToString()).Distinct().ToList();

    // Debug builds, test runs: FERNPLAY_REMOTE_LOOPBACK=1 listens on this PC only (no firewall prompt, no discovery port).
#if DEBUG
    private static bool LoopbackOnly => Environment.GetEnvironmentVariable("FERNPLAY_REMOTE_LOOPBACK") == "1";
#else
    private static bool LoopbackOnly => false;
#endif

    // ================= IRemoteHost (server threads → UI thread) =================

    public string PcName => Environment.MachineName;

    public event EventHandler? Changed;

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    private void BumpList()
    {
        _listVersion++;
        Raise();
    }

    public Task<RemoteState> GetStateAsync() => _ui.InvokeAsync(() =>
    {
        var e = _c.Engine;
        var now = _c.NowPlaying;
        string? title = now?.Title, artist = now?.Artist;
        if (now is { IsStream: true })
        {
            // A station: its song (when it says) as the title, the station's name under it.
            title = _c.StreamTitle ?? now.DisplayName;
            artist = now.DisplayName;
        }
        else if (now is not null && string.IsNullOrWhiteSpace(title)) title = now.DisplayName;
        string play = e.State switch { PlayState.Playing => "playing", PlayState.Paused => "paused", _ => "stopped" };
        string repeat = _c.Playlist.Repeat switch { RepeatMode.All => "all", RepeatMode.One => "one", _ => "off" };
        return new RemoteState(play, title, artist, now?.IsStream == true, e.Position.TotalSeconds, e.Duration.TotalSeconds,
            e.CanSeek, e.Volume, _c.Playlist.Shuffle, repeat, _c.CrossfadeOn, _c.Playlist.CurrentIndex, _listVersion);
    }).Task;

    public Task<IReadOnlyList<RemoteTrack>> GetPlaylistAsync() => _ui.InvokeAsync<IReadOnlyList<RemoteTrack>>(() =>
        _c.Playlist.Tracks.Select(t => new RemoteTrack(t.DisplayName, t.Duration?.TotalSeconds, t.IsStream)).ToList()).Task;

    public Task ExecuteAsync(RemoteCommand command) => _ui.InvokeAsync(async () =>
    {
        var e = _c.Engine;
        switch (command.Do)
        {
            case "play": if (e.State == PlayState.Paused) _c.TogglePause(); else if (e.State == PlayState.Stopped) await _c.PlayAsync(); break;
            case "pause": if (e.State == PlayState.Playing) _c.TogglePause(); break;
            case "toggle": if (e.State == PlayState.Stopped) await _c.PlayAsync(); else _c.TogglePause(); break;
            case "stop": _c.Stop(); break;
            case "next": await _c.NextAsync(); break;
            case "previous": await _c.PreviousAsync(); break;
            case "seek": if (e.CanSeek) { e.Seek(TimeSpan.FromSeconds(command.Number!.Value)); Raise(); } break;
            case "volume": _main.RemoteSetVolume(command.Number!.Value); break;
            case "shuffle": _main.RemoteSetShuffle(command.Flag!.Value); break;
            case "repeat": _main.RemoteSetRepeat(command.Text switch { "all" => RepeatMode.All, "one" => RepeatMode.One, _ => RepeatMode.Off }); break;
            case "xfade": _main.RemoteSetXfade(command.Flag!.Value); break;
            case "playIndex":
                int i = (int)command.Number!.Value;
                if (i < _c.Playlist.Tracks.Count) await _c.PlayIndexAsync(i, forward: true);
                break;
        }
        DiagnosticLog.Write($"remote: {command.Do}");
    }).Task.Unwrap();

    public void Dispose()
    {
        TurnOff();
        _cert?.Dispose();
    }
}
