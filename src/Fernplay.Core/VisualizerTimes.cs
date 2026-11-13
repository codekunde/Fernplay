namespace Fernplay.Core;

/// <summary>The visualizer's preset time: the steps Settings offers, from 5 seconds to 5 minutes.</summary>
public static class VisualizerTimes
{
    public const int Default = 20;

    /// <summary>Seconds; finer where small changes are noticed.</summary>
    public static IReadOnlyList<int> Steps { get; } = [5, 10, 15, 20, 25, 30, 45, 60, 90, 120, 180, 300];

    /// <summary>The step closest to <paramref name="seconds"/> (a hand-edited or old value lands on a step).</summary>
    public static int StepIndex(int seconds)
    {
        int best = 0;
        for (int i = 1; i < Steps.Count; i++)
            if (Math.Abs(Steps[i] - seconds) < Math.Abs(Steps[best] - seconds)) best = i;
        return best;
    }

    /// <summary>Kept within the steps' range (5 to 300).</summary>
    public static int Clamp(int seconds) => Math.Clamp(seconds, Steps[0], Steps[^1]);

    /// <summary>"20 S", "45 S", "1 MIN", "1.5 MIN", in the skin's capitals.</summary>
    public static string Format(int seconds) =>
        seconds < 60 ? $"{seconds} S" : $"{(seconds / 60.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} MIN";
}
