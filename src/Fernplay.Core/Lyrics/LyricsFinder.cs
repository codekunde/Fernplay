namespace Fernplay.Core.Lyrics;

/// <summary>Where lyrics came from.</summary>
public enum LyricsSource
{
    /// <summary>A .lrc file next to the song.</summary>
    LrcFile,
    /// <summary>Synced lyrics in the song's tags (ID3 SYLT, or a lyrics tag holding LRC times).</summary>
    EmbeddedSynced,
    /// <summary>Plain lyrics text in the song's tags.</summary>
    EmbeddedText,
}

/// <summary>A song's lyrics: the lines (sorted by time when synced), and where they came from.</summary>
public sealed record SongLyrics(IReadOnlyList<LyricLine> Lines, LyricsSource Source, string SongPath)
{
    /// <summary>Every line has a time: the current one can be followed while the song plays.</summary>
    public bool IsSynced => Lines.Count > 0 && Lines[0].Time is not null;

    /// <summary>The line showing at that position (-1 before the first one, or when unsynced).</summary>
    public int IndexAt(TimeSpan position) => IsSynced ? Lrc.IndexAt(Lines, position) : -1;
}

/// <summary>
/// Finds a song's lyrics on this PC only (nothing is looked up online). In this order: a .lrc file with the song's
/// name next to it (Song.mp3 → Song.lrc); synced lyrics in its tags; plain lyrics in its tags.
/// </summary>
public static class LyricsFinder
{
    /// <summary>A .lrc file bigger than this is not lyrics.</summary>
    private const long MaxLrcBytes = 4 * 1024 * 1024;

    /// <summary>The .lrc file looked for: the song's path with .lrc instead of its own extension.</summary>
    public static string LrcPathFor(string songPath) => Path.ChangeExtension(songPath, ".lrc");

    /// <summary>The song's lyrics, or null when it has none (or is a stream). Never throws for a missing or unreadable file.</summary>
    public static SongLyrics? Find(string songPath) => Find(songPath, EmbeddedLyrics.Read);

    internal static SongLyrics? Find(string songPath, Func<string, EmbeddedFound> readTags)
    {
        if (Track.IsStreamPath(songPath)) return null;
        string lrc = LrcPathFor(songPath);
        try
        {
            var info = new FileInfo(lrc);
            if (info.Exists && info.Length <= MaxLrcBytes)
            {
                var lines = Lrc.Parse(Lrc.Decode(File.ReadAllBytes(lrc)));
                if (lines.Any(l => !l.IsGap)) return new SongLyrics(lines, LyricsSource.LrcFile, songPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }

        EmbeddedFound found;
        try { found = File.Exists(songPath) ? readTags(songPath) : EmbeddedFound.None; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }

        if (found.Synced is { Count: > 0 } synced) return new SongLyrics(synced, LyricsSource.EmbeddedSynced, songPath);
        SongLyrics? plain = null;
        foreach (var text in found.Texts)
        {
            var lines = Lrc.Parse(text);
            if (!lines.Any(l => !l.IsGap)) continue;
            if (lines[0].Time is not null) return new SongLyrics(lines, LyricsSource.EmbeddedSynced, songPath); // LRC times in the tag
            plain ??= new SongLyrics(lines, LyricsSource.EmbeddedText, songPath);
        }
        return plain;
    }
}
