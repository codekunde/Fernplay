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
    private bool _busy;
    private CancellationTokenSource? _tags;

    public PlayerController(Dispatcher ui)
    {
        _ui = ui;
        // Engine events can come from the audio thread.
        Engine.StateChanged += (_, _) => _ui.BeginInvoke(() => StateChanged?.Invoke(this, EventArgs.Empty));
        Engine.TrackEnded += (_, _) => _ui.BeginInvoke(async () =>
        {
            if (Playlist.NextIndex(trackEnded: true) is { } next) await PlayIndexAsync(next, forward: true);
        });
        Engine.PlaybackFailed += (_, ex) => _ui.BeginInvoke(() => SetStatus(PlaybackErrors.Describe(ex, Engine.CurrentPath ?? "")));
    }

    public PlayerEngine Engine { get; } = PlayerEngine.CreateDefault();
    public Playlist Playlist { get; } = new();
    public Track? NowPlaying { get; private set; }
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
        var playlistName = paths.Where(M3u.IsPlaylist).Select(System.IO.Path.GetFileName).FirstOrDefault();
        if (files.Count == 0)
        {
            var rejected = paths.FirstOrDefault(p => System.IO.File.Exists(p) && !Playlist.IsSupported(p) && !M3u.IsPlaylist(p));
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
        int first = Playlist.AddRange(files.Select(f => new Track(f, null, null, null, null, null)));
        ReadTagsInBackground();
        if (replace) await PlayIndexAsync(first, forward: true);
        if (missing > 0)
            SetStatus($"{missing} {(missing == 1 ? "file" : "files")} in {playlistName} could not be found or played and {(missing == 1 ? "was" : "were")} left out.");
    }

    /// <summary>Entries of the .m3u/.m3u8 files among paths that are missing or not playable.</summary>
    private static int MissingInPlaylists(IEnumerable<string> paths)
    {
        int n = 0;
        foreach (var p in paths.Where(p => M3u.IsPlaylist(p) && System.IO.File.Exists(p)))
        {
            try { n += M3u.Read(p).Count(f => !System.IO.File.Exists(f) || !Playlist.IsSupported(f)); }
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

    /// <summary>Restores a saved list without playing it.</summary>
    public void Restore(IEnumerable<string> paths, int current)
    {
        Playlist.AddRange(paths.Where(System.IO.File.Exists).Select(f => new Track(f, null, null, null, null, null)));
        if (current >= 0 && current < Playlist.Tracks.Count) Playlist.MoveTo(current);
        ReadTagsInBackground();
    }

    /// <summary>Reads title, artist and length for every track without a title yet, in batches, off the UI thread.</summary>
    private void ReadTagsInBackground()
    {
        _tags?.Cancel();
        var cts = _tags = new CancellationTokenSource();
        var todo = Playlist.Tracks.Select((t, i) => (t.Path, i)).Where(x => Playlist.Tracks[x.i].Duration is null).ToList();
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
    /// Plays the track at index; if it cannot be opened, tries the next one in that direction
    /// and says which file was skipped and why.
    /// </summary>
    public async Task PlayIndexAsync(int index, bool forward)
    {
        if (_busy) return;
        _busy = true;
        string? skipped = null;
        try
        {
            for (int i = index, tries = 0; i >= 0 && i < Playlist.Tracks.Count && tries < Playlist.Tracks.Count; i += forward ? 1 : -1, tries++)
            {
                var path = Playlist.Tracks[i].Path;
                try { await Engine.LoadAsync(path); }
                catch (Exception ex)
                {
                    skipped ??= PlaybackErrors.Describe(ex, path);
                    continue;
                }
                Playlist.MoveTo(i);
                var track = Track.FromFile(path);
                Playlist.Update(i, track);
                SetNowPlaying(track);
                Engine.Play();
                SetStatus(skipped is null ? null : skipped + " Skipped.");
                return;
            }
            if (skipped is not null)
            {
                SetNowPlaying(null);
                SetStatus(skipped);
            }
        }
        finally { _busy = false; }
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

    public void Stop() => Engine.Stop();

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

    /// <summary>File dialog filter for opening or adding music and playlists.</summary>
    public const string OpenFilter =
        "Music files and playlists|*.mp3;*.flac;*.wav;*.wma;*.m4a;*.aac;*.m3u;*.m3u8|Playlists|*.m3u;*.m3u8|All files|*.*";

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
        NowPlaying = t;
        NowPlayingChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _tags?.Cancel();
        Engine.Dispose();
    }
}
