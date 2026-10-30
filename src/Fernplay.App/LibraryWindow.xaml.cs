using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fernplay.Core;
using Fernplay.Core.Library;

namespace Fernplay.App;

/// <summary>One line in the navigation list: a heading, a view, a playlist file or the history.</summary>
public sealed class NavEntry
{
    public enum Kinds { Heading, View, Playlist, History }

    public required string Text { get; init; }
    public Kinds Kind { get; init; }
    public LibraryView View { get; init; }
    public string? PlaylistPath { get; init; }
    public bool IsHeading => Kind == Kinds.Heading;
    public Thickness Indent => Kind is Kinds.View or Kinds.Playlist ? new Thickness(6, 0, 0, 0) : new Thickness(0);
    /// <summary>Same entry across rebuilds of the list (to keep the selection).</summary>
    public string Key => $"{Kind}|{View}|{PlaylistPath}";
}

/// <summary>A row of the artist pane; Key null = the "All" row.</summary>
public sealed record ArtistEntry(string Name, int Albums, string? Key)
{
    public string AccessibleName => Key is null ? Name : $"{Name}, {Albums} album{(Albums == 1 ? "" : "s")}";
}

/// <summary>A row of the album pane; Key null = the "All" row.</summary>
public sealed record AlbumEntry(string Album, string Artist, int Tracks, int? Year, string? Genre, string? Key)
{
    public string AccessibleName => Key is null ? Album : $"{Album} by {Artist}, {Tracks} track{(Tracks == 1 ? "" : "s")}{(Year is { } y ? $", {y}" : "")}";
}

/// <summary>A row of the track list. In History one track can appear several times (once per play).</summary>
public sealed class LibraryRow(LibraryTrack track, DateTime? playedAt = null)
{
    public LibraryTrack Track { get; } = track;
    public string Path => Track.Path;
    public string Artist => Track.ArtistName;
    public string Album => Track.AlbumName;
    public string Number => Track.TrackNumber?.ToString() ?? "";
    public string Title => Track.TitleText;
    public string Length => Track.Duration is { } d ? TimeFormat.Clock(d) : "";
    public string Genre => Track.Genre ?? "";
    public string Year => Track.Year?.ToString() ?? "";
    public string Rating => new('★', Math.Clamp(Track.Rating, 0, 5));
    public string Plays => Track.PlayCount > 0 ? Track.PlayCount.ToString() : "";
    /// <summary>History: when this play happened; elsewhere the track's last play.</summary>
    public DateTime? Played => playedAt ?? Track.LastPlayed;
    public string LastPlayed => Played is { } p ? p.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
    public string AccessibleName => $"{Title} by {Artist}, {Album}{(Length == "" ? "" : ", " + Length)}";
}

/// <summary>
/// A grid row with narrow cell margins: WPF's own gives every cell 6 px on each side, which at the panel's scale
/// eats most of a short column ("3:13" showed as "3:1").
/// </summary>
public sealed class TightRowPresenter : GridViewRowPresenter
{
    private static readonly Thickness CellMargin = new(1.5, 0, 1.5, 0);

    protected override Size MeasureOverride(Size constraint)
    {
        Tighten(); // cells already made
        var size = base.MeasureOverride(constraint);
        Tighten(); // cells made during this pass: measured again with the new margin
        return size;
    }

    private void Tighten()
    {
        for (int i = 0; i < VisualChildrenCount; i++)
            if (GetVisualChild(i) is FrameworkElement cell && cell.Margin != CellMargin) cell.Margin = CellMargin;
    }
}

public partial class LibraryWindow : SkinWindow
{
    private readonly PlayerController _c;
    private readonly LibraryService _lib;
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _refreshDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _scanNotice = new() { Interval = TimeSpan.FromSeconds(6) };

    private NavEntry? _nav;
    private string? _artistKey, _albumKey; // null = All
    private List<LibraryRow> _searched = []; // the view's rows after the search
    private List<LibraryRow> _shown = [];
    private string? _status; // last query status line
    private bool _filling; // true while panes are refilled, so their selection events are not the user's
    private bool _dirty = true; // the index changed while the window was hidden

    // Sorting of the track list: column key and direction; null = the view's own order.
    private string? _sortKey;
    private bool _sortDescending;
    private readonly Dictionary<GridViewColumn, (string Key, string Header)> _columns = [];

