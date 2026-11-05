using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fernplay.Core;

namespace Fernplay.App;

public partial class MainWindow : SkinWindow
{
    private readonly PlayerController _c;
    private readonly UserSettings _settings;
    private readonly DispatcherTimer _anim = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private int _frame;
    private bool _showRemaining;
    private bool _seekDragging;
    private bool _updatingSeek; // true while the timer moves the slider, so that is not taken as a seek
    private Window? _eq, _playlist;
    private bool _ready; // false while the constructor sets controls from the saved settings
    private MediaSession? _media; // Windows media session: background media keys and the overlay

    private static readonly Brush LitLabel = new SolidColorBrush(Color.FromRgb(0x4B, 0xE8, 0x68));
    private static readonly Brush DimLabel = new SolidColorBrush(Color.FromRgb(0x4A, 0x4F, 0x5E));

    public MainWindow(PlayerController controller, UserSettings settings)
    {
        _c = controller;
        _settings = settings;
        InitializeComponent();
        CarriesDocked = true;
        AttachDragHandle(TitleBar);
        AttachDragHandle(ShadeBar);
        // A double-click on the title bar rolls the player up; on the rolled-up strip, down again (not on its buttons).
        TitleBar.PreviewMouseLeftButtonDown += (_, e) => ShadeOnDoubleClick(e, TitleBar, true);
        ShadeBar.PreviewMouseLeftButtonDown += (_, e) => ShadeOnDoubleClick(e, ShadeBar, false);
        AppMenuIcon.Source = ShadeMenuIcon.Source = AppIcon.Frame(16);

        VolumeSlider.Value = Math.Round(settings.Volume * 100);
        _c.Engine.Volume = (float)settings.Volume;
        _c.Engine.Balance = (float)settings.Balance;
        BalanceSlider.Value = Math.Round(settings.Balance * 100);
        BalanceSlider.ToolTip = BalanceText(BalanceSlider.Value);
        SliderReset.OnDoubleClick(BalanceSlider, 0);
        Activated += (_, _) => ClassicActivation(true);
        Deactivated += (_, _) => ClassicActivation(false);
        DpiChanged += (_, _) => ApplyClassicScale();
        _showRemaining = settings.ShowRemaining;
        _shaded = settings.PlayerShaded;
        ShowShadeState();
        ShuffleToggle.IsChecked = _c.Playlist.Shuffle;
        ShowRepeat();

        _c.StateChanged += (_, _) => UpdateState();
        _c.NowPlayingChanged += (_, _) => ShowNowPlaying();
        _c.StatusChanged += (_, _) => ShowNowPlaying();
        _c.Playlist.Changed += (_, _) => UpdateState();
        _anim.Tick += (_, _) => Animate();
        _anim.Start();
        Closed += (_, _) => { _anim.Stop(); _media?.Dispose(); };
        SourceInitialized += (_, _) => { StartMediaSession(); ApplyClassicScale(); };
        ShowNowPlaying();
        UpdateState();
        _ready = true;
    }

    /// <summary>Connects the EQ and playlist panels to their switches.</summary>
    public void AttachPanels(Window eq, Window playlist)
    {
        _eq = eq;
        _playlist = playlist;
        eq.IsVisibleChanged += (_, _) => EqToggle.IsChecked = eq.IsVisible;
        playlist.IsVisibleChanged += (_, _) => PlaylistToggle.IsChecked = playlist.IsVisible;
        EqToggle.IsChecked = eq.IsVisible;
        PlaylistToggle.IsChecked = playlist.IsVisible;
    }

    // ================= Title bar and panels =================

    private void AppMenu_Click(object sender, RoutedEventArgs e)
    {
        AppMenu.PlacementTarget = sender as UIElement ?? AppMenuButton; // the title bar's or the rolled-up strip's menu button
        AppMenu.Placement = PlacementMode.Bottom;
        AppMenu.IsOpen = true;
    }

    private void Shade_Click(object sender, RoutedEventArgs e) => SetShaded(true);
    private void Unshade_Click(object sender, RoutedEventArgs e) => SetShaded(false);

    private void ShadeOnDoubleClick(MouseButtonEventArgs e, FrameworkElement bar, bool shade)
    {
        if (e.ClickCount != 2 || e.OriginalSource is not DependencyObject d || IsInsideButton(d, bar) || IsInside<System.Windows.Controls.Slider>(d, bar)) return;
        SetShaded(shade);
        e.Handled = true;
    }

    private static bool IsInside<T>(DependencyObject d, DependencyObject stopAt) where T : DependencyObject
    {
        for (var x = d; x is not null && x != stopAt; x = x is Visual ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x is T) return true;
        return false;
    }

    /// <summary>Shows or hides the library window (set by App).</summary>
    public Action<bool>? ShowLibrary { get; set; }
    /// <summary>Whether the library window is shown (set by App).</summary>
    public Func<bool>? LibraryShown { get; set; }

