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
    public void Parse_skips_comments_and_streams_and_resolves_paths()
    {
        var text = "#EXTM3U\r\n#EXTINF:123,Artist - Title\r\nsub\\one.mp3\r\n\r\n# comment\r\nC:\\Music\\two.flac\r\n" +
                   "http://radio.example/stream\r\nfile:///C:/Music/three%20x.wav\r\n../up.mp3\n";
        var paths = M3u.Parse(text, @"D:\Lists");
        Assert.Equal([@"D:\Lists\sub\one.mp3", @"C:\Music\two.flac", @"C:\Music\three x.wav", @"D:\up.mp3"], paths);
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
        Assert.Equal([inside, outside], M3u.Read(file));
    }

    [Fact]
    public void An_old_m3u_in_windows_1252_reads_accents()
    {
        var file = Path.Combine(_dir, "old.m3u");
        File.WriteAllBytes(file, [.. Encoding.ASCII.GetBytes("Caf"), 0xE9, 0x20, 0x80, .. Encoding.ASCII.GetBytes(".mp3\r\n")]);
        Assert.Equal([Path.Combine(_dir, "Café €.mp3")], M3u.Read(file));
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
        Assert.Equal([b, a], Playlist.ExpandPaths([list]).ToList());
    }
}
