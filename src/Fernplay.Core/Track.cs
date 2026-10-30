using System.Runtime.InteropServices;

namespace Fernplay.Core;

/// <summary>One file in the playlist, with what Windows knows about it.</summary>
public sealed record Track(string Path, string? Title, string? Artist, string? Album, TimeSpan? Duration, int? BitrateKbps, int? TrackNumber = null)
{
    /// <summary>
    /// "03 - Title - Artist": track number (when tagged and the title does not already start with a number),
    /// title, artist. The file name when there is no title.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Title)) return System.IO.Path.GetFileNameWithoutExtension(Path);
            var title = Title!.Trim();
            var name = TrackNumber is > 0 && !StartsWithNumber(title) ? $"{TrackNumber:00} - {title}" : title;
            return string.IsNullOrWhiteSpace(Artist) ? name : $"{name} - {Artist!.Trim()}";
        }
    }

    /// <summary>"Artist - Title", the form other players expect in an M3U #EXTINF line.</summary>
    public string ArtistTitle =>
        !string.IsNullOrWhiteSpace(Title)
            ? (string.IsNullOrWhiteSpace(Artist) ? Title!.Trim() : $"{Artist!.Trim()} - {Title!.Trim()}")
            : System.IO.Path.GetFileNameWithoutExtension(Path);

    private static bool StartsWithNumber(string s) => s.Length > 0 && char.IsAsciiDigit(s[0]);

    /// <summary>Reads tags through the Windows property system (the same data Explorer shows). Never throws.</summary>
    public static Track FromFile(string path)
    {
        try { return TagReader.Read(path); }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new Track(path, null, null, null, null, null);
        }
    }
}
