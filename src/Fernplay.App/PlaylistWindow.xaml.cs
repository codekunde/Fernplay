using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Fernplay.Core;
using Track = Fernplay.Core.Track;

namespace Fernplay.App;

/// <summary>One line in the playlist: "03 - Artist - Title" and the length. Number = place in the list (1-based).</summary>
public sealed class PlaylistRow(int number, Track track) : INotifyPropertyChanged
{
    private Track _track = track;
    private bool _current;

    public int Number { get; set; } = number;
    public string Text => _track.DisplayName;
    public string Length => _track.IsStream ? "LIVE" : _track.Duration is { } d ? TimeFormat.Clock(d) : "";
    public string AccessibleName => (IsCurrent ? "Playing: " : "") + Text + (Length == "" ? "" : ", " + Length);

    public bool IsCurrent
    {
        get => _current;
        set { if (_current != value) { _current = value; Raise(nameof(IsCurrent)); Raise(nameof(AccessibleName)); } }
    }

    public void Refresh(Track t)
    {
        _track = t;
        Raise(nameof(Text)); Raise(nameof(Length)); Raise(nameof(AccessibleName));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class PlaylistWindow : SkinWindow
{
    private readonly PlayerController _c;
    private readonly ObservableCollection<PlaylistRow> _rows = [];
    private int _shownCurrent = -1;

    public PlaylistWindow(PlayerController controller)
    {
        _c = controller;
        InitializeComponent();
        AttachDragHandle(TitleBar);
        List.ItemsSource = _rows;
        List.ItemContainerGenerator.StatusChanged += List_ContainersGenerated;
        _c.Playlist.Changed += (_, _) => Rebuild();
        _c.Playlist.ItemUpdated += (_, i) => { if (i < _rows.Count) _rows[i].Refresh(_c.Playlist.Tracks[i]); UpdateTotal(); };
        _c.Playlist.CurrentChanged += (_, _) => MarkCurrent(scroll: true);
        Rebuild();
        // Classic skin: title bar state, sharp pixels, running time, mini clock, scroll knob.
        Activated += (_, _) => ClassicActivation(true);
        Deactivated += (_, _) => ClassicActivation(false);
        DpiChanged += (_, _) => ApplyClassicScale();
        List.SelectionChanged += (_, _) => UpdateRunningTime();
        List.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => SyncScroll()));
        _miniClock.Tick += (_, _) => UpdateMiniClock();
        // A skin applied before the window was shown could not reach the list's scroll bar yet.
        List.Loaded += (_, _) =>
        {
            if (_classic is not null && ScrollBarOf(FindScroller()) is { } bar) bar.Visibility = Visibility.Collapsed;
            SyncScroll();
        };
    }

    private void Rebuild()
    {
        _rows.Clear();
        for (int i = 0; i < _c.Playlist.Tracks.Count; i++) _rows.Add(new PlaylistRow(i + 1, _c.Playlist.Tracks[i]));
        _shownCurrent = -1;
        // No scrolling here: removing or moving rows must leave the view where it is (it jumped to the playing track).
        // A new current track scrolls through CurrentChanged.
        MarkCurrent(scroll: false);
        UpdateTotal();
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MarkCurrent(bool scroll)
    {
        if (_shownCurrent >= 0 && _shownCurrent < _rows.Count) _rows[_shownCurrent].IsCurrent = false;
        _shownCurrent = _c.Playlist.CurrentIndex;
        if (_shownCurrent >= 0 && _shownCurrent < _rows.Count)
        {
            _rows[_shownCurrent].IsCurrent = true;
            if (scroll) List.ScrollIntoView(_rows[_shownCurrent]);
        }
    }

    private void UpdateTotal()
    {
        TotalText.Text = _c.Playlist.Summary;
        UpdateRunningTime();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private async void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedIndex >= 0) await _c.PlayIndexAsync(List.SelectedIndex, forward: true);
    }

