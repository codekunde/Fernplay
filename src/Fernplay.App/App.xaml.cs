using System.IO;
using System.Windows;
using System.Windows.Threading;
using Fernplay.Core;

namespace Fernplay.App;

public partial class App : Application
{
    private PlayerController? _controller;
    private UserSettings _settings = new();
    private SingleInstance? _instance;
    private readonly OpenBatch _batch = new();
    private DispatcherTimer? _batchTimer;

    /// <summary>The exe and file icon Explorer is pointed at.</summary>
    public static ShellRegistration Shell { get; } = ShellRegistration.ForCurrentUser(
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Fernplay.exe"),
        Path.Combine(AppContext.BaseDirectory, "Assets", "FernplayFile.ico"));

    /// <summary>
    /// Arguments: music files, folders or playlists to play; with /add they go to the end of the list
    /// (Explorer's "Open", "Play in Fernplay" and "Add to Fernplay playlist" pass them this way).
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var request = OpenRequest.FromArgs(e.Args, Environment.CurrentDirectory);
        _instance = SingleInstance.Claim(request);
        if (_instance is null) { Shutdown(); return; } // handed to the running Fernplay

        FocusRing.Install();
        KeepShellRegistrationCurrent();
        _settings = UserSettings.Load();
        var c = _controller = new PlayerController(Dispatcher);
        var eqSettings = c.Engine.Equalizer;
        eqSettings.Preamp = _settings.Preamp;
        for (int i = 0; i < 10; i++) eqSettings.SetBand(i, _settings.Bands[i]);
        eqSettings.Enabled = _settings.EqEnabled;
        c.Playlist.Repeat = _settings.Repeat;
        c.Playlist.Shuffle = _settings.Shuffle;

        var main = new MainWindow(c, _settings);
        MainWindow = main;
        ApplySavedSkin(main, c);
        Place(main, _settings.Main, null);
        main.Show();
        if (_settings.Main is null) // first run: centre the stack of three panels
        {
            main.Left = (SystemParameters.WorkArea.Width - main.ActualWidth) / 2;
            main.Top = Math.Max(SystemParameters.WorkArea.Top, (SystemParameters.WorkArea.Height - main.ActualHeight * 3.2) / 2);
        }

        var eq = new EqWindow(c.Engine.Equalizer) { Owner = main };
        eq.ApplySkin(main.Skin, _settings.SkinSize);
        var playlist = new PlaylistWindow(c) { Owner = main };
        playlist.ListHeight = _settings.PlaylistHeight; // snapped to rows once shown and filled (below)
        // Positions saved at another skin scale no longer line up: stack the panels under the player again.
        bool sameScale = (_settings.Scale == 0 ? 2.0 : _settings.Scale) == SkinWindow.Scale;
        // Show to measure, then place: below the main panel unless a position was saved.
        eq.Show();
        Place(eq, sameScale ? _settings.Equalizer : null, new Point(main.Left, main.Top + main.ActualHeight));
        playlist.Show();
        Place(playlist, sameScale ? _settings.PlaylistWindow : null, new Point(eq.Left, eq.Top + eq.ActualHeight));
        if (!_settings.EqualizerVisible) eq.Hide();
        if (!_settings.PlaylistVisible) playlist.Hide();
        main.AttachPanels(eq, playlist);
        foreach (var panel in new Window[] { main, eq, playlist })
        {
            panel.PreviewKeyDown += (s, args) =>
            {
                var key = args.Key == System.Windows.Input.Key.System ? args.SystemKey : args.Key;
                if (main.HandleKey(key, System.Windows.Input.Keyboard.Modifiers, s == main)) args.Handled = true;
            };
        }
        ApplyWindowSettings(_settings);
        main.Activate();

