using System.IO;
using System.Windows.Threading;
using Fernplay.Core;
using Fernplay.Core.Podcasts;

namespace Fernplay.App;

/// <summary>What the player needs to know about podcast episodes (web addresses of audio files, not stations).</summary>
public interface IEpisodeSource
{
    /// <summary>Safe to ask from any thread.</summary>
    bool IsEpisode(string url);
    /// <summary>The episode as a playlist track (show and episode title, length); null when not an episode.</summary>
    Track? TrackFor(string url);
    /// <summary>Where to start it; null = the beginning.</summary>
    TimeSpan? ResumeAt(string url);
}

/// <summary>
/// The podcasts behind the library: the subscriptions file next to the settings, reading feeds, and remembering how
/// far each episode was heard. Nothing is written until the first podcast is added.
/// </summary>
public sealed class PodcastService : IEpisodeSource
{
    /// <summary>Feeds are read again when the library opens, at most this often.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(30);

    private readonly PlayerController _c;
    private readonly PodcastClient _client;
    private readonly DispatcherTimer _listen = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _savePosition = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _positionDirty;
    private int _checking;

    public static string FilePath => Path.Combine(Path.GetDirectoryName(UserSettings.FilePath)!, "podcasts.json");

    public PodcastStore Store { get; }

    /// <summary>"Checking 3 podcasts…" while feeds are read; null otherwise.</summary>
    public string? Status { get; private set; }

    public event EventHandler? StatusChanged;

    public PodcastService(PlayerController controller, string version)
    {
        _c = controller;
        _client = new PodcastClient(version: version);
        Store = PodcastStore.Load(FilePath);
        Store.Changed += (_, _) => { _save.Stop(); _save.Start(); };
        Store.PositionChanged += (_, _) => _positionDirty = true;
        _save.Tick += (_, _) => { _save.Stop(); SaveNow(); };
        _savePosition.Tick += (_, _) => { if (_positionDirty) SaveNow(); };
        _savePosition.Start();
        _listen.Tick += (_, _) => Listen();
        _listen.Start();
        // Paused or stopped: keep the place at once (Stop unloads an episode, as it does a station).
        _c.StateChanged += (_, _) => { Listen(); if (_c.Engine.State != PlayState.Playing && _positionDirty) SaveNow(); };
    }

    public void SaveNow()
    {
        _positionDirty = false;
        if (Store.Shows.Count == 0 && !File.Exists(FilePath)) return;
        try { Store.Save(FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _c.ShowNotice($"Podcasts not saved: {ex.Message}"); }
    }

    // ================= IEpisodeSource =================

    public bool IsEpisode(string url) => Store.IsEpisode(url);

    public Track? TrackFor(string url) => Store.Find(url) is { } x ? EpisodeTrack(x.Show, x.Episode) : null;

    public TimeSpan? ResumeAt(string url) => Store.ResumeAt(url);

    /// <summary>"Show - Episode" in the playlist, with the length the feed or an earlier play gave.</summary>
    public static Track EpisodeTrack(PodcastShow show, PodcastEpisode e) => new(e.Url, e.Title, show.Title, show.Title, e.Duration, null);

    // ================= Listening =================

    /// <summary>Once a second while an episode plays (and on pause or stop): remember where it is.</summary>
    private void Listen()
    {
        var now = _c.NowPlaying;
        if (now is not { IsStream: true } || !_c.Engine.IsLoaded || !string.Equals(_c.Engine.CurrentPath, now.Path, StringComparison.Ordinal)) return;
        if (!Store.IsEpisode(now.Path)) return;
        if (_c.Engine.State == PlayState.Stopped && _c.Engine.Position == TimeSpan.Zero) return; // not started yet
        Store.SetPosition(now.Path, _c.Engine.Position, _c.Engine.Duration, DateTime.UtcNow);
    }

    // ================= Feeds =================

    /// <summary>Adds the podcast at what was typed or pasted. Returns the show, or the reason it could not be added.</summary>
    public async Task<(PodcastShow? Show, string? Error)> SubscribeAsync(string text)
    {
        try
        {
            SetStatus("Reading the feed…");
            string feedUrl = await _client.ResolveAsync(text);
            if (Store.Show(feedUrl) is { } known) return (known, $"{known.Title} is already in your podcasts.");
            var feed = await _client.FetchAsync(feedUrl);
            var show = Store.Subscribe(feedUrl, feed, DateTime.UtcNow);
            SaveNow();
            return (show, null);
        }
        catch (FeedException ex) { return (null, ex.Message); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or System.Net.Http.HttpRequestException)
        {
            return (null, $"Could not read that feed: {ex.Message}");
        }
        finally { SetStatus(null); }
    }

    /// <summary>
    /// Reads the feeds again: all of them not checked for <see cref="CheckEvery"/> (or all with force), or one show.
    /// Four at a time. A feed that fails keeps its episodes and shows why.
    /// </summary>
    public async Task RefreshAsync(PodcastShow? only = null, bool force = false)
    {
        var now = DateTime.UtcNow;
        var due = only is not null ? [only]
            : Store.Shows.Where(s => force || s.Checked is not { } c || now - c >= CheckEvery || s.Error is not null).ToList();
        if (due.Count == 0 || _checking > 0 && only is null) return;
        _checking++;
        SetStatus(due.Count == 1 ? $"Checking {due[0].Title}…" : $"Checking {due.Count} podcasts…");
        int before = due.Sum(s => s.Episodes.Count);
        try
        {
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(due.Select(async show =>
            {
                await gate.WaitAsync();
                try
                {
                    var feed = await _client.FetchAsync(show.FeedUrl);
                    Store.Apply(show, feed, DateTime.UtcNow); // back on the UI thread (no ConfigureAwait)
                }
                catch (FeedException ex) { Store.SetError(show, ex.Message); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Net.Http.HttpRequestException) { Store.SetError(show, ex.Message); }
                finally { gate.Release(); }
            }));
        }
        finally
        {
            _checking--;
            SetStatus(null);
        }
        int added = due.Sum(s => s.Episodes.Count) - before;
        if (added > 0) _c.ShowNotice($"{added} new podcast episode{(added == 1 ? "" : "s")}.");
    }

    public void Unsubscribe(PodcastShow show)
    {
        if (Store.Unsubscribe(show)) SaveNow();
    }

    private void SetStatus(string? text)
    {
        Status = text;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