    private async void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && List.SelectedIndex >= 0) { e.Handled = true; await _c.PlayIndexAsync(List.SelectedIndex, forward: true); }
        else if (e.Key == Key.Delete) { e.Handled = true; RemoveSelected(); }
        else if (e.Key == Key.System && Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Up or Key.Down)
        {
            e.Handled = true;
            MoveSelectedBy(e.SystemKey == Key.Up ? -1 : 1);
        }
        else if (e.Key == Key.System && Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.D3)
        {
            e.Handled = true; // Alt+3: file info, as on the classic player
            ShowFileInfo();
        }
    }

    // ================= File info =================

    /// <summary>The row the keyboard is on (or the first selected one): the file Alt+3 and the menu act on.</summary>
    private Track? InfoTrack()
    {
        var row = Keyboard.FocusedElement is ListBoxItem { IsSelected: true, DataContext: PlaylistRow focused }
            ? focused
            : List.SelectedItems.Cast<PlaylistRow>().OrderBy(r => r.Number).FirstOrDefault();
        return row is null || row.Number - 1 >= _c.Playlist.Tracks.Count ? null : _c.Playlist.Tracks[row.Number - 1];
    }

    internal void ShowFileInfo()
    {
        if (InfoTrack() is { } track) TagEditorWindow.Open(this, _c, track);
        else _c.ShowNotice("Select a track first, then press Alt+3 for its file info.");
    }

    private void FileInfo_Click(object sender, RoutedEventArgs e) => ShowFileInfo();

    private void RowMenu_Opened(object sender, RoutedEventArgs e)
    {
        bool any = List.SelectedItems.Count > 0;
        RowMenuInfo.IsEnabled = InfoTrack() is { IsStream: false };
        RowMenuRemove.IsEnabled = any;
    }

    // ================= Reordering =================

    private const string RowsFormat = "Fernplay.PlaylistRows";
    private Point? _pressAt;
    private PlaylistRow? _pressRow;
    private bool _deferSelect; // a press on one of several selected rows: keep the selection in case it becomes a drag

    private List<int> SelectedIndexes() => List.SelectedItems.Cast<PlaylistRow>().Select(r => r.Number - 1).Order().ToList();

    /// <summary>Alt+Up / Alt+Down: the selected tracks move one place.</summary>
    private void MoveSelectedBy(int step)
    {
        var sel = SelectedIndexes();
        if (sel.Count == 0) return;
        if (step < 0 && sel[0] == 0 || step > 0 && sel[^1] == _rows.Count - 1) return;
        var moved = _c.Playlist.Move(sel, step < 0 ? sel[0] - 1 : sel[^1] + 2);
        Reselect(moved, scroll: true);
    }

    private void Reselect(IReadOnlyList<int> indexes, bool scroll)
    {
        List.SelectedItems.Clear();
        foreach (var i in indexes) List.SelectedItems.Add(_rows[i]);
        if (indexes.Count == 0) return;
        if (scroll) List.ScrollIntoView(_rows[indexes[0]]);
        List.UpdateLayout();
        (List.ItemContainerGenerator.ContainerFromIndex(indexes[0]) as ListBoxItem)?.Focus();
    }

    private ListBoxItem? ItemAt(object source)
    {
        for (var d = source as DependencyObject; d is not null && d != List; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ListBoxItem item) return item;
            if (d is ScrollBar) return null;
        }
        return null;
    }

    private void List_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemAt(e.OriginalSource);
        _pressRow = item?.DataContext as PlaylistRow;
        _pressAt = item is null ? null : e.GetPosition(List);
        _deferSelect = item is { IsSelected: true } && List.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None && e.ClickCount == 1;
        if (_deferSelect) { e.Handled = true; item!.Focus(); }
    }

    private void List_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferSelect && _pressRow is not null) List.SelectedItem = _pressRow; // it was a click after all
        _deferSelect = false;
        _pressAt = null;
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressAt is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(List) - start;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pressAt = null;
        _deferSelect = false;
        var sel = SelectedIndexes();
        if (sel.Count == 0) return;
        DragDrop.DoDragDrop(List, new DataObject(RowsFormat, sel.ToArray()), DragDropEffects.Move);
        DropLine.Visibility = Visibility.Collapsed;
    }

    /// <summary>Index the dragged tracks would be inserted before, and the y of the line that shows it.</summary>
    private (int Index, double? LineY) DropTarget(DragEventArgs e)
    {
        var item = ItemAt(List.InputHitTest(e.GetPosition(List)) ?? List);
        if (item is not null)
        {
            int index = List.ItemContainerGenerator.IndexFromContainer(item);
            bool after = e.GetPosition(item).Y > item.ActualHeight / 2;
            double y = item.TranslatePoint(new Point(0, after ? item.ActualHeight : 0), ListHost).Y;
            return (after ? index + 1 : index, y);
        }
        // Below the last row: the end of the list.
        var last = _rows.Count > 0 ? List.ItemContainerGenerator.ContainerFromIndex(_rows.Count - 1) as ListBoxItem : null;
        return (_rows.Count, last?.TranslatePoint(new Point(0, last.ActualHeight), ListHost).Y);
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(RowsFormat)) return; // files: the window adds them at the end
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        var (_, y) = DropTarget(e);
        if (y is { } lineY && lineY >= 0 && lineY <= ListHost.ActualHeight)
        {
            DropLine.Margin = new Thickness(0, Math.Max(0, lineY - 0.5), 0, 0);
            DropLine.Visibility = Visibility.Visible;
        }
        else DropLine.Visibility = Visibility.Collapsed;

        // Scroll while hovering near the top or bottom edge.
        double py = e.GetPosition(List).Y;
        if (FindScroller() is { } sv)
        {
            if (py < 8) sv.LineUp();
            else if (py > List.ActualHeight - 8) sv.LineDown();
        }
    }

    private void List_DragLeave(object sender, DragEventArgs e) => DropLine.Visibility = Visibility.Collapsed;

    private void List_Drop(object sender, DragEventArgs e)
    {
        DropLine.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(RowsFormat) is not int[] indexes) return;
        e.Handled = true;
        var (target, _) = DropTarget(e);
        var sv = FindScroller();
        double offset = sv?.VerticalOffset ?? 0;
        var moved = _c.Playlist.Move(indexes, target);
        sv?.ScrollToVerticalOffset(offset); // stay where the drop happened
        Reselect(moved, scroll: false);
    }

    private ScrollViewer? FindScroller() => List.Template?.FindName("Scroller", List) as ScrollViewer;

    // ================= Height =================

    private const double ListInset = 1; // the list well's top border (Inset style), skin px
    private const double FallbackRowHeight = 9; // until a row exists to measure
    private double _dragStartY, _dragStartHeight;
    private double _listTotal = PlaylistHeight.Default;
    private bool _rowsMeasured;
    // While dragging: list heights that line the panel's bottom up with another panel's bottom, and those bottoms (DIP).
    private List<(double Height, double BottomDip)> _edges = [];

    /// <summary>
    /// Height of the list area in skin pixels (remembered in the settings): whole rows for the list, plus any spare
    /// part (when the bottom was snapped to another panel) as space under the list, so no half row is cut off.
    /// </summary>
    public double ListHeight
    {
        get => _listTotal;
        set
        {
            _listTotal = double.IsFinite(value) ? Math.Max(value, PlaylistHeight.MinRows * FallbackRowHeight * 0.8 + ListInset) : PlaylistHeight.Default;
            ApplyListHeight();
        }
    }

    private void ApplyListHeight()
    {
        var (list, spare) = PlaylistHeight.Split(_listTotal, RowHeight(), ListInset);
        ListBorder.Height = list;
        var m = ListBorder.Margin;
        if (Math.Abs(m.Bottom - spare) > 0.001) ListBorder.Margin = new Thickness(m.Left, m.Top, m.Right, spare);
    }

    /// <summary>At start-up no row exists to measure: split the saved height again once the first rows are there.</summary>
    private void List_ContainersGenerated(object? sender, EventArgs e)
    {
        if (_rowsMeasured || List.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_rowsMeasured || List.ItemContainerGenerator.ContainerFromIndex(0) is not ListBoxItem { ActualHeight: > 0 }) return;
            _rowsMeasured = true;
            ApplyListHeight();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private double RowHeight() =>
        List.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem { ActualHeight: > 0 } row ? row.ActualHeight : FallbackRowHeight;

    /// <summary>Tallest list that keeps the panel on the desktop (never less than it is now; no limit before it is shown).</summary>
    private double MaxListHeight()
    {
        if (!IsLoaded) return double.MaxValue;
        double spareDip = Desktop.Bottom - (Top + ActualHeight);
        return _listTotal + Math.Max(0, spareDip) / SkinWindow.Scale;
    }

    private void SizeGrip_DragStarted(object sender, DragStartedEventArgs e)
    {
        // Window coordinates: the panel's top left stays put while its size changes.
        var at = Mouse.GetPosition(this);
        _dragStartY = at.Y;
        _cornerStartX = at.X;
        _dragStartHeight = _listTotal;
        _dragStartWidth = FernplayView.Width;
        double bottom = Top + ActualHeight;
        _edges = !SnapEnabled ? [] : OtherVisiblePanels()
            .Select(p => p.Top + p.ActualHeight)
            .Select(b => (_dragStartHeight + (b - bottom) / SkinWindow.Scale, b))
            .ToList();
    }

    private void SizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double raw = _dragStartHeight + (Mouse.GetPosition(this).Y - _dragStartY) / SkinWindow.Scale;
        double snapped = PlaylistHeight.Snap(raw, RowHeight(), ListInset, MaxListHeight(), _edges.Select(x => x.Height));
        if (Math.Abs(snapped - _listTotal) > 0.001) ListHeight = snapped;
    }

    /// <summary>After a snap to another panel's bottom, take up the rounding to whole screen pixels so the edges line up exactly.</summary>
    private void AlignToSnappedEdge()
    {
        if (_edges.FirstOrDefault(x => Math.Abs(x.Height - _listTotal) < 0.001) is not { BottomDip: > 0 } edge) return;
        UpdateLayout();
        double diff = edge.BottomDip - (Top + ActualHeight);
        if (Math.Abs(diff) > 0.01) ListHeight = _listTotal + diff / SkinWindow.Scale;
    }

    private void CornerGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        SizeGrip_DragDelta(sender, e);
        double raw = _dragStartWidth + (Mouse.GetPosition(this).X - _cornerStartX) / SkinWindow.Scale;
        double max = _dragStartWidth + Math.Max(0, Desktop.Right - (Left + _dragStartWidth * SkinWindow.Scale)) / SkinWindow.Scale;
        double width = Math.Round(Math.Clamp(raw, PlaylistWidth.Default, Math.Max(PlaylistWidth.Default, max)));
        if (Math.Abs(width - FernplayView.Width) > 0.01) FernplayView.Width = width;
    }

    private double _cornerStartX, _dragStartWidth;

    /// <summary>Width of the playlist in Fernplay's look, in skin pixels (remembered in the settings; 280 at least).</summary>
    public double ListWidth
    {
        get => FernplayView.Width;
        set => FernplayView.Width = double.IsFinite(value) ? Math.Max(value, PlaylistWidth.Default) : PlaylistWidth.Default;
    }

    private void SizeGrip_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        AlignToSnappedEdge();
        _edges = [];
        ScrollIntoViewCurrent();
    }

    private void ScrollIntoViewCurrent()
    {
        if (_shownCurrent >= 0 && _shownCurrent < _rows.Count) List.ScrollIntoView(_rows[_shownCurrent]);
    }

    // ================= Playlist files =================

    private void List_Click(object sender, RoutedEventArgs e)
    {
        ListMenu.PlacementTarget = ListButton;
        ListMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        ListMenu.IsOpen = true;
    }

    private void OpenList_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Open a playlist", Filter = "Playlists|*.m3u;*.m3u8;*.pls|All files|*.*" };
        if (dialog.ShowDialog(this) == true) _ = _c.OpenAsync([dialog.FileName], replace: true);
    }

    private void SaveList_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) { _c.ShowNotice("The playlist is empty: nothing to save."); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the playlist",
            Filter = "M3U8 playlist (UTF-8)|*.m3u8|M3U playlist|*.m3u",
            DefaultExt = ".m3u8",
            FileName = "Playlist",
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        var error = _c.SavePlaylist(dialog.FileName);
        if (error is not null) _c.SetStatus(error);
        else _c.ShowNotice($"Saved {_rows.Count} tracks to {System.IO.Path.GetFileName(dialog.FileName)}.");
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        AddMenu.PlacementTarget = AddButton;
        AddMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        AddMenu.IsOpen = true;
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add music files",
            Multiselect = true,
            Filter = PlayerController.OpenFilter,
        };
        if (dialog.ShowDialog(this) == true) _ = _c.OpenAsync(dialog.FileNames, replace: false);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Add a music folder" };
        if (dialog.ShowDialog(this) == true) _ = _c.OpenAsync([dialog.FolderName], replace: false);
    }

    private void Remove_Click(object sender, RoutedEventArgs e) => RemoveSelected();

    private void RemoveSelected()
    {
        var indexes = List.SelectedItems.Cast<PlaylistRow>().Select(r => r.Number - 1).ToList();
        if (indexes.Count == 0) return;
        int first = indexes.Min();
        // The rows are rebuilt after a removal: keep the scroll position, and put the selection and keyboard focus on
        // the row that took the removed one's place, so Delete can be pressed again and arrows go on from there.
        var sv = FindScroller();
        double offset = sv?.VerticalOffset ?? 0;
        _c.Playlist.RemoveAt(indexes);
        if (_rows.Count == 0) return;
        sv?.ScrollToVerticalOffset(Math.Min(offset, Math.Max(0, _rows.Count - 1)));
        Reselect([Math.Min(first, _rows.Count - 1)], scroll: false);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _c.ClearList();

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = _c.OpenAsync(paths, replace: false);
    }
}
