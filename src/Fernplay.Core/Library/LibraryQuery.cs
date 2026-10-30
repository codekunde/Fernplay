using System.Globalization;

namespace Fernplay.Core.Library;

/// <summary>The views in the library tree under Local Library.</summary>
public enum LibraryView { Audio, MostPlayed, RecentlyAdded, RecentlyPlayed, NeverPlayed, TopRated }

public sealed record ArtistRow(string Name, int Albums, int Tracks);

public sealed record AlbumRow(string Album, string Artist, int Tracks, int? Year, string? Genre, int Rating, string FirstTrackPath);

/// <summary>Views, search, the artist and album panes, totals: everything the library window shows, without UI.</summary>
public static class LibraryQuery
{
    public const string UnknownArtist = "Unknown artist", UnknownAlbum = "Unknown album", VariousArtists = "Various artists";

    /// <summary>"Recently" in the tree means the last two weeks.</summary>
    public static readonly TimeSpan Recent = TimeSpan.FromDays(14);

    /// <summary>Top Rated: three stars or more.</summary>
    public const int TopRatedStars = 3;

    /// <summary>The tracks of a view, in that view's natural order.</summary>
    public static List<LibraryTrack> View(IEnumerable<LibraryTrack> all, LibraryView view, DateTime now) => view switch
    {
        LibraryView.MostPlayed => [.. all.Where(t => t.PlayCount > 0).OrderByDescending(t => t.PlayCount).ThenByDescending(t => t.LastPlayed)],
        LibraryView.RecentlyAdded => [.. all.Where(t => now - t.Added <= Recent).OrderByDescending(t => t.Added).ThenBy(t => t.Path, StringComparer.OrdinalIgnoreCase)],
        LibraryView.RecentlyPlayed => [.. all.Where(t => t.LastPlayed is { } p && now - p <= Recent).OrderByDescending(t => t.LastPlayed)],
        LibraryView.NeverPlayed => [.. InAlbumOrder(all.Where(t => t.PlayCount == 0))],
        LibraryView.TopRated => [.. all.Where(t => t.Rating >= TopRatedStars).OrderByDescending(t => t.Rating).ThenByDescending(t => t.PlayCount)],
        _ => [.. InAlbumOrder(all)],
    };

    /// <summary>Artist, album, track number, title: the order of the Audio view.</summary>
    public static IOrderedEnumerable<LibraryTrack> InAlbumOrder(IEnumerable<LibraryTrack> tracks) => tracks
        .OrderBy(t => t.ArtistName, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(t => t.AlbumName, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(t => t.TrackNumber ?? int.MaxValue)
        .ThenBy(t => t.TitleText, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>
    /// Tracks matching every word of the search (artist, album artist, album, title, genre, year, file name), ignoring case and
    /// accents. An empty search matches everything.
    /// </summary>
    public static List<LibraryTrack> Search(IEnumerable<LibraryTrack> tracks, string? text)
    {
        var words = Fold(text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [.. tracks];
        return [.. tracks.Where(t =>
        {
            string hay = Fold($"{t.Artist} {t.AlbumArtist} {t.Album} {t.TitleText} {t.Genre} {t.Year}");
            return words.All(w => hay.Contains(w, StringComparison.Ordinal));
        })];
    }

    /// <summary>Lower case without accents ("Björk" → "bjork"), so a search finds names typed without them.</summary>
    public static string Fold(string s)
    {
        var d = s.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var b = new System.Text.StringBuilder(d.Length);
        foreach (char c in d) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) b.Append(c);
        return b.ToString();
    }

    /// <summary>The artist pane: each artist with album and track counts, by name.</summary>
    public static List<ArtistRow> Artists(IEnumerable<LibraryTrack> tracks) => [.. tracks
        .GroupBy(t => t.ArtistName, StringComparer.CurrentCultureIgnoreCase)
        .Select(g => new ArtistRow(g.First().ArtistName, g.Select(t => t.AlbumName).Distinct(StringComparer.CurrentCultureIgnoreCase).Count(), g.Count()))
        .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>"All (3 artists)": the first row of the artist pane.</summary>
    public static string AllArtistsLabel(int artists) => $"All ({artists} artist{(artists == 1 ? "" : "s")})";

    /// <summary>
    /// The album pane for one artist (or all when null): album, its artist (album artist, else the one artist, else
    /// "Various artists"), track count, earliest year, the most common genre, the highest rating, a track for the cover.
    /// </summary>
    public static List<AlbumRow> Albums(IEnumerable<LibraryTrack> tracks, string? artist = null) => [.. tracks
        .Where(t => artist is null || string.Equals(t.ArtistName, artist, StringComparison.CurrentCultureIgnoreCase))
        .GroupBy(t => t.AlbumName, StringComparer.CurrentCultureIgnoreCase)
        .Select(g =>
        {
            var ordered = g.OrderBy(t => t.TrackNumber ?? int.MaxValue).ToList();
            var artists = g.Select(t => t.ArtistName).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
            string who = g.Select(t => t.AlbumArtist).FirstOrDefault(a => a is not null) ?? (artists.Count == 1 ? artists[0] : VariousArtists);
            string? genre = g.Where(t => t.Genre is not null).GroupBy(t => t.Genre!, StringComparer.CurrentCultureIgnoreCase)
                .OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault();
            return new AlbumRow(g.First().AlbumName, who, g.Count(), g.Min(t => t.Year), genre, g.Max(t => t.Rating), ordered[0].Path);
        })
        .OrderBy(a => a.Artist, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Year ?? int.MaxValue).ThenBy(a => a.Album, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>
    /// Names for the playlist files in the tree: the file name, and its folder after it when two files share a name
    /// ("Favourites (Rock)", "Favourites (Jazz)").
    /// </summary>
    public static List<(string Path, string Label)> PlaylistLabels(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        var counts = list.GroupBy(p => System.IO.Path.GetFileNameWithoutExtension(p), StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.CurrentCultureIgnoreCase);
        return [.. list.Select(p =>
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(p);
            string folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(p) ?? "");
            return (p, counts[name] > 1 && folder.Length > 0 ? $"{name} ({folder})" : name);
        })];
    }

    /// <summary>The status line: "40 items [2:40:25] [220.90 MB] in 0.001 sec".</summary>
    public static string Status(IReadOnlyCollection<LibraryTrack> tracks, TimeSpan queryTime)
    {
        var total = TimeSpan.FromMilliseconds(tracks.Sum(t => t.DurationMs ?? 0));
        double mb = tracks.Sum(t => t.Size) / 1048576.0;
        string length = total.TotalHours >= 1 ? $"{(int)total.TotalHours}:{total.Minutes:00}:{total.Seconds:00}" : $"{total.Minutes}:{total.Seconds:00}";
        // Fixed format (dot decimals), whatever the Windows region.
        string size = mb >= 1024 ? FormattableString.Invariant($"{mb / 1024:0.00} GB") : FormattableString.Invariant($"{mb:0.00} MB");
        return FormattableString.Invariant(
            $"{tracks.Count} item{(tracks.Count == 1 ? "" : "s")} [{length}] [{size}] in {queryTime.TotalSeconds:0.000} sec");
    }
}
