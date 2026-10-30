using System.Text;
using Fernplay.Core;

namespace Fernplay.Tests;

public sealed class MoveAndM3uTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fernplay-m3u-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Playlist ListOf(params string[] names)
    {
        var p = new Playlist(new Random(1));
        p.AddRange(names.Select(n => new Track(n, null, null, null, null, null)));
        return p;
    }

    private static string Names(Playlist p) => string.Join("", p.Tracks.Select(t => t.Path));

    // ---------- Move

    [Fact]
    public void Move_one_track_down_and_up()
    {
        var p = ListOf("a", "b", "c", "d");
        Assert.Equal([2], p.Move([0], 3));
        Assert.Equal("bcad", Names(p));
        Assert.Equal([0], p.Move([2], 0));
        Assert.Equal("abcd", Names(p));
    }

    [Fact]
    public void Move_to_the_end()
    {
        var p = ListOf("a", "b", "c");
        Assert.Equal([2], p.Move([0], 3));
        Assert.Equal("bca", Names(p));
    }

    [Fact]
    public void Move_keeps_a_scattered_selection_in_order_as_one_block()
    {
        var p = ListOf("a", "b", "c", "d", "e");
        var moved = p.Move([4, 1], 3); // b and e before d
        Assert.Equal("acbed", Names(p));
        Assert.Equal([2, 3], moved);
    }

    [Fact]
    public void Move_onto_itself_changes_nothing_and_raises_nothing()
    {
        var p = ListOf("a", "b", "c");
        int changed = 0;
        p.Changed += (_, _) => changed++;
        Assert.Equal([1], p.Move([1], 1));
        Assert.Equal([1], p.Move([1], 2));
        Assert.Equal("abc", Names(p));
        Assert.Equal(0, changed);
    }

    [Fact]
    public void The_playing_track_stays_current_after_a_move()
    {
        var p = ListOf("a", "b", "c", "d");
        p.MoveTo(1); // b plays
        p.Move([3], 0);
        Assert.Equal("dabc", Names(p));
        Assert.Equal("b", p.Current!.Path);
        Assert.Equal("c", p.Tracks[p.NextIndex(false)!.Value].Path);
    }

    [Fact]
    public void Shuffle_order_follows_the_tracks_through_a_move()
    {
        var p = ListOf("a", "b", "c", "d", "e");
        p.Shuffle = true;
        p.MoveTo(0);
        var before = new List<string>();
        var q = ListOf("a", "b", "c", "d", "e"); // same seed, same order
        q.Shuffle = true;
        q.MoveTo(0);
        while (q.Next() is { } t) before.Add(t.Path);

        p.Move([4], 0);
        var after = new List<string>();
        while (p.Next() is { } t) after.Add(t.Path);
        Assert.NotEmpty(before);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Tags_read_before_a_move_land_on_the_right_track()
    {
        var p = ListOf("a", "b", "c");
        p.Move([0], 3); // b c a
        p.Update(0, new Track("a", "Song A", null, null, TimeSpan.FromSeconds(10), null)); // index 0 was a's old place
        Assert.Null(p.Tracks[0].Title);
        Assert.Equal("Song A", p.Tracks[2].Title);
    }

    // ---------- M3U

    [Fact]
    public void Parse_skips_comments_keeps_streams_and_resolves_paths()
    {
        var text = "#EXTM3U\r\n#EXTINF:123,Artist - Title\r\nsub\\one.mp3\r\n\r\n# comment\r\nC:\\Music\\two.flac\r\n" +
                   "#EXTINF:0,Radio Example\r\nhttp://radio.example/stream\r\nfile:///C:/Music/three%20x.wav\r\n../up.mp3\nrtsp://old.example/x\n";
        var entries = M3u.Parse(text, @"D:\Lists");
        Assert.Equal([@"D:\Lists\sub\one.mp3", @"C:\Music\two.flac", "http://radio.example/stream", @"C:\Music\three x.wav", @"D:\up.mp3"],
            entries.Select(e => e.Path));
        Assert.Equal("Artist - Title", entries[0].Title);
        Assert.Null(entries[1].Title); // #EXTINF applies to the next entry only
        Assert.Equal("Radio Example", entries[2].Title);
        Assert.True(entries[2].IsStream);
    }

    [Fact]
    public void An_owner_style_radio_m3u_gives_one_named_station()
    {
        var entries = M3u.Parse("#EXTM3U\n#EXTINF:0,ANTENNE BAYERN 2000er Hits\nhttps://stream.antenne.de/2000er-hits\n", @"D:\Radio");
        var station = Assert.Single(entries);
        var track = Track.Stream(station.Path, station.Title);
        Assert.True(track.IsStream);
        Assert.Equal("ANTENNE BAYERN 2000er Hits", track.DisplayName);
        Assert.Equal("stream.antenne.de/2000er-hits", Track.Stream(station.Path, null).DisplayName);
    }

    [Fact]
    public void Pls_reads_files_and_titles_in_number_order()
    {
        var text = "[playlist]\r\nNumberOfEntries=3\r\nFile2=http://b.example/live\r\nTitle2=Station B\r\nFile1=https://a.example/stream\r\n" +
                   "Title1=Station A\r\nFile3=song.mp3\r\nLength3=200\r\nVersion=2\r\n";
        var entries = Pls.Parse(text, @"D:\Lists");
        Assert.Equal(["https://a.example/stream", "http://b.example/live", @"D:\Lists\song.mp3"], entries.Select(e => e.Path));
        Assert.Equal(["Station A", "Station B", null], entries.Select(e => e.Title));
    }

    [Fact]
    public void Expanding_radio_playlists_keeps_every_station()
    {
        var a = Path.Combine(_dir, "a.m3u");
        var b = Path.Combine(_dir, "b.pls");
        File.WriteAllText(a, "#EXTM3U\n#EXTINF:0,Station A\nhttps://a.example/stream\n");
        File.WriteAllText(b, "[playlist]\nFile1=http://b.example/live\nTitle1=Station B\n");
        var entries = Playlist.ExpandPaths([a, b, "https://c.example/direct"]).ToList();
        Assert.Equal(["https://a.example/stream", "http://b.example/live", "https://c.example/direct"], entries.Select(e => e.Path));
        Assert.Equal(["Station A", "Station B", null], entries.Select(e => e.Title));
    }

    [Fact]
    public void Saving_keeps_stations_as_addresses_with_their_names()
    {
        var file = Path.Combine(_dir, "radio.m3u8");
        M3u.Write(file, [Track.Stream("https://a.example/stream", "Station A")]);
        var text = File.ReadAllText(file);
        Assert.Contains("#EXTINF:-1,Station A\r\nhttps://a.example/stream\r\n", text);
        var back = Assert.Single(M3u.Read(file));
        Assert.Equal(("https://a.example/stream", "Station A"), (back.Path, back.Title));
    }

    [Theory]
    [InlineData("list.m3u8", false)]
    [InlineData("list.m3u", true)]
    public void Write_then_read_round_trips_with_relative_and_full_paths(string name, bool bom)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Album"));
        var inside = Path.Combine(_dir, "Album", "Café – 01.mp3");
        var outside = Path.GetFullPath(Path.Combine(_dir, "..", "elsewhere.flac"));
        var tracks = new[]
        {
            new Track(inside, "Café", "Zoë", null, TimeSpan.FromSeconds(200.4), null),
            new Track(outside, null, null, null, null, null),
        };
        var file = Path.Combine(_dir, name);
        M3u.Write(file, tracks);

        var bytes = File.ReadAllBytes(file);
        Assert.Equal(bom, bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        var text = File.ReadAllText(file, Encoding.UTF8);
        Assert.StartsWith("#EXTM3U", text.TrimStart('\uFEFF'));
        Assert.Contains("#EXTINF:200,Zoë - Café", text);
        Assert.Contains(@"Album\Café – 01.mp3", text);
        Assert.Contains(outside, text);
        Assert.Equal([inside, outside], M3u.Read(file).Select(e => e.Path));
    }

    [Fact]
    public void An_old_m3u_in_windows_1252_reads_accents()
    {
        var file = Path.Combine(_dir, "old.m3u");
        File.WriteAllBytes(file, [.. Encoding.ASCII.GetBytes("Caf"), 0xE9, 0x20, 0x80, .. Encoding.ASCII.GetBytes(".mp3\r\n")]);
        Assert.Equal([Path.Combine(_dir, "Café €.mp3")], M3u.Read(file).Select(e => e.Path));
    }

    [Fact]
    public void ExpandPaths_opens_a_playlist_and_leaves_out_missing_and_unplayable_entries()
    {
        var a = Path.Combine(_dir, "a.mp3");
        var b = Path.Combine(_dir, "b.flac");
        var ogg = Path.Combine(_dir, "c.ogg");
        foreach (var f in new[] { a, b, ogg }) File.WriteAllText(f, "");
        var list = Path.Combine(_dir, "mix.m3u8");
        File.WriteAllText(list, "#EXTM3U\nb.flac\nmissing.mp3\nc.ogg\na.mp3\n");
        Assert.Equal([b, a], Playlist.ExpandPaths([list]).Select(e => e.Path).ToList());
    }
}
