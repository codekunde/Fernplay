using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fernplay.App.Classic;
using Fernplay.Core.Skins;
using P = Fernplay.Core.Skins.ClassicPlaylist;

namespace Fernplay.App;

/// <summary>
/// The playlist drawn from a classic skin (PLEDIT.BMP / PLEDIT.TXT). The list itself (ListHost: list, empty text,
/// drop line) moves into the skin's frame, so selection, drag-reorder, keys and screen-reader names stay the same;
/// only its colours, font and scroll bar change.
/// </summary>
public partial class PlaylistWindow
{
    /// <summary>Where a control's x is measured from: the left edge, the right edge, or the bottom-right piece.</summary>
    private enum AnchorX { Left, RightEdge, BottomRight }

    private sealed record Anchor(FrameworkElement Element, int X, int Y, AnchorX From, bool InBottomBar);

    private sealed class ClassicView
    {
        public required SkinBitmaps Bitmaps;
        public required Canvas Root, Frame;
        public required Border ListArea, TitleDrag;
        public required SpriteSlider Scroll;
        public required SpriteText RunningTime, MiniMinutes, MiniSeconds;
        public required List<Anchor> Anchors; // controls that move with the width and height
        public required Thumb Grip;
        public bool Active = true;
    }

    private ClassicView? _classic;
    private int _skinSize = 2;
    private int _tiles = P.DefaultTiles;
    private int _widthSteps;

    /// <summary>Width of a classic skin's playlist in 25 px steps beyond 275 (remembered in the settings).</summary>
    public int ClassicWidthSteps
    {
        get => _widthSteps;
        set { _widthSteps = Math.Max(value, 0); if (_classic is not null) LayoutClassic(); }
    }
    private readonly DispatcherTimer _miniClock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _syncingScroll;

    // Fernplay's look of the list, put back when the skin goes.
    private Style? _fernplayContainer;
    private DataTemplate? _fernplayTemplate;
    private Brush? _fernplayEmptyBrush;

    /// <summary>Height of a classic skin's playlist in 29 px tiles (remembered in the settings).</summary>
    public int ClassicTiles
    {
        get => _tiles;
        set { _tiles = Math.Max(value, P.MinTiles); if (_classic is not null) LayoutClassic(); }
    }

    /// <summary>Switches to a classic skin, or back to Fernplay's look (null).</summary>
    public void ApplySkin(ClassicSkin? skin, int size)
    {
        _skinSize = Math.Clamp(size, 1, 3);
        ChangeLook(() =>
        {
            LeaveClassic();
            if (skin is null) return;
            _classic = BuildClassic(skin);
            ClassicHost.Content = _classic.Root;
            ClassicHost.Visibility = Visibility.Visible;
            FernplayView.Visibility = Visibility.Collapsed;
            ApplyClassicScale();
            LayoutClassic();
            UpdateRunningTime();
            _miniClock.Start();
        });
    }

    private void LeaveClassic()
    {
        _miniClock.Stop();
        if (_classic is { } v)
        {
            v.ListArea.Child = null;
            ListBorder.Child = ListHost;
            List.ItemContainerStyle = _fernplayContainer;
            List.ItemTemplate = _fernplayTemplate;
            EmptyText.Foreground = _fernplayEmptyBrush;
            EmptyText.ClearValue(TextBlock.FontFamilyProperty);
            EmptyText.ClearValue(TextBlock.FontSizeProperty);
            if (ScrollBarOf(FindScroller()) is { } bar) bar.ClearValue(VisibilityProperty);
            _classic = null;
        }
        ClassicHost.Content = null;
        ClassicHost.Visibility = Visibility.Collapsed;
        FernplayView.Visibility = Visibility.Visible;
    }

