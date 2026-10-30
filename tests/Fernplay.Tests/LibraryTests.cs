using Fernplay.Core;
using Fernplay.Core.Library;

namespace Fernplay.Tests;

public sealed class LibraryTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("fernplay-lib-");
    private int _reads;

    public void Dispose() => _dir.Delete(recursive: true);

    /// <summary>A file whose "tags" come from its name: "Artist - Album - 03 - Title.mp3".</summary>
    private string File(string relative, int bytes = 100)
    {
        var path = Path.Combine(_dir.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private TagInfo? FakeTags(string path)
    {
        _reads++;
        var p = Path.GetFileNameWithoutExtension(path).Split(" - ");
        if (p.Length < 4) return null;
        return new TagInfo(p[3], p[0], p[1], TimeSpan.FromSeconds(200), 320, int.Parse(p[2]), Genre: "Ambient", Year: 1998, Rating: 4);
    }

    private (LibraryIndex Index, LibraryScanner Scanner) NewLibrary(params string[] folders)
    {
        var index = new LibraryIndex();
        foreach (var f in folders) index.AddFolder(f);
        return (index, new LibraryScanner(FakeTags));
    }

    private static ScanChanges Rescan(LibraryIndex index, LibraryScanner scanner, DateTime? now = null)
    {
        var changes = scanner.Scan(index.Folders, LibraryScanner.Snapshot(index), now: now);
        index.Apply(changes);
        return changes;
    }

    [Fact]
    public void Scan_finds_music_and_playlists_and_reads_tags_only_when_files_change()
    {
        var root = Path.Combine(_dir.FullName, "Music");
        var a = File(@"Music\Soul\Soul - El Oso - 12 - I Miss the Girl.mp3");
        File(@"Music\Soul\Soul - El Oso - 13 - So Far.flac");
        File(@"Music\Soul\cover.jpg");
        File(@"Music\Lists\mix.m3u8");
        var (index, scanner) = NewLibrary(root);

        var first = Rescan(index, scanner);
        Assert.Equal(2, index.Count);
        Assert.Equal(2, _reads);
        Assert.Single(index.Playlists);
        var t = index.Find(a)!;
        Assert.Equal(("Soul", "El Oso", 12, "I Miss the Girl", 4), (t.Artist, t.Album, t.TrackNumber, t.Title, t.Rating));
        Assert.Equal(first.ScannedAt, t.Added);

        // Nothing changed: no tags read again.
        var second = Rescan(index, scanner);
        Assert.Equal(2, _reads);
        Assert.Equal(2, second.Unchanged);

        // A changed file is read again; its date added, plays and rating stay.
        index.RecordPlay(a, DateTime.UtcNow);
        index.SetRating(a, 2);
        System.IO.File.WriteAllBytes(a, new byte[500]);
        Rescan(index, scanner);
        Assert.Equal(3, _reads);
        t = index.Find(a)!;
        Assert.Equal((500L, 1, 2, first.ScannedAt), (t.Size, t.PlayCount, t.Rating, t.Added));
    }

    [Fact]
    public void Deleted_files_drop_out_but_a_missing_folder_keeps_its_tracks()
    {
        var music = Path.Combine(_dir.FullName, "Music");
        var usb = Path.Combine(_dir.FullName, "Usb");
        var gone = File(@"Music\A - B - 01 - Gone.mp3");
        File(@"Music\A - B - 02 - Stays.mp3");
        File(@"Usb\C - D - 01 - On the stick.mp3");
        var (index, scanner) = NewLibrary(music, usb);
        Rescan(index, scanner);
        Assert.Equal(3, index.Count);

        System.IO.File.Delete(gone);
        Directory.Move(usb, usb + "-unplugged"); // the drive is not there
        var changes = Rescan(index, scanner);
        Assert.Equal([gone], changes.Removed);
        Assert.Equal([usb], changes.MissingFolders);
        Assert.Equal(2, index.Count); // the USB track is kept
    }

    [Fact]
    public void Removing_a_folder_removes_only_its_tracks()
    {
        var a = Path.Combine(_dir.FullName, "A");
        var ab = Path.Combine(_dir.FullName, "AB"); // same prefix, different folder
        File(@"A\X - Y - 01 - One.mp3");
        File(@"AB\X - Y - 02 - Two.mp3");
        var (index, scanner) = NewLibrary(a, ab);
        Rescan(index, scanner);
        index.RemoveFolder(a);
        Assert.Single(index.All);
        Assert.Equal([ab], index.Folders);
        Assert.False(index.AddFolder(ab + Path.DirectorySeparatorChar)); // already there
    }

    [Fact]
    public void Index_survives_a_round_trip_and_a_damaged_file_is_kept_aside()
    {
        var music = Path.Combine(_dir.FullName, "Music");
        var a = File(@"Music\A - B - 01 - Song.mp3");
        var (index, scanner) = NewLibrary(music);
        Rescan(index, scanner);
        index.RecordPlay(a, new DateTime(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc));
        var file = Path.Combine(_dir.FullName, "library.json");
        index.Save(file);

        var back = LibraryIndex.Load(file);
        var t = back.Find(a.ToUpperInvariant())!; // paths compare without case, as Windows does
        Assert.Equal((1, "Song", 200_000L), (t.PlayCount, t.Title, t.DurationMs));
        Assert.Single(back.History);
        Assert.Equal([music], back.Folders);

        System.IO.File.WriteAllText(file, "{ not json");
        var empty = LibraryIndex.Load(file);
        Assert.Equal(0, empty.Count);
        Assert.True(System.IO.File.Exists(file + ".bad"));
        Assert.Equal(0, LibraryIndex.Load(Path.Combine(_dir.FullName, "none.json")).Count);
    }

    [Theory]
    [InlineData(99, 200, false)]   // under half
    [InlineData(100, 200, true)]   // half of a short track
    [InlineData(239, 1200, false)] // long track: four minutes are enough...
    [InlineData(240, 1200, true)]
    [InlineData(240, 0, true)]     // unknown length: four minutes
    public void A_play_counts_after_half_the_track_or_four_minutes(int played, int length, bool counts) =>
        Assert.Equal(counts, PlayRules.Counts(TimeSpan.FromSeconds(played), length > 0 ? TimeSpan.FromSeconds(length) : null));

    private static LibraryTrack T(string artist, string album, int n, string title, int plays = 0, int rating = 0,
        int addedDaysAgo = 100, int? playedDaysAgo = null, string? genre = null, int? year = null, string? albumArtist = null) =>
        new()
        {
            Path = $@"C:\m\{artist}\{album}\{n:00} {title}.mp3", Artist = artist, Album = album, TrackNumber = n, Title = title,
            PlayCount = plays, Rating = rating, Added = Now.AddDays(-addedDaysAgo), LastPlayed = playedDaysAgo is { } d ? Now.AddDays(-d) : null,
            Genre = genre, Year = year, AlbumArtist = albumArtist, DurationMs = 180_000, Size = 5 * 1048576,
        };

    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    private static readonly LibraryTrack[] Sample =
    [
        T("Soul Coughing", "El Oso", 13, "So Far I Have Not Found the Science", plays: 9, rating: 5, playedDaysAgo: 1, genre: "Alternative", year: 1998),
        T("Soul Coughing", "El Oso", 12, "I Miss the Girl", plays: 2, playedDaysAgo: 30, genre: "Alternative", year: 1998),
        T("Soul Coughing", "Irresistible Bliss", 1, "Super Bon Bon", rating: 3, addedDaysAgo: 3, genre: "Alternative", year: 1996),
        T("Björk", "Post", 1, "Army of Me", plays: 1, playedDaysAgo: 2, genre: "Electronic", year: 1995),
        T("Various One", "Mix", 1, "First", albumArtist: null),
        T("Various Two", "Mix", 2, "Second"),
    ];

    [Fact]
    public void Views_pick_and_order_tracks()
    {
        Assert.Equal(["So Far I Have Not Found the Science", "I Miss the Girl", "Army of Me"],
            LibraryQuery.View(Sample, LibraryView.MostPlayed, Now).Select(t => t.Title));
        Assert.Equal(["Super Bon Bon"], LibraryQuery.View(Sample, LibraryView.RecentlyAdded, Now).Select(t => t.Title));
        Assert.Equal(["So Far I Have Not Found the Science", "Army of Me"], LibraryQuery.View(Sample, LibraryView.RecentlyPlayed, Now).Select(t => t.Title));
        Assert.Equal(3, LibraryQuery.View(Sample, LibraryView.NeverPlayed, Now).Count);
        Assert.Equal(["So Far I Have Not Found the Science", "Super Bon Bon"], LibraryQuery.View(Sample, LibraryView.TopRated, Now).Select(t => t.Title));
        // Audio: artist, album, track number.
        Assert.Equal(["Army of Me", "I Miss the Girl", "So Far I Have Not Found the Science", "Super Bon Bon", "First", "Second"],
            LibraryQuery.View(Sample, LibraryView.Audio, Now).Select(t => t.Title));
    }

    [Fact]
    public void Search_matches_every_word_ignoring_case_and_accents()
    {
        Assert.Equal(["Army of Me"], LibraryQuery.Search(Sample, "bjork").Select(t => t.Title));
        Assert.Equal(2, LibraryQuery.Search(Sample, "soul OSO").Count);
        Assert.Equal(["Super Bon Bon"], LibraryQuery.Search(Sample, "1996").Select(t => t.Title));
        Assert.Empty(LibraryQuery.Search(Sample, "soul post"));
        Assert.Equal(Sample.Length, LibraryQuery.Search(Sample, "  ").Count);
    }

    [Fact]
    public void Artist_and_album_panes()
    {
        var artists = LibraryQuery.Artists(Sample);
        Assert.Equal(["Björk", "Soul Coughing", "Various One", "Various Two"], artists.Select(a => a.Name));
        Assert.Equal(new ArtistRow("Soul Coughing", 2, 3), artists[1]);
        Assert.Equal("All (4 artists)", LibraryQuery.AllArtistsLabel(artists.Count));
        Assert.Equal("All (1 artist)", LibraryQuery.AllArtistsLabel(1));

        var soul = LibraryQuery.Albums(Sample, "soul coughing");
        Assert.Equal(["Irresistible Bliss", "El Oso"], soul.Select(a => a.Album)); // by year
        var elOso = soul[1];
        Assert.Equal(("Soul Coughing", 2, 1998, "Alternative", 5), (elOso.Artist, elOso.Tracks, elOso.Year, elOso.Genre, elOso.Rating));
        Assert.EndsWith("12 I Miss the Girl.mp3", elOso.FirstTrackPath); // the lowest track number gives the cover

        var mix = LibraryQuery.Albums(Sample).Single(a => a.Album == "Mix");
        Assert.Equal(LibraryQuery.VariousArtists, mix.Artist);
    }

    [Fact]
    public void Status_line_reads_like_the_classic_library()
    {
        Assert.Equal("6 items [18:00] [30.00 MB] in 0.001 sec", LibraryQuery.Status(Sample, TimeSpan.FromMilliseconds(1)));
        var many = Enumerable.Repeat(Sample[0], 400).ToList();
        Assert.Equal("400 items [20:00:00] [1.95 GB] in 0.010 sec", LibraryQuery.Status(many, TimeSpan.FromMilliseconds(10)));
    }

    [Theory]
    [InlineData(@"Anna Grey\Träume\01 - Sag mir nur Warum.mp3", "Anna Grey", "Träume", 1, "Sag mir nur Warum")]
    [InlineData(@"Apache 207\Apache 207 - Bläulich.mp3", "Apache 207", null, null, "Bläulich")]
    [InlineData(@"Mix\03 - Fern Gully Radio - Night Bus.flac", "Fern Gully Radio", null, 3, "Night Bus")]
    [InlineData(@"loose track.mp3", null, null, null, "loose track")]
    [InlineData(@"Artist\Box Set\CD1\02 - Song.mp3", "Artist", "CD1", 2, "Song")]
    public void Missing_tags_come_from_folders_and_file_names(string relative, string? artist, string? album, int? number, string title)
    {
        var root = @"D:\Musik";
        Assert.Equal((artist, album, number, title), PathTags.Guess(Path.Combine(root, relative), root));
    }

    [Fact]
    public void Real_tags_always_win_over_the_path()
    {
        var root = @"D:\Musik";
        var file = Path.Combine(root, @"Anna Grey\Träume\01 - Sag mir nur Warum.mp3");
        var filled = PathTags.Fill(new TagInfo("Real Title", null, "Real Album", null, null, 7), file, root);
        Assert.Equal(("Real Title", "Anna Grey", "Real Album", 7), (filled.Title, filled.Artist, filled.Album, filled.TrackNumber));
        // An album artist in the tags: the artist is not guessed from the folder (the album artist names it).
        var compilation = PathTags.Fill(new TagInfo("T", null, "A", null, null, 1, AlbumArtist: "Various"), file, root);
        Assert.Null(compilation.Artist);
        // No tags at all (Windows could not read the file): everything from the path.
        var none = PathTags.Fill(null, file, root);
        Assert.Equal(("Sag mir nur Warum", "Anna Grey", "Träume", 1), (none.Title, none.Artist, none.Album, none.TrackNumber));
    }

    [Fact]
    public void Windows_ratings_become_stars_as_Explorer_shows_them()
    {
        Assert.Equal([0, 1, 1, 2, 3, 4, 5, 5], new[] { 0, 1, 12, 25, 50, 75, 99, 120 }.Select(TagInfo.Stars));
    }

    [Fact]
    public void Tags_read_from_a_real_file_include_the_library_fields()
    {
        // A WAV written by the tests has no tags: everything empty, but reading works and never throws.
        using var audio = new TestAudio();
        var info = TagInfo.FromFile(audio.Tone("tone.wav", 1.0));
        Assert.NotNull(info);
        Assert.Equal(1.0, info!.Duration!.Value.TotalSeconds, 1);
        Assert.Null(TagInfo.FromFile(Path.Combine(_dir.FullName, "nope.mp3")));
    }
}
