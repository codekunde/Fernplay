namespace Fernplay.Core.Audio;

/// <summary>
/// The last moments of what is playing, as stereo samples (LRLR), for the visualizer. The audio thread writes,
/// the visualizer's render thread takes. Nothing is kept while <see cref="Enabled"/> is off, so it costs nothing
/// when the visualizer is closed.
/// </summary>
public sealed class PcmRing
{
    private readonly float[] _ring;
    private int _write, _count;
    private readonly Lock _gate = new();
    private volatile bool _enabled;

    /// <param name="capacityFrames">Stereo frames kept (older ones are dropped); one second at 48 kHz by default.</param>
    public PcmRing(int capacityFrames = 48000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, 1);
        _ring = new float[capacityFrames * 2];
    }

    /// <summary>Off: writes are ignored. Turning it off also forgets what was kept.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value) lock (_gate) _count = 0;
        }
    }

    public int CapacityFrames => _ring.Length / 2;

    /// <summary>
    /// Adds interleaved samples with <paramref name="channels"/> channels: mono goes to both sides; more than two are
    /// averaged to stereo, channels 0, 2, 4… to the left and 1, 3, 5… to the right. A trailing partial frame is dropped.
    /// </summary>
    public void Write(ReadOnlySpan<float> samples, int channels)
    {
        if (!_enabled || channels < 1) return;
        lock (_gate)
        {
            for (int i = 0; i + channels <= samples.Length; i += channels)
            {
                float l, r;
                if (channels == 1) l = r = samples[i];
                else if (channels == 2) (l, r) = (samples[i], samples[i + 1]);
                else
                {
                    float sl = 0, sr = 0;
                    int nl = 0, nr = 0;
                    for (int c = 0; c < channels; c++)
                        if (c % 2 == 0) { sl += samples[i + c]; nl++; } else { sr += samples[i + c]; nr++; }
                    (l, r) = (sl / nl, sr / nr);
                }
                _ring[_write] = l;
                _ring[_write + 1] = r;
                _write = (_write + 2) % _ring.Length;
                if (_count < _ring.Length) _count += 2;
            }
        }
    }

    /// <summary>Takes everything written since the last call, oldest first (LRLR); empty when nothing came.</summary>
    public float[] Drain()
    {
        lock (_gate)
        {
            if (_count == 0) return [];
            var data = new float[_count];
            int start = (_write - _count + _ring.Length) % _ring.Length;
            int first = Math.Min(_count, _ring.Length - start);
            Array.Copy(_ring, start, data, 0, first);
            Array.Copy(_ring, 0, data, first, _count - first);
            _count = 0;
            return data;
        }
    }
}
