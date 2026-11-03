using System.Windows.Threading;
using Fernplay.Core;

namespace Fernplay.App;

/// <summary>
/// Playback logic shared by the three panels: what to play next, skipping files that cannot be played,
/// reading tags in the background. All members are used on the UI thread.
/// </summary>
public sealed class PlayerController : IDisposable
{
    private readonly Dispatcher _ui;
    private int _request; // the newest play request wins; older ones stop when they see a newer number
    private CancellationTokenSource? _tags;
    private readonly List<DateTime> _reconnects = []; // recent automatic reconnects of the current station

    private const int MaxReconnects = 3;
    private static readonly TimeSpan ReconnectWindow = TimeSpan.FromMinutes(1);

    public PlayerController(Dispatcher ui)
    {
        _ui = ui;
        // Engine events can come from the audio thread.
        Engine.StateChanged += (_, _) => _ui.BeginInvoke(() => StateChanged?.Invoke(this, EventArgs.Empty));
        Engine.TrackEnded += (_, _) => _ui.BeginInvoke(async () =>
        {
            // A radio stream never ends by itself: the station dropped the connection.
            if (NowPlaying is { IsStream: true } station) { await ReconnectAsync(station, null); return; }
            if (Playlist.NextIndex(trackEnded: true) is { } next) await PlayIndexAsync(next, forward: true);
        });
        Engine.PlaybackFailed += (_, ex) => _ui.BeginInvoke(async () =>
        {
            if (NowPlaying is { IsStream: true } station) { await ReconnectAsync(station, ex); return; }
            SetStatus(PlaybackErrors.Describe(ex, Engine.CurrentPath ?? ""));
        });
        Engine.StreamTitleChanged += (_, title) => _ui.BeginInvoke(() =>
        {
            if (NowPlaying is not { IsStream: true } || title == StreamTitle) return;
            StreamTitle = title;
            NowPlayingChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>The station dropped out: try again a few times, then say so.</summary>
    private async Task ReconnectAsync(Track station, Exception? error)
    {
        var now = DateTime.UtcNow;
        _reconnects.RemoveAll(t => now - t > ReconnectWindow);
        if (_reconnects.Count >= MaxReconnects || Playlist.Current?.Path != station.Path)
        {
            SetStatus(error is null
                ? $"Lost the connection to {station.DisplayName}. Press play to try again."
                : PlaybackErrors.Describe(error, station.Path, station.DisplayName));
            return;
        }
        _reconnects.Add(now);
        int request = _request;
        SetStatus($"Reconnecting to {station.DisplayName}…");
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (request != _request || Playlist.Current?.Path != station.Path) return; // the user moved on meanwhile
        await PlayIndexAsync(Playlist.CurrentIndex, forward: true, reconnecting: true);
    }

    public PlayerEngine Engine { get; } = PlayerEngine.CreateDefault();
    public Playlist Playlist { get; } = new();
    public Track? NowPlaying { get; private set; }
    /// <summary>The song the playing station says it is playing ("Artist - Title"), or null.</summary>
    public string? StreamTitle { get; private set; }
    public string? Status { get; private set; }

    public event EventHandler? StateChanged;
    public event EventHandler? NowPlayingChanged;
    public event EventHandler? StatusChanged;

    // ================= Adding =================

    /// <summary>
    /// replace: the list becomes these files and the first one plays (Open, drop on the player).
    /// Otherwise they are added at the end (Add on the playlist, drop on the playlist).
    /// </summary>
    public async Task OpenAsync(IReadOnlyList<string> paths, bool replace)
    {
        var (files, missing) = await Task.Run(() => (Playlist.ExpandPaths(paths).ToList(), MissingInPlaylists(paths)));
        var playlistName = paths.Where(Playlist.IsPlaylistFile).Select(System.IO.Path.GetFileName).FirstOrDefault();
        if (files.Count == 0)
        {
            var rejected = paths.FirstOrDefault(p => System.IO.File.Exists(p) && !Playlist.IsSupported(p) && !Playlist.IsPlaylistFile(p));
            if (playlistName is not null && rejected is null) { SetStatus($"None of the files in {playlistName} were found."); return; }
            SetStatus(rejected is not null
                ? $"Can't play {System.IO.Path.GetFileName(rejected)}: Windows has no decoder for {System.IO.Path.GetExtension(rejected).TrimStart('.').ToUpperInvariant()} files. Fernplay plays MP3, FLAC, WAV, WMA and AAC (M4A)."
                : "No music files there. Fernplay plays MP3, FLAC, WAV, WMA and AAC (M4A) files.");
            return;
        }
        if (replace)
        {
            Engine.Unload();
            Playlist.Clear();
            SetNowPlaying(null);
        }
        int first = Playlist.AddRange(files.Select(ToTrack));
        ReadTagsInBackground();
        if (replace) await PlayIndexAsync(first, forward: true);
        if (missing > 0)
            SetStatus($"{missing} {(missing == 1 ? "file" : "files")} in {playlistName} could not be found or played and {(missing == 1 ? "was" : "were")} left out.");
    }

    private static Track ToTrack(PlaylistEntry e) => e.IsStream ? Track.Stream(e.Path, e.Title) : new Track(e.Path, null, null, null, null, null);

    /// <summary>Entries of the playlist files among paths that are missing or not playable (streams count as playable).</summary>
    private static int MissingInPlaylists(IEnumerable<string> paths)
    {
        int n = 0;
        foreach (var p in paths.Where(p => Playlist.IsPlaylistFile(p) && System.IO.File.Exists(p)))
        {
            try { n += Playlist.ReadPlaylistFile(p).Count(e => !Playlist.IsPlayable(e)); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        }
        return n;
    }

    /// <summary>Saves the list as an extended M3U; returns an error to show, or null.</summary>
    public string? SavePlaylist(string path)
    {
        try { M3u.Write(path, Playlist.Tracks); return null; }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return $"Could not save {System.IO.Path.GetFileName(path)}: {ex.Message}";
        }
    }

    /// <summary>Restores a saved list without playing it. Station names come from <paramref name="stationNames"/> (address → name).</summary>
    public void Restore(IEnumerable<string> paths, int current, IReadOnlyDictionary<string, string>? stationNames = null)
    {
        Playlist.AddRange(paths
            .Where(p => Track.IsStreamPath(p) || System.IO.File.Exists(p))
            .Select(p => Track.IsStreamPath(p) ? Track.Stream(p, stationNames?.GetValueOrDefault(p)) : new Track(p, null, null, null, null, null)));
        if (current >= 0 && current < Playlist.Tracks.Count) Playlist.MoveTo(current);
        ReadTagsInBackground();
    }

    /// <summary>Reads title, artist and length for every file without them yet, in batches, off the UI thread (never streams).</summary>
    private void ReadTagsInBackground()
    {
        _tags?.Cancel();
        var cts = _tags = new CancellationTokenSource();
        var todo = Playlist.Tracks.Select((t, i) => (t.Path, i)).Where(x => Playlist.Tracks[x.i] is { Duration: null, IsStream: false }).ToList();
        _ = Task.Run(() =>
        {
            foreach (var chunk in todo.Chunk(25))
            {
                if (cts.IsCancellationRequested) return;
                var read = chunk.Select(x => (x.i, Track.FromFile(x.Path))).ToList();
                _ui.BeginInvoke(() => { foreach (var (i, t) in read) Playlist.Update(i, t); });
            }
        }, cts.Token);
    }

    // ================= Playing =================

    /// <summary>
    /// Plays the track at index; if a file cannot be opened, tries the next one in that direction and says
    /// which file was skipped and why. A station that cannot be reached is not skipped: its message stays.
    /// A newer request (another click while a station connects) takes over; this one then stops quietly.
    /// </summary>
    public async Task PlayIndexAsync(int index, bool forward, bool reconnecting = false)
    {
        int request = ++_request;
        if (!reconnecting) _reconnects.Clear();
        string? skipped = null;
        for (int i = index, tries = 0; i >= 0 && i < Playlist.Tracks.Count && tries < Playlist.Tracks.Count; i += forward ? 1 : -1, tries++)
        {
            var entry = Playlist.Tracks[i];
            if (entry.IsStream)
            {
                Playlist.MoveTo(i);
                SetNowPlaying(entry);
                SetStatus($"Connecting to {entry.DisplayName}…");
            }
            try { await Engine.LoadAsync(entry.Path); }
            catch (OperationCanceledException) { return; } // a newer request is loading
            catch (Exception ex)
            {
                if (request != _request) return;
                if (entry.IsStream)
                {
                    SetStatus(PlaybackErrors.Describe(ex, entry.Path, entry.DisplayName));
                    return;
                }
                skipped ??= PlaybackErrors.Describe(ex, entry.Path);
                continue;
            }
            if (request != _request) return;
            Playlist.MoveTo(i);
            var track = entry.IsStream ? entry : Track.FromFile(entry.Path);
            // A station without a playlist name takes the name it sends (icy-name); it is then remembered.
            if (entry is { IsStream: true, Title: null } && Engine.StationName is { } name) track = Track.Stream(entry.Path, name);
            if (track != entry) Playlist.Update(i, track);
            SetNowPlaying(track);
            Engine.Play();
            SetStatus(skipped is null ? null : skipped + " Skipped.");
            return;
        }
        if (skipped is not null && request == _request)
        {
            SetNowPlaying(null);
            SetStatus(skipped);
        }
    }

    /// <summary>Play: resume, start again, or start the list (opens files when the list is empty).</summary>
    public async Task<bool> PlayAsync()
    {
        if (!Engine.IsLoaded)
        {
            if (Playlist.Tracks.Count == 0) return false;
            await PlayIndexAsync(Math.Max(Playlist.CurrentIndex, 0), forward: true);
            return true;
        }
        if (Engine.State == PlayState.Playing) Engine.Seek(TimeSpan.Zero); // Play while playing starts the track again
        else Engine.Play();
        return true;
    }

    public void TogglePause()
    {
        if (Engine.State == PlayState.Playing) Engine.Pause();
        else if (Engine.State == PlayState.Paused) Engine.Play();
    }

    /// <summary>Stop. A station is disconnected (Play connects again, live); a file goes back to its start.</summary>
    public void Stop()
    {
        _request++; // a station still connecting gives up
        if (NowPlaying is { IsStream: true })
        {
            Engine.Unload();
            if (StreamTitle is not null) { StreamTitle = null; NowPlayingChanged?.Invoke(this, EventArgs.Empty); }
            if (Status is { } s && (s.StartsWith("Connecting", StringComparison.Ordinal) || s.StartsWith("Reconnecting", StringComparison.Ordinal))) SetStatus(null);
        }
        else Engine.Stop();
    }

    /// <summary>Empties the list and stops: nothing loaded, nothing shown.</summary>
    public void ClearList()
    {
        _tags?.Cancel();
        Engine.Unload();
        Playlist.Clear();
        SetNowPlaying(null);
        SetStatus(null);
    }

    public async Task NextAsync()
    {
        if (Playlist.NextIndex(trackEnded: false) is { } i) await PlayIndexAsync(i, forward: true);
    }

    public async Task PreviousAsync()
    {
        int i = Playlist.PreviousIndex();
        if (i < 0) return;
        if (i == Playlist.CurrentIndex && Engine.IsLoaded) { Engine.Seek(TimeSpan.Zero); return; }
        await PlayIndexAsync(i, forward: false);
    }

    // ================= Tags =================

    /// <summary>A file's tags were saved (the path); the library refreshes its entry.</summary>
    public event EventHandler<string>? TagsSaved;

    /// <summary>
    /// Writes edited tags, then shows them everywhere the file appears. The playing file is let go first (Windows
    /// would otherwise rewrite it under the decoder) and picked up again at the same place and state.
    /// Returns an error to show, or null.
    /// </summary>
    public async Task<string?> SaveTagsAsync(string path, TagFields before, TagFields after)
    {
        if (TagEditor.Changed(before, after).Count == 0) return null;
        bool playing = Engine.IsLoaded && string.Equals(Engine.CurrentPath, path, StringComparison.OrdinalIgnoreCase);
        string? error = null;
        if (!playing)
        {
            try { await Task.Run(() => TagEditor.Write(path, before, after)); }
            catch (TagEditException ex) { return ex.Message; }
        }
        else
        {
            var state = Engine.State;
            var at = Engine.Position;
            int request = ++_request; // a play request meanwhile wins
            Engine.Unload();
            try { await Task.Run(() => TagEditor.Write(path, before, after)); }
            catch (TagEditException ex) { error = ex.Message; }
            if (request == _request)
            {
                try
                {
                    await Engine.LoadAsync(path);
                    if (request == _request)
                    {
                        Engine.Seek(at);
                        if (state == PlayState.Playing) Engine.Play();
                        else if (state == PlayState.Paused) Engine.ShowPaused();
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { SetStatus(PlaybackErrors.Describe(ex, path)); }
            }
            if (error is not null) return error;
        }
        var track = await Task.Run(() => Track.FromFile(path));
        Playlist.Refresh(track);
        if (NowPlaying is { } now && string.Equals(now.Path, path, StringComparison.OrdinalIgnoreCase)) SetNowPlaying(track with { Path = now.Path });
        TagsSaved?.Invoke(this, path);
        return null;
    }

    /// <summary>File dialog filter for opening or adding music and playlists.</summary>
    public const string OpenFilter =
        "Music files and playlists|*.mp3;*.flac;*.wav;*.wma;*.m4a;*.aac;*.m3u;*.m3u8;*.pls|Playlists|*.m3u;*.m3u8;*.pls|All files|*.*";

    /// <summary>A short message in the title display that clears itself after a few seconds.</summary>
    public void ShowNotice(string text)
    {
        SetStatus(text);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => { timer.Stop(); if (Status == text) SetStatus(null); };
        timer.Start();
    }

    public void SetStatus(string? text)
    {
        Status = text;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetNowPlaying(Track? t)
    {
        if (t?.Path != NowPlaying?.Path || !Engine.IsLoaded) StreamTitle = null;
        NowPlaying = t;
        NowPlayingChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _tags?.Cancel();
        Engine.Dispose();
    }
}
