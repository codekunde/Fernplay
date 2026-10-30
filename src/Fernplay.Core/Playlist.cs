namespace Fernplay.Core;

public enum RepeatMode { Off, All, One }

/// <summary>
/// Ordered list of tracks with a current position, shuffle and repeat.
/// Shuffle plays every track once in a random order before any repeats.
/// </summary>
public sealed class Playlist
{
    private readonly List<Track> _tracks = [];
    private List<int> _order = [];   // shuffle order (indexes into _tracks)
    private readonly Random _random;
    private bool _shuffle;

    public Playlist(Random? random = null) => _random = random ?? Random.Shared;

    public IReadOnlyList<Track> Tracks => _tracks;
    public int CurrentIndex { get; private set; } = -1;
    public Track? Current => CurrentIndex >= 0 && CurrentIndex < _tracks.Count ? _tracks[CurrentIndex] : null;
    public RepeatMode Repeat { get; set; }

    public bool Shuffle
    {
        get => _shuffle;
        set { _shuffle = value; Reshuffle(); }
    }

    /// <summary>Tracks were added, removed or cleared (the rows must be rebuilt).</summary>
    public event EventHandler? Changed;
    /// <summary>One track's details changed (tags read later).</summary>
    public event EventHandler<int>? ItemUpdated;
    /// <summary>The current track moved.</summary>
    public event EventHandler? CurrentChanged;

    /// <summary>Sum of the durations that are known so far.</summary>
    public TimeSpan TotalDuration => TimeSpan.FromTicks(_tracks.Sum(t => t.Duration?.Ticks ?? 0));

    /// <summary>
    /// Text for the playlist's total box: "6 tracks  19:09", "3 stations", or "6 tracks  19:09 + 3 stations".
    /// Stations have no length, so they are counted apart and never add "0:00".
    /// </summary>
    public string Summary
    {
        get
        {
            int stations = _tracks.Count(t => t.IsStream), files = _tracks.Count - stations;
            static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";
            if (files == 0) return stations == 0 ? "0 tracks" : Plural(stations, "station");
            var text = $"{Plural(files, "track")}  {TimeFormat.Clock(TotalDuration)}";
            return stations == 0 ? text : $"{text} + {Plural(stations, "station")}";
        }
    }

    /// <summary>Adds tracks at the end; returns the index of the first one added.</summary>
    public int AddRange(IEnumerable<Track> tracks)
    {
        int first = _tracks.Count;
        _tracks.AddRange(tracks);
        if (_tracks.Count > first) { Reshuffle(); Changed?.Invoke(this, EventArgs.Empty); }
        return first;
    }

    /// <summary>Replaces a track's details (tags read later) without moving anything.</summary>
    public void Update(int index, Track track)
    {
        // The track may have moved since its tags were requested: look it up by path then.
        if (index < 0 || index >= _tracks.Count || _tracks[index].Path != track.Path)
            index = _tracks.FindIndex(t => t.Path == track.Path && t.Duration is null);
        if (index < 0) return;
        _tracks[index] = track;
        ItemUpdated?.Invoke(this, index);
    }

    /// <summary>
    /// Moves tracks (kept in their order) so they sit before the track that was at <paramref name="insertAt"/>
    /// (Count = the end). The playing track and the shuffle order follow. Returns the moved tracks' new indexes.
    /// </summary>
    public IReadOnlyList<int> Move(IEnumerable<int> indexes, int insertAt)
    {
        var moved = indexes.Where(i => i >= 0 && i < _tracks.Count).Distinct().Order().ToList();
        if (moved.Count == 0) return [];
        insertAt = Math.Clamp(insertAt, 0, _tracks.Count);
        var movedSet = moved.ToHashSet();
        var rest = Enumerable.Range(0, _tracks.Count).Where(i => !movedSet.Contains(i)).ToList();
        int at = rest.Count(i => i < insertAt);
        var newOrder = rest.Take(at).Concat(moved).Concat(rest.Skip(at)).ToList(); // newOrder[new] = old
        if (newOrder.Select((old, n) => old == n).All(same => same)) return moved;

        var map = new int[newOrder.Count]; // map[old] = new
        for (int n = 0; n < newOrder.Count; n++) map[newOrder[n]] = n;
        var old = _tracks.ToList();
        for (int n = 0; n < newOrder.Count; n++) _tracks[n] = old[newOrder[n]];
        if (CurrentIndex >= 0) CurrentIndex = map[CurrentIndex];
        _order = _order.Select(i => map[i]).ToList();
        Changed?.Invoke(this, EventArgs.Empty);
        return Enumerable.Range(at, moved.Count).ToList();
    }

