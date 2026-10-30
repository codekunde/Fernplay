using NAudio.Wave;

namespace Fernplay.Core.Audio;

/// <summary>Player volume as a software gain, so moving it never changes the Windows mixer.</summary>
public sealed class GainStage(ISampleProvider source) : ISampleProvider
{
    private float _gain = 1f;

    /// <summary>0 to 1, applied on a curve so the slider feels even (half way is about -12 dB, not -6 dB).</summary>
    public float Volume
    {
        get => _volume;
        set { _volume = Math.Clamp(value, 0f, 1f); _gain = ToGain(_volume); }
    }
    private float _volume = 1f;

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(Span<float> buffer)
    {
        int n = source.Read(buffer);
        float g = _gain;
        if (g != 1f)
            for (int i = 0; i < n; i++) buffer[i] *= g;
        return n;
    }

    internal static float ToGain(float volume) => volume <= 0f ? 0f : volume * volume;
}

/// <summary>Remembers whether the source ran out, so a natural end can be told apart from Stop.</summary>
public sealed class EndWatchStage(ISampleProvider source) : ISampleProvider
{
    private volatile bool _reachedEnd;
    public bool ReachedEnd => _reachedEnd;
    public void Reset() => _reachedEnd = false;
    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(Span<float> buffer)
    {
        int n = source.Read(buffer);
        if (n == 0 && buffer.Length > 0) _reachedEnd = true;
        return n;
    }
}
