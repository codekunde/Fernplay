using System.Windows;
using System.Windows.Controls.Primitives;
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

    private static readonly Brush LitLabel = new SolidColorBrush(Color.FromRgb(0x4B, 0xE8, 0x68));
    private static readonly Brush DimLabel = new SolidColorBrush(Color.FromRgb(0x4A, 0x4F, 0x5E));

    public MainWindow(PlayerController controller, UserSettings settings)
    {
        _c = controller;
        _settings = settings;
        InitializeComponent();
        CarriesDocked = true;
        AttachDragHandle(TitleBar);
        AppMenuIcon.Source = AppIcon.Frame(16);

        VolumeSlider.Value = Math.Round(settings.Volume * 100);
        _c.Engine.Volume = (float)settings.Volume;
        _showRemaining = settings.ShowRemaining;
        ShuffleToggle.IsChecked = _c.Playlist.Shuffle;
        ShowRepeat();

        _c.StateChanged += (_, _) => UpdateState();
        _c.NowPlayingChanged += (_, _) => ShowNowPlaying();
        _c.StatusChanged += (_, _) => ShowNowPlaying();
        _c.Playlist.Changed += (_, _) => UpdateState();
        _anim.Tick += (_, _) => Animate();
        _anim.Start();
        Closed += (_, _) => _anim.Stop();
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
        AppMenu.PlacementTarget = AppMenuButton;
        AppMenu.Placement = PlacementMode.Bottom;
        AppMenu.IsOpen = true;
    }

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

    // ================= Opening =================

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open music files",
            Multiselect = true,
            Filter = "Music files|*.mp3;*.flac;*.wav;*.wma;*.m4a;*.aac|All files|*.*",
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
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = _c.OpenAsync(paths, replace: true);
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

    // ================= Display =================

    private void Animate()
    {
        _frame++;
        TitleMarquee.Step();
        Spectrum.Push(_c.Engine.GetSpectrum(SpectrumBars.Bands));
        if (_frame % 3 == 0) UpdateClock();
    }

    private void UpdateClock()
    {
        var duration = _c.Engine.Duration;
        var position = _seekDragging ? TimeSpan.FromSeconds(SeekSlider.Value) : _c.Engine.Position;
        string text = _showRemaining ? TimeFormat.Clock(duration - position, remaining: true) : TimeFormat.Clock(position);
        // The clock blinks while paused, as the old players did.
        Clock.Visibility = _c.Engine.State == PlayState.Paused && (_frame / 15) % 2 == 1 ? Visibility.Hidden : Visibility.Visible;
        if (Clock.Text != text) Clock.Text = text;
        if (_seekDragging) return;
        _updatingSeek = true;
        SeekSlider.Maximum = Math.Max(duration.TotalSeconds, 1);
        SeekSlider.Value = Math.Min(position.TotalSeconds, SeekSlider.Maximum);
        _updatingSeek = false;
    }

    private void ShowNowPlaying()
    {
        var t = _c.NowPlaying;
        if (_c.Status is { } status)
            TitleMarquee.Text = status;
        else if (t is not null)
            TitleMarquee.Text = $"{_c.Playlist.CurrentIndex + 1}. {t.DisplayName}" + (t.Duration is { } d ? $" ({TimeFormat.Clock(d)})" : "");
        else
            TitleMarquee.Text = "Fernplay  Open music with the eject button or drop it here";

        var f = _c.Engine.IsLoaded ? _c.Engine.Format : null;
        KbpsText.Text = t?.BitrateKbps is { } kbps && f is not null ? kbps.ToString() : "";
        KhzText.Text = f is null ? "" : (f.SampleRate / 1000).ToString();
        MonoText.Foreground = f?.Channels == 1 ? LitLabel : DimLabel;
        StereoText.Foreground = f?.Channels >= 2 ? LitLabel : DimLabel;
        Title = t is null ? "Fernplay" : $"{t.DisplayName} - Fernplay";
        UpdateState();
    }

    private void UpdateState()
    {
        var state = _c.Engine.State;
        StateGlyph.Data = (Geometry)FindResource(state switch
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
        SeekSlider.IsEnabled = loaded;
        UpdateClock();
    }
}
