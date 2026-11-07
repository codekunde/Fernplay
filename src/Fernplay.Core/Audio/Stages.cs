using NAudio.Wave;

namespace Fernplay.Core.Audio;

/// <summary>Player volume and balance as a software gain, so moving them never changes the Windows mixer.</summary>
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

    /// <summary>-1 (left only) to 1 (right only); 0 = centre. The far side is turned down, the near side stays. Stereo only.</summary>
    public float Balance
    {
        get => _balance;
        set => _balance = Math.Clamp(value, -1f, 1f);
    }
    private float _balance;

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(Span<float> buffer)
    {
        int n = source.Read(buffer);
        float g = _gain, b = _balance;
        if (b != 0f && WaveFormat.Channels == 2)
        {
            var (left, right) = BalanceGains(b);
            left *= g; right *= g;
            for (int i = 0; i + 1 < n; i += 2) { buffer[i] *= left; buffer[i + 1] *= right; }
        }
        else if (g != 1f)
            for (int i = 0; i < n; i++) buffer[i] *= g;
        return n;
    }

    internal static float ToGain(float volume) => volume <= 0f ? 0f : volume * volume;

    /// <summary>Left and right gain for a balance: the side it moves away from fades linearly to silence.</summary>
    internal static (float Left, float Right) BalanceGains(float balance) =>
        balance >= 0 ? (1f - balance, 1f) : (1f, 1f + balance);
}