    private void ApplyClassicScale()
    {
        if (_classic is null) return;
        double s = ClassicScale(_skinSize);
        _classic.Root.LayoutTransform = new ScaleTransform(s, s);
        // The list's text: the skin's 9 px at 1x was hard to read; one size up there, the skin's own above that.
        var (font, row) = ClassicPlaylist.ListText(s * (PresentationSource.FromVisual(this) is not null ? VisualTreeHelper.GetDpi(this).DpiScaleY : 1));
        List.Resources["PlFontSize"] = font;
        List.Resources["PlRowHeight"] = row;
        EmptyText.FontSize = font;
    }

    private ClassicView BuildClassic(ClassicSkin skin)
    {
        var bm = new SkinBitmaps(skin);
        var root = new Canvas { ClipToBounds = true, Background = Brushes.Black, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(root, BitmapScalingMode.NearestNeighbor);
        var frame = new Canvas();
        root.Children.Add(frame);
        var anchors = new List<Anchor>();

        T Place<T>(T e, int x, int y) where T : UIElement
        {
            Canvas.SetLeft(e, x); Canvas.SetTop(e, y);
            root.Children.Add(e);
            return e;
        }
        T PlaceAt<T>(T e, int x, int y, AnchorX from, bool inBottomBar) where T : FrameworkElement
        {
            root.Children.Add(e);
            anchors.Add(new Anchor(e, x, y, from, inBottomBar));
            return e;
        }
        // A click area over a part drawn in the skin (bottom bar buttons, mini transport, close).
        SpriteButton Hit(int x, int y, int w, int h, string name, string tip, Action click, AnchorX from, bool inBottomBar = true, string? pressed = null)
        {
            var b = new SpriteButton { Width = w, Height = h, ToolTip = tip, Pressed = pressed is null ? null : bm[pressed] };
            AutomationProperties.SetName(b, name);
            b.Click += (_, _) => click();
            return PlaceAt(b, x, y, from, inBottomBar);
        }

        // The list: PLEDIT.TXT colours and font; the list's own scroll bar makes way for the skin's.
        var style = skin.Playlist;
        var listArea = Place(new Border { Background = new SolidColorBrush(SkinBitmaps.ToColor(style.NormalBackground)) }, P.ListLeft, P.TopHeight);
        _fernplayContainer ??= List.ItemContainerStyle;
        _fernplayTemplate ??= List.ItemTemplate;
        _fernplayEmptyBrush ??= EmptyText.Foreground;
        List.Resources["PlNormal"] = Frozen(style.Normal);
        List.Resources["PlCurrent"] = Frozen(style.Current);
        List.Resources["PlSelectedBg"] = Frozen(style.SelectedBackground);
        List.Resources["PlFont"] = new FontFamily($"{style.Font}, Arial");
        List.ItemContainerStyle = (Style)FindResource("ClassicRowContainer");
        List.ItemTemplate = (DataTemplate)FindResource("ClassicRowTemplate");
        EmptyText.Foreground = Frozen(style.Normal);
        EmptyText.FontFamily = new FontFamily($"{style.Font}, Arial");
        EmptyText.FontSize = 9; // ApplyClassicScale sets the size for the scale
        ListBorder.Child = null;
        listArea.Child = ListHost;
        if (ScrollBarOf(FindScroller()) is { } bar) bar.Visibility = Visibility.Collapsed;

        // Title row: close (its normal look is in the title sprite), shade drawn inactive.
        var titleDrag = Place(new Border { Height = P.TopHeight, Background = Brushes.Transparent }, 0, 0);
        AttachDragHandle(titleDrag);
        Hit(P.CloseFromRight, P.ButtonsY, 9, 9, "Hide the playlist", "Hide the playlist", Hide, AnchorX.RightEdge, inBottomBar: false, pressed: "Pl.Close.Pressed");

        // Scroll knob in the right tile.
        var scroll = PlaceAt(new SpriteSlider
        {
            Style = (Style)FindResource("ClassicPlScroll"), Width = 8, Minimum = 0, Maximum = 0, SmallChange = 1, LargeChange = 5,
            ThumbWidth = 8, ThumbHeight = 18, ThumbImage = bm["Pl.ScrollThumb"], ThumbPressedImage = bm["Pl.ScrollThumb.Pressed"],
        }, P.ScrollFromRight, P.TopHeight, AnchorX.RightEdge, inBottomBar: false);
        scroll.ValueChanged += (_, e) => { if (!_syncingScroll) FindScroller()?.ScrollToVerticalOffset(Math.Round(e.NewValue)); };

        // Bottom bar: the menu buttons, running time, mini transport and clock, scroll arrows, resize grip.
        Hit(P.AddX, P.ButtonY, P.ButtonWidth, P.ButtonHeight, "Add", "Add files, a folder or a stream", () => OpenMenu(bm, P.AddX, AddItems()), AnchorX.Left);
        Hit(P.RemX, P.ButtonY, P.ButtonWidth, P.ButtonHeight, "Remove", "Remove tracks", () => OpenMenu(bm, P.RemX, RemoveItems()), AnchorX.Left);
        Hit(P.SelX, P.ButtonY, P.ButtonWidth, P.ButtonHeight, "Select", "Select all, none or the others", () => OpenMenu(bm, P.SelX, SelectItems()), AnchorX.Left);
        Hit(P.MiscX, P.ButtonY, P.ButtonWidth, P.ButtonHeight, "Miscellaneous", "Sort, file info, options", () => OpenMenu(bm, P.MiscX, MiscItems()), AnchorX.Left);
        Hit(P.ListX, P.ButtonY, P.ButtonWidth, P.ButtonHeight, "Playlist file", "New, save or open a playlist",
            () => OpenMenu(bm, P.BottomRightX(P.Width(_widthSteps)) + P.ListX, ListItems()), AnchorX.BottomRight);

        var running = PlaceAt(new SpriteText(bm) { Width = P.RunningTimeChars * SkinFont.CharWidth, Height = SkinFont.CharHeight }, P.RunningTime.X, P.RunningTime.Y, AnchorX.BottomRight, true);
        var minutes = PlaceAt(new SpriteText(bm) { Width = 2 * SkinFont.CharWidth, Height = SkinFont.CharHeight, IsHitTestVisible = false }, P.MiniMinutes.X, P.MiniMinutes.Y, AnchorX.BottomRight, true);
        var seconds = PlaceAt(new SpriteText(bm) { Width = 2 * SkinFont.CharWidth, Height = SkinFont.CharHeight, IsHitTestVisible = false }, P.MiniSeconds.X, P.MiniSeconds.Y, AnchorX.BottomRight, true);

        var main = Owner as MainWindow;
        string[] miniNames = ["Previous track", "Play", "Pause", "Stop", "Next track", "Open music files"];
        Key[] miniKeys = [Key.Z, Key.X, Key.C, Key.V, Key.B, Key.None];
        for (int i = 0; i < 6; i++)
        {
            var key = miniKeys[i];
            var (x, w) = P.MiniButtons[i];
            Hit(x, P.MiniButtonY, w, P.MiniButtonHeight, miniNames[i], miniNames[i], () =>
            {
                if (key == Key.None) main?.OpenFilesDialog();
                else main?.HandleKey(key, ModifierKeys.None, false);
            }, AnchorX.BottomRight);
        }
        Hit(P.ScrollUp.X, P.ScrollUp.Y, 12, 6, "Scroll up", "Scroll up", () => FindScroller()?.LineUp(), AnchorX.BottomRight);
        Hit(P.ScrollDown.X, P.ScrollDown.Y, 12, 6, "Scroll down", "Scroll down", () => FindScroller()?.LineDown(), AnchorX.BottomRight);

        // Resize grip: taller or shorter in 29 px steps, wider or narrower in 25 px steps, never past the screen edge.
        var grip = PlaceAt(new Thumb
        {
            Width = 20, Height = 20, Cursor = Cursors.SizeNWSE, Focusable = false,
            ToolTip = "Drag to make the playlist taller, shorter, wider or narrower", Template = TransparentThumb(),
        }, P.Grip.X, P.Grip.Y, AnchorX.BottomRight, true);
        grip.DragStarted += (_, _) =>
        {
            var at = Mouse.GetPosition(this);
            _dragStartX = at.X; _dragStartY = at.Y; _dragStartTiles = _tiles; _dragStartSteps = _widthSteps;
        };
        grip.DragDelta += (_, _) =>
        {
            double s = ClassicScale(_skinSize);
            var at = Mouse.GetPosition(this);
            // Limit: the edge of the whole desktop, all screens and the taskbar included (the cursor stops there anyway).
            // The main screen's work area left no room on another screen, or when the panel reached behind the taskbar.
            var desk = Desktop;
            double spareDown = desk.Bottom - (Top + P.Height(_dragStartTiles) * s);
            double spareRight = desk.Right - (Left + P.Width(_dragStartSteps) * s);
            int maxTiles = _dragStartTiles + (int)Math.Floor(Math.Max(0, spareDown) / s / P.TileHeight);
            int maxSteps = _dragStartSteps + (int)Math.Floor(Math.Max(0, spareRight) / s / P.WidthStep);
            int tiles = P.TilesAfterDrag(_dragStartTiles, (at.Y - _dragStartY) / s, maxTiles);
            int steps = P.WidthStepsAfterDrag(_dragStartSteps, (at.X - _dragStartX) / s, maxSteps);
            if (tiles != _tiles || steps != _widthSteps) { _tiles = tiles; _widthSteps = steps; LayoutClassic(); }
        };
        grip.DragCompleted += (_, _) => ScrollIntoViewCurrent();

        return new ClassicView
        {
            Bitmaps = bm, Root = root, Frame = frame, ListArea = listArea, TitleDrag = titleDrag, Scroll = scroll, RunningTime = running,
            MiniMinutes = minutes, MiniSeconds = seconds, Anchors = anchors, Grip = grip, Active = IsActive,
        };
    }

    private int _dragStartTiles, _dragStartSteps;
    private double _dragStartX;

    private static ControlTemplate TransparentThumb()
    {
        var t = new ControlTemplate(typeof(Thumb));
        var f = new FrameworkElementFactory(typeof(Border));
        f.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        t.VisualTree = f;
        return t;
    }

    /// <summary>Draws the frame for the current size and moves the bottom bar, list, scroll knob and buttons to fit.</summary>
    private void LayoutClassic()
    {
        if (_classic is not { } v) return;
        var bm = v.Bitmaps;
        int width = P.Width(_widthSteps), height = P.Height(_tiles), bottomY = height - P.BottomHeight, listHeight = bottomY - P.TopHeight;
        int bottomRight = P.BottomRightX(width);
        v.Root.Width = width; v.Root.Height = height;
        v.TitleDrag.Width = width;

        var f = v.Frame;
        f.Children.Clear();
        f.Width = width; f.Height = height;
        void Img(string sprite, int x, int y)
        {
            var s = SpriteMap.Get(sprite);
            var img = new Image { Source = bm[sprite], Width = s.W, Height = s.H, Stretch = Stretch.Fill };
            Canvas.SetLeft(img, x); Canvas.SetTop(img, y);
            f.Children.Add(img);
        }
        string state = v.Active ? "Active" : "Inactive";
        for (int x = 25; x < width - 25; x += 25) Img($"Pl.TopTile.{state}", x, 0);
        Img($"Pl.Title.{state}", P.TitleX(width), 0);
        Img($"Pl.TopLeft.{state}", 0, 0);
        Img($"Pl.TopRight.{state}", width - 25, 0);
        for (int y = P.TopHeight; y < bottomY; y += P.TileHeight)
        {
            Img("Pl.LeftTile", 0, y);
            Img("Pl.RightTile", width - P.ListRight, y);
        }
        for (int x = P.BottomLeftWidth; x < bottomRight; x += 25) Img("Pl.BottomTile", x, bottomY);
        if (P.VisualizerX(width) is { } vx) Img("Pl.Visualizer", vx, bottomY);
        Img("Pl.BottomLeft", 0, bottomY);
        Img("Pl.BottomRight", bottomRight, bottomY);

        v.ListArea.Width = P.ListWidth(width);
        v.ListArea.Height = listHeight;
        v.Scroll.Height = listHeight;
        foreach (var a in v.Anchors)
        {
            Canvas.SetLeft(a.Element, a.From switch
            {
                AnchorX.RightEdge => width - a.X,
                AnchorX.BottomRight => bottomRight + a.X,
                _ => a.X,
            });
            Canvas.SetTop(a.Element, a.InBottomBar ? bottomY + a.Y : a.Y);
        }
        SyncScroll();
    }

    private void ClassicActivation(bool active)
    {
        if (_classic is not { } v || v.Active == active) return;
        v.Active = active;
        LayoutClassic();
    }

    // ================= Scroll knob =================

    private static ScrollBar? ScrollBarOf(ScrollViewer? sv) => sv?.Template?.FindName("PART_VerticalScrollBar", sv) as ScrollBar;

    private void SyncScroll()
    {
        if (_classic is not { } v || FindScroller() is not { } sv) return;
        _syncingScroll = true;
        v.Scroll.Maximum = sv.ScrollableHeight;
        v.Scroll.Value = sv.VerticalOffset;
        _syncingScroll = false;
    }

    // ================= Running time and mini clock =================

    private void UpdateRunningTime()
    {
        if (_classic is not { } v) return;
        var tracks = _c.Playlist.Tracks;
        var selected = List.SelectedItems.Cast<PlaylistRow>().Select(r => r.Number - 1).Where(i => i < tracks.Count);
        TimeSpan Sum(IEnumerable<int> idx) => TimeSpan.FromTicks(idx.Sum(i => tracks[i].Duration?.Ticks ?? 0));
        bool unknown = tracks.Any(t => t.Duration is null);
        v.RunningTime.Text = P.RunningTimeText(Sum(selected), Sum(Enumerable.Range(0, tracks.Count)), unknown);
    }

    private void UpdateMiniClock()
    {
        if (_classic is not { } v) return;
        if (!_c.Engine.IsLoaded) { v.MiniMinutes.Text = v.MiniSeconds.Text = ""; return; }
        var (m, s) = P.MiniClock(_c.Engine.Position);
        v.MiniMinutes.Text = m;
        v.MiniSeconds.Text = s;
    }

    // ================= Button menus =================

    private sealed record MenuEntry(string Sprite, string Name, Action Act);

    private MenuEntry[] AddItems() =>
    [
        new("Pl.Add.Url", "Add a stream address (not in Fernplay yet)", () => _c.ShowNotice("Adding a stream address: not in Fernplay yet. Open the station's .m3u or .pls instead.")),
        new("Pl.Add.Dir", "Add a folder", () => AddFolder_Click(this, new RoutedEventArgs())),
        new("Pl.Add.File", "Add files", () => AddFiles_Click(this, new RoutedEventArgs())),
    ];

    private MenuEntry[] RemoveItems() =>
    [
        new("Pl.Rem.All", "Remove all", () => _c.ClearList()),
        new("Pl.Rem.Crop", "Crop: keep only the selected tracks", () => RemoveIndexes(Enumerable.Range(0, _rows.Count).Except(SelectedIndexes()))),
        new("Pl.Rem.Selected", "Remove the selected tracks", RemoveSelected),
        new("Pl.Rem.Misc", "Remove missing files", () => RemoveIndexes(Enumerable.Range(0, _rows.Count)
            .Where(i => _c.Playlist.Tracks[i] is { IsStream: false } t && !System.IO.File.Exists(t.Path)))),
    ];

    private MenuEntry[] SelectItems() =>
    [
        new("Pl.Sel.Invert", "Invert the selection", () =>
        {
            var selected = List.SelectedItems.Cast<PlaylistRow>().ToHashSet();
            List.SelectedItems.Clear();
            foreach (var r in _rows.Where(r => !selected.Contains(r))) List.SelectedItems.Add(r);
        }),
        new("Pl.Sel.None", "Select none", () => List.UnselectAll()),
        new("Pl.Sel.All", "Select all", () => List.SelectAll()),
    ];

    private MenuEntry[] MiscItems() =>
    [
        new("Pl.Misc.Sort", "Sort (not in Fernplay yet)", () => _c.ShowNotice("Sorting the list: not in Fernplay yet.")),
        new("Pl.Misc.Info", "File info: edit the selected track's tags (Alt+3)", ShowFileInfo),
        new("Pl.Misc.Options", "Playlist options (not in Fernplay yet)", () => _c.ShowNotice("Playlist options: not in Fernplay yet.")),
    ];

    private MenuEntry[] ListItems() =>
    [
        new("Pl.List.New", "New playlist (empties the list)", () => _c.ClearList()),
        new("Pl.List.Save", "Save the playlist", () => SaveList_Click(this, new RoutedEventArgs())),
        new("Pl.List.Load", "Open a playlist", () => OpenList_Click(this, new RoutedEventArgs())),
    ];

    private void RemoveIndexes(IEnumerable<int> indexes)
    {
        var list = indexes.ToList();
        if (list.Count > 0) _c.Playlist.RemoveAt(list);
    }

    /// <summary>
    /// A classic button menu: the items stacked upwards from the button (the last item over the button), the skin's bar on
    /// the left. Arrow keys move, Enter picks, Escape or a click elsewhere closes.
    /// </summary>
    private void OpenMenu(SkinBitmaps bm, int buttonX, MenuEntry[] items)
    {
        if (_classic is not { } v) return;
        double s = ClassicScale(_skinSize);
        int h = items.Length * P.ButtonHeight;
        var stack = new StackPanel();
        KeyboardNavigation.SetDirectionalNavigation(stack, KeyboardNavigationMode.Cycle);
        // Offsets are in the frame's own (skin px) coordinates: WPF applies the frame's scale to them.
        var popup = new Popup
        {
            AllowsTransparency = true, StaysOpen = false, Placement = PlacementMode.Relative, PlacementTarget = v.Root,
            HorizontalOffset = buttonX - 3,
            VerticalOffset = P.Height(_tiles) - P.BottomHeight + P.ButtonY + P.ButtonHeight - h,
        };
        string bar = items[0].Sprite[..items[0].Sprite.LastIndexOf('.')] + ".Bar";
        var row = new StackPanel { Orientation = Orientation.Horizontal, LayoutTransform = new ScaleTransform(s, s) };
        RenderOptions.SetBitmapScalingMode(row, BitmapScalingMode.NearestNeighbor);
        row.Children.Add(new Image { Source = bm[bar], Width = 3, Height = h, Stretch = Stretch.Fill, VerticalAlignment = VerticalAlignment.Top });
        row.Children.Add(stack);
        foreach (var item in items)
        {
            var b = new SpriteButton
            {
                Style = (Style)FindResource("ClassicMenuItem"), Normal = bm[item.Sprite], Pressed = bm[item.Sprite + ".Pressed"],
                Width = P.ButtonWidth, Height = P.ButtonHeight, ToolTip = item.Name,
            };
            AutomationProperties.SetName(b, item.Name);
            b.Click += (_, _) => { popup.IsOpen = false; item.Act(); };
            stack.Children.Add(b);
        }
        popup.Child = row;
        popup.KeyDown += (_, e) => { if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; } };
        popup.Opened += (_, _) => (stack.Children[^1] as UIElement)?.Focus();
        popup.IsOpen = true;
    }

    private static SolidColorBrush Frozen(uint argb)
    {
        var b = new SolidColorBrush(SkinBitmaps.ToColor(argb));
        b.Freeze();
        return b;
    }
}
