using System.Text;

namespace Fernplay.Core;

/// <summary>
/// Reads and writes M3U playlists (plain and extended). <c>.m3u8</c> is UTF-8; <c>.m3u</c> is read as UTF-8 when
/// it decodes cleanly (or has a BOM), otherwise as Windows-1252, and written as UTF-8 with a BOM so other players
/// read accented names correctly.
/// </summary>
public static class M3u
{
    public static bool IsPlaylist(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".m3u", StringComparison.OrdinalIgnoreCase) || ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Full paths of the entries, in order. Relative entries are taken from the playlist's folder; web addresses are skipped.</summary>
    public static List<string> Read(string playlistPath)
    {
        var bytes = File.ReadAllBytes(playlistPath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(playlistPath))!;
        return Parse(Decode(bytes), dir);
    }

    public static List<string> Parse(string text, string baseDir)
    {
        var result = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().Trim('﻿');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string? path = line;
            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && !IsDrivePath(line))
            {
                if (!uri.IsFile) continue; // http(s) streams: not supported
                path = uri.LocalPath;
            }
            try { result.Add(Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path))); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return result;
    }

    /// <summary>Writes an extended M3U. Paths inside the playlist's folder are stored relative to it, others in full.</summary>
    public static void Write(string playlistPath, IEnumerable<Track> tracks)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(playlistPath))!;
        var sb = new StringBuilder("#EXTM3U\r\n");
        foreach (var t in tracks)
        {
            int seconds = t.Duration is { } d ? (int)Math.Round(d.TotalSeconds) : -1;
            sb.Append("#EXTINF:").Append(seconds).Append(',').Append(t.ArtistTitle.Replace('\r', ' ').Replace('\n', ' ')).Append("\r\n");
            sb.Append(EntryPath(t.Path, dir)).Append("\r\n");
        }
        bool bom = !Path.GetExtension(playlistPath).Equals(".m3u8", StringComparison.OrdinalIgnoreCase);
        var tmp = playlistPath + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(bom));
        File.Move(tmp, playlistPath, overwrite: true);
    }

    public static string EntryPath(string trackPath, string playlistDir)
    {
        var full = Path.GetFullPath(trackPath);
        var rel = Path.GetRelativePath(playlistDir, full);
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? full : rel;
    }

    private static bool IsDrivePath(string s) => s.Length >= 2 && char.IsAsciiLetter(s[0]) && s[1] == ':';

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { return Latin1Windows(bytes); }
    }

    // Windows-1252 without the code-pages package: Latin-1 plus the 0x80-0x9F block.
    private static readonly string Cp1252High = "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F\u0090‘’“”•–—˜™š›œ\u009DžŸ";

    private static string Latin1Windows(byte[] bytes)
    {
        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
            chars[i] = bytes[i] is >= 0x80 and <= 0x9F ? Cp1252High[bytes[i] - 0x80] : (char)bytes[i];
        return new string(chars);
    }
}
