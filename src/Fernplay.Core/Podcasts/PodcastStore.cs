using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fernplay.Core.Podcasts;

/// <summary>One episode of a subscribed podcast, with how far the owner got.</summary>
public sealed class PodcastEpisode
{
    /// <summary>The feed's guid (the audio address when the feed has none); stays when the address changes.</summary>
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>Earlier addresses of this episode (feeds rotate tracking links), so a saved playlist still finds it.</summary>
    public List<string> OldUrls { get; set; } = [];
    public DateTime? Published { get; set; }
    /// <summary>Length the feed states.</summary>
    public long? DurationMs { get; set; }
    /// <summary>Length measured while it played (feeds with inserted ads state less).</summary>
    public long? MeasuredMs { get; set; }
    public long? Size { get; set; }
    public int? Number { get; set; }
    /// <summary>Where listening stopped; 0 = from the start.</summary>
    public long PositionMs { get; set; }
    public bool Played { get; set; }
    public DateTime? LastPlayed { get; set; }

    [JsonIgnore] public TimeSpan? Duration => (MeasuredMs ?? DurationMs) is { } ms and > 0 ? TimeSpan.FromMilliseconds(ms) : null;
    [JsonIgnore] public TimeSpan Position => TimeSpan.FromMilliseconds(PositionMs);
    [JsonIgnore] public bool Started => !Played && PositionMs > 0;
}

/// <summary>A subscribed podcast: its feed address, what the feed said last, and its episodes newest first.</summary>
public sealed class PodcastShow
{
    public string FeedUrl { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Author { get; set; }
    public string? ImageUrl { get; set; }
    public string? Link { get; set; }
    public DateTime Added { get; set; }
    /// <summary>Last time the feed was read successfully.</summary>
    public DateTime? Checked { get; set; }
    /// <summary>Why the last check failed; null after a good one.</summary>
    public string? Error { get; set; }
    public List<PodcastEpisode> Episodes { get; set; } = [];

    /// <summary>Episodes out since subscribing and not yet started (the back catalogue is not "new").</summary>
    [JsonIgnore] public int NewCount => Episodes.Count(e => !e.Played && e.PositionMs == 0 && e.Published >= Added);
}

/// <summary>
/// The podcasts: subscriptions, episodes and listening state, kept as one JSON file next to the settings. Change it on
/// one thread (the UI thread); <see cref="IsEpisode"/> may be asked from any thread.
/// </summary>
public sealed class PodcastStore
{
    public int Version { get; set; } = 1;
    public List<PodcastShow> Shows { get; set; } = [];

    /// <summary>Raised when what the library shows changes (subscriptions, episodes, played state).</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when only a listening position moved (worth saving, not worth redrawing).</summary>
    public event EventHandler? PositionChanged;

    // Episode address -> (show, episode); rebuilt after every change. Swapped whole, so readers on other threads see
    // either the old or the new map.
    private volatile Dictionary<string, (PodcastShow Show, PodcastEpisode Episode)>? _byUrl;

    private Dictionary<string, (PodcastShow Show, PodcastEpisode Episode)> ByUrl => _byUrl ??= BuildMap();

    private Dictionary<string, (PodcastShow, PodcastEpisode)> BuildMap()
    {
        var map = new Dictionary<string, (PodcastShow, PodcastEpisode)>(StringComparer.Ordinal);
        foreach (var show in Shows)
            foreach (var e in show.Episodes)
            {
                foreach (var old in e.OldUrls) map.TryAdd(old, (show, e));
                map[e.Url] = (show, e);
            }
        return map;
    }