    public LibraryWindow(PlayerController controller, LibraryService library)
    {
        _c = controller;
        _lib = library;
        InitializeComponent();
        Background = Root.BorderBrush; // the strip below the panel (see LibrarySize) reads as its bottom edge
        AttachDragHandle(TitleBar);
        BuildColumns();
        TrackList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(TrackHeader_Click));
        TrackList.PreviewMouseLeftButtonUp += Track_PreviewMouseUp;

        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); Requery(); };
        _refreshDelay.Tick += (_, _) => { _refreshDelay.Stop(); Refresh(); };
        _scanNotice.Tick += (_, _) => { _scanNotice.Stop(); ShowStatus(); };
        _lib.Index.Changed += (_, _) =>
        {
            if (IsVisible) { _refreshDelay.Stop(); _refreshDelay.Start(); }
            else _dirty = true;
        };
        _lib.ScanStatusChanged += (_, _) =>
        {
            if (!_lib.IsScanning && _lib.LastScan is not null) { _scanNotice.Stop(); _scanNotice.Start(); }
            ShowStatus();
        };
        IsVisibleChanged += (_, _) => { if (IsVisible && _dirty) Refresh(); };
        PreviewKeyDown += Window_PreviewKeyDown;

        BuildNav();
        Nav.SelectedIndex = 1; // Audio
    }

    // ================= Size =================

    public const double DefaultWidth = 460, DefaultHeight = 270, MinSkinWidth = 300, MinSkinHeight = 170;

    /// <summary>Size of the library in skin pixels (remembered in the settings); 0 = default.</summary>
    public Size LibrarySize
    {
        get => new(Root.Width, Root.Height + Root.Margin.Bottom / Scale);
        set
        {
            Root.Width = value.Width > 0 ? Math.Max(value.Width, MinSkinWidth) : DefaultWidth;
            double h = value.Height > 0 ? Math.Max(value.Height, MinSkinHeight) : DefaultHeight;
            // Layout rounding keeps the panel at whole skin pixels, which are 1.7 screen pixels apart; the screen pixels
            // left over go below it (in the border colour), so the window can end level with the playlist's bottom.
            Root.Height = Math.Floor(h);
            Root.Margin = new Thickness(0, 0, 0, Math.Max(0, Math.Round(h * Scale) - Math.Round(Root.Height * Scale)));
        }
    }

    private Point _gripStart;
    private Size _gripStartSize;

    private void Grip_DragStarted(object sender, DragStartedEventArgs e)
    {
        _gripStart = Mouse.GetPosition(this); // window coordinates: the top left stays put
        _gripStartSize = LibrarySize;
    }

    private void Grip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var p = Mouse.GetPosition(this);
        var wa = SystemParameters.WorkArea;
        double maxW = Math.Max(MinSkinWidth, (wa.Right - Left) / Scale), maxH = Math.Max(MinSkinHeight, (wa.Bottom - Top) / Scale);
        double w = Math.Clamp(_gripStartSize.Width + (p.X - _gripStart.X) / Scale, MinSkinWidth, maxW);
        double h = Math.Clamp(_gripStartSize.Height + (p.Y - _gripStart.Y) / Scale, MinSkinHeight, maxH);
        LibrarySize = new Size(Math.Round(w), Math.Round(h));
    }

    // ================= Navigation =================

    private void BuildNav()
    {
        string? keep = _nav?.Key;
        var items = new List<NavEntry>
        {
            new() { Text = "LOCAL LIBRARY", Kind = NavEntry.Kinds.Heading },
            new() { Text = "Audio", Kind = NavEntry.Kinds.View, View = LibraryView.Audio },
            new() { Text = "Most Played", Kind = NavEntry.Kinds.View, View = LibraryView.MostPlayed },
            new() { Text = "Recently Added", Kind = NavEntry.Kinds.View, View = LibraryView.RecentlyAdded },
            new() { Text = "Recently Played", Kind = NavEntry.Kinds.View, View = LibraryView.RecentlyPlayed },
            new() { Text = "Never Played", Kind = NavEntry.Kinds.View, View = LibraryView.NeverPlayed },
            new() { Text = "Top Rated", Kind = NavEntry.Kinds.View, View = LibraryView.TopRated },
            new() { Text = "PLAYLISTS", Kind = NavEntry.Kinds.Heading },
        };
        items.AddRange(_lib.Index.Playlists.Select(p => new NavEntry
        {
            Text = Path.GetFileNameWithoutExtension(p), Kind = NavEntry.Kinds.Playlist, PlaylistPath = p,
        }));
        items.Add(new NavEntry { Text = "HISTORY", Kind = NavEntry.Kinds.History });

        _filling = true;
        Nav.ItemsSource = items;
        var again = items.FirstOrDefault(i => i.Key == keep);
        if (again is not null) Nav.SelectedItem = again;
        _filling = false;
        if (keep is not null && again is null) Nav.SelectedIndex = 1; // that playlist is gone: back to Audio
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        if (Nav.SelectedItem is not NavEntry { IsHeading: false } entry)
        {
            // A heading cannot be chosen: stay on the previous view.
            _filling = true;
            Nav.SelectedItem = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null;
            _filling = false;
            return;
        }
        _nav = entry;
        _artistKey = _albumKey = null;
        _sortKey = null;
        ShowSortMarks();
        Requery();
    }

    /// <summary>The rows of the chosen view, in its natural order.</summary>
    private List<LibraryRow> ViewRows()
    {
        var index = _lib.Index;
        switch (_nav?.Kind)
        {
            case NavEntry.Kinds.History:
                return [.. index.History.Select(h => index.Find(h.Path) is { } t ? new LibraryRow(t, h.PlayedAt) : null).OfType<LibraryRow>()];
            case NavEntry.Kinds.Playlist when _nav.PlaylistPath is { } file:
                try
                {
                    // Entries in the library show its facts; others (outside the folders) only their file name.
                    return [.. Playlist.ReadPlaylistFile(file).Where(e => !e.IsStream)
                        .Select(e => index.Find(e.Path) ?? (File.Exists(e.Path) ? new LibraryTrack { Path = e.Path, Title = e.Title } : null))
                        .OfType<LibraryTrack>().Select(t => new LibraryRow(t))];
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
            default:
                return [.. LibraryQuery.View(index.All, _nav?.View ?? LibraryView.Audio, DateTime.UtcNow).Select(t => new LibraryRow(t))];
        }
    }

    // ================= Querying =================

    /// <summary>After the index changed: query again, keeping the selection and the scroll position.</summary>
    private void Refresh()
    {
        _dirty = false;
        BuildNav();
        var selected = TrackList.SelectedItems.Cast<LibraryRow>().Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        double offset = TrackScroller()?.VerticalOffset ?? 0;
        Requery();
        if (selected.Count > 0)
        {
            foreach (var row in _shown.Where(r => selected.Contains(r.Path))) TrackList.SelectedItems.Add(row);
        }
        TrackList.UpdateLayout();
        TrackScroller()?.ScrollToVerticalOffset(offset);
    }

    /// <summary>View → search → artist pane → album pane → tracks.</summary>
    private void Requery()
    {
        var clock = Stopwatch.StartNew();
        var rows = ViewRows();
        var hits = LibraryQuery.Search(rows.Select(r => r.Track).Distinct(), SearchBox.Text).ToHashSet();
        _searched = [.. rows.Where(r => hits.Contains(r.Track))];
        FillArtists();
        FillAlbums();
        FillTracks(clock);
    }

    private IEnumerable<LibraryTrack> SearchedTracks => _searched.Select(r => r.Track).Distinct();

    private void FillArtists()
    {
        var artists = LibraryQuery.Artists(SearchedTracks);
        int albums = SearchedTracks.Select(t => t.AlbumName).Distinct(StringComparer.CurrentCultureIgnoreCase).Count();
        var items = new List<ArtistEntry> { new(LibraryQuery.AllArtistsLabel(artists.Count), albums, null) };
        items.AddRange(artists.Select(a => new ArtistEntry(a.Name, a.Albums, a.Name)));
        if (_artistKey is not null && !artists.Any(a => Same(a.Name, _artistKey))) _artistKey = null;
        _filling = true;
        ArtistList.ItemsSource = items;
        ArtistList.SelectedItem = items.First(i => Same(i.Key, _artistKey));
        _filling = false;
    }

    private void FillAlbums()
    {
        var albums = LibraryQuery.Albums(SearchedTracks, _artistKey);
        var items = new List<AlbumEntry> { new($"All ({albums.Count} album{(albums.Count == 1 ? "" : "s")})", "", albums.Sum(a => a.Tracks), null, null, null) };
        items.AddRange(albums.Select(a => new AlbumEntry(a.Album, a.Artist, a.Tracks, a.Year, a.Genre, a.Album)));
        if (_albumKey is not null && !albums.Any(a => Same(a.Album, _albumKey))) _albumKey = null;
        _filling = true;
        AlbumList.ItemsSource = items;
        AlbumList.SelectedItem = items.First(i => Same(i.Key, _albumKey));
        _filling = false;
    }

    private void FillTracks(Stopwatch? clock = null)
    {
        clock ??= Stopwatch.StartNew();
        IEnumerable<LibraryRow> rows = _searched;
        if (_artistKey is { } artist) rows = rows.Where(r => Same(r.Artist, artist));
        if (_albumKey is { } album) rows = rows.Where(r => Same(r.Album, album));
        _shown = Sort([.. rows]);
        TrackList.ItemsSource = _shown;
        _status = LibraryQuery.Status(_shown.Select(r => r.Track).ToList(), clock.Elapsed);
        EmptyText.Text = _shown.Count > 0 ? "" : EmptyMessage();
        EmptyText.Visibility = _shown.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        ShowStatus();
    }

    private string EmptyMessage()
    {
        if (_lib.Index.Folders.Count == 0) return "No music folders yet. Click FOLDERS below and add the folder that holds your music.";
        if (_lib.Index.Count == 0) return _lib.IsScanning ? "Scanning your folders…" : "No music found in your folders yet. FOLDERS ▸ Rescan looks again.";
        if (SearchBox.Text.Trim().Length > 0) return "Nothing matches the search.";
        return _nav?.Kind switch
        {
            NavEntry.Kinds.History => "Nothing played yet. A play counts after half the track (or 4 minutes).",
            NavEntry.Kinds.Playlist => "None of this playlist's files were found.",
            _ => _nav?.View switch
            {
                LibraryView.MostPlayed or LibraryView.RecentlyPlayed => "Nothing played yet. A play counts after half the track (or 4 minutes).",
                LibraryView.RecentlyAdded => "Nothing added in the last two weeks.",
                LibraryView.NeverPlayed => "Every track has been played.",
                LibraryView.TopRated => "No tracks rated three stars or more.",
                _ => "No tracks.",
            },
        };
    }

    private void ShowStatus()
    {
        StatusText.Text = _lib.ScanStatus ?? (_scanNotice.IsEnabled ? _lib.LastScan : null) ?? _status ?? "";
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.CurrentCultureIgnoreCase);

    // ================= Search =================

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        _searchDelay.Stop();
        Requery();
        SearchBox.Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocused && SearchBox.Text.Length > 0)
        {
            e.Handled = true;
            ClearSearch_Click(this, e);
        }
        else if (e.Key == Key.Down && SearchBox.IsKeyboardFocused && _shown.Count > 0)
        {
            // From the search box down into the results.
            e.Handled = true;
            TrackList.SelectedIndex = 0;
            TrackList.UpdateLayout();
            (TrackList.ItemContainerGenerator.ContainerFromIndex(0) as ListViewItem)?.Focus();
        }
    }

    // ================= Artist and album panes =================

    private void Artist_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || ArtistList.SelectedItem is not ArtistEntry a) return;
        _artistKey = a.Key;
        _albumKey = null;
        FillAlbums();
        FillTracks();
    }

    private void Album_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || AlbumList.SelectedItem is not AlbumEntry a) return;
        _albumKey = a.Key;
        FillTracks();
    }

    // Double-click on an artist or album plays what it shows.
    private void Artist_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt(ArtistList, e.OriginalSource) is not null) PlayRows(_shown, replace: true);
    }

    private void Album_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt(AlbumList, e.OriginalSource) is not null) PlayRows(_shown, replace: true);
    }

    // ================= Track list =================

    private void BuildColumns()
    {
        (string Key, string Header, double Width)[] columns =
        [
            ("Artist", "ARTIST", 50), ("Album", "ALBUM", 50), ("Number", "#", 12), ("Title", "TITLE", 60), ("Length", "TIME", 22),
            ("Genre", "GENRE", 32), ("Year", "YEAR", 22), ("Rating", "RATING", 28), ("Plays", "PLAYS", 24), ("LastPlayed", "LAST PLAYED", 52),
        ];
        foreach (var (key, header, width) in columns)
        {
            var column = new GridViewColumn { Header = header, Width = width, DisplayMemberBinding = new Binding(key) };
            _columns[column] = (key, header);
            TrackColumns.Columns.Add(column);
        }
        StretchColumn(TrackList, TrackColumns.Columns[3]);
        StretchColumn(ArtistList, ((GridView)ArtistList.View).Columns[0]);
        StretchColumn(AlbumList, ((GridView)AlbumList.View).Columns[0]);
    }

    /// <summary>One column (title, artist, album) takes the width the others leave, as the window is resized.</summary>
    private static void StretchColumn(ListView list, GridViewColumn stretch, double min = 40)
    {
        void Fit()
        {
            var view = (GridView)list.View;
            var scroller = list.Template?.FindName("Scroller", list) as ScrollViewer;
            double room = scroller is { ViewportWidth: > 0 } s ? s.ViewportWidth : list.ActualWidth;
            if (room <= 0) return;
            double others = view.Columns.Where(c => c != stretch).Sum(c => c.ActualWidth > 0 ? c.ActualWidth : c.Width);
            double width = Math.Max(min, Math.Floor(room - others - 1));
            if (Math.Abs(stretch.Width - width) >= 1) stretch.Width = width;
        }
        list.SizeChanged += (_, _) => Fit();
        list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) => { if (e.ViewportWidthChange != 0) Fit(); }));
        list.Loaded += (_, _) => Fit();
    }

    /// <summary>A click on a column header sorts by it; a second click reverses.</summary>
    private void TrackHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column } || !_columns.TryGetValue(column, out var c)) return;
        if (_sortKey == c.Key) _sortDescending = !_sortDescending;
        else { _sortKey = c.Key; _sortDescending = false; }
        ShowSortMarks();
        FillTracks();
    }

    private void ShowSortMarks()
    {
        foreach (var (column, (key, header)) in _columns)
            column.Header = key == _sortKey ? header + (_sortDescending ? " ▼" : " ▲") : header;
    }

    private List<LibraryRow> Sort(List<LibraryRow> rows)
    {
        if (_sortKey is null) return rows;
        var text = StringComparer.CurrentCultureIgnoreCase;
        // Empty values last in both directions; ties keep the view's order (OrderBy is stable).
        IOrderedEnumerable<LibraryRow> By<T>(Func<LibraryRow, T?> key, IComparer<T>? cmp = null) where T : class
        {
            var withValue = rows.Where(r => key(r) is not null);
            var sorted = _sortDescending ? withValue.OrderByDescending(r => key(r)!, cmp) : withValue.OrderBy(r => key(r)!, cmp);
            return sorted;
        }
        IEnumerable<LibraryRow> ByValue<T>(Func<LibraryRow, T?> key) where T : struct
        {
            var withValue = rows.Where(r => key(r).HasValue);
            var sorted = _sortDescending ? withValue.OrderByDescending(r => key(r)!.Value) : withValue.OrderBy(r => key(r)!.Value);
            return sorted.Concat(rows.Where(r => !key(r).HasValue));
        }
        IEnumerable<LibraryRow> ByText(Func<LibraryRow, string> key)
        {
            var sorted = By<string>(r => key(r) is { Length: > 0 } s ? s : null, text);
            return sorted.Concat(rows.Where(r => key(r).Length == 0));
        }
        IEnumerable<LibraryRow> result = _sortKey switch
        {
            "Artist" => ByText(r => r.Artist),
            "Album" => ByText(r => r.Album),
            "Number" => ByValue(r => r.Track.TrackNumber),
            "Title" => ByText(r => r.Title),
            "Length" => ByValue(r => r.Track.DurationMs),
            "Genre" => ByText(r => r.Genre),
            "Year" => ByValue(r => r.Track.Year),
            "Rating" => ByValue(r => r.Track.Rating > 0 ? r.Track.Rating : (int?)null),
            "Plays" => ByValue(r => r.Track.PlayCount > 0 ? r.Track.PlayCount : (int?)null),
            "LastPlayed" => ByValue(r => r.Played),
            _ => rows,
        };
        return [.. result];
    }

    /// <summary>The selected rows in the order shown; all shown rows when none is selected.</summary>
    private List<LibraryRow> SelectedOrAll()
    {
        var selected = TrackList.SelectedItems.Cast<LibraryRow>().ToHashSet();
        return selected.Count == 0 ? _shown : [.. _shown.Where(selected.Contains)];
    }

    private void PlayRows(IReadOnlyList<LibraryRow> rows, bool replace)
    {
        if (rows.Count == 0) return;
        var paths = rows.Select(r => r.Path).ToList();
        _ = _c.OpenAsync(paths, replace);
        if (!replace) _c.ShowNotice($"Added {paths.Count:N0} track{(paths.Count == 1 ? "" : "s")} to the playlist.");
    }

    private void Play_Click(object sender, RoutedEventArgs e) => PlayRows(SelectedOrAll(), replace: true);
    private void Enqueue_Click(object sender, RoutedEventArgs e) => PlayRows(SelectedOrAll(), replace: false);

    /// <summary>Double-click plays the selected tracks (the clicked one, or several when they were selected first).</summary>
    private void Track_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemAt(TrackList, e.OriginalSource) is not null) PlayRows(SelectedOrAll(), replace: true);
    }

    private void Track_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && TrackList.SelectedItems.Count > 0)
        {
            e.Handled = true;
            PlayRows(SelectedOrAll(), replace: Keyboard.Modifiers != ModifierKeys.Shift); // Shift+Enter enqueues
        }
    }

    // Dragging tracks out (to the playlist or the player) as files, as Explorer does.
    private Point? _pressAt;
    private LibraryRow? _pressRow;
    private bool _deferSelect; // a press on one of several selected rows: keep the selection in case it becomes a drag

    private void Track_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemAt(TrackList, e.OriginalSource);
        _pressRow = item?.DataContext as LibraryRow;
        _pressAt = item is null ? null : e.GetPosition(TrackList);
        _deferSelect = item is { IsSelected: true } && TrackList.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None && e.ClickCount == 1;
        if (_deferSelect) { e.Handled = true; item!.Focus(); }
    }

    private void Track_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferSelect && _pressRow is not null) TrackList.SelectedItem = _pressRow; // it was a click after all
        _deferSelect = false;
        _pressAt = null;
    }

    private void Track_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressAt is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(TrackList) - start;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pressAt = null;
        _deferSelect = false;
        var paths = TrackList.SelectedItems.Cast<LibraryRow>().Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) return;
        // Keep the shown order, not the order the rows were clicked in.
        var order = _shown.Select((r, i) => (r.Path, i)).GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
        paths = [.. paths.OrderBy(p => order.GetValueOrDefault(p))];
        DragDrop.DoDragDrop(TrackList, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy);
    }

    private static ListViewItem? ItemAt(ListView list, object source)
    {
        for (var d = source as DependencyObject; d is not null && d != list; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ListViewItem item) return item;
            if (d is ScrollBar or GridViewColumnHeader) return null;
        }
        return null;
    }

    private ScrollViewer? TrackScroller() => TrackList.Template?.FindName("Scroller", TrackList) as ScrollViewer;

    // ================= Folders =================

    private void Folders_Click(object sender, RoutedEventArgs e)
    {
        FoldersMenu.Items.Clear();
        var add = new MenuItem { Header = "Add folder…" };
        add.Click += AddFolder_Click;
        FoldersMenu.Items.Add(add);
        var rescan = new MenuItem { Header = "Rescan", IsEnabled = _lib.Index.Folders.Count > 0 && !_lib.IsScanning };
        rescan.Click += (_, _) => _ = _lib.ScanAsync();
        FoldersMenu.Items.Add(rescan);
        var stop = new MenuItem { Header = "Stop scan", IsEnabled = _lib.IsScanning };
        stop.Click += (_, _) => _lib.StopScan();
        FoldersMenu.Items.Add(stop);
        if (_lib.Index.Folders.Count > 0)
        {
            FoldersMenu.Items.Add(new Separator());
            foreach (var folder in _lib.Index.Folders)
            {
                var remove = new MenuItem { Header = $"Remove {FolderName(folder)}", ToolTip = folder };
                System.Windows.Automation.AutomationProperties.SetName(remove, $"Remove the folder {folder} from the library");
                remove.Click += (_, _) => RemoveFolder(folder);
                FoldersMenu.Items.Add(remove);
            }
        }
        FoldersMenu.PlacementTarget = FoldersButton;
        FoldersMenu.Placement = PlacementMode.Top;
        FoldersMenu.IsOpen = true;
    }

    private static string FolderName(string folder) => Path.GetFileName(folder) is { Length: > 0 } name ? name : folder;

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Add a music folder to the library" };
        if (dialog.ShowDialog(this) != true) return;
        if (!_lib.Index.AddFolder(dialog.FolderName)) { _c.ShowNotice($"{FolderName(dialog.FolderName)} is already in the library."); return; }
        _ = _lib.ScanAsync();
    }

    private void RemoveFolder(string folder)
    {
        int tracks = _lib.Index.All.Count(t => LibraryIndex.IsUnder(t.Path, folder));
        var answer = MessageBox.Show(this,
            $"Remove {folder} from the library?\n\nIts {tracks:N0} tracks leave the library with their play counts and ratings. The files stay on disk.",
            "Fernplay library", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;
        _lib.Index.RemoveFolder(folder);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
}
