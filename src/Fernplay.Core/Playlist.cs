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
        if (index < 0 || index >= _tracks.Count || _tracks[index].Path != track.Path) return;
        _tracks[index] = track;
        ItemUpdated?.Invoke(this, index);
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

    /// <summary>Expands files and folders (folders recursively, sorted by path) to playable files.</summary>
    public static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Where(IsSupported).Order(StringComparer.OrdinalIgnoreCase).ToList(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var f in files) yield return f;
            }
            else if (File.Exists(p) && IsSupported(p)) yield return p;
        }
    }

    public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path));
}
