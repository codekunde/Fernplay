using System.Text.RegularExpressions;

namespace Fernplay.Core.Library;

/// <summary>
/// Fills tags a file does not have from where it lives (library only, never written to the file); real tags always win.
/// Understands the usual layouts under a library folder:
/// <c>Artist\Album\01 - Title.mp3</c>, <c>Artist\Artist - Title.mp3</c>, <c>01 - Artist - Title.mp3</c>.
/// </summary>
public static partial class PathTags
{
    [GeneratedRegex(@"^(\d{1,3})\s*[-.–]\s+(\S.*)$")]
    private static partial Regex LeadingNumber();

    public static TagInfo Fill(TagInfo? tags, string file, string libraryFolder)
    {
        tags ??= new TagInfo(null, null, null, null, null, null);
        var (artist, album, number, title) = Guess(file, libraryFolder);
        return tags with
        {
            Artist = tags.Artist ?? (tags.AlbumArtist is null ? artist : null),
            Album = tags.Album ?? album,
            TrackNumber = tags.TrackNumber ?? number,
            Title = tags.Title ?? title,
        };
    }

    /// <summary>Artist, album, track number and title as the folder and file name suggest them (null where they say nothing).</summary>
    public static (string? Artist, string? Album, int? Number, string Title) Guess(string file, string libraryFolder)
    {
        string stem = Path.GetFileNameWithoutExtension(file).Trim();
        int? number = null;
        var m = LeadingNumber().Match(stem);
        if (m.Success) { number = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture); stem = m.Groups[2].Value.Trim(); }

        string? nameArtist = null;
        int dash = stem.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && dash < stem.Length - 3) { nameArtist = stem[..dash].Trim(); stem = stem[(dash + 3)..].Trim(); }

        // Folders between the library folder and the file: [Artist], [Artist, Album], or deeper (first = artist, last = album).
        string rel = Path.GetRelativePath(libraryFolder, Path.GetDirectoryName(file)!);
        var folders = rel == "." || rel.StartsWith("..", StringComparison.Ordinal)
            ? [] : rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        string? artist = nameArtist ?? (folders.Length >= 1 ? folders[0] : null);
        string? album = folders.Length >= 2 ? folders[^1] : null;
        return (artist, album, number, stem);
    }
}
