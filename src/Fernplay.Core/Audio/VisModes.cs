namespace Fernplay.Core.Audio;

/// <summary>What the player's small display area shows (a left-click steps through them, as on Winamp 2.x).</summary>
public enum VisMode { Bars, Oscilloscope, Vectorscope, Off }

/// <summary>Spectrum bar width: thick (3 px with a gap on a classic skin) or thin (1 px, Winamp's "thin bands").</summary>
public enum BarStyle { Thick, Thin }

/// <summary>How fast bars or peak caps come down.</summary>
public enum Falloff { Slow, Normal, Fast }

/// <summary>Oscilloscope drawing: one dot per column, or columns joined into a line.</summary>
public enum ScopeStyle { Dots, Lines }

/// <summary>The display area's choices, kept in settings.json.</summary>
public sealed class VisSettings
{
    public VisMode Mode { get; set; } = VisMode.Bars;
    public BarStyle Bars { get; set; } = BarStyle.Thick;
    public bool Peaks { get; set; } = true;
    public Falloff BarFalloff { get; set; } = Falloff.Normal;
    public Falloff PeakFalloff { get; set; } = Falloff.Normal;
    public ScopeStyle Scope { get; set; } = ScopeStyle.Lines;

    /// <summary>Values out of range (a hand-edited file) go back to the defaults.</summary>
    public VisSettings Clean()
    {
        if (!Enum.IsDefined(Mode)) Mode = VisMode.Bars;
        if (!Enum.IsDefined(Bars)) Bars = BarStyle.Thick;
        if (!Enum.IsDefined(BarFalloff)) BarFalloff = Falloff.Normal;
        if (!Enum.IsDefined(PeakFalloff)) PeakFalloff = Falloff.Normal;
        if (!Enum.IsDefined(Scope)) Scope = ScopeStyle.Lines;
        return this;
    }
}

/// <summary>The click cycle, per look.</summary>
public static class VisCycle
{
    /// <summary>
    /// The mode after a click: bars → oscilloscope → (vectorscope, Fernplay's look only) → off → bars. A classic skin's
    /// area is too small for the vectorscope, so its cycle leaves it out.
    /// </summary>
    public static VisMode Next(VisMode current, bool hasVectorscope) => Shown(current, hasVectorscope) switch
    {
        VisMode.Bars => VisMode.Oscilloscope,
        VisMode.Oscilloscope => hasVectorscope ? VisMode.Vectorscope : VisMode.Off,
        VisMode.Vectorscope => VisMode.Off,
        _ => VisMode.Bars,
    };

    /// <summary>The mode a look shows for the saved one: a classic skin shows the oscilloscope for a saved vectorscope.</summary>
    public static VisMode Shown(VisMode saved, bool hasVectorscope) =>
        saved == VisMode.Vectorscope && !hasVectorscope ? VisMode.Oscilloscope : saved;

    /// <summary>The modes a look offers, in click order.</summary>
    public static IReadOnlyList<VisMode> Modes(bool hasVectorscope) =>
        hasVectorscope ? [VisMode.Bars, VisMode.Oscilloscope, VisMode.Vectorscope, VisMode.Off] : [VisMode.Bars, VisMode.Oscilloscope, VisMode.Off];
}

/// <summary>
/// Spectrum bars and peak caps that rise at once and fall at the chosen speed (per frame of the ~30 fps display timer).
/// Arrays are kept between frames; they are only made again when the number of bands changes.
/// </summary>
public sealed class SpectrumSmoother
{
    private float[] _levels = [], _peaks = [];

    public int Count => _levels.Length;
    public ReadOnlySpan<float> Levels => _levels;
    public ReadOnlySpan<float> Peaks => _peaks;

    /// <summary>Height lost per frame (0..1 of the area).</summary>
    public static float BarStep(Falloff f) => f switch { Falloff.Slow => 0.04f, Falloff.Fast => 0.16f, _ => 0.08f };
    public static float PeakStep(Falloff f) => f switch { Falloff.Slow => 0.007f, Falloff.Fast => 0.03f, _ => 0.015f };

