using System.Windows;

namespace Fernplay.App;

public partial class App : Application
{
    private PlayerController? _controller;
    private UserSettings _settings = new();

    /// <summary>Arguments: music files or folders to play (Explorer's "Open with" passes them this way).</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
        Place(main, _settings.Main, null);
        main.Show();
        if (_settings.Main is null) // first run: centre the stack of three panels
        {
            main.Left = (SystemParameters.WorkArea.Width - main.ActualWidth) / 2;
            main.Top = Math.Max(SystemParameters.WorkArea.Top, (SystemParameters.WorkArea.Height - main.ActualHeight * 3.2) / 2);
        }

        var eq = new EqWindow(c.Engine.Equalizer) { Owner = main };
        var playlist = new PlaylistWindow(c) { Owner = main };
        // Show to measure, then place: below the main panel unless a position was saved.
        eq.Show();
        Place(eq, _settings.Equalizer, new Point(main.Left, main.Top + main.ActualHeight));
        playlist.Show();
        Place(playlist, _settings.PlaylistWindow, new Point(eq.Left, eq.Top + eq.ActualHeight));
        if (!_settings.EqualizerVisible) eq.Hide();
        if (!_settings.PlaylistVisible) playlist.Hide();
        main.AttachPanels(eq, playlist);
        main.Activate();

        main.Closing += (_, _) =>
        {
            _settings.Main = new WindowPlace(main.Left, main.Top);
            _settings.Equalizer = new WindowPlace(eq.Left, eq.Top);
            _settings.PlaylistWindow = new WindowPlace(playlist.Left, playlist.Top);
            _settings.EqualizerVisible = eq.IsVisible;
            _settings.PlaylistVisible = playlist.IsVisible;
            _settings.EqEnabled = eqSettings.Enabled;
            _settings.Preamp = eqSettings.Preamp;
            _settings.Bands = eqSettings.Bands;
            _settings.Shuffle = c.Playlist.Shuffle;
            _settings.Repeat = c.Playlist.Repeat;
            _settings.Playlist = c.Playlist.Tracks.Select(t => t.Path).ToList();
            _settings.CurrentIndex = c.Playlist.CurrentIndex;
            _settings.Save();
        };

        if (e.Args.Length > 0) _ = c.OpenAsync(e.Args, replace: true);
        else c.Restore(_settings.Playlist, _settings.CurrentIndex);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        base.OnExit(e);
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
