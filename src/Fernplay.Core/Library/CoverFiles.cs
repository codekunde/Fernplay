namespace Fernplay.Core.Library;

/// <summary>
/// Album art kept as a picture next to the music (when a file has none of its own): the names other players and
/// Windows Media Player write, in order of preference.
/// </summary>
public static class CoverFiles
{
    private static readonly string[] Names = ["folder", "cover", "front", "album", "albumart"];
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png"];

    /// <summary>The cover picture in this folder, or null. Never throws (a folder that cannot be read has none).</summary>
    public static string? Find(string folder)
    {
        List<string> files;
        try { files = [.. Directory.EnumerateFiles(folder)]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }

        var pictures = files.Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var name in Names)
        {
            var hit = pictures.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        // Windows Media Player: AlbumArt_{id}_Large.jpg, then AlbumArtSmall.jpg.
        return pictures.FirstOrDefault(f => Path.GetFileName(f).StartsWith("AlbumArt", StringComparison.OrdinalIgnoreCase) && f.Contains("Large", StringComparison.OrdinalIgnoreCase))
            ?? pictures.FirstOrDefault(f => Path.GetFileName(f).StartsWith("AlbumArt", StringComparison.OrdinalIgnoreCase));
    }
}