    public void Push(ReadOnlySpan<float> input, Falloff bars, Falloff peaks)
    {
        if (input.Length != _levels.Length) { _levels = new float[input.Length]; _peaks = new float[input.Length]; }
        float bs = BarStep(bars), ps = PeakStep(peaks);
        for (int i = 0; i < input.Length; i++)
        {
            _levels[i] = Math.Max(input[i], _levels[i] - bs);
            _peaks[i] = Math.Max(_levels[i], _peaks[i] - ps);
        }
    }

    public void Clear()
    {
        Array.Clear(_levels);
        Array.Clear(_peaks);
    }
}

/// <summary>Oscilloscope maths shared by both looks.</summary>
public static class ScopeMath
{
    /// <summary>Samples spread over the scope's width (Winamp drew 576).</summary>
    public const int Samples = 576;

    /// <summary>Row (0 = top) for a sample: +1 at the top, -1 at the bottom, silence in the middle.</summary>
    public static int Row(float sample, int height)
    {
        if (height <= 1) return 0;
        float s = float.IsNaN(sample) ? 0 : Math.Clamp(sample, -1f, 1f);
        return Math.Clamp((int)Math.Round((1 - s) * (height - 1) / 2.0, MidpointRounding.AwayFromZero), 0, height - 1);
    }

    /// <summary>The sample (of <paramref name="count"/>, oldest first) that column <paramref name="x"/> of <paramref name="width"/> shows.</summary>
    public static int SampleIndex(int x, int width, int count) =>
        width <= 0 || count <= 0 ? 0 : (int)Math.Clamp((long)x * count / width, 0, count - 1);

    /// <summary>
    /// VISCOLOR.TXT colour for a scope row: 18 (osc 1, the brightest) at the middle, down to 22 (osc 5) at the top and bottom.
    /// </summary>
    public static int ColorIndex(int row, int height)
    {
        if (height <= 1) return 18;
        double t = Math.Abs(2.0 * row - (height - 1)) / (height - 1); // 0 in the middle, 1 at the edges
        return 18 + Math.Min(4, (int)(t * 5));
    }

    /// <summary>First and last row a "lines" column covers: from one short of the previous column's row to this one's.</summary>
    public static (int From, int To) Span(int row, int previous)
    {
        if (row == previous) return (row, row);
        int near = previous + Math.Sign(row - previous);
        return (Math.Min(row, near), Math.Max(row, near));
    }
}

/// <summary>
/// Stereo vectorscope (goniometer) maths: left and right rotated by 45°, so mono is a vertical line, a left-only
/// signal leans up-left, a right-only one up-right, and out-of-phase sound spreads sideways.
/// </summary>
public static class VectorscopeMath
{
    /// <summary>
    /// Side (x, positive = right) and mid (y, positive = up): x = (R - L) / 2, y = (L + R) / 2. Samples within ±1 land
    /// inside the diamond |x| + |y| ≤ 1.
    /// </summary>
    public static (float X, float Y) Rotate(float left, float right) => ((right - left) * 0.5f, (left + right) * 0.5f);

    /// <summary>Pixel for a rotated point in a square of <paramref name="size"/> pixels (centre = silence), clamped inside.</summary>
    public static (int Px, int Py) ToPixel(float x, float y, int size)
    {
        float half = (size - 1) / 2f;
        int px = (int)MathF.Round(half + x * half), py = (int)MathF.Round(half - y * half);
        return (Math.Clamp(px, 0, size - 1), Math.Clamp(py, 0, size - 1));
    }

    /// <summary>
    /// Gentle automatic gain so quiet music still fills the picture: follows the loudest point at once and lets go
    /// slowly; never less than 1x or more than <paramref name="maxGain"/>.
    /// </summary>
    public static float Envelope(float previous, float framePeak, float release = 0.96f) =>
        Math.Max(framePeak, previous * release);

    public static float Gain(float envelope, float target = 0.85f, float maxGain = 4f) =>
        envelope <= 0 ? maxGain : Math.Clamp(target / envelope, 1f, maxGain);
}