    private void RaiseChanged()
    {
        _byUrl = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A played-or-not episode with this audio address (its current or an earlier one).</summary>
    public (PodcastShow Show, PodcastEpisode Episode)? Find(string url) => ByUrl.TryGetValue(url, out var x) ? x : null;

    public bool IsEpisode(string url) => Track.IsStreamPath(url) && ByUrl.ContainsKey(url);

    public PodcastShow? Show(string feedUrl) => Shows.FirstOrDefault(s => string.Equals(s.FeedUrl, feedUrl, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds the podcast (or refreshes it when already subscribed) and returns it.</summary>
    public PodcastShow Subscribe(string feedUrl, FeedShow feed, DateTime now)
    {
        var show = Show(feedUrl);
        if (show is null)
        {
            show = new PodcastShow { FeedUrl = feedUrl, Added = now };
            Shows.Add(show);
        }
        Merge(show, feed, now);
        Shows.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Title, b.Title));
        RaiseChanged();
        return show;
    }

    /// <summary>The feed was read again: new episodes in, changed ones updated, listening state kept.</summary>
    public void Apply(PodcastShow show, FeedShow feed, DateTime now)
    {
        if (!Shows.Contains(show)) return; // unsubscribed while the feed was read
        Merge(show, feed, now);
        RaiseChanged();
    }

    private static void Merge(PodcastShow show, FeedShow feed, DateTime now)
    {
        show.Title = feed.Title;
        show.Author = feed.Author;
        show.ImageUrl = feed.ImageUrl;
        show.Link = feed.Link;
        show.Checked = now;
        show.Error = null;
        var byId = show.Episodes.GroupBy(e => e.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var byUrl = show.Episodes.GroupBy(e => e.Url, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var kept = new List<PodcastEpisode>();
        var seen = new HashSet<PodcastEpisode>();
        foreach (var f in feed.Episodes)
        {
            var e = byId.GetValueOrDefault(f.Id) ?? byUrl.GetValueOrDefault(f.Url) ?? new PodcastEpisode { Id = f.Id };
            if (!seen.Add(e)) continue; // the feed lists it twice
            if (e.Url.Length > 0 && e.Url != f.Url)
            {
                e.OldUrls.Remove(f.Url);
                e.OldUrls.Insert(0, e.Url);
                if (e.OldUrls.Count > 3) e.OldUrls.RemoveRange(3, e.OldUrls.Count - 3);
            }
            e.Id = f.Id; e.Title = f.Title; e.Url = f.Url; e.Published = f.Published;
            e.DurationMs = f.Duration is { } d ? (long)d.TotalMilliseconds : null;
            e.Size = f.Size; e.Number = f.Number;
            kept.Add(e);
        }
        // Episodes the feed no longer lists stay only when listened to (their address may be gone; the state is not).
        kept.AddRange(show.Episodes.Where(e => !seen.Contains(e) && (e.Played || e.PositionMs > 0)));
        show.Episodes = Newest(kept);
    }

    /// <summary>Newest first; episodes without a date after the dated ones, in feed order.</summary>
    public static List<PodcastEpisode> Newest(IEnumerable<PodcastEpisode> episodes) =>
        [.. episodes.Select((e, i) => (e, i)).OrderBy(x => x.e.Published is null).ThenByDescending(x => x.e.Published).ThenBy(x => x.i).Select(x => x.e)];

    /// <summary>The feed could not be read: keep what is known and say why.</summary>
    public void SetError(PodcastShow show, string error)
    {
        if (!Shows.Contains(show) || show.Error == error) return;
        show.Error = error;
        RaiseChanged();
    }

    public bool Unsubscribe(PodcastShow show)
    {
        if (!Shows.Remove(show)) return false;
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Listening reached <paramref name="position"/> of <paramref name="length"/> (the length the player measured; zero
    /// when unknown). Near the end the episode counts as played and starts from the beginning next time.
    /// </summary>
    public void SetPosition(string url, TimeSpan position, TimeSpan length, DateTime now)
    {
        if (Find(url) is not { Episode: var e }) return;
        if (length > TimeSpan.Zero) e.MeasuredMs = (long)length.TotalMilliseconds;
        e.LastPlayed = now;
        var total = length > TimeSpan.Zero ? length : e.Duration ?? TimeSpan.Zero;
        if (IsDone(position, total))
        {
            bool was = e.Played;
            e.Played = true;
            e.PositionMs = 0;
            if (!was) RaiseChanged(); else PositionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        bool started = e.PositionMs == 0 && position > TimeSpan.Zero;
        e.PositionMs = (long)position.TotalMilliseconds;
        if (e.Played && position > TimeSpan.FromSeconds(30)) { e.Played = false; started = true; } // heard again
        if (started) RaiseChanged(); else PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Played: the last minute, or the last 5%, was reached (outros and ads are often skipped).</summary>
    public static bool IsDone(TimeSpan position, TimeSpan length) =>
        length > TimeSpan.Zero && (length - position <= TimeSpan.FromMinutes(1) || position.TotalMilliseconds >= length.TotalMilliseconds * 0.95);

    /// <summary>Marks episodes played (position cleared) or not played.</summary>
    public void SetPlayed(IEnumerable<string> urls, bool played)
    {
        bool any = false;
        foreach (var url in urls)
        {
            if (Find(url) is not { Episode: var e }) continue;
            if (e.Played == played && e.PositionMs == 0) continue;
            e.Played = played;
            e.PositionMs = 0;
            any = true;
        }
        if (any) RaiseChanged();
    }

    /// <summary>
    /// Where to start an episode: a few seconds before where listening stopped (to pick up the thread), or null for
    /// the beginning (never started, played, or stopped in the first seconds).
    /// </summary>
    public TimeSpan? ResumeAt(string url)
    {
        if (Find(url) is not { Episode: var e } || e.Played || e.PositionMs < 15_000) return null;
        return TimeSpan.FromMilliseconds(e.PositionMs - 5_000);
    }

    // ================= File =================

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Reads the file; missing gives none. One that cannot be read is kept as "&lt;name&gt;.bad". Never throws.</summary>
    public static PodcastStore Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new PodcastStore();
            return JsonSerializer.Deserialize<PodcastStore>(File.ReadAllText(path), Json) ?? new PodcastStore();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try { File.Copy(path, path + ".bad", overwrite: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return new PodcastStore();
        }
    }

    /// <summary>Writes through a temporary file, so a crash mid-write never leaves a half file.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
