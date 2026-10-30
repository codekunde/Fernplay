using Fernplay.Core.Audio;
using NAudio.Wave;

namespace Fernplay.Core;

public enum PlayState { Stopped, Playing, Paused }

/// <summary>
/// Plays one track at a time through the chain decoder → EQ → volume → output.
/// Events may arrive on the audio thread; the window marshals them to its own thread.
/// </summary>
public sealed class PlayerEngine : IDisposable
{
    private readonly Func<string, ITrackDecoder> _openDecoder;
    private readonly Func<Task<IAudioOutput>> _createOutput;

    private ITrackDecoder? _decoder;
    private IAudioOutput? _output;
    private GainStage? _gain;
    private EndWatchStage? _end;
    private SpectrumTap? _spectrum;
    private float _volume = 0.8f;
    // Each loaded track gets a number; a PlaybackStopped from an older output is ignored.
    private int _generation;

    public PlayerEngine(Func<string, ITrackDecoder> openDecoder, Func<Task<IAudioOutput>> createOutput)
    {
        _openDecoder = openDecoder;
        _createOutput = createOutput;
    }

    /// <summary>The real thing: Media Foundation in, WASAPI out.</summary>
    public static PlayerEngine CreateDefault() =>
        new(path => new MediaFoundationDecoder(path), WasapiAudioOutput.CreateAsync);

    public PlayState State { get; private set; }
    public string? CurrentPath { get; private set; }
    public AudioFormatInfo? Format => _decoder?.Format;
    public TimeSpan Duration => _decoder?.TotalTime ?? TimeSpan.Zero;
    public TimeSpan Position => _decoder?.CurrentTime ?? TimeSpan.Zero;
    public bool IsLoaded => _decoder is not null;

    /// <summary>EQ settings; kept across tracks.</summary>
    public Equalizer Equalizer { get; } = new();

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

    public event EventHandler? StateChanged;
    /// <summary>The track played to its end (not raised for Stop or a new Load).</summary>
    public event EventHandler? TrackEnded;
    public event EventHandler<Exception>? PlaybackFailed;

    /// <summary>Opens a file, ready to play. Throws when the file cannot be decoded; the previous track is unloaded either way.</summary>
    public async Task LoadAsync(string path)
    {
        Unload();
        var decoder = _openDecoder(path);
        try
        {
            var output = await _createOutput();
            // decoder → EQ → spectrum tap → volume → end watch → output
            var spectrum = new SpectrumTap(new EqualizerStage(decoder, Equalizer));
            var gain = new GainStage(spectrum) { Volume = _volume };
            var end = new EndWatchStage(gain);
            output.Init(end);
            int generation = ++_generation;
            output.PlaybackStopped += (_, e) => OnStopped(generation, e);
            (_decoder, _output, _gain, _end, _spectrum) = (decoder, output, gain, end, spectrum);
            CurrentPath = path;
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
        SetState(PlayState.Stopped);
    }

    public void Play()
    {
        if (_output is null || State == PlayState.Playing) return;
        if (State == PlayState.Stopped && _end!.ReachedEnd) Rewind();
        _output.Play();
        SetState(PlayState.Playing);
    }

    public void Pause()
    {
        if (_output is null || State != PlayState.Playing) return;
        _output.Pause();
        SetState(PlayState.Paused);
    }

    public void Stop()
    {
        if (_output is null) return;
        _output.Stop();
        Rewind();
        SetState(PlayState.Stopped);
    }

    public void Seek(TimeSpan position)
    {
        if (_decoder is null) return;
        _decoder.Seek(position);
        _end!.Reset();
    }

    public void Unload()
    {
        _generation++;
        _output?.Dispose();
        _decoder?.Dispose();
        (_decoder, _output, _gain, _end, _spectrum) = (null, null, null, null, null);
        CurrentPath = null;
        SetState(PlayState.Stopped);
    }

    public void Dispose() => Unload();

    private void Rewind()
    {
        _decoder!.Seek(TimeSpan.Zero);
        _end!.Reset();
    }

    private void OnStopped(int generation, StoppedEventArgs e)
    {
        if (generation != _generation) return;
        if (e.Exception is not null)
        {
            SetState(PlayState.Stopped);
            PlaybackFailed?.Invoke(this, e.Exception);
        }
        else if (_end?.ReachedEnd == true)
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
