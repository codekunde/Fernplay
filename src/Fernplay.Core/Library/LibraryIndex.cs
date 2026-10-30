using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fernplay.Core.Library;

/// <summary>One music file in the library: its tags, file facts, and Fernplay's own play statistics and rating.</summary>
public sealed class LibraryTrack
{
    public string Path { get; set; } = "";
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Album { get; set; }
    public string? Genre { get; set; }
    public int? TrackNumber { get; set; }
    public int? Year { get; set; }
    public int? BitrateKbps { get; set; }
    public long? DurationMs { get; set; }
    public long Size { get; set; }
    /// <summary>File's last-write time (UTC); with <see cref="Size"/> it tells whether the file changed since the last scan.</summary>
    public DateTime Modified { get; set; }
    public DateTime Added { get; set; }
    /// <summary>0 (none) to 5 stars; starts as the file's own rating, then only Fernplay changes it (never written to the file).</summary>
    public int Rating { get; set; }
    public int PlayCount { get; set; }
    public DateTime? LastPlayed { get; set; }

    [JsonIgnore] public TimeSpan? Duration => DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null;
    [JsonIgnore] public string ArtistName => Artist ?? AlbumArtist ?? LibraryQuery.UnknownArtist;
    [JsonIgnore] public string AlbumName => Album ?? LibraryQuery.UnknownAlbum;
    [JsonIgnore] public string TitleText => Title ?? System.IO.Path.GetFileNameWithoutExtension(Path);

    public Track ToTrack() => new(Path, Title, Artist, Album, Duration, BitrateKbps, TrackNumber);

    /// <summary>Copies tags and file facts from a fresh read; keeps the date added, play statistics and rating.</summary>
    public void SetTags(TagInfo? t, long size, DateTime modified)
    {
        Title = t?.Title; Artist = t?.Artist; AlbumArtist = t?.AlbumArtist; Album = t?.Album; Genre = t?.Genre;
        TrackNumber = t?.TrackNumber; Year = t?.Year; BitrateKbps = t?.BitrateKbps;
        DurationMs = t?.Duration is { } d ? (long)d.TotalMilliseconds : null;
        Size = size; Modified = modified;
    }
}

/// <summary>One play, for the History view.</summary>
public sealed record HistoryEntry(string Path, DateTime PlayedAt);

/// <summary>
/// The library: the owner's folders, the tracks found in them, playlist files found there, and the play history.
/// Kept as one JSON file next to the settings. Not thread-safe: change it on one thread (the UI thread).
/// </summary>
public sealed class LibraryIndex
{
    public const int MaxHistory = 1000;

    private readonly Dictionary<string, LibraryTrack> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public int Version { get; set; } = 1;
    public List<string> Folders { get; set; } = [];
    public List<string> Playlists { get; set; } = [];
    public List<HistoryEntry> History { get; set; } = [];

    public List<LibraryTrack> Tracks
    {
        get => [.. _byPath.Values];
        set { _byPath.Clear(); foreach (var t in value) _byPath[t.Path] = t; }
    }

    [JsonIgnore] public int Count => _byPath.Count;
    [JsonIgnore] public IEnumerable<LibraryTrack> All => _byPath.Values;

    /// <summary>Raised after any change (scan applied, play recorded, rating set, folder added or removed).</summary>
    public event EventHandler? Changed;

    public LibraryTrack? Find(string path) => _byPath.GetValueOrDefault(path);

    public bool AddFolder(string folder)
    {
        folder = System.IO.Path.GetFullPath(folder).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        if (Folders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase))) return false;
        Folders.Add(folder);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Removes a folder and every track and playlist under it (only from the library; files stay on disk).</summary>
    public void RemoveFolder(string folder)
    {
        Folders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        foreach (var path in _byPath.Keys.Where(p => IsUnder(p, folder)).ToList()) _byPath.Remove(path);
        Playlists.RemoveAll(p => IsUnder(p, folder));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static bool IsUnder(string path, string folder) =>
        path.StartsWith(folder.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Applies what a scan found.</summary>
    public void Apply(ScanChanges changes)
    {
        foreach (var t in changes.Upserts)
        {
            if (_byPath.TryGetValue(t.Path, out var old))
            {
                old.SetTags(t.Tags, t.Size, t.Modified);
            }
            else
            {
                var n = new LibraryTrack { Path = t.Path, Added = changes.ScannedAt, Rating = t.Tags?.Rating ?? 0 };
                n.SetTags(t.Tags, t.Size, t.Modified);
                _byPath[t.Path] = n;
            }
        }
        foreach (var p in changes.Removed) _byPath.Remove(p);
        foreach (var folder in changes.ScannedFolders)
        {
            Playlists.RemoveAll(p => IsUnder(p, folder));
        }
        Playlists.AddRange(changes.Playlists.Where(p => !Playlists.Contains(p, StringComparer.OrdinalIgnoreCase)));
        Playlists.Sort(StringComparer.OrdinalIgnoreCase);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A play that counted (see <see cref="PlayRules"/>): play count, last played, history.</summary>
    public void RecordPlay(string path, DateTime at)
    {
        if (_byPath.TryGetValue(path, out var t)) { t.PlayCount++; t.LastPlayed = at; }
        History.Insert(0, new HistoryEntry(path, at));
        if (History.Count > MaxHistory) History.RemoveRange(MaxHistory, History.Count - MaxHistory);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes these plays out of the history (play counts stay: they say how often, the history says when).</summary>
    public void RemoveHistory(IEnumerable<HistoryEntry> entries)
    {
        var gone = entries.ToHashSet();
        if (History.RemoveAll(gone.Contains) > 0) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ClearHistory()
    {
        if (History.Count == 0) return;
        History.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets the rating of several tracks with one change notice.</summary>
    public void SetRating(IEnumerable<string> paths, int stars)
    {
        bool any = false;
        foreach (var p in paths)
            if (_byPath.TryGetValue(p, out var t)) { t.Rating = Math.Clamp(stars, 0, 5); any = true; }
        if (any) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetRating(string path, int stars)
    {
        if (!_byPath.TryGetValue(path, out var t)) return;
        t.Rating = Math.Clamp(stars, 0, 5);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ================= File =================

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// Reads the index; a missing file gives an empty library. A file that cannot be read is kept as "&lt;name&gt;.bad"
    /// (never overwritten silently) and an empty library is returned. Never throws.
    /// </summary>
    public static LibraryIndex Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new LibraryIndex();
            return JsonSerializer.Deserialize<LibraryIndex>(File.ReadAllText(path), Json) ?? new LibraryIndex();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try { File.Copy(path, path + ".bad", overwrite: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return new LibraryIndex();
        }
    }

    /// <summary>Writes the index through a temporary file, so a crash mid-write never leaves a half file.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
