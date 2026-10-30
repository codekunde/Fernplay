using System.Runtime.InteropServices;

namespace Fernplay.Core;

/// <summary>One file in the playlist, with what Windows knows about it.</summary>
public sealed record Track(string Path, string? Title, string? Artist, string? Album, TimeSpan? Duration, int? BitrateKbps, int? TrackNumber = null)
{
    /// <summary>An internet radio stream (http or https address) rather than a file.</summary>
    public bool IsStream => IsStreamPath(Path);

    public static bool IsStreamPath(string path) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>A station from a playlist: its name from the playlist, or the address when there is none.</summary>
    public static Track Stream(string url, string? name) => new(url, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), null, null, null, null);

    /// <summary>
    /// "03 - Title - Artist": track number (when tagged and the title does not already start with a number),
    /// title, artist. The file name when there is no title.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Title)) return IsStream ? StreamLabel(Path) : System.IO.Path.GetFileNameWithoutExtension(Path);
            var title = Title!.Trim();
            var name = TrackNumber is > 0 && !StartsWithNumber(title) ? $"{TrackNumber:00} - {title}" : title;
            return string.IsNullOrWhiteSpace(Artist) ? name : $"{name} - {Artist!.Trim()}";
        }
    }

    /// <summary>"Artist - Title", the form other players expect in an M3U #EXTINF line.</summary>
    public string ArtistTitle =>
        !string.IsNullOrWhiteSpace(Title)
            ? (string.IsNullOrWhiteSpace(Artist) ? Title!.Trim() : $"{Artist!.Trim()} - {Title!.Trim()}")
            : IsStream ? StreamLabel(Path) : System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>"stream.antenne.de/2000er-hits" for a station without a name.</summary>
    private static string StreamLabel(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? (u.Host + u.AbsolutePath).TrimEnd('/') : url;

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
