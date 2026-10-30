using System.Text;

namespace Fernplay.Core;

/// <summary>One line of a playlist file: a full file path or a stream address, with the name the playlist gives it.</summary>
public sealed record PlaylistEntry(string Path, string? Title = null)
{
    public bool IsStream => Track.IsStreamPath(Path);
}

/// <summary>
/// Reads and writes M3U playlists (plain and extended). <c>.m3u8</c> is UTF-8; <c>.m3u</c> is read as UTF-8 when
/// it decodes cleanly (or has a BOM), otherwise as Windows-1252, and written as UTF-8 with a BOM so other players
/// read accented names correctly. Radio streams (http/https) are kept with their #EXTINF name.
/// </summary>
public static class M3u
{
    public static bool IsPlaylist(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".m3u", StringComparison.OrdinalIgnoreCase) || ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Entries in order. Relative paths are taken from the playlist's folder.</summary>
    public static List<PlaylistEntry> Read(string playlistPath)
    {
        var bytes = File.ReadAllBytes(playlistPath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(playlistPath))!;
        return Parse(TextDecoding.Decode(bytes), dir);
    }

    public static List<PlaylistEntry> Parse(string text, string baseDir)
    {
        var result = new List<PlaylistEntry>();
        string? title = null; // from the #EXTINF line just above an entry
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().Trim('﻿');
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = line.IndexOf(',');
                title = comma >= 0 ? line[(comma + 1)..].Trim() : null;
                continue;
            }
            if (line.StartsWith('#')) continue;
            if (ToEntryPath(line, baseDir) is { } path) result.Add(new PlaylistEntry(path, string.IsNullOrWhiteSpace(title) ? null : title));
            title = null;
        }
        return result;
    }

    /// <summary>A stream address as is; a file as a full path (file:/// addresses and relative paths resolved). Null when unusable.</summary>
    internal static string? ToEntryPath(string line, string baseDir)
    {
        if (Track.IsStreamPath(line)) return line;
        string path = line;
        if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && !IsDrivePath(line))
        {
            if (!uri.IsFile) return null; // other schemes (rtsp, mms...): not supported
            path = uri.LocalPath;
        }
        try { return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>Writes an extended M3U. Paths inside the playlist's folder are stored relative to it, others in full; streams as their address.</summary>
    public static void Write(string playlistPath, IEnumerable<Track> tracks)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(playlistPath))!;
        var sb = new StringBuilder("#EXTM3U\r\n");
        foreach (var t in tracks)
        {
            int seconds = t.Duration is { } d ? (int)Math.Round(d.TotalSeconds) : -1;
            sb.Append("#EXTINF:").Append(seconds).Append(',').Append(t.ArtistTitle.Replace('\r', ' ').Replace('\n', ' ')).Append("\r\n");
            sb.Append(t.IsStream ? t.Path : EntryPath(t.Path, dir)).Append("\r\n");
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
}

/// <summary>
/// Reads PLS playlists (common for internet radio): <c>[playlist]</c>, <c>File1=</c>, <c>Title1=</c>, ...
/// Entries come in their number order.
/// </summary>
public static class Pls
{
    public static bool IsPlaylist(string path) => Path.GetExtension(path).Equals(".pls", StringComparison.OrdinalIgnoreCase);

    public static List<PlaylistEntry> Read(string playlistPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(playlistPath))!;
        return Parse(TextDecoding.Decode(File.ReadAllBytes(playlistPath)), dir);
    }

    public static List<PlaylistEntry> Parse(string text, string baseDir)
    {
        var files = new SortedDictionary<int, string>();
        var titles = new Dictionary<int, string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().Trim('﻿');
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (TryNumbered(key, "File", out int n)) files[n] = value;
            else if (TryNumbered(key, "Title", out n)) titles[n] = value;
        }
        var result = new List<PlaylistEntry>();
        foreach (var (n, file) in files)
            if (M3u.ToEntryPath(file, baseDir) is { } path)
                result.Add(new PlaylistEntry(path, titles.TryGetValue(n, out var t) && t.Length > 0 ? t : null));
        return result;
    }

    private static bool TryNumbered(string key, string prefix, out int n)
    {
        n = 0;
        return key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(key.AsSpan(prefix.Length), out n);
    }
}

/// <summary>Playlist text: UTF-8 (with or without a BOM) or, when that fails, Windows-1252.</summary>
internal static class TextDecoding
{
    public static string Decode(byte[] bytes)
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
