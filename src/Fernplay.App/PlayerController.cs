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
        var files = await Task.Run(() => Playlist.ExpandPaths(paths).ToList());
        if (files.Count == 0)
        {
            var rejected = paths.FirstOrDefault(p => System.IO.File.Exists(p) && !Playlist.IsSupported(p));
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