    /// <summary>Removes tracks. When the current one goes, the next track to play is the one that followed it.</summary>
    public void RemoveAt(IEnumerable<int> indexes)
    {
        var set = indexes.Where(i => i >= 0 && i < _tracks.Count).Distinct().OrderDescending().ToList();
        if (set.Count == 0) return;
        foreach (var i in set)
        {
            _tracks.RemoveAt(i);
            if (i < CurrentIndex) CurrentIndex--;
            else if (i == CurrentIndex) CurrentIndex = i - 1; // so Next lands on the follower
        }
        if (CurrentIndex >= _tracks.Count) CurrentIndex = _tracks.Count - 1;
        Reshuffle();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _tracks.Clear();
        _order.Clear();
        CurrentIndex = -1;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Track? MoveTo(int index)
    {
        if (index < 0 || index >= _tracks.Count) return null;
        CurrentIndex = index;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        return Current;
    }

    /// <summary>
    /// Index of the track after the current one, or null when the list ends.
    /// <paramref name="trackEnded"/> is true when the current track played to its end (repeat one only applies then).
    /// </summary>
    public int? NextIndex(bool trackEnded)
    {
        if (_tracks.Count == 0) return null;
        if (trackEnded && Repeat == RepeatMode.One && Current is not null) return CurrentIndex;
        if (_shuffle)
        {
            int p = _order.IndexOf(CurrentIndex);
            if (p + 1 < _order.Count) return _order[p + 1];
            if (Repeat == RepeatMode.Off) return null;
            Reshuffle(avoidFirst: CurrentIndex);
            return _order[0];
        }
        if (CurrentIndex + 1 < _tracks.Count) return CurrentIndex + 1;
        return Repeat == RepeatMode.Off ? null : 0;
    }

    /// <summary>Index of the track before the current one; at the start it stays on the first.</summary>
    public int PreviousIndex()
    {
        if (_tracks.Count == 0) return -1;
        if (_shuffle)
        {
            int p = _order.IndexOf(CurrentIndex);
            return p > 0 ? _order[p - 1] : Math.Max(CurrentIndex, 0);
        }
        return Math.Max(CurrentIndex - 1, 0);
    }

    /// <summary>The next track, or null at the end of the list.</summary>
    public Track? Next() => NextIndex(trackEnded: false) is { } i ? MoveTo(i) : null;

    public Track? Previous() => MoveTo(PreviousIndex());

    private void Reshuffle(int avoidFirst = -1)
    {
        _order = Enumerable.Range(0, _tracks.Count).ToList();
        if (!_shuffle) return;
        for (int i = _order.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (_order[i], _order[j]) = (_order[j], _order[i]);
        }
        // Keep the playing track first, so a reshuffle mid-list does not replay it.
        int cur = avoidFirst >= 0 ? -1 : CurrentIndex;
        if (cur >= 0) { _order.Remove(cur); _order.Insert(0, cur); }
        else if (avoidFirst >= 0 && _order.Count > 1 && _order[0] == avoidFirst) (_order[0], _order[1]) = (_order[1], _order[0]);
    }

    /// <summary>File types Windows decodes out of the box (Media Foundation).</summary>
    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".flac", ".wav", ".wma", ".m4a", ".aac" };

    /// <summary>
    /// Expands files, folders (recursively, sorted by path; playlists inside folders are not opened, they
    /// usually list the same songs), .m3u/.m3u8/.pls playlists (their entries, in order) and stream addresses
    /// to playable entries. Playlist entries that are missing or not playable are left out.
    /// </summary>
    public static IEnumerable<PlaylistEntry> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Track.IsStreamPath(p)) yield return new PlaylistEntry(p);
            else if (IsPlaylistFile(p) && File.Exists(p))
            {
                List<PlaylistEntry> entries;
                try { entries = ReadPlaylistFile(p); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var e in entries.Where(IsPlayable)) yield return e;
            }
            else if (Directory.Exists(p))
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Where(IsSupported).Order(StringComparer.OrdinalIgnoreCase).ToList(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var f in files) yield return new PlaylistEntry(f);
            }
            else if (File.Exists(p) && IsSupported(p)) yield return new PlaylistEntry(p);
        }
    }

    public static bool IsPlaylistFile(string path) => M3u.IsPlaylist(path) || Pls.IsPlaylist(path);

    public static List<PlaylistEntry> ReadPlaylistFile(string path) => Pls.IsPlaylist(path) ? Pls.Read(path) : M3u.Read(path);

    /// <summary>A stream, or a file that exists and Windows can decode.</summary>
    public static bool IsPlayable(PlaylistEntry e) => e.IsStream || (File.Exists(e.Path) && IsSupported(e.Path));

    public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path));
}
