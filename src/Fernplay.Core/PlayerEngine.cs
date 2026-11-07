using Fernplay.Core.Audio;
using NAudio.Wave;

namespace Fernplay.Core;

public enum PlayState { Stopped, Playing, Paused }

/// <summary>
/// Plays tracks through one output that stays open: decoder(s) → mixer (one fixed format; gapless hand-over or
/// crossfade) → EQ → spectrum → volume → output. A loaded track cuts in at once; a queued one follows the playing
/// track gapless or with a crossfade. Events may arrive on the audio thread; the window marshals them to its own thread.
/// </summary>
public sealed class PlayerEngine : IDisposable
{
    /// <summary>Time allowed to open the next file before the fade (or the gapless join) must start.</summary>
    public static readonly TimeSpan OpenAhead = TimeSpan.FromSeconds(3);

    private readonly Func<string, ITrackDecoder> _openDecoder;
    private readonly Func<Task<IAudioOutput>> _createOutput;

    private IAudioOutput? _output;
    private TrackMixer? _mixer;
    private GainStage? _gain;
    private SpectrumTap? _spectrum;
    private bool _outputBroken;
    private volatile ITrackDecoder? _decoder;
    private volatile string? _currentPath;
    private ITrackDecoder? _queuedDecoder;
    private string? _queuedPath;
    private float _volume = 0.8f;
    private TimeSpan _crossfade;
    // Each load or unload gets a number; work started for an older one is dropped.
    private int _generation;

    public PlayerEngine(Func<string, ITrackDecoder> openDecoder, Func<Task<IAudioOutput>> createOutput)
    {
        _openDecoder = openDecoder;
        _createOutput = createOutput;
    }

    /// <summary>
    /// The real thing: Media Foundation in (files, and stations without song titles), the own ICY reader for MP3
    /// stations that send titles, WASAPI out.
    /// </summary>
    public static PlayerEngine CreateDefault() =>
        new(path => Track.IsStreamPath(path)
            ? (ITrackDecoder?)IcyMp3Decoder.TryOpen(path) ?? new MediaFoundationDecoder(path)
            : new MediaFoundationDecoder(path),
            WasapiAudioOutput.CreateAsync);

    public PlayState State { get; private set; }
    public string? CurrentPath => _currentPath;
    public AudioFormatInfo? Format => _decoder?.Format;
    public TimeSpan Duration => _decoder?.TotalTime ?? TimeSpan.Zero;
    public TimeSpan Position => _decoder?.CurrentTime ?? TimeSpan.Zero;
    public bool IsLoaded => _decoder is not null;
    /// <summary>False for a live stream (no length, no seeking).</summary>
    public bool CanSeek => Duration > TimeSpan.Zero;
    /// <summary>A station's own name, when it sends one.</summary>
    public string? StationName => (_decoder as ILiveStream)?.StationName;
    /// <summary>The song a station is playing now, when it says.</summary>
    public string? StreamTitle => (_decoder as ILiveStream)?.Title;
    /// <summary>The next track is opened and waiting to follow the playing one.</summary>
    public bool HasQueued => _mixer?.HasQueued == true;

    /// <summary>EQ settings; kept across tracks.</summary>
    public Equalizer Equalizer { get; } = new();

    /// <summary>
    /// Crossfade length for queued tracks (zero = gapless). Changing it changes how early <see cref="NearEnd"/> fires;
    /// a track already queued keeps the length it was queued with.
    /// </summary>
    public TimeSpan Crossfade
    {
        get => _crossfade;
        set
        {
            _crossfade = value < TimeSpan.Zero ? TimeSpan.Zero : value;
            if (_mixer is not null) _mixer.NearEndLead = _crossfade + OpenAhead;
        }
    }

    /// <summary>Spectrum levels 0..1 of what is playing (after the EQ, before the volume); all zero when nothing plays.</summary>
    public float[] GetSpectrum(int bands) =>
        _spectrum is { } s && State == PlayState.Playing ? s.GetLevels(bands) : new float[bands];

