using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fernplay.Core;
using Fernplay.Core.Lyrics;
using Fernplay.Core.Skins;

namespace Fernplay.App;

/// <summary>
/// Lyrics of the playing song, from this PC only: a .lrc file next to it, or its tags (Fernplay.Core.Lyrics). Synced
/// lyrics follow the song: the line being sung is selected and kept in the middle, scrolling smoothly (no animation when
/// Windows' "show animations" is off); a click on a line plays from there. Unsynced lyrics are plain text to scroll.
/// Reloads when the song changes and when its .lrc file changes on disk.
/// </summary>
public partial class LyricsWindow : SkinWindow
{
    public const double DefaultWidth = 220, DefaultHeight = 260, MinSkinWidth = 140, MinSkinHeight = 110;

    /// <summary>After the owner scrolls by hand, the lines stay where they put them this long before following again.</summary>
    private static readonly TimeSpan HoldAfterScroll = TimeSpan.FromSeconds(5);

    private readonly PlayerController _c;
    private readonly DispatcherTimer _follow = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _reloadDelay = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private SongLyrics? _lyrics;
    private string? _songPath;     // the song the window is for (null: nothing playing)
    private int _load;             // newer loads win over older ones still reading
    private int _current = -1;     // the line selected as being sung
    private bool _syncing;         // the code selects the current line: not a request to seek
    private DateTime _holdUntil;   // scrolled by hand: no following until then
    private TimeSpan _lastPosition;
    private long _lastTick;
    private FileSystemWatcher? _watcher;

    public LyricsWindow(PlayerController controller, UserSettings settings)
    {
        _c = controller;
        InitializeComponent();
        ApplyColors(null);
        AttachDragHandle(TitleBar);
        AttachDragHandle(FrameTitle);
        Activated += (_, _) => Frame.IsActiveLook = true;
        Deactivated += (_, _) => Frame.IsActiveLook = false;
        DpiChanged += (_, _) => ApplyChrome();
        SourceInitialized += (_, _) => ApplyChrome(); // the skin's pixel scale depends on the screen's DPI
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(Button))
            .AddValueChanged(FrameClose, (_, _) => Frame.ClosePressed = FrameClose.IsPressed);
        LyricsSize = new Size(settings.LyricsWidth, settings.LyricsHeight);

        Lines.SelectionChanged += Lines_SelectionChanged;
        Lines.PreviewMouseLeftButtonDown += Lines_PreviewMouseDown;
        Lines.PreviewKeyDown += Lines_PreviewKeyDown;
        Lines.PreviewMouseWheel += (_, _) => HoldFollowing();
        Lines.AddHandler(ScrollBar.ScrollEvent, new ScrollEventHandler((_, _) => HoldFollowing()));
        Lines.SizeChanged += (_, _) => PadLines();

