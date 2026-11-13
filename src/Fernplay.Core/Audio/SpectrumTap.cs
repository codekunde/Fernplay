using NAudio.Dsp;
using NAudio.Wave;

namespace Fernplay.Core.Audio;

/// <summary>
/// Keeps the most recent samples (mixed to mono) so the window can draw a spectrum analyser, and hands them in
/// stereo to <see cref="Pcm"/> for the visualizer. Passes audio through unchanged.
/// </summary>
public sealed class SpectrumTap(ISampleProvider source, PcmRing? pcm = null) : ISampleProvider
{
    /// <summary>The visualizer's samples (it keeps nothing while switched off).</summary>
    public PcmRing? Pcm { get; } = pcm;

    private const int Size = 1024; // FFT length (power of two)
    private readonly float[] _ring = new float[Size];
    private int _pos;
    private readonly Lock _gate = new();

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(Span<float> buffer)
    {
        int n = source.Read(buffer);
        int ch = WaveFormat.Channels;
        Pcm?.Write(buffer[..n], ch);
        lock (_gate)
        {
            for (int i = 0; i + ch <= n; i += ch)
            {
                float sum = 0;
                for (int c = 0; c < ch; c++) sum += buffer[i + c];
                _ring[_pos] = sum / ch;
                _pos = (_pos + 1) % Size;
            }
        }
        return n;
    }

    public void Clear()
    {
        lock (_gate) { Array.Clear(_ring); _pos = 0; }
    }

    /// <summary>Levels 0..1 for <paramref name="bands"/> log-spaced bands from 40 Hz to 16 kHz.</summary>
    public float[] GetLevels(int bands) => Analyse(Snapshot(), WaveFormat.SampleRate, bands);

    private float[] Snapshot()
    {
        var data = new float[Size];
        lock (_gate)
            for (int i = 0; i < Size; i++) data[i] = _ring[(_pos + i) % Size];
        return data;
    }

    internal static float[] Analyse(float[] samples, int sampleRate, int bands)
    {
        int m = (int)Math.Log2(samples.Length);
        var fft = new Complex[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            fft[i].X = (float)(samples[i] * FastFourierTransform.HannWindow(i, samples.Length));
            fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, m, fft);

        var levels = new float[bands];
        double lo = 40, hi = Math.Min(16000, sampleRate / 2.0);
        double binHz = (double)sampleRate / samples.Length;
        for (int b = 0; b < bands; b++)
        {
            double f0 = lo * Math.Pow(hi / lo, (double)b / bands), f1 = lo * Math.Pow(hi / lo, (double)(b + 1) / bands);
            int k0 = Math.Max(1, (int)(f0 / binHz)), k1 = Math.Max(k0 + 1, (int)Math.Ceiling(f1 / binHz));
            double peak = 0;
            for (int k = k0; k < k1 && k < samples.Length / 2; k++)
                peak = Math.Max(peak, Math.Sqrt(fft[k].X * fft[k].X + fft[k].Y * fft[k].Y));
            // NAudio's FFT scales by 1/N; a full-scale sine lands near 0.25 after the Hann window. Show 60 dB of range.
            double db = 20 * Math.Log10(peak * 4 + 1e-9);
            levels[b] = (float)Math.Clamp((db + 60) / 60, 0, 1);
        }
        return levels;
    }
}
