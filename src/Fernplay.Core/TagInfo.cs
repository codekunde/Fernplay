namespace Fernplay.Core;

/// <summary>A file's tags as Windows reads them. Rating is 0 (none) to 5 stars.</summary>
public sealed record TagInfo(
    string? Title, string? Artist, string? Album, TimeSpan? Duration, int? BitrateKbps, int? TrackNumber,
    string? AlbumArtist = null, string? Genre = null, int? Year = null, int Rating = 0)
{
    public Track ToTrack(string path) => new(path, Title, Artist, Album, Duration, BitrateKbps, TrackNumber);

    /// <summary>Reads a file's tags through Windows; null when Windows cannot read the file. Never throws.</summary>
    public static TagInfo? FromFile(string path)
    {
        try { return TagReader.ReadTags(path); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Windows' 1-99 rating as stars, the way Explorer shows it: 1-12 = 1, 13-37 = 2, 38-62 = 3, 63-87 = 4, 88-99 = 5; 0 = none.
    /// </summary>
    public static int Stars(int windowsRating) => windowsRating switch
    {
        <= 0 => 0,
        < 13 => 1,
        < 38 => 2,
        < 63 => 3,
        < 88 => 4,
        _ => 5,
    };
}
