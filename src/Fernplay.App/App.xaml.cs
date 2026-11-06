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
        if (e.Args is ["/register"]) { RegisterForInstall(); Shutdown(); return; }
        if (e.Args is ["/unregister"]) { UnregisterForUninstall(); Shutdown(); return; }
        var request = OpenRequest.FromArgs(e.Args, Environment.CurrentDirectory);
        _instance = SingleInstance.Claim(request);
        if (_instance is null) { Shutdown(); return; } // handed to the running Fernplay

        if (UserSettings.IsPortable) DataFolder.SeedPortable(UserSettings.AppDataFolder, Path.GetDirectoryName(UserSettings.FilePath)!);
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

        var eq = new EqWindow(c.Engine.Equalizer, _settings.EqPresets) { Owner = main };
        eq.PresetsChanged += () => _settings.Save(); // kept at once, not only at the next close
        eq.ApplySkin(main.Skin, _settings.SkinSize);
        var playlist = new PlaylistWindow(c) { Owner = main };
        playlist.ListHeight = _settings.PlaylistHeight; // snapped to rows once shown and filled (below)
        playlist.ListWidth = _settings.PlaylistWidth;
        playlist.ClassicTiles = _settings.PlaylistTiles;
        playlist.ClassicWidthSteps = _settings.PlaylistWidthSteps;
        playlist.ApplySkin(main.Skin, _settings.SkinSize);
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

        // The library: nothing is scanned or saved until a folder is added; hidden unless it was open at the last close.
        var library = new LibraryService(c);
        var libraryWindow = new LibraryWindow(c, library) { Owner = main };
        libraryWindow.ApplySkin(main.Skin, _settings.SkinSize);
        libraryWindow.LibrarySize = new Size(_settings.LibraryWidth, _settings.LibraryHeight);
        libraryWindow.ResizedByHand += (_, _) => _settings.LibraryHeightByHand = true;
        bool libraryPlaced = false, quitting = false;

        // As tall as the stack docked to the player (its top to the lowest panel's bottom), until resized by hand.
        void FitLibraryToStack()
        {
            if (_settings.LibraryHeightByHand) return;
            double stack = SkinWindow.DockedTo(main).Where(w => w.IsVisible && w != libraryWindow).Max(w => w.Top + w.ActualHeight) - main.Top;
            libraryWindow.LibrarySize = new Size(libraryWindow.LibrarySize.Width, stack / SkinWindow.Scale);
        }

        // A new skin or size, or rolling the player up or down (windowshade), changes the player's size and the stack's
        // height: the library stays beside the player.
        int librarySide = 0; // 1 = docked right of the player, -1 = left, 0 = elsewhere
        void NoteLibrarySide()
        {
            bool level = libraryWindow.IsVisible && Math.Abs(libraryWindow.Top - main.Top) < 1;
            librarySide = !level ? 0
                : Math.Abs(libraryWindow.Left - (main.Left + main.ActualWidth)) < 1 ? 1
                : Math.Abs(libraryWindow.Left + libraryWindow.ActualWidth - main.Left) < 1 ? -1 : 0;
        }
        void KeepLibraryBeside()
        {
            if (librarySide == 0) return;
            main.UpdateLayout();
            FitLibraryToStack();
            libraryWindow.UpdateLayout();
            libraryWindow.Left = librarySide > 0 ? main.Left + main.ActualWidth : main.Left - libraryWindow.ActualWidth;
            libraryWindow.Top = main.Top;
        }
        main.SkinApplying += NoteLibrarySide;
        main.SkinApplied += (skin, size) =>
        {
            libraryWindow.ApplySkin(skin, size);
            KeepLibraryBeside();
        };
        main.ShadeChanging += NoteLibrarySide;
        main.ShadeChanged += KeepLibraryBeside;

        void ShowLibrary(bool show)
        {
            if (!show) { libraryWindow.Hide(); return; }
            if (!libraryPlaced)
            {
                libraryPlaced = true;
                FitLibraryToStack();
                // Beside the player (left of it when there is no room on the right) unless a place was saved.
                var wa = SystemParameters.WorkArea;
                double width = libraryWindow.LibrarySize.Width * SkinWindow.Scale;
                double x = main.Left + main.ActualWidth;
                if (x + width > wa.Right) x = Math.Max(wa.Left, main.Left - width);
                Place(libraryWindow, sameScale ? _settings.Library : null, new Point(x, main.Top));
            }
            if (!libraryWindow.IsVisible) libraryWindow.Show();
            libraryWindow.Activate();
        }
        main.ShowLibrary = ShowLibrary;
        main.LibraryShown = () => libraryWindow.IsVisible;
        libraryWindow.Closing += (_, args) =>
        {
            if (quitting) return;
            args.Cancel = true; // Alt+F4 on the library hides it, as its x does
            libraryWindow.Hide();
        };
        if (_settings.LibraryVisible) ShowLibrary(true);
        if (library.Index.Folders.Count > 0)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _ = library.ScanAsync()); // pick up changes since the last run

        foreach (var panel in new Window[] { main, eq, playlist, libraryWindow })
        {
            panel.PreviewKeyDown += (s, args) =>
            {
                var key = args.Key == System.Windows.Input.Key.System ? args.SystemKey : args.Key;
                var mods = System.Windows.Input.Keyboard.Modifiers;
                // Typing in the library's search box: letters and Space are text, not player keys (Alt+L still works).
                if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox && !(key == System.Windows.Input.Key.L && mods == System.Windows.Input.ModifierKeys.Alt)) return;
                if (main.HandleKey(key, mods, s == main)) args.Handled = true;
            };
        }
        ApplyWindowSettings(_settings);
        main.Activate();

        main.Closing += (_, _) =>
        {
            quitting = true;
            library.StopScan();
            library.SaveNow();
            _settings.LibraryVisible = libraryWindow.IsVisible;
            if (libraryPlaced) _settings.Library = new WindowPlace(libraryWindow.Left, libraryWindow.Top);
            _settings.LibraryWidth = libraryWindow.LibrarySize.Width;
            _settings.LibraryHeight = libraryWindow.LibrarySize.Height;
            _settings.Scale = SkinWindow.Scale;
            _settings.Main = new WindowPlace(main.Left, main.Top);
            _settings.Equalizer = new WindowPlace(eq.Left, eq.Top);
            _settings.PlaylistWindow = new WindowPlace(playlist.Left, playlist.Top);
            _settings.EqualizerVisible = eq.IsVisible;
            _settings.PlaylistVisible = playlist.IsVisible;
            _settings.PlaylistHeight = playlist.ListHeight;
            _settings.PlaylistWidth = playlist.ListWidth;
            _settings.PlaylistTiles = playlist.ClassicTiles;
            _settings.PlaylistWidthSteps = playlist.ClassicWidthSteps;
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

    /// <summary>
    /// "Fernplay.exe /register", run by the MSI on install and on every update: sets Fernplay up in Explorer
    /// ("Open with", folder verbs, Default apps) unless the user unticked that in Settings. No window.
    /// </summary>
    private static void RegisterForInstall()
    {
        try
        {
            if (Shell.TurnedOff) return;
            Shell.Register();
            ShellNotify.AssociationsChanged();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
    }

    /// <summary>
    /// "Fernplay.exe /unregister", run by the MSI on uninstall (not on an upgrade): removes the Explorer set-up
    /// if it points at this exe (a portable copy that took Explorer over keeps it) and forgets an untick, so a
    /// later install starts fresh. No window, no settings touched.
    /// </summary>
    private static void UnregisterForUninstall()
    {
        try
        {
            Shell.TurnedOff = false;
            if (!Shell.IsRegisteredHere()) return;
            Shell.Unregister();
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
