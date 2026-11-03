using System.Diagnostics;
using System.IO;
using System.Windows;
using Fernplay.Core;

namespace Fernplay.App;

/// <summary>
/// File info (Alt+3, as on the classic player): edit one file's tags. Opened from the playlist, the library or the player
/// (the playing track). Saving writes only the fields that changed.
/// </summary>
public partial class TagEditorWindow : SkinDialog
{
    private static TagEditorWindow? _open;
    private readonly PlayerController _c;
    private readonly string _path;
    private readonly TagFields _before;

    private TagEditorWindow(PlayerController controller, string path, TagFields fields, Track? track)
    {
        _c = controller;
        _path = path;
        _before = fields;
        InitializeComponent();
        AttachDragHandle(TitleBar);
        FileNameText.Text = Path.GetFileName(path);
        FolderText.Text = Path.GetDirectoryName(path);
        FormatText.Text = FormatLine(path, track);
        (TitleBox.Text, ArtistBox.Text, AlbumBox.Text, AlbumArtistBox.Text) = (fields.Title, fields.Artist, fields.Album, fields.AlbumArtist);
        (TrackBox.Text, YearBox.Text, GenreBox.Text, ComposerBox.Text, CommentBox.Text) = (fields.TrackNumber, fields.Year, fields.Genre, fields.Composer, fields.Comment);
        Loaded += (_, _) => { TitleBox.Focus(); TitleBox.SelectAll(); };
        Closed += (_, _) => { if (_open == this) _open = null; };
    }

    /// <summary>Opens file info for a file; says why when it cannot (a station, a missing file, unreadable tags).</summary>
    public static async void Open(Window owner, PlayerController controller, Track? track)
    {
        if (track is null) return;
        if (track.IsStream) { controller.ShowNotice("A station has no tags to edit."); return; }
        if (_open is { } existing)
        {
            if (string.Equals(existing._path, track.Path, StringComparison.OrdinalIgnoreCase)) { existing.Activate(); return; }
            existing.Close(); // one file at a time, as the classic player: unsaved edits in the other one are dropped
        }
        if (!File.Exists(track.Path)) { controller.ShowNotice($"{Path.GetFileName(track.Path)} is no longer there."); return; }
        TagFields fields;
        try { fields = await Task.Run(() => TagEditor.Read(track.Path)); }
        catch (TagEditException ex) { controller.ShowNotice(ex.Message); return; }
        if (_open is not null) return; // opened twice quickly
        var window = new TagEditorWindow(controller, track.Path, fields, track) { Owner = owner, Topmost = owner.Topmost };
        _open = window;
        window.Show();
    }

    private static string FormatLine(string path, Track? track)
    {
        var parts = new List<string> { Path.GetExtension(path).TrimStart('.').ToUpperInvariant() };
        if (track?.Duration is { } d) parts.Add(TimeFormat.Clock(d));
        if (track?.BitrateKbps is { } kbps) parts.Add($"{kbps} kbps");
        try { parts.Add($"{new FileInfo(path).Length / 1048576.0:0.0} MB"); } catch (IOException) { }
        return string.Join("  ·  ", parts);
    }

    private TagFields Edited() => new(TitleBox.Text, ArtistBox.Text, AlbumBox.Text, AlbumArtistBox.Text, TrackBox.Text,
        YearBox.Text, GenreBox.Text, ComposerBox.Text, CommentBox.Text);

    private void ShowMessage(string? text)
    {
        MessageText.Text = text ?? "";
        MessageText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var after = Edited();
        if (TagEditor.Validate(after) is { } problem)
        {
            ShowMessage(problem);
            (problem.StartsWith("Track", StringComparison.Ordinal) ? TrackBox : YearBox).Focus();
            return;
        }
        if (TagEditor.Changed(_before, after).Count == 0) { Close(); return; }
        SaveButton.IsEnabled = false;
        ShowMessage("Saving…");
        var error = await _c.SaveTagsAsync(_path, _before, after);
        SaveButton.IsEnabled = true;
        if (error is null)
        {
            _c.ShowNotice($"Saved the tags of {Path.GetFileName(_path)}.");
            Close();
        }
        else ShowMessage(error);
    }

    private void Explorer_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_path}\"") { UseShellExecute = false }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { ShowMessage($"Could not open Explorer: {ex.Message}"); }
    }
}