        _follow.Tick += (_, _) => Follow(animate: true);
        _reloadDelay.Tick += (_, _) => { _reloadDelay.Stop(); Load(keepPlace: true); };
        _c.NowPlayingChanged += OnNowPlayingChanged;
        _c.TagsSaved += OnTagsSaved;
        IsVisibleChanged += (_, _) => UpdateFollowing();
        StateChanged += (_, _) => UpdateFollowing();
        Closed += (_, _) =>
        {
            _load++; // a read still running is dropped
            _c.NowPlayingChanged -= OnNowPlayingChanged;
            _c.TagsSaved -= OnTagsSaved;
            _follow.Stop();
            _reloadDelay.Stop();
            StopAnimation();
            Watch(null);
        };
        Load(keepPlace: false);
    }

    // ================= Size =================

    /// <summary>Size of the window in Fernplay skin pixels (remembered in the settings); 0 = default.</summary>
    public Size LyricsSize
    {
        get => new(Shell.Width / Scale, Shell.Height / Scale);
        set
        {
            double w = value.Width > 0 ? Math.Max(value.Width, MinSkinWidth) : DefaultWidth;
            double h = value.Height > 0 ? Math.Max(value.Height, MinSkinHeight) : DefaultHeight;
            SetShell(w * Scale, h * Scale);
        }
    }

    /// <summary>Whole DIPs, and on a classic skin whole skin pixels, so the frame meets the window's edges.</summary>
    private void SetShell(double widthDip, double heightDip)
    {
        double step = FrameScale > 0 ? FrameScale : 1;
        Shell.Width = Math.Max(step, Math.Round(widthDip / step) * step);
        Shell.Height = Math.Max(step, Math.Round(heightDip / step) * step);
    }

    private Point _gripStart;
    private Size _gripStartSize; // DIPs

    private void Grip_DragStarted(object sender, DragStartedEventArgs e)
    {
        _gripStart = Mouse.GetPosition(this); // window coordinates: the top left stays put
        _gripStartSize = new Size(Shell.Width, Shell.Height);
    }

    private void Grip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var p = Mouse.GetPosition(this);
        var desk = Desktop;
        double minW = MinSkinWidth * Scale, minH = MinSkinHeight * Scale;
        double maxW = Math.Max(Math.Max(minW, _gripStartSize.Width), desk.Right - Left);
        double maxH = Math.Max(Math.Max(minH, _gripStartSize.Height), desk.Bottom - Top);
        SetShell(Math.Clamp(_gripStartSize.Width + p.X - _gripStart.X, minW, maxW),
                 Math.Clamp(_gripStartSize.Height + p.Y - _gripStart.Y, minH, maxH));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ================= Classic skin =================

    private GenSkin? _gen;
    private int _skinSize = 2;

    /// <summary>Screen DIPs per classic skin pixel while the classic frame is on; 0 in Fernplay's look.</summary>
    private double FrameScale => _gen is null ? 0 : ClassicScale(_skinSize);

    /// <summary>A classic skin draws GEN.BMP's frame around the lyrics and gives them GENEX.BMP's colours; null: Fernplay's look.</summary>
    public void ApplySkin(ClassicSkin? skin, int size)
    {
        _skinSize = Math.Clamp(size, 1, 3);
        _gen = skin is null ? null : GenSkin.From(skin);
        Frame.Gen = _gen;
        Frame.Title = "FERNPLAY LYRICS"; // drawn in the skin's letters
        ApplyColors(_gen?.Colors);
        ApplyChrome();
    }

    private void ApplyChrome()
    {
        bool classic = _gen is not null;
        Frame.Visibility = FrameTitle.Visibility = FrameClose.Visibility = FrameGrip.Visibility = classic ? Visibility.Visible : Visibility.Collapsed;
        TitleBar.Visibility = Grip.Visibility = classic ? Visibility.Collapsed : Visibility.Visible;
        Root.BorderThickness = new Thickness(classic ? 0 : 0.5);
        Well.Margin = classic ? new Thickness(0, 0, 0, 0) : new Thickness(4, 3, 4, 0);
        double s = FrameScale;
        if (classic && s > 0)
        {
            Frame.SkinScale = s;
            Root.Margin = new Thickness(GenSkin.LeftWidth * s, GenSkin.TitleHeight * s, GenSkin.RightWidth * s, GenSkin.BottomHeight * s);
            FrameTitle.Height = GenSkin.TitleHeight * s;
            FrameClose.Width = FrameClose.Height = GenSkin.CloseSize * s;
            FrameClose.Margin = new Thickness(0, GenSkin.CloseTop * s, (GenSkin.CloseRight - GenSkin.CloseSize) * s, 0);
            FrameGrip.Width = FrameGrip.Height = GenSkin.BottomHeight * s;
        }
        else Root.Margin = new Thickness(0);
        SetShell(Shell.Width, Shell.Height);
    }

    /// <summary>Every "Ly.*" colour the XAML uses: Fernplay's look (null) or a classic skin's GENEX colours.</summary>
    private void ApplyColors(GenColors? c)
    {
        static Brush Solid(uint argb, double opacity = 1)
        {
            var color = Classic.SkinBitmaps.ToColor(argb);
            color.A = (byte)Math.Round(255 * opacity);
            var b = new SolidColorBrush(color);
            b.Freeze();
            return b;
        }
        Brush Res(string key) => (Brush)FindResource(key);
        var set = c is null
            ? new Dictionary<string, Brush>
            {
                ["Ly.Window"] = Res("PanelBrush"), ["Ly.Bg"] = Res("Lcd"), ["Ly.Edge"] = Res("Shadow"),
                ["Ly.Text"] = Res("SkinTextDim"), ["Ly.Dim"] = Solid(0xFF5C6170),
                ["Ly.Current"] = Res("LcdGreen"), ["Ly.CurrentBg"] = Solid(0xFF0F2A16), ["Ly.Hover"] = Solid(0xFF14301A),
                ["Ly.Status"] = Res("SkinTextDim"),
            }
            : new Dictionary<string, Brush>
            {
                ["Ly.Window"] = Solid(c.WindowBackground), ["Ly.Bg"] = Solid(c.ItemBackground), ["Ly.Edge"] = Solid(c.Divider),
                ["Ly.Text"] = Solid(c.ItemText), ["Ly.Dim"] = Solid(c.ItemText, 0.55),
                ["Ly.Current"] = Solid(c.WindowText), ["Ly.CurrentBg"] = Solid(c.SelectionBackground), ["Ly.Hover"] = Solid(c.SelectionBackground, 0.35),
                ["Ly.Status"] = Solid(c.WindowText),
            };
        foreach (var (key, brush) in set) Resources[key] = brush;
    }

    // ================= Loading =================

    private void OnNowPlayingChanged(object? sender, EventArgs e)
    {
        if (string.Equals(_c.NowPlaying?.Path, _songPath, StringComparison.OrdinalIgnoreCase)) return; // same song, other tags
        Load(keepPlace: false);
    }

    private void OnTagsSaved(object? sender, string path)
    {
        if (string.Equals(path, _songPath, StringComparison.OrdinalIgnoreCase)) Load(keepPlace: true);
    }

    /// <summary>Reads the lyrics of the song playing (off the UI thread); keepPlace: the same song again (its .lrc changed).</summary>
    private void Load(bool keepPlace)
    {
        int load = ++_load;
        var track = _c.NowPlaying;
        _songPath = track?.Path;
        if (track is null)
        {
            Watch(null);
            Show(null, "Nothing is playing. The lyrics of the song that plays show here.");
            return;
        }
        if (track.IsStream)
        {
            Watch(null);
            string what = _c.IsEpisode(track.Path) ? "podcast episodes played from the web" : "radio stations";
            Show(null, $"No lyrics for {what}. Fernplay shows lyrics for songs on this PC, from a .lrc file next to the song or from its tags.");
            return;
        }
        string path = track.Path;
        Watch(LyricsFinder.LrcPathFor(path));
        Task.Run(() => LyricsFinder.Find(path)).ContinueWith(t =>
        {
            if (load != _load) return; // another song since
            if (t.Exception is not null) DiagnosticLog.Write($"lyrics not read for {Path.GetFileName(path)}: {PlaybackErrors.Detail(t.Exception.GetBaseException())}");
            var lyrics = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
            Show(lyrics, lyrics is null
                ? $"No lyrics for this song. Put a .lrc file with the same name next to it ({Path.GetFileName(LyricsFinder.LrcPathFor(path))}), or save the lyrics in its tags."
                : null, keepPlace);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Lyrics to show, or the message in their place.</summary>
    private void Show(SongLyrics? lyrics, string? message, bool keepPlace = false)
    {
        double offset = Scroller?.VerticalOffset ?? 0;
        _lyrics = lyrics;
        _current = -1;
        StopAnimation();
        bool synced = lyrics?.IsSynced == true;
        _syncing = true;
        Lines.ItemsSource = lyrics?.Lines.Select(l => new LineItem(l, synced)).ToList();
        Lines.SelectedIndex = -1;
        _syncing = false;
        Lines.Visibility = lyrics is null ? Visibility.Collapsed : Visibility.Visible;
        MessageText.Text = message ?? "";
        MessageText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        string lrcName = lyrics is null ? "" : Path.GetFileName(LyricsFinder.LrcPathFor(lyrics.SongPath));
        SourceText.Text = lyrics is null ? "" : (lyrics.Source, synced) switch
        {
            (LyricsSource.LrcFile, true) => $"Synced · {lrcName}",
            (LyricsSource.LrcFile, false) => $"{lrcName} · not timed",
            (LyricsSource.EmbeddedSynced, _) => "Synced · from the song's tags",
            _ => "From the song's tags · not timed",
        };
        if (!keepPlace) _holdUntil = default;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            PadLines();
            if (keepPlace && !synced) Scroller?.ScrollToVerticalOffset(offset);
            else if (!synced) Scroller?.ScrollToTop();
            Follow(animate: false);
        });
        UpdateFollowing();
    }

    /// <summary>A line as the list shows it.</summary>
    private sealed class LineItem(LyricLine line, bool synced)
    {
        public TimeSpan? Time => line.Time;
        public bool IsGap => line.IsGap;
        public bool Clickable => synced;
        public string Display => line.IsGap && synced ? "♪" : line.Text;
        public string AutomationName => line.IsGap ? (synced ? "Instrumental" : "Blank line") : line.Text;
        public TextAlignment Alignment => synced ? TextAlignment.Center : TextAlignment.Left;
    }

    // ================= .lrc changes on disk =================

    /// <summary>Watches that one .lrc file (also before it exists): a change reloads the lyrics after a short pause.</summary>
    private void Watch(string? lrcPath)
    {
        if (_watcher is { } old && lrcPath is not null && string.Equals(Path.Combine(old.Path, old.Filter), lrcPath, StringComparison.OrdinalIgnoreCase)) return;
        _watcher?.Dispose();
        _watcher = null;
        if (lrcPath is null || Path.GetDirectoryName(lrcPath) is not { Length: > 0 } folder) return;
        try
        {
            var w = new FileSystemWatcher(folder, Path.GetFileName(lrcPath))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(() => { _reloadDelay.Stop(); _reloadDelay.Start(); });
            w.Changed += changed;
            w.Created += changed;
            w.Deleted += changed;
            w.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { _reloadDelay.Stop(); _reloadDelay.Start(); });
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // A folder that cannot be watched (some network drives): the lyrics still load with each song.
        }
    }

    // ================= Following the song =================

    private ScrollViewer? Scroller => Lines.Template?.FindName("Scroller", Lines) as ScrollViewer;

    private void UpdateFollowing()
    {
        bool run = IsVisible && WindowState != WindowState.Minimized && _lyrics?.IsSynced == true;
        if (run) _follow.Start(); else _follow.Stop();
    }

    /// <summary>Synced lyrics: room above the first and below the last line, so every line can sit in the middle.</summary>
    private void PadLines()
    {
        double half = _lyrics?.IsSynced == true ? Math.Max(0, Lines.ActualHeight / 2 - 8) : 2;
        var pad = new Thickness(0, half, 0, half);
        if (Lines.Padding != pad) Lines.Padding = pad;
    }

    /// <summary>The position of the song the lyrics belong to, or null when another one (or nothing) is loaded.</summary>
    private TimeSpan? SongPosition()
    {
        var engine = _c.Engine;
        if (_lyrics is null || !engine.IsLoaded || !string.Equals(engine.CurrentPath, _lyrics.SongPath, StringComparison.OrdinalIgnoreCase)) return null;
        return engine.Position;
    }

    /// <summary>Selects the line being sung and keeps it in the middle (unless the owner scrolled away a moment ago).</summary>
    private void Follow(bool animate)
    {
        if (_lyrics is not { IsSynced: true } lyrics) return;
        var position = SongPosition();
        // A jump (seek, a line clicked, another start) brings the lines back from where they were scrolled.
        long now = Environment.TickCount64;
        if (position is { } p)
        {
            var expected = _lastPosition + TimeSpan.FromMilliseconds(now - _lastTick);
            if (Math.Abs((p - expected).TotalSeconds) > 1.5 && Math.Abs((p - _lastPosition).TotalSeconds) > 1.5) _holdUntil = default;
            _lastPosition = p;
        }
        _lastTick = now;
        int index = position is { } at ? lyrics.IndexAt(at) : -1;
        if (index != _current)
        {
            _current = index;
            _syncing = true;
            Lines.SelectedIndex = index;
            _syncing = false;
            if (DateTime.UtcNow >= _holdUntil) Centre(index, animate);
        }
        else if (!animate && DateTime.UtcNow >= _holdUntil) Centre(index, animate: false);
    }

    private void Centre(int index, bool animate)
    {
        if (Scroller is not { } sv) return;
        double target;
        if (index < 0) target = 0;
        else if (Lines.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement row && row.IsVisible)
        {
            var top = row.TranslatePoint(new Point(0, 0), sv).Y;
            target = sv.VerticalOffset + top + row.ActualHeight / 2 - sv.ViewportHeight / 2;
        }
        else return;
        ScrollTo(Math.Clamp(target, 0, sv.ScrollableHeight), animate);
    }

    private void HoldFollowing()
    {
        _holdUntil = DateTime.UtcNow + HoldAfterScroll;
        StopAnimation();
    }

    // ================= Smooth scrolling =================

    private double _from, _to;
    private long _animStart;
    private bool _animating;
    private const double AnimationMs = 320;

    /// <summary>Scrolls to an offset: eased over a third of a second, or at once when Windows' animations are off.</summary>
    private void ScrollTo(double offset, bool animate)
    {
        if (Scroller is not { } sv) return;
        if (!animate || !SystemParameters.ClientAreaAnimation || !IsVisible || Math.Abs(offset - sv.VerticalOffset) < 0.5)
        {
            StopAnimation();
            sv.ScrollToVerticalOffset(offset);
            return;
        }
        (_from, _to, _animStart) = (sv.VerticalOffset, offset, Environment.TickCount64);
        if (!_animating)
        {
            _animating = true;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (Scroller is not { } sv) { StopAnimation(); return; }
        double t = Math.Clamp((Environment.TickCount64 - _animStart) / AnimationMs, 0, 1);
        double eased = 1 - Math.Pow(1 - t, 3);
        sv.ScrollToVerticalOffset(_from + (_to - _from) * eased);
        if (t >= 1) StopAnimation();
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    // ================= Lines: click, select, keys =================

    /// <summary>A click on a synced line plays from it (also on the line already being sung: back to its start).</summary>
    private void Lines_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Lines, e.OriginalSource as DependencyObject) is not ListBoxItem { DataContext: LineItem item }) return;
        Lines.Focus();
        e.Handled = true; // the selection follows the song, not the mouse
        if (item.Time is { } t) SeekTo(t);
    }

    /// <summary>A line selected through UI Automation (Narrator, a test) plays from it; the code's own selection does not.</summary>
    private void Lines_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (Lines.SelectedItem is LineItem { Time: { } t }) SeekTo(t);
        else
        {
            _syncing = true;
            Lines.SelectedIndex = _current; // unsynced lines or a gap: nothing to select
            _syncing = false;
        }
    }

    private void SeekTo(TimeSpan time)
    {
        var engine = _c.Engine;
        if (SongPosition() is null || !engine.CanSeek)
        {
            // Not the song loaded (it was stopped and another one opened): put the selection back.
            _syncing = true;
            Lines.SelectedIndex = _current;
            _syncing = false;
            return;
        }
        engine.Seek(time < engine.Duration ? time : engine.Duration);
        _holdUntil = default;
        _lastPosition = time;
        _lastTick = Environment.TickCount64;
        _current = -2; // select and centre again on the next tick, also when it is the same line
        Follow(animate: true);
    }

    /// <summary>Up, Down, Page Up, Page Down, Home and End scroll the lines (the selection stays with the song).</summary>
    private void Lines_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Scroller is not { } sv || Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Up: sv.ScrollToVerticalOffset(sv.VerticalOffset - 24); break;
            case Key.Down: sv.ScrollToVerticalOffset(sv.VerticalOffset + 24); break;
            case Key.PageUp: sv.PageUp(); break;
            case Key.PageDown: sv.PageDown(); break;
            case Key.Home: sv.ScrollToTop(); break;
            case Key.End: sv.ScrollToBottom(); break;
            default: return;
        }
        HoldFollowing();
        e.Handled = true;
    }
}
