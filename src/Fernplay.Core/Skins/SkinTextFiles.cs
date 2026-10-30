using System.Globalization;
using System.Text.RegularExpressions;

namespace Fernplay.Core.Skins;

/// <summary>Playlist colours and font from PLEDIT.TXT ([Text] section). Missing or bad values keep the defaults.</summary>
public sealed record PlaylistStyle(uint Normal, uint Current, uint NormalBackground, uint SelectedBackground, string Font)
{
    public static PlaylistStyle Default { get; } = new(0xFF00FF00, 0xFFFFFFFF, 0xFF000000, 0xFF0000C6, "Arial");

    public static PlaylistStyle Parse(string? text)
    {
        var d = Default;
        if (string.IsNullOrWhiteSpace(text)) return d;
        var v = Ini.Parse(text).GetValueOrDefault("text") ?? [];
        string? font = v.GetValueOrDefault("font")?.Trim();
        return new PlaylistStyle(
            Color(v, "normal") ?? d.Normal,
            Color(v, "current") ?? d.Current,
            Color(v, "normalbg") ?? d.NormalBackground,
            Color(v, "selectedbg") ?? d.SelectedBackground,
            string.IsNullOrWhiteSpace(font) ? d.Font : font);
    }

    private static uint? Color(Dictionary<string, string> v, string key) =>
        v.TryGetValue(key, out var s) ? ParseColor(s) : null;

    /// <summary>"#RRGGBB" or "RRGGBB" (some skins drop the #); anything else is null.</summary>
    internal static uint? ParseColor(string s)
    {
        s = s.Trim().TrimStart('#');
        if (s.Length >= 6 && uint.TryParse(s[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb)) return 0xFF000000u | rgb;
        return null;
    }
}

/// <summary>
/// The 24 spectrum colours of VISCOLOR.TXT: 0 background, 1 dots, 2-17 the analyser bars from top to bottom,
/// 18-22 the oscilloscope, 23 the peak caps. One "r,g,b" per line; text after the numbers is a comment.
/// </summary>
public static class VisColors
{
    public const int Count = 24;

    /// <summary>Fernplay's own colours, used for lines a skin leaves out: green at the bottom to red at the top.</summary>
    public static IReadOnlyList<uint> Default { get; } = BuildDefault();

    private static uint[] BuildDefault()
    {
        var c = new uint[Count];
        c[0] = 0xFF000000; c[1] = 0xFF181818;
        for (int i = 0; i < 16; i++) // 2 = top (red) ... 17 = bottom (green)
        {
            double t = i / 15.0; // 0 at the top
            byte r = (byte)(t < 0.5 ? 230 : 230 - (t - 0.5) * 2 * 190), g = (byte)(t < 0.5 ? 60 + t * 2 * 170 : 230);
            c[2 + i] = 0xFF000000u | (uint)r << 16 | (uint)g << 8 | 30;
        }
        for (int i = 18; i < 23; i++) { byte v = (byte)(255 - (i - 18) * 40); c[i] = 0xFF000000u | (uint)v << 16 | (uint)v << 8 | v; }
        c[23] = 0xFFC8C8C8;
        return c;
    }

    private static readonly Regex Numbers = new(@"\d+", RegexOptions.CultureInvariant);

    public static uint[] Parse(string? text)
    {
        var colors = Default.ToArray();
        if (string.IsNullOrEmpty(text)) return colors;
        int i = 0;
        foreach (var raw in text.Split('\n'))
        {
            if (i >= Count) break;
            var line = raw.Trim();
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment];
            var m = Numbers.Matches(line);
            if (m.Count < 3) continue; // blank or comment-only line
            colors[i++] = 0xFF000000u | (uint)Clamp(m[0].Value) << 16 | (uint)Clamp(m[1].Value) << 8 | (uint)Clamp(m[2].Value);
        }
        return colors;
    }

    private static int Clamp(string s) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? Math.Min(v, 255) : 255;
}

/// <summary>
/// Window outlines from REGION.TXT: per window ([Normal], [Equalizer], [WindowShade], [EqualizerWS]) a list of
/// polygons; NumPoints gives each polygon's point count, PointList the x,y pairs. A section that does not add up is left out.
/// </summary>
public sealed class SkinRegions
{
    public IReadOnlyDictionary<string, IReadOnlyList<(int X, int Y)[]>> Windows { get; }
    public IReadOnlyList<string> Warnings { get; }

    private SkinRegions(Dictionary<string, IReadOnlyList<(int X, int Y)[]>> windows, List<string> warnings) { Windows = windows; Warnings = warnings; }

    public static SkinRegions Empty { get; } = new([], []);

    /// <summary>Polygons for one window ("normal", "equalizer", ...), or null when the skin gives none (a plain rectangle).</summary>
    public IReadOnlyList<(int X, int Y)[]>? For(string window) => Windows.GetValueOrDefault(window.ToLowerInvariant());

    private static readonly Regex Ints = new(@"-?\d+", RegexOptions.CultureInvariant);

    public static SkinRegions Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Empty;
        var windows = new Dictionary<string, IReadOnlyList<(int X, int Y)[]>>();
        var warnings = new List<string>();
        foreach (var (section, values) in Ini.Parse(text))
        {
            if (!values.TryGetValue("numpoints", out var np) || !values.TryGetValue("pointlist", out var pl)) continue;
            var counts = Ints.Matches(np).Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
            var coords = Ints.Matches(pl).Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
            if (counts.Length == 0 || counts.Any(c => c < 3) || counts.Sum() * 2 != coords.Length)
            {
                warnings.Add($"REGION.TXT [{section}]: {counts.Sum()} points announced, {coords.Length / 2} given; outline ignored.");
                continue;
            }
            var polys = new List<(int X, int Y)[]>();
            int at = 0;
            foreach (int c in counts)
            {
                var poly = new (int X, int Y)[c];
                for (int k = 0; k < c; k++, at += 2) poly[k] = (coords[at], coords[at + 1]);
                polys.Add(poly);
            }
            windows[section] = polys;
        }
        return new SkinRegions(windows, warnings);
    }
}

/// <summary>
/// Forgiving INI reader for skin text files: sections and keys lower-cased, ';' and '//' comments, and lines without '='
/// appended to the previous value (long point lists sometimes wrap).
/// </summary>
internal static class Ini
{
    public static Dictionary<string, Dictionary<string, string>> Parse(string text)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        string? lastKey = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            int c = line.IndexOf("//", StringComparison.Ordinal);
            if (c >= 0) line = line[..c].Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            if (line[0] == '[')
            {
                int end = line.IndexOf(']');
                var name = (end > 1 ? line[1..end] : line[1..]).Trim().ToLowerInvariant();
                if (!result.TryGetValue(name, out current)) result[name] = current = [];
                lastKey = null;
                continue;
            }
            if (current is null) continue;
            int eq = line.IndexOf('=');
            if (eq > 0)
            {
                lastKey = line[..eq].Trim().ToLowerInvariant();
                current[lastKey] = line[(eq + 1)..].Trim();
            }
            else if (lastKey is not null) current[lastKey] += " " + line;
        }
        return result;
    }
}
