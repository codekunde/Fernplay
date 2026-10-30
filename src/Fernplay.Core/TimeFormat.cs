namespace Fernplay.Core;

public static class TimeFormat
{
    /// <summary>"3:07", "1:02:03"; remaining time gets a minus sign: "-0:42".</summary>
    public static string Clock(TimeSpan t, bool remaining = false)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var s = t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        return remaining ? "-" + s : s;
    }
}
