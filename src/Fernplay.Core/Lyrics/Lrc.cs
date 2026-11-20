using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Fernplay.Core.Lyrics;

/// <summary>One line of lyrics: its start time (null in unsynced lyrics) and its text ("" = a gap between lines).</summary>
public sealed record LyricLine(TimeSpan? Time, string Text)
{
    public bool IsGap => Text.Length == 0;
}

/// <summary>
/// LRC lyrics: "[mm:ss.xx]text" lines. Reads [mm:ss], [mm:ss.x], [mm:ss.xx] and [mm:ss.xxx] (also ":" before the
/// fraction), several time tags on one line, [offset:+/-ms]; metadata tags ([ar:], [ti:], [al:], [length:]…) are
/// not shown, enhanced word times (&lt;mm:ss.xx&gt;) are removed, and the lines are sorted by time. Text with no time
/// tag at all is unsynced lyrics: its lines are kept as they are, blank lines included.
/// </summary>
public static partial class Lrc
{
    /// <summary>
    /// The lines of LRC text. Synced (every line has a time) when any line carries a time tag; lines without one are
    /// then left out. A time tag with no text is kept as a gap. Unsynced otherwise (no times, blank lines kept as gaps,
    /// blank lines at the start and end dropped). Never throws.
    /// </summary>
    public static IReadOnlyList<LyricLine> Parse(string text)
    {
        var timed = new List<(TimeSpan Time, int Order, string Text)>();
        var plain = new List<string>();
        long offsetMs = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var times = new List<TimeSpan>();
            bool metadata = false;
            int pos = 0;
            while (true)
            {
                while (pos < raw.Length && char.IsWhiteSpace(raw[pos])) pos++;
                if (pos >= raw.Length || raw[pos] != '[') break;
                int close = raw.IndexOf(']', pos + 1);
                if (close < 0) break;
                var tag = raw.AsSpan(pos + 1, close - pos - 1).Trim();
                if (TryParseTime(tag, out var t)) times.Add(t);
                else if (times.Count == 0 && MetaTag().Match(tag.ToString()) is { Success: true } meta)
                {
                    metadata = true;
                    if (meta.Groups[1].Value.Equals("offset", StringComparison.OrdinalIgnoreCase)
                        && long.TryParse(meta.Groups[2].Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms))
                        offsetMs = ms;
                }
                else break; // "[Chorus]" and the like: part of the text
                pos = close + 1;
            }
            string rest = raw[pos..];
            if (times.Count > 0)
            {
                string line = Clean(rest);
                foreach (var t in times) timed.Add((t, timed.Count, line));
            }
            else if (metadata && string.IsNullOrWhiteSpace(rest)) continue;
            else plain.Add(Clean(raw));
        }

        if (timed.Count > 0)
        {
            // [offset:+500] shows the lyrics 500 ms sooner (the LRC convention); never before the start.
            var offset = TimeSpan.FromMilliseconds(offsetMs);
            return [.. timed
                .Select(x => (Time: x.Time - offset < TimeSpan.Zero ? TimeSpan.Zero : x.Time - offset, x.Order, x.Text))
                .OrderBy(x => x.Time).ThenBy(x => x.Order)
                .Select(x => new LyricLine(x.Time, x.Text))];
        }
        int first = plain.FindIndex(l => l.Length > 0), last = plain.FindLastIndex(l => l.Length > 0);
        if (first < 0) return [];
        return [.. plain.Skip(first).Take(last - first + 1).Select(l => new LyricLine(null, l))];
    }

    /// <summary>The text without enhanced word times, its ends trimmed, runs of spaces made one.</summary>
    private static string Clean(string text)
    {
        if (text.Contains('<')) text = MultiSpace().Replace(WordTime().Replace(text, ""), " ");
        return text.Trim();
    }

    /// <summary>"mm:ss", "mm:ss.x", "mm:ss.xx", "mm:ss.xxx" (or ":" before the fraction).</summary>
    public static bool TryParseTime(ReadOnlySpan<char> tag, out TimeSpan time)
    {
        time = default;
        var m = TimeTag().Match(tag.ToString());
        if (!m.Success) return false;
        int minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int seconds = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (seconds >= 60) return false;
        int ms = 0;
        if (m.Groups[3].Success)
        {
            var f = m.Groups[3].Value;
            ms = int.Parse(f, CultureInfo.InvariantCulture) * (f.Length switch { 1 => 100, 2 => 10, _ => 1 });
        }
        time = new TimeSpan(0, 0, minutes, seconds, ms);
        return true;
    }

    /// <summary>
    /// The line showing at <paramref name="position"/>: the last one whose time has come; -1 before the first line or
    /// for unsynced lyrics. Lines must be sorted (as <see cref="Parse"/> returns them).
    /// </summary>
    public static int IndexAt(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        int lo = 0, hi = lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (lines[mid].Time is not { } t) return -1;
            if (t <= position) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    // ================= Bytes to text =================

    static Lrc() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// Text of a lyrics file: UTF-8 (with or without a byte order mark) or UTF-16 with a mark; bytes that are not valid
    /// UTF-8 are read in the Windows ANSI code page (<paramref name="ansiCodePage"/>; 0 = this PC's).
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> bytes, int ansiCodePage = 0)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return AnsiEncoding(ansiCodePage).GetString(bytes); }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static Encoding AnsiEncoding(int codePage)
    {
        if (codePage == 0) codePage = GetACP();
        if (codePage is 65001 or <= 0) codePage = 1252; // a PC set to UTF-8 everywhere: the bytes were not UTF-8, so Western
        try { return Encoding.GetEncoding(codePage); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return Encoding.Latin1; }
    }

    [DllImport("kernel32.dll")]
    private static extern int GetACP();

    [GeneratedRegex(@"^(\d{1,4}):(\d{1,2})(?:[.:](\d{1,3}))?$")]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"^([A-Za-z#]+)\s*:(.*)$", RegexOptions.Singleline)]
    private static partial Regex MetaTag();

    [GeneratedRegex(@"<\d{1,4}:\d{1,2}(?:[.:]\d{1,3})?>")]
    private static partial Regex WordTime();

    [GeneratedRegex(@" {2,}")]
    private static partial Regex MultiSpace();
}
