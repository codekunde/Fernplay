using NAudio.Wave;

namespace Fernplay.Core.Audio;

/// <summary>
/// The 10-band graphic EQ settings: band gains and preamp in dB (±12), on/off.
/// Shared by the window (writes) and <see cref="EqualizerStage"/> on the audio thread (reads).
/// </summary>
public sealed class Equalizer
{
    /// <summary>The classic band centres, in Hz.</summary>
    public static readonly IReadOnlyList<double> BandFrequencies = [60, 170, 310, 600, 1000, 3000, 6000, 12000, 14000, 16000];
    public const double MaxDb = 12;

    private readonly double[] _gains = new double[10];
    private double _preamp;
    private bool _enabled = true;
    private int _version;

    public event EventHandler? Changed;

    /// <summary>Goes up on every change; the audio thread compares it to know when to recompute filters.</summary>
    public int Version => Volatile.Read(ref _version);

    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled != value) { _enabled = value; Bump(); } }
    }

    public double Preamp
    {
        get => _preamp;
        set { var v = Clamp(value); if (v != _preamp) { _preamp = v; Bump(); } }
    }

    public double GetBand(int index) => _gains[index];

    public void SetBand(int index, double db)
    {
        var v = Clamp(db);
        if (_gains[index] == v) return;
        _gains[index] = v;
        Bump();
    }

    public double[] Bands => (double[])_gains.Clone();

    public void Apply(EqPreset preset)
    {
        for (int i = 0; i < _gains.Length; i++) _gains[i] = Clamp(preset.Bands[i]);
        _preamp = Clamp(preset.Preamp);
        Bump();
    }

    private static double Clamp(double db) => Math.Round(Math.Clamp(db, -MaxDb, MaxDb), 1);

    private void Bump()
    {
        Interlocked.Increment(ref _version);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record EqPreset(string Name, double Preamp, double[] Bands)
{
    public static readonly IReadOnlyList<EqPreset> BuiltIn =
    [
        new("Flat", 0, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
        new("Rock", -2, [5, 3, -2, -4, -1, 2, 5, 6, 6, 6]),
        new("Pop", -1, [-1, 2, 4, 5, 3, 0, -1, -1, -1, -1]),
        new("Classical", 0, [0, 0, 0, 0, 0, 0, -4, -4, -4, -6]),
        new("Dance", -2, [7, 5, 2, 0, 0, -3, -4, -4, 0, 0]),
        new("Bass boost", -4, [8, 6, 4, 1, 0, 0, 0, 0, 0, 0]),
        new("Treble boost", -4, [0, 0, 0, 0, 0, 2, 5, 7, 8, 8]),
        new("Vocal", -2, [-3, -3, -1, 2, 4, 4, 2, 0, -1, -2]),
    ];
}

/// <summary>The user's own EQ presets: kept in the settings, listed above the built-in ones.</summary>
public static class UserEqPresets
{
    public const int MaxNameLength = 30;

    /// <summary>The EQ as it is now, under a name.</summary>
    public static EqPreset FromCurrent(string name, Equalizer eq) => new(name.Trim(), eq.Preamp, eq.Bands);

    /// <summary>Why a name cannot be used, or null when it can. A name already among the user's presets is fine (it replaces).</summary>
    public static string? NameProblem(string name)
    {
        var n = name.Trim();
        if (n.Length == 0) return "Type a name for the preset.";
        if (n.Length > MaxNameLength) return $"Keep the name to {MaxNameLength} characters.";
        if (EqPreset.BuiltIn.Any(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)))
            return $"{n} is a built-in preset. Pick another name.";
        return null;
    }

    /// <summary>Adds the preset, or replaces the one with the same name (any case) in its place. True when it replaced one.</summary>
    public static bool Save(List<EqPreset> list, EqPreset preset)
    {
        int at = list.FindIndex(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) { list[at] = preset; return true; }
        list.Add(preset);
        return false;
    }

    public static bool Delete(List<EqPreset> list, string name) =>
        list.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>Drops presets a damaged or hand-edited settings file could hold: no name, or not ten bands.</summary>
    public static List<EqPreset> Clean(IEnumerable<EqPreset>? list) =>
        (list ?? []).Where(p => p is { Name: { Length: > 0 } } && p.Bands is { Length: 10 } && p.Name.Trim().Length > 0)
            .DistinctBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>
/// Middle of the chain (decoder → EQ → output): one peaking biquad per band and channel (RBJ cookbook),
/// plus the preamp. Filter memory is kept when settings change, so moving a slider does not click.
/// </summary>
public sealed class EqualizerStage : ISampleProvider
{
    private const double Q = 1.4;
    private readonly ISampleProvider _source;
    private readonly Equalizer? _eq;
    private readonly int _channels;
    private readonly double _rate;
    private int _seenVersion = -1;
    private Coefficients[] _coef = [];  // only bands that do something
    private float _preampGain = 1f;
    private bool _active;
    // State per band and channel: x1, x2, y1, y2.
    private readonly double[,,] _state;

    public EqualizerStage(ISampleProvider source, Equalizer? eq = null)
    {
        _source = source;
        _eq = eq;
        _channels = source.WaveFormat.Channels;
        _rate = source.WaveFormat.SampleRate;
        _state = new double[Equalizer.BandFrequencies.Count, _channels, 4];
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(Span<float> buffer)
    {
        int n = _source.Read(buffer);
        if (_eq is null) return n;
        if (_eq.Version != _seenVersion) Rebuild();
        if (!_active) return n;

        var coef = _coef;
        float pre = _preampGain;
        for (int i = 0; i < n; i++)
        {
            int ch = i % _channels;
            double x = buffer[i] * pre;
            for (int b = 0; b < coef.Length; b++)
            {
                ref readonly var c = ref coef[b];
                int s = c.Slot;
                double y = c.B0 * x + c.B1 * _state[s, ch, 0] + c.B2 * _state[s, ch, 1] - c.A1 * _state[s, ch, 2] - c.A2 * _state[s, ch, 3];
                _state[s, ch, 1] = _state[s, ch, 0]; _state[s, ch, 0] = x;
                _state[s, ch, 3] = _state[s, ch, 2]; _state[s, ch, 2] = y;
                x = y;
            }
            buffer[i] = (float)x;
        }
        return n;
    }

    private void Rebuild()
    {
        _seenVersion = _eq!.Version;
        var list = new List<Coefficients>();
        if (_eq.Enabled)
            for (int b = 0; b < Equalizer.BandFrequencies.Count; b++)
            {
                double db = _eq.GetBand(b), f = Equalizer.BandFrequencies[b];
                // A band at 0 dB, or too close to Nyquist for this sample rate, does nothing.
                if (db == 0 || f >= _rate * 0.45) continue;
                list.Add(Peaking(b, f, db));
            }
        _preampGain = _eq.Enabled ? (float)Math.Pow(10, _eq.Preamp / 20) : 1f;
        _coef = [.. list];
        _active = _coef.Length > 0 || _preampGain != 1f;
    }

    private Coefficients Peaking(int slot, double f, double db)
    {
        double a = Math.Pow(10, db / 40), w0 = 2 * Math.PI * f / _rate, alpha = Math.Sin(w0) / (2 * Q), cos = Math.Cos(w0);
        double a0 = 1 + alpha / a;
        return new Coefficients(slot, (1 + alpha * a) / a0, -2 * cos / a0, (1 - alpha * a) / a0, -2 * cos / a0, (1 - alpha / a) / a0);
    }

    private readonly record struct Coefficients(int Slot, double B0, double B1, double B2, double A1, double A2);
}
