using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Fernplay.Core;
using Track = Fernplay.Core.Track;

namespace Fernplay.App;

/// <summary>One line in the playlist: "12. Artist - Title" and the length.</summary>
public sealed class PlaylistRow(int number, Track track) : INotifyPropertyChanged
{
    private Track _track = track;
    private bool _current;

    public int Number { get; set; } = number;
    public string Text => $"{Number}. {_track.DisplayName}";
    public string Length => _track.Duration is { } d ? TimeFormat.Clock(d) : "";
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
        _c.Playlist.Changed += (_, _) => Rebuild();
        _c.Playlist.ItemUpdated += (_, i) => { if (i < _rows.Count) _rows[i].Refresh(_c.Playlist.Tracks[i]); UpdateTotal(); };
        _c.Playlist.CurrentChanged += (_, _) => MarkCurrent();
        Rebuild();
    }

    private void Rebuild()
    {
        _rows.Clear();
        for (int i = 0; i < _c.Playlist.Tracks.Count; i++) _rows.Add(new PlaylistRow(i + 1, _c.Playlist.Tracks[i]));
        _shownCurrent = -1;
        MarkCurrent();
        UpdateTotal();
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MarkCurrent()
    {
        if (_shownCurrent >= 0 && _shownCurrent < _rows.Count) _rows[_shownCurrent].IsCurrent = false;
        _shownCurrent = _c.Playlist.CurrentIndex;
        if (_shownCurrent >= 0 && _shownCurrent < _rows.Count)
        {
            _rows[_shownCurrent].IsCurrent = true;
            List.ScrollIntoView(_rows[_shownCurrent]);
        }
    }

    private void UpdateTotal()
    {
        int n = _rows.Count;
        TotalText.Text = n == 0 ? "0 tracks" : $"{n} track{(n == 1 ? "" : "s")}  {TimeFormat.Clock(_c.Playlist.TotalDuration)}";
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
            Filter = "Music files|*.mp3;*.flac;*.wav;*.wma;*.m4a;*.aac|All files|*.*",
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
        int first = indexes.Count > 0 ? indexes.Min() : -1;
        _c.Playlist.RemoveAt(indexes);
        // Keep the selection where it was, so Delete can be pressed again.
        if (first >= 0 && _rows.Count > 0) List.SelectedIndex = Math.Min(first, _rows.Count - 1);
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
