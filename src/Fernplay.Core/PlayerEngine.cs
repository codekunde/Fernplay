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
    private readonly Func<string?, Task<IAudioOutput>> _createOutput;
    private readonly Func<string, ReplayGain?> _readGain;
    private string? _outputDevice;
    private int _outputSwitch; // each change of output gets a number; an older one still opening is dropped
    private ReplayGain? _currentGain, _queuedGain;
    private LevelMode _levelling;

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

    /// <param name="readGain">Reads a file's ReplayGain tags (null: none); not called for stations. Default: no tags.</param>
    public PlayerEngine(Func<string, ITrackDecoder> openDecoder, Func<Task<IAudioOutput>> createOutput, Func<string, ReplayGain?>? readGain = null)
        : this(openDecoder, _ => createOutput(), readGain) { }

    /// <param name="createOutput">Opens an output on a playback device by its ID (null: the Windows default).</param>
    public PlayerEngine(Func<string, ITrackDecoder> openDecoder, Func<string?, Task<IAudioOutput>> createOutput, Func<string, ReplayGain?>? readGain = null)
    {
        _openDecoder = openDecoder;
        _createOutput = createOutput;
        _readGain = readGain ?? (_ => null);
    }

    /// <summary>
    /// The real thing: Media Foundation in (files, and stations without song titles), the own ICY reader for MP3
    /// stations that send titles, WASAPI out. <paramref name="isEpisode"/> picks out web addresses of podcast episodes
    /// (audio files, not stations): they go straight to Media Foundation, which seeks in them, and skip the ICY probe.
    /// </summary>
    public static PlayerEngine CreateDefault(Func<string, bool>? isEpisode = null) =>
        new(path => Track.IsStreamPath(path) && isEpisode?.Invoke(path) != true
            ? (ITrackDecoder?)IcyMp3Decoder.TryOpen(path) ?? new MediaFoundationDecoder(path)
            : new MediaFoundationDecoder(path),
            device => WasapiAudioOutput.CreateAsync(device),
            ReplayGainReader.TryRead);

    /// <summary>The playback device outputs open on (null: the Windows default, following changes).</summary>
    public string? OutputDevice => _outputDevice;

    /// <summary>The device the open output is fixed to; null when it follows the Windows default or none is open.</summary>
    public string? ActiveOutputDevice => _output?.DeviceId;

    /// <summary>
    /// Plays on this device from now on (null: the Windows default). With an output open, a new one is opened on the
    /// device and takes over the chain at once: the track keeps its place and its playing or paused state (a gap of
    /// about the output's buffer). Nothing happens when the output is on that device already. The caller checks that
    /// the device is connected; one that cannot be opened gives the default (see the output factory).
    /// </summary>
    public async Task SetOutputDeviceAsync(string? deviceId)
    {
        _outputDevice = deviceId;
        if (_output is not { } current || _outputBroken || current.DeviceId == deviceId) return;
        await SwitchOutputAsync(current, deviceId, currentFailed: false);
    }

    /// <summary>The move to the default after a chosen device was lost, while it runs (tests wait for it).</summary>
    internal Task OutputRecovery { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Volume levelling by ReplayGain tags; changes the playing and queued tracks at once. Untagged files and stations
    /// play at <see cref="ReplayGain.UntaggedDb"/> while it is on.
    /// </summary>
    public LevelMode Levelling
    {
        get => _levelling;
        set
        {
            _levelling = value;
            if (_mixer is not { } mixer) return;
            if (_decoder is { } d) mixer.SetGain(d, ReplayGain.GainFor(_currentGain, value));
            if (_queuedDecoder is { } q) mixer.SetGain(q, ReplayGain.GainFor(_queuedGain, value));
        }
    }

    /// <summary>The playing file's ReplayGain tags (null: none, or a station).</summary>
    public ReplayGain? CurrentGainTags => _currentGain;

    private (ITrackDecoder Decoder, ReplayGain? Gain) Open(string path) =>
        (_openDecoder(path), Track.IsStreamPath(path) ? null : _readGain(path));

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

    /// <summary>
    /// What is playing, in stereo, for the visualizer (after the EQ, before the volume, like the spectrum). It keeps
    /// samples only while the visualizer has it switched on.
    /// </summary>
    public PcmRing VisualizerPcm { get; } = new();

    /// <summary>Spectrum levels 0..1 of what is playing (after the EQ, before the volume); all zero when nothing plays.</summary>
    public float[] GetSpectrum(int bands) =>
        _spectrum is { } s && State == PlayState.Playing ? s.GetLevels(bands) : new float[bands];

    /// <summary>
    /// The latest stereo samples (oldest first) for the oscilloscope and the vectorscope, after the EQ and before the
    /// volume. False (and the spans untouched) when nothing plays, also while paused.
    /// </summary>
    public bool GetWave(Span<float> left, Span<float> right)
    {
        if (_spectrum is not { } s || State != PlayState.Playing) return false;
        s.CopyWave(left, right);
        return true;
    }

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
        long started = Environment.TickCount64;
        var (decoder, tags) = await Task.Run(() => Open(path));
        if (opening != _generation) { decoder.Dispose(); throw new OperationCanceledException("Another track was loaded meanwhile."); }
        DiagnosticLog.Write($"opened {DiagnosticLog.Name(path)} in {Environment.TickCount64 - started} ms: {Describe(decoder)}{(tags is null ? "" : ", ReplayGain tags")}");
        try
        {
            if (_output is null || _outputBroken)
            {
                DiagnosticLog.Write(_outputBroken ? "audio output: opening a new one after an error" : "audio output: opening");
                CloseOutput();
                var output = await _createOutput(_outputDevice);
                if (opening != _generation) { output.Dispose(); throw new OperationCanceledException("Another track was loaded meanwhile."); }
                OpenChain(output, decoder.WaveFormat.SampleRate);
            }
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException) DiagnosticLog.Write($"audio output could not be opened: {PlaybackErrors.Detail(ex)}");
            decoder.Dispose();
            throw;
        }
        if (decoder is ILiveStream live)
            live.TitleChanged += (_, title) => { if (opening == _generation && _decoder == decoder) StreamTitleChanged?.Invoke(this, title); };
        _mixer!.SetCurrent(decoder, ReplayGain.GainFor(tags, _levelling));
        (_decoder, _currentPath, _currentGain) = (decoder, path, tags);
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
        var (next, tags) = await Task.Run(() => Open(path));
        var fade = gapless ? TimeSpan.Zero : _crossfade;
        if (fade > TimeSpan.Zero && (next.TotalTime < 2 * fade || current.TotalTime < 2 * fade)) fade = TimeSpan.Zero;
        if (generation != _generation || mixer != _mixer || current != _decoder) { next.Dispose(); return false; }
        (_queuedDecoder, _queuedPath, _queuedGain) = (next, path, tags);
        if (mixer.Queue(next, fade, forTrack, ReplayGain.GainFor(tags, _levelling))) return true;
        (_queuedDecoder, _queuedPath, _queuedGain) = (null, null, null);
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
        (_currentGain, _queuedGain) = (null, null);
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
        (_currentGain, _queuedGain) = (null, null);
        SetState(PlayState.Stopped);
    }

    private void OpenChain(IAudioOutput output, int sampleRate)
    {
        // mixer → EQ → spectrum tap → volume → output
        var mixer = new TrackMixer(sampleRate) { NearEndLead = _crossfade + OpenAhead };
        var spectrum = new SpectrumTap(new EqualizerStage(mixer, Equalizer), VisualizerPcm);
        var gain = new GainStage(spectrum) { Volume = _volume, Balance = _balance };
        output.Init(gain);
        output.PlaybackStopped += (_, e) => OnStopped(output, e);
        _outputSwitch++; // a switch still opening its output was meant for the old chain
        mixer.NearEnd += (_, id) => { if (mixer == _mixer && _decoder is { } d && !Track.IsStreamPath(_currentPath ?? "") && d.TotalTime > TimeSpan.Zero) NearEnd?.Invoke(this, id); };
        mixer.Switched += (_, decoder) => OnSwitched(mixer, decoder);
        (_output, _mixer, _gain, _spectrum, _outputBroken) = (output, mixer, gain, spectrum, false);
    }

    /// <summary>
    /// Opens an output on <paramref name="deviceId"/> and moves the chain (mixer → … → volume) over from
    /// <paramref name="old"/>: the decoders keep their place, so the track goes on where it was. False when it was
    /// dropped (a newer switch or load came first, or the device gave the same output as before).
    /// </summary>
    private async Task<bool> SwitchOutputAsync(IAudioOutput old, string? deviceId, bool currentFailed)
    {
        int switching = ++_outputSwitch;
        long started = Environment.TickCount64;
        IAudioOutput next;
        try { next = await _createOutput(deviceId); }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"audio output: could not switch device: {PlaybackErrors.Detail(ex)}");
            return false;
        }
        if (switching != _outputSwitch || old != _output || _gain is not { } source || (!currentFailed && next.DeviceId == old.DeviceId))
        {
            next.Dispose();
            return false;
        }
        bool play = State == PlayState.Playing;
        _output = next; // from here the old output's events (its stop, a late error) are ignored
        next.PlaybackStopped += (_, e) => OnStopped(next, e);
        try { old.Dispose(); } // stops it and waits for its audio thread: never two threads reading the chain
        catch (Exception ex) { DiagnosticLog.Write($"audio output: closing the old one failed: {PlaybackErrors.Detail(ex)}"); }
        try
        {
            next.Init(source);
            if (play) next.Play();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"audio output: the new device failed to start: {PlaybackErrors.Detail(ex)}");
            _outputBroken = true; // the next load opens a new one
            SetState(PlayState.Stopped);
            PlaybackFailed?.Invoke(this, ex);
            return false;
        }
        _outputBroken = false;
        DiagnosticLog.Write($"audio output: moved to {(next.DeviceId is null ? "the Windows default" : "the chosen device")} in {Environment.TickCount64 - started} ms at {Position:m\\:ss\\.f}, {State}");
        return true;
    }

    /// <summary>A chosen device was lost (unplugged): go on on the Windows default without stopping, if it opens.</summary>
    private async Task RecoverOnDefaultAsync(IAudioOutput lost, Exception error)
    {
        await Task.Yield(); // off the output's own stop event before it is closed
        if (lost != _output) return;
        if (await SwitchOutputAsync(lost, null, currentFailed: true) || lost != _output) return;
        DiagnosticLog.Write("audio output: the Windows default could not take over either");
        _outputBroken = true;
        SetState(PlayState.Stopped);
        PlaybackFailed?.Invoke(this, error);
    }

    private void CloseOutput()
    {
        var (output, mixer) = (_output, _mixer);
        (_output, _mixer, _gain, _spectrum) = (null, null, null, null);
        output?.Dispose();
        mixer?.SetCurrent(null);
    }

    /// <summary>"MediaFoundationDecoder, 44100 Hz 2 ch, 3:41" for the log.</summary>
    private static string Describe(ITrackDecoder d)
    {
        var f = d.WaveFormat;
        string length = d.TotalTime > TimeSpan.Zero ? $", {d.TotalTime:h\\:mm\\:ss}" : ", live";
        return $"{d.GetType().Name}, {f.SampleRate} Hz {f.Channels} ch{length}";
    }

    private void OnSwitched(TrackMixer mixer, ITrackDecoder decoder)
    {
        if (mixer != _mixer || decoder != _queuedDecoder || _queuedPath is not { } path) return;
        (_decoder, _currentPath, _currentGain, _queuedDecoder, _queuedPath, _queuedGain) = (decoder, path, _queuedGain, null, null, null);
        TrackChanged?.Invoke(this, path);
    }

    private void OnStopped(IAudioOutput output, StoppedEventArgs e)
    {
        if (output != _output) return;
        if (e.Exception is not null && output.DeviceId is not null)
        {
            DiagnosticLog.Write($"audio output: the chosen device stopped with an error (unplugged?), going on on the Windows default: {PlaybackErrors.Detail(e.Exception)}");
            OutputRecovery = RecoverOnDefaultAsync(output, e.Exception);
        }
        else if (e.Exception is not null)
        {
            DiagnosticLog.Write($"audio output stopped with an error (device gone or changed?): {PlaybackErrors.Detail(e.Exception)}");
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