        main.Closing += (_, _) =>
        {
            _settings.Scale = SkinWindow.Scale;
            _settings.Main = new WindowPlace(main.Left, main.Top);
            _settings.Equalizer = new WindowPlace(eq.Left, eq.Top);
            _settings.PlaylistWindow = new WindowPlace(playlist.Left, playlist.Top);
            _settings.EqualizerVisible = eq.IsVisible;
            _settings.PlaylistVisible = playlist.IsVisible;
            _settings.PlaylistHeight = playlist.ListHeight;
            _settings.EqEnabled = eqSettings.Enabled;
            _settings.Preamp = eqSettings.Preamp;
            _settings.Bands = eqSettings.Bands;
            _settings.Shuffle = c.Playlist.Shuffle;
            _settings.Repeat = c.Playlist.Repeat;
            _settings.Playlist = _settings.RememberPlaylist ? c.Playlist.Tracks.Select(t => t.Path).ToList() : [];
            _settings.CurrentIndex = _settings.RememberPlaylist ? c.Playlist.CurrentIndex : -1;
            _settings.StationNames = _settings.RememberPlaylist
                ? c.Playlist.Tracks.Where(t => t is { IsStream: true, Title: not null }).DistinctBy(t => t.Path).ToDictionary(t => t.Path, t => t.Title!)
                : [];
            _settings.Save();
        };

        if (request.Paths.Count == 0 || request.Add) c.Restore(_settings.Playlist, _settings.CurrentIndex, _settings.StationNames);
        if (request.Paths.Count > 0) Queue(request);
        _instance.Listen(r => Dispatcher.BeginInvoke(() => { Queue(r); BringToFront(main); }));
    }

    /// <summary>Puts the remembered classic skin on the player; one that is gone or broken is dropped with a notice.</summary>
    private void ApplySavedSkin(MainWindow main, PlayerController c)
    {
        if (_settings.Skin is not { } name) return;
        try
        {
            var skins = Fernplay.App.MainWindow.Skins;
            var path = skins.Find(name) ?? throw new Fernplay.Core.Skins.SkinLoadException($"{name} is no longer in {skins.Folder}.");
            main.ApplySkin(Fernplay.Core.Skins.ClassicSkin.Load(path), _settings.SkinSize);
        }
        catch (Fernplay.Core.Skins.SkinLoadException ex)
        {
            _settings.Skin = null;
            c.ShowNotice($"Skin not used: {ex.Message}");
        }
    }

    /// <summary>Gathers requests until 350 ms pass without a new one, then opens them as one batch.</summary>
    private void Queue(OpenRequest request)
    {
        _batch.Add(request);
        if (_batchTimer is null)
        {
            _batchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _batchTimer.Tick += async (_, _) =>
            {
                _batchTimer.Stop();
                var (open, add) = _batch.Take();
                if (_controller is null) return;
                if (open.Count > 0) await _controller.OpenAsync(open, replace: true);
                if (add.Count > 0) await _controller.OpenAsync(add, replace: false);
            };
        }
        _batchTimer.Stop();
        _batchTimer.Start();
    }

    private static void BringToFront(Window main)
    {
        if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
        main.Activate();
    }

    /// <summary>If Fernplay was set up for Explorer from another folder, point Explorer at this exe.</summary>
    private static void KeepShellRegistrationCurrent()
    {
        try
        {
            if (!Shell.NeedsRefresh()) return;
            Shell.Register();
            ShellNotify.AssociationsChanged();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Applies the options from the settings dialog to every panel.</summary>
    public static void ApplyWindowSettings(UserSettings s)
    {
        SkinWindow.SnapEnabled = s.SnapPanels;
        foreach (Window w in Current.Windows)
            if (w is SkinWindow) w.Topmost = s.AlwaysOnTop;
    }

    /// <summary>Uses the saved position when it is still on a screen, otherwise the fallback.</summary>
    private static void Place(Window w, WindowPlace? saved, Point? fallback)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (saved is not null && screen.Contains(new Point(saved.Left + 40, saved.Top + 10)))
        {
            w.Left = saved.Left;
            w.Top = saved.Top;
        }
        else if (fallback is { } p)
        {
            w.Left = p.X;
            w.Top = p.Y;
        }
    }
}