    /// <summary>0 to 1.</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_gain is not null) _gain.Volume = _volume;
        }
    }

    /// <summary>-1 (left) to 1 (right), 0 = centre; kept across tracks.</summary>
    public float Balance
    {
        get => _balance;
        set
        {
            _balance = Math.Clamp(value, -1f, 1f);
            if (_gain is not null) _gain.Balance = _balance;
        }
    }
    private float _balance;

    public event EventHandler? StateChanged;
    /// <summary>The track played to its end with nothing queued (not raised for Stop or a new Load).</summary>
    public event EventHandler? TrackEnded;
    public event EventHandler<Exception>? PlaybackFailed;
    /// <summary>The station's song changed (null: no song info). May come from the audio thread.</summary>
    public event EventHandler<string?>? StreamTitleChanged;
    /// <summary>
    /// The playing file is close to its end and nothing is queued: pick the next track and call
    /// <see cref="QueueNextAsync"/> with this number. Comes from the audio thread. Never for a station.
    /// </summary>
    public event EventHandler<int>? NearEnd;
    /// <summary>The queued track took over by itself (the path). From the audio thread.</summary>
    public event EventHandler<string>? TrackChanged;

    /// <summary>
    /// Opens a file or stream, ready to play; it cuts in at once (no fade), replacing anything queued. Throws when it
    /// cannot be decoded; the previous track is unloaded either way. Opening runs off the caller's thread: a radio
    /// stream takes a few seconds to connect. The output stays open from track to track.
    /// </summary>
    public async Task LoadAsync(string path)
    {
        int opening = ++_generation;
        Detach();
        var decoder = await Task.Run(() => _openDecoder(path));
        if (opening != _generation) { decoder.Dispose(); throw new OperationCanceledException("Another track was loaded meanwhile."); }
        try
        {
            if (_output is null || _outputBroken)
            {
                CloseOutput();
                var output = await _createOutput();
                if (opening != _generation) { output.Dispose(); throw new OperationCanceledException("Another track was loaded meanwhile."); }
                OpenChain(output, decoder.WaveFormat.SampleRate);
            }
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
        if (decoder is ILiveStream live)
            live.TitleChanged += (_, title) => { if (opening == _generation && _decoder == decoder) StreamTitleChanged?.Invoke(this, title); };
        _mixer!.SetCurrent(decoder);
        (_decoder, _currentPath) = (decoder, path);
        SetState(PlayState.Stopped);
    }

    /// <summary>
    /// Opens the track that should follow the playing one and queues it: crossfaded by <see cref="Crossfade"/>, or
    /// gapless when <paramref name="gapless"/> is set, the fade is off, or either track is shorter than twice the fade.
    /// <paramref name="forTrack"/> is the number <see cref="NearEnd"/> gave. False when the playing track changed meanwhile.
    /// Throws when the file cannot be opened (the playing track then simply ends).
    /// </summary>
    public async Task<bool> QueueNextAsync(string path, int forTrack, bool gapless = false)
    {
        var mixer = _mixer;
        var current = _decoder;
        if (mixer is null || current is null || Track.IsStreamPath(path) || !CanSeek) return false;
        int generation = _generation;
        var next = await Task.Run(() => _openDecoder(path));
        var fade = gapless ? TimeSpan.Zero : _crossfade;
        if (fade > TimeSpan.Zero && (next.TotalTime < 2 * fade || current.TotalTime < 2 * fade)) fade = TimeSpan.Zero;
        if (generation != _generation || mixer != _mixer || current != _decoder) { next.Dispose(); return false; }
        (_queuedDecoder, _queuedPath) = (next, path);
        if (mixer.Queue(next, fade, forTrack)) return true;
        (_queuedDecoder, _queuedPath) = (null, null);
        next.Dispose();
        return false;
    }

    /// <summary>Drops the queued track; <see cref="NearEnd"/> fires again if the end is close (to queue a new choice).</summary>
    public void CancelQueued()
    {
        _mixer?.CancelQueued();
        (_queuedDecoder, _queuedPath) = (null, null);
    }

    public void Play()
    {
        if (_output is null || _decoder is null || State == PlayState.Playing) return;
        if (State == PlayState.Stopped && _mixer!.ReachedEnd) Rewind();
        _output.Play();
        SetState(PlayState.Playing);
    }

    public void Pause()
    {
        if (_output is null || State != PlayState.Playing) return;
        _output.Pause();
        SetState(PlayState.Paused);
    }

    /// <summary>A track just loaded shows as paused at its position (no sound): Play continues from there.</summary>
    public void ShowPaused()
    {
        if (_decoder is not null && State == PlayState.Stopped) SetState(PlayState.Paused);
    }

    /// <summary>Stops and goes back to the start of the playing track; a fade in progress is cut off, a queued track stays.</summary>
    public void Stop()
    {
        if (_output is null || _decoder is null) return;
        _output.Stop();
        _mixer!.EndFade();
        Rewind();
        SetState(PlayState.Stopped);
    }

    public void Seek(TimeSpan position) => _mixer?.Seek(position);

    /// <summary>Lets go of everything, the output too.</summary>
    public void Unload()
    {
        _generation++;
        CloseOutput();
        (_decoder, _currentPath, _queuedDecoder, _queuedPath) = (null, null, null, null);
        SetState(PlayState.Stopped);
    }

    public void Dispose() => Unload();

    private void Rewind() => _mixer!.Seek(TimeSpan.Zero); // a no-op for a live stream

    // The output is kept; what it plays is dropped. Order matters: the mixer forgets the end first, so the output's
    // stop is not taken for a natural end.
    private void Detach()
    {
        _mixer?.SetCurrent(null);
        _output?.Stop();
        (_decoder, _currentPath, _queuedDecoder, _queuedPath) = (null, null, null, null);
        SetState(PlayState.Stopped);
    }

    private void OpenChain(IAudioOutput output, int sampleRate)
    {
        // mixer → EQ → spectrum tap → volume → output
        var mixer = new TrackMixer(sampleRate) { NearEndLead = _crossfade + OpenAhead };
        var spectrum = new SpectrumTap(new EqualizerStage(mixer, Equalizer));
        var gain = new GainStage(spectrum) { Volume = _volume, Balance = _balance };
        output.Init(gain);
        output.PlaybackStopped += (_, e) => OnStopped(output, e);
        mixer.NearEnd += (_, id) => { if (mixer == _mixer && _decoder is { } d && !Track.IsStreamPath(_currentPath ?? "") && d.TotalTime > TimeSpan.Zero) NearEnd?.Invoke(this, id); };
        mixer.Switched += (_, decoder) => OnSwitched(mixer, decoder);
        (_output, _mixer, _gain, _spectrum, _outputBroken) = (output, mixer, gain, spectrum, false);
    }

    private void CloseOutput()
    {
        var (output, mixer) = (_output, _mixer);
        (_output, _mixer, _gain, _spectrum) = (null, null, null, null);
        output?.Dispose();
        mixer?.SetCurrent(null);
    }

    private void OnSwitched(TrackMixer mixer, ITrackDecoder decoder)
    {
        if (mixer != _mixer || decoder != _queuedDecoder || _queuedPath is not { } path) return;
        (_decoder, _currentPath, _queuedDecoder, _queuedPath) = (decoder, path, null, null);
        TrackChanged?.Invoke(this, path);
    }

    private void OnStopped(IAudioOutput output, StoppedEventArgs e)
    {
        if (output != _output) return;
        if (e.Exception is not null)
        {
            _outputBroken = true; // the next load opens a new one
            SetState(PlayState.Stopped);
            PlaybackFailed?.Invoke(this, e.Exception);
        }
        else if (_mixer?.ReachedEnd == true)
        {
            SetState(PlayState.Stopped);
            TrackEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetState(PlayState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