    private void Library_Click(object sender, RoutedEventArgs e) => ShowLibrary?.Invoke(true);

    private void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow(_settings) { Owner = this }.ShowDialog();
    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // Checked/Unchecked rather than Click: Narrator and other UI Automation clients toggle without a click.
    private void EqToggle_Changed(object sender, RoutedEventArgs e) => Toggle(_eq, EqToggle);
    private void PlaylistToggle_Changed(object sender, RoutedEventArgs e) => Toggle(_playlist, PlaylistToggle);

    private static void Toggle(Window? w, ToggleButton t)
    {
        if (w is null) return;
        if (t.IsChecked == true) w.Show(); else w.Hide();
    }

    // ================= Keys =================

    /// <summary>
    /// The classic single keys, for every panel: Z X C V B = previous, play, pause, stop, next; Space = play/pause
    /// (always; Enter presses a focused button); media keys. On the player panel also
    /// Left/Right = seek 5 s and Up/Down = volume. Returns true when the key was used.
    /// </summary>
    public bool HandleKey(Key key, ModifierKeys mods, bool onPlayerPanel)
    {
        // With the media session on, Windows delivers the media keys through it (also while Fernplay has focus);
        // handling them here as well would act twice.
        if (_media is { IsActive: true } && key is Key.MediaPlayPause or Key.MediaStop or Key.MediaNextTrack or Key.MediaPreviousTrack)
            return false;
        switch (key)
        {
            case Key.MediaPlayPause: _ = PlayPauseAsync(); return true;
            case Key.MediaStop: _c.Stop(); return true;
            case Key.MediaNextTrack: _ = _c.NextAsync(); return true;
            case Key.MediaPreviousTrack: _ = _c.PreviousAsync(); return true;
        }
        if (key == Key.L && mods == ModifierKeys.Alt && ShowLibrary is not null)
        {
            ShowLibrary(LibraryShown?.Invoke() != true); // Alt+L shows or hides the library
            return true;
        }
        if (key == Key.D3 && mods == ModifierKeys.Alt && onPlayerPanel)
        {
            // Alt+3 on the player: file info of the playing track (the playlist and library use their selection).
            if (_c.NowPlaying is { } now) TagEditorWindow.Open(this, _c, now);
            else _c.ShowNotice("Nothing is playing. Select a track in the playlist and press Alt+3 for its file info.");
            return true;
        }
        if (key == Key.W && mods == ModifierKeys.Control)
        {
            SetShaded(!_shaded); // Ctrl+W rolls the player up or down, as on the classic player
            return true;
        }
        if (mods != ModifierKeys.None) return false; // Ctrl+O, Alt+Up in the playlist and so on

        switch (key)
        {
            case Key.Z: _ = _c.PreviousAsync(); return true;
            case Key.X: Play_Click(this, new RoutedEventArgs()); return true;
            case Key.C: _c.TogglePause(); return true;
            case Key.V: _c.Stop(); return true;
            case Key.B: _ = _c.NextAsync(); return true;
            // Space is always play/pause, also when a button has the focus (a clicked button keeps it, and Space would
            // press it again: clicking Play, then Space, restarted the track). Enter presses a focused button.
            case Key.Space:
                _ = PlayPauseAsync();
                return true;
        }
        if (!onPlayerPanel) return false;
        switch (key)
        {
            case Key.Left or Key.Right when _c.Engine.IsLoaded && _c.Engine.CanSeek:
                var to = _c.Engine.Position + TimeSpan.FromSeconds(key == Key.Left ? -5 : 5);
                _c.Engine.Seek(to < TimeSpan.Zero ? TimeSpan.Zero : to > _c.Engine.Duration ? _c.Engine.Duration : to);
                UpdateClock();
                return true;
            case Key.Up or Key.Down:
                VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + (key == Key.Up ? 5 : -5), 0, 100);
                return true;
        }
        return false;
    }

    // ================= Windows media session =================

    private void StartMediaSession()
    {
        _media = MediaSession.TryCreate(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        if (_media is null) return;
        _media.ButtonPressed += button => Dispatcher.BeginInvoke(async () =>
        {
            var state = _c.Engine.State;
            DiagnosticLog.Write($"Windows media button {button} while {state}");
            switch (button)
            {
                case MediaSession.Button.Play when state == PlayState.Paused: _c.TogglePause(); break;
                case MediaSession.Button.Play when state == PlayState.Stopped: await _c.PlayAsync(); break;
                case MediaSession.Button.Pause when state == PlayState.Playing: _c.TogglePause(); break;
                case MediaSession.Button.Stop: _c.Stop(); break;
                case MediaSession.Button.Next: await _c.NextAsync(); break;
                case MediaSession.Button.Previous: await _c.PreviousAsync(); break;
            }
        });
        UpdateMediaSession();
    }

    private void UpdateMediaSession()
    {
        if (_media is null) return;
        var t = _c.NowPlaying;
        if (t is { IsStream: true } && _c.StreamTitle is { } song)
        {
            // Stations send "Artist - Title"; without the separator the whole text is the title and the station the artist.
            int dash = song.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0) _media.SetTrack(song[(dash + 3)..].Trim(), song[..dash].Trim());
            else _media.SetTrack(song, t.DisplayName);
        }
        else _media.SetTrack(t is null ? null : t.IsStream ? t.DisplayName : t.Title ?? System.IO.Path.GetFileNameWithoutExtension(t.Path), t?.Artist);
        _media.SetStatus(!_c.Engine.IsLoaded ? MediaSession.Status.Closed : _c.Engine.State switch
        {
            PlayState.Playing => MediaSession.Status.Playing,
            PlayState.Paused => MediaSession.Status.Paused,
            _ => MediaSession.Status.Stopped,
        });
    }

    private async Task PlayPauseAsync()
    {
        if (_c.Engine.State == PlayState.Stopped) await _c.PlayAsync();
        else _c.TogglePause();
    }

    // ================= Opening =================

    /// <summary>The open dialog (eject), also used by the skinned playlist's mini transport.</summary>
    public void OpenFilesDialog() => Open_Click(this, new RoutedEventArgs());

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open music files",
            Multiselect = true,
            Filter = PlayerController.OpenFilter,
        };
        if (dialog.ShowDialog(this) == true) _ = _c.OpenAsync(dialog.FileNames, replace: true);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Open a music folder" };
        if (dialog.ShowDialog(this) == true) _ = _c.OpenAsync([dialog.FolderName], replace: true);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        if (IsSkinDrop(paths)) ApplySkinFile(paths[0]); // a classic skin (.wsz) dropped on the player puts it on
        else _ = _c.OpenAsync(paths, replace: true);
    }

    // ================= Transport =================

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (!await _c.PlayAsync()) Open_Click(sender, e);
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => _c.TogglePause();
    private void Stop_Click(object sender, RoutedEventArgs e) => _c.Stop();
    private async void Prev_Click(object sender, RoutedEventArgs e) => await _c.PreviousAsync();
    private async void Next_Click(object sender, RoutedEventArgs e) => await _c.NextAsync();

    private void Shuffle_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) _c.Playlist.Shuffle = ShuffleToggle.IsChecked == true;
    }

    /// <summary>Each press (click or toggle) moves repeat on: off, whole list, this track, off.</summary>
    private void Repeat_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _showingRepeat) return;
        _c.Playlist.Repeat = _c.Playlist.Repeat switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off,
        };
        ShowRepeat();
    }

    private bool _showingRepeat;

    private void ShowRepeat()
    {
        _showingRepeat = true;
        RepeatToggle.IsChecked = _c.Playlist.Repeat != RepeatMode.Off;
        _showingRepeat = false;
        RepeatToggle.Content = _c.Playlist.Repeat == RepeatMode.One ? "REPEAT 1" : "REPEAT";
        System.Windows.Automation.AutomationProperties.SetHelpText(RepeatToggle, _c.Playlist.Repeat.ToString());
    }

    // ================= Seek, clock, volume =================

    private void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSeek || _seekDragging || !_c.Engine.IsLoaded) return;
        _c.Engine.Seek(TimeSpan.FromSeconds(SeekSlider.Value)); // a click on the bar or the arrow keys
        UpdateClock();
    }

    private void Seek_DragStarted(object sender, DragStartedEventArgs e) => _seekDragging = true;

    private void Seek_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _seekDragging = false;
        _c.Engine.Seek(TimeSpan.FromSeconds(SeekSlider.Value));
        UpdateClock();
    }

    private void Clock_Click(object sender, RoutedEventArgs e)
    {
        _showRemaining = !_showRemaining;
        _settings.ShowRemaining = _showRemaining;
        UpdateClock();
    }

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return; // XAML or the constructor setting the value, not the user
        _c.Engine.Volume = (float)(e.NewValue / 100);
        _settings.Volume = e.NewValue / 100;
        VolumeSlider.ToolTip = $"Volume {e.NewValue:0}%";
    }

    /// <summary>Mouse wheel over the volume bar: 5 % per notch (touchpads send smaller steps, added up).</summary>
    private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _wheel += e.Delta;
        int notches = _wheel / Mouse.MouseWheelDeltaForOneLine;
        if (notches != 0)
        {
            _wheel -= notches * Mouse.MouseWheelDeltaForOneLine;
            VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + notches * 5, 0, 100);
        }
        e.Handled = true;
    }

    private int _wheel;

    /// <summary>
    /// Mouse wheel over a position bar: 5 s per notch, up = forward (touchpad deltas summed). Setting the seek bar's
    /// value seeks through Seek_ValueChanged; off for a live station (the bar is disabled) and while the thumb is dragged.
    /// </summary>
    private void Seek_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (!SeekSlider.IsEnabled || _seekDragging) return;
        _seekWheel += e.Delta;
        int notches = _seekWheel / Mouse.MouseWheelDeltaForOneLine;
        if (notches != 0)
        {
            _seekWheel -= notches * Mouse.MouseWheelDeltaForOneLine;
            SeekSlider.Value = Math.Clamp(SeekSlider.Value + notches * SeekSlider.SmallChange, 0, SeekSlider.Maximum);
        }
    }

    private int _seekWheel;

    // ================= Display =================

    private void Animate()
    {
        _frame++;
        if (_shaded) ShadeMarquee.Step(); else TitleMarquee.Step();
        var levels = _c.Engine.GetSpectrum(SpectrumBars.Bands);
        Spectrum.Push(levels);
        ClassicAnimate(levels);
        if (_frame % 3 == 0) UpdateClock();
    }

    private void UpdateClock()
    {
        var duration = _c.Engine.Duration;
        var position = _seekDragging ? TimeSpan.FromSeconds(SeekSlider.Value) : _c.Engine.Position;
        // A live station has no length: the clock always counts up.
        bool remaining = _showRemaining && _c.Engine.CanSeek;
        string text = remaining ? TimeFormat.Clock(duration - position, remaining: true) : TimeFormat.Clock(position);
        // The clock blinks while paused, as the old players did.
        bool blink = _c.Engine.State == PlayState.Paused && (_frame / 15) % 2 == 1;
        Clock.Visibility = ShadeClock.Visibility = blink ? Visibility.Hidden : Visibility.Visible;
        if (Clock.Text != text) Clock.Text = text;
        if (ShadeClock.Text != text) ShadeClock.Text = text;
        ClassicUpdateClock(remaining ? -(duration - position) : position, blink);
        if (_seekDragging) return;
        _updatingSeek = true;
        SeekSlider.Maximum = Math.Max(duration.TotalSeconds, 1);
        // A live station has no length: keep the knob at the start instead of pinned to the end.
        SeekSlider.Value = _c.Engine.CanSeek ? Math.Min(position.TotalSeconds, SeekSlider.Maximum) : 0;
        _updatingSeek = false;
    }

    private void ShowNowPlaying()
    {
        var t = _c.NowPlaying;
        if (_c.Status is { } status)
            TitleMarquee.Text = status;
        else if (t is not null)
            TitleMarquee.Text = NowPlayingText(t) + (t.Duration is { } d ? $" ({TimeFormat.Clock(d)})" : "");
        else
            TitleMarquee.Text = "Fernplay  Open music with the eject button or drop it here";
        ShadeMarquee.Text = TitleMarquee.Text;

        var f = _c.Engine.IsLoaded ? _c.Engine.Format : null;
        KbpsText.Text = (t?.BitrateKbps ?? f?.BitrateKbps) is { } kbps && f is not null ? kbps.ToString() : "";
        KhzText.Text = f is null ? "" : (f.SampleRate / 1000).ToString();
        MonoText.Foreground = f?.Channels == 1 ? LitLabel : DimLabel;
        StereoText.Foreground = f?.Channels >= 2 ? LitLabel : DimLabel;
        Title = t is null ? "Fernplay" : $"{NowPlayingText(t)} - Fernplay";
        ClassicShowNowPlaying(f);
        UpdateState();
    }

    /// <summary>The track's name; for a station that says what it plays: "Artist - Title (Station)".</summary>
    private string NowPlayingText(Fernplay.Core.Track t) =>
        t.IsStream && _c.StreamTitle is { } song ? $"{song} ({t.DisplayName})" : t.DisplayName;

    private void UpdateState()
    {
        var state = _c.Engine.State;
        StateGlyph.Data = ShadeStateGlyph.Data = (Geometry)FindResource(state switch
        {
            PlayState.Playing => "GlyphPlay",
            PlayState.Paused => "GlyphPause",
            _ => "GlyphStop",
        });
        bool loaded = _c.Engine.IsLoaded;
        bool hasList = _c.Playlist.Tracks.Count > 0;
        PrevButton.IsEnabled = hasList;
        NextButton.IsEnabled = hasList;
        PauseButton.IsEnabled = loaded && state != PlayState.Stopped;
        StopButton.IsEnabled = loaded && state != PlayState.Stopped;
        SeekSlider.IsEnabled = loaded && _c.Engine.CanSeek; // off for a live station
        ClassicUpdateState(state);
        UpdateClock();
        UpdateMediaSession();
    }
}
