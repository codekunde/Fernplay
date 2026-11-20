using System.Buffers.Binary;
using System.Text;
using Fernplay.Core.Lyrics;

namespace Fernplay.Tests;

// All lyrics here are invented for the tests.
public class LrcParserTests
{
    private static TimeSpan T(int m, int s, int ms = 0) => new(0, 0, m, s, ms);

    [Fact]
    public void Reads_the_three_time_forms()
    {
        var lines = Lrc.Parse("[00:01.50]Paper lanterns\n[00:02.125]over the harbour\n[01:03]tide goes out");
        Assert.Equal([T(0, 1, 500), T(0, 2, 125), T(1, 3)], lines.Select(l => l.Time!.Value));
        Assert.Equal(["Paper lanterns", "over the harbour", "tide goes out"], lines.Select(l => l.Text));
    }

    [Fact]
    public void One_tenth_and_colon_fractions_are_read()
    {
        Assert.True(Lrc.TryParseTime("00:04.5", out var a));
        Assert.Equal(T(0, 4, 500), a);
        Assert.True(Lrc.TryParseTime("02:10:25", out var b));
        Assert.Equal(T(2, 10, 250), b);
        Assert.False(Lrc.TryParseTime("00:75.00", out _));
        Assert.False(Lrc.TryParseTime("ar:Someone", out _));
    }

    [Fact]
    public void Several_time_tags_on_one_line_repeat_it_and_lines_are_sorted()
    {
        var lines = Lrc.Parse("[00:30.00][00:10.00]Kettle on the stove\n[00:20.00]Rain on the roof");
        Assert.Equal(["Kettle on the stove", "Rain on the roof", "Kettle on the stove"], lines.Select(l => l.Text));
        Assert.Equal([T(0, 10), T(0, 20), T(0, 30)], lines.Select(l => l.Time!.Value));
    }

    [Fact]
    public void Equal_times_keep_the_file_order()
    {
        var lines = Lrc.Parse("[00:05.00]first\n[00:05.00]second");
        Assert.Equal(["first", "second"], lines.Select(l => l.Text));
    }

    [Theory]
    [InlineData("+500", 500)]
    [InlineData("-250", 1250)]
    [InlineData("250", 750)]
    public void Offset_moves_every_line(string offset, int expectedMs)
    {
        var lines = Lrc.Parse($"[offset:{offset}]\n[00:01.00]Moth at the window");
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), lines.Single().Time);
    }

    [Fact]
    public void Offset_never_goes_below_zero()
    {
        var lines = Lrc.Parse("[offset:+5000]\n[00:01.00]Early");
        Assert.Equal(TimeSpan.Zero, lines.Single().Time);
    }

    [Fact]
    public void Metadata_tags_are_not_shown()
    {
        var lines = Lrc.Parse("[ar:Nobody Real]\n[ti:Invented Tune]\n[al:Test Album]\n[length: 03:20]\n[by:tester]\n[00:01.00]Only this line");
        Assert.Equal(["Only this line"], lines.Select(l => l.Text));
    }

    [Fact]
    public void Blank_timed_lines_are_gaps_and_untimed_blank_lines_are_dropped()
    {
        var lines = Lrc.Parse("[00:01.00]Before the bridge\n\n[00:05.00]\n[00:09.00]After the bridge\n");
        Assert.Equal(["Before the bridge", "", "After the bridge"], lines.Select(l => l.Text));
        Assert.True(lines[1].IsGap);
    }

    [Fact]
    public void Enhanced_word_times_are_removed()
    {
        var lines = Lrc.Parse("[00:01.00]<00:01.00>Lemon <00:01.40>trees <00:01.90>in <00:02.10>winter");
        Assert.Equal("Lemon trees in winter", lines.Single().Text);
    }

    [Fact]
    public void Text_in_brackets_that_is_not_a_tag_stays()
    {
        var lines = Lrc.Parse("[00:01.00][Chorus] Lanterns again");
        Assert.Equal("[Chorus] Lanterns again", lines.Single().Text);
    }

    [Fact]
    public void Text_without_times_is_unsynced_with_its_blank_lines()
    {
        var lines = Lrc.Parse("\n\nFirst verse line\nSecond verse line\n\nChorus line\n\n");
        Assert.Equal(["First verse line", "Second verse line", "", "Chorus line"], lines.Select(l => l.Text));
        Assert.All(lines, l => Assert.Null(l.Time));
        Assert.Equal(-1, Lrc.IndexAt(lines, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Untimed_text_with_metadata_only_is_empty()
    {
        Assert.Empty(Lrc.Parse("[ar:Nobody]\n[ti:Nothing]\n"));
        Assert.Empty(Lrc.Parse(""));
    }

    [Fact]
    public void Crlf_and_cr_line_ends_are_read()
    {
        Assert.Equal(3, Lrc.Parse("[00:01.00]a\r\n[00:02.00]b\r[00:03.00]c").Count);
    }

    [Fact]
    public void IndexAt_finds_the_line_showing()
    {
        var lines = Lrc.Parse("[00:02.00]one\n[00:04.00]two\n[00:06.00]three");
        Assert.Equal(-1, Lrc.IndexAt(lines, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, Lrc.IndexAt(lines, TimeSpan.FromSeconds(2)));
        Assert.Equal(0, Lrc.IndexAt(lines, TimeSpan.FromSeconds(3.99)));
        Assert.Equal(1, Lrc.IndexAt(lines, TimeSpan.FromSeconds(4)));
        Assert.Equal(2, Lrc.IndexAt(lines, TimeSpan.FromMinutes(9)));
        Assert.Equal(-1, Lrc.IndexAt([], TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Decodes_utf8_with_and_without_bom()
    {
        byte[] text = Encoding.UTF8.GetBytes("[00:01.00]Café für Größe");
        Assert.Equal("[00:01.00]Café für Größe", Lrc.Decode(text));
        Assert.Equal("[00:01.00]Café für Größe", Lrc.Decode([0xEF, 0xBB, 0xBF, .. text]));
    }

    [Fact]
    public void Decodes_utf16_with_a_mark()
    {
        Assert.Equal("Ünder", Lrc.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("Ünder")]));
    }

    [Fact]
    public void Falls_back_to_the_ansi_code_page_for_bytes_that_are_not_utf8()
    {
        byte[] ansi = [.. "[00:01.00]Caf"u8, 0xE9, (byte)' ', 0xFC, (byte)'b', (byte)'e', (byte)'r']; // é ü in Windows-1252
        Assert.Equal("[00:01.00]Café über", Lrc.Decode(ansi, 1252));
        Assert.Equal("Café über", Lrc.Parse(Lrc.Decode(ansi, 1252)).Single().Text);
    }
}

public class EmbeddedLyricsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fernplay-lyrics-" + Guid.NewGuid().ToString("N"));

    public EmbeddedLyricsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    // ---------- builders ----------

    private static byte[] SyncSafe(int n) => [(byte)(n >> 21 & 0x7F), (byte)(n >> 14 & 0x7F), (byte)(n >> 7 & 0x7F), (byte)(n & 0x7F)];

    private static byte[] Frame(int version, string id, byte[] body, byte formatFlags = 0)
    {
        byte[] size = version == 4 ? SyncSafe(body.Length) : [(byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length];
        return [.. Encoding.ASCII.GetBytes(id), .. size, 0, formatFlags, .. body];
    }

    private static byte[] Tag(int version, byte flags, params byte[][] frames)
    {
        byte[] body = [.. frames.SelectMany(f => f), .. new byte[16]]; // padding
        return [.. "ID3"u8, (byte)version, 0, flags, .. SyncSafe(body.Length), .. body];
    }

    private static byte[] Uslt(string text, byte enc = 3)
    {
        Encoding e = enc switch { 1 => Encoding.Unicode, 2 => Encoding.BigEndianUnicode, 3 => Encoding.UTF8, _ => Encoding.Latin1 };
        byte[] term = enc is 1 or 2 ? [0, 0] : [0];
        byte[] bom = enc == 1 ? [0xFF, 0xFE] : [];
        return [enc, .. "eng"u8, .. bom, .. e.GetBytes("desc"), .. term, .. bom, .. e.GetBytes(text)];
    }

    private static byte[] Sylt(params (string Text, int Ms)[] entries)
    {
        var body = new List<byte> { 3 };
        body.AddRange("eng"u8.ToArray());
        body.Add(2); // milliseconds
        body.Add(1); // lyrics
        body.Add(0); // empty description
        foreach (var (text, ms) in entries)
        {
            body.AddRange(Encoding.UTF8.GetBytes(text));
            body.Add(0);
            var t = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(t, (uint)ms);
            body.AddRange(t);
        }
        return [.. body];
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static readonly byte[] FakeAudio = [0xFF, 0xFB, 0x90, 0x00, .. new byte[64]];

    // ---------- ID3 ----------

    [Theory]
    [InlineData(3, 3)]
    [InlineData(3, 1)]
    [InlineData(4, 3)]
    [InlineData(4, 2)]
    [InlineData(3, 0)]
    public void Reads_uslt_in_each_encoding(int version, byte enc)
    {
        var tag = Tag(version, 0, Frame(version, "TIT2", [3, .. "Invented"u8]), Frame(version, "USLT", Uslt("Salt on the window\nSand in the shoes", enc)));
        var found = EmbeddedLyrics.Read(new MemoryStream([.. tag, .. FakeAudio]));
        Assert.Null(found.Synced);
        Assert.Equal("Salt on the window\nSand in the shoes", found.Texts.Single());
    }

    [Fact]
    public void Reads_uslt_in_an_id3v22_tag()
    {
        byte[] body = Uslt("Old tag words", 0);
        byte[] frame = [.. "ULT"u8, (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length, .. body];
        byte[] tag = [.. "ID3"u8, 2, 0, 0, .. SyncSafe(frame.Length + 8), .. frame, .. new byte[8]];
        Assert.Equal("Old tag words", EmbeddedLyrics.Read(new MemoryStream(tag)).Texts.Single());
    }

    [Fact]
    public void Reads_sylt_in_milliseconds()
    {
        var tag = Tag(4, 0, Frame(4, "SYLT", Sylt(("Second line", 4000), ("First line", 1500))));
        var synced = EmbeddedLyrics.Read(new MemoryStream(tag)).Synced!;
        Assert.Equal(["First line", "Second line"], synced.Select(l => l.Text));
        Assert.Equal([TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(4000)], synced.Select(l => l.Time!.Value));
    }

    [Fact]
    public void Sylt_syllables_join_into_lines_at_line_breaks()
    {
        var tag = Tag(3, 0, Frame(3, "SYLT", Sylt(("Fog ", 1000), ("on ", 1300), ("the hill", 1600), ("\nBells ", 3000), ("ring", 3400))));
        var synced = EmbeddedLyrics.Read(new MemoryStream(tag)).Synced!;
        Assert.Equal(["Fog on the hill", "Bells ring"], synced.Select(l => l.Text));
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)], synced.Select(l => l.Time!.Value));
    }

    [Fact]
    public void Sylt_in_mpeg_frames_is_left_out()
    {
        byte[] sylt = Sylt(("Frames", 10));
        sylt[4] = 1;
        Assert.Null(EmbeddedLyrics.Read(new MemoryStream(Tag(3, 0, Frame(3, "SYLT", sylt)))).Synced);
    }

    [Fact]
    public void Undoes_tag_unsynchronisation_in_v23()
    {
        // Text whose UTF-8 holds FF? It cannot; put an FF 00 pair into the language field instead, which moves nothing
        // after resync only if it is undone: "ÿ" in Latin-1 is FF.
        byte[] body = [0, (byte)'e', (byte)'n', (byte)'g', 0, .. Encoding.Latin1.GetBytes("Caf\u00ff words")];
        byte[] frame = Frame(3, "USLT", body);
        // Unsynchronise: FF -> FF 00 (the frame size counts the bytes before unsynchronisation).
        var unsynced = new List<byte>();
        foreach (byte b in frame) { unsynced.Add(b); if (b == 0xFF) unsynced.Add(0); }
        byte[] tagBody = [.. unsynced, .. new byte[8]];
        byte[] tag = [.. "ID3"u8, 3, 0, 0x80, .. SyncSafe(tagBody.Length), .. tagBody];
        Assert.Equal("Caf\u00ff words", EmbeddedLyrics.Read(new MemoryStream(tag)).Texts.Single());
    }

    [Fact]
    public void Skips_other_frames_and_large_pictures()
    {
        var picture = Frame(3, "APIC", [0, .. "image/png"u8, 0, 3, 0, .. new byte[200_000]]);
        var tag = Tag(3, 0, picture, Frame(3, "USLT", Uslt("After the picture")));
        Assert.Equal("After the picture", EmbeddedLyrics.Read(new MemoryStream(tag)).Texts.Single());
    }

    [Fact]
    public void A_file_without_tags_has_none()
    {
        var found = EmbeddedLyrics.Read(new MemoryStream(FakeAudio));
        Assert.Null(found.Synced);
        Assert.Empty(found.Texts);
    }

    [Fact]
    public void A_broken_tag_gives_none_and_does_not_throw()
    {
        byte[] tag = [.. "ID3"u8, 3, 0, 0, .. SyncSafe(500), .. "USLT"u8, 0, 0, 9, 0, 0, 0, 3]; // cut short
        var found = EmbeddedLyrics.Read(new MemoryStream(tag));
        Assert.Empty(found.Texts);
    }

    // ---------- FLAC ----------

    private static byte[] Flac(params string[] comments)
    {
        var block = new List<byte>();
        void U32(int n) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)n); block.AddRange(b); }
        byte[] vendor = "test vendor"u8.ToArray();
        U32(vendor.Length); block.AddRange(vendor);
        U32(comments.Length);
        foreach (var c in comments) { var b = Encoding.UTF8.GetBytes(c); U32(b.Length); block.AddRange(b); }
        byte[] streamInfo = new byte[34];
        return [.. "fLaC"u8, 0x00, 0, 0, 34, .. streamInfo, 0x84, (byte)(block.Count >> 16), (byte)(block.Count >> 8), (byte)block.Count, .. block, .. FakeAudio];
    }

    [Fact]
    public void Reads_flac_lyrics_and_unsyncedlyrics()
    {
        var found = EmbeddedLyrics.Read(new MemoryStream(Flac("TITLE=Invented", "lyrics=Green door, red door", "UNSYNCEDLYRICS=Blue door")));
        Assert.Equal(["Green door, red door", "Blue door"], found.Texts);
    }

    [Fact]
    public void Reads_flac_behind_an_id3_tag()
    {
        byte[] id3 = Tag(3, 0, Frame(3, "TIT2", [3, .. "Invented"u8]));
        var found = EmbeddedLyrics.Read(new MemoryStream([.. id3, .. Flac("LYRICS=Behind the tag")]));
        Assert.Equal("Behind the tag", found.Texts.Single());
    }

    // ---------- MP4 ----------

    private static byte[] Atom(string type, params byte[][] children) => Atom(Encoding.Latin1.GetBytes(type), children);

    private static byte[] Atom(byte[] type, params byte[][] children)
    {
        byte[] body = [.. children.SelectMany(c => c)];
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, (uint)(body.Length + 8));
        return [.. size, .. type, .. body];
    }

    [Fact]
    public void Reads_mp4_lyr_after_the_audio()
    {
        byte[] data = Atom("data", [0, 0, 0, 1, 0, 0, 0, 0], Encoding.UTF8.GetBytes("Orchard in the rain"));
        byte[] lyr = Atom([0xA9, (byte)'l', (byte)'y', (byte)'r'], data);
        byte[] meta = Atom("meta", [0, 0, 0, 0], Atom("hdlr", new byte[25]), Atom("ilst", Atom([0xA9, (byte)'n', (byte)'a', (byte)'m'], new byte[20]), lyr));
        byte[] moov = Atom("moov", Atom("mvhd", new byte[100]), Atom("udta", meta));
        byte[] file = [.. Atom("ftyp", "M4A "u8.ToArray(), new byte[8]), .. Atom("mdat", new byte[5000]), .. moov];
        Assert.Equal("Orchard in the rain", EmbeddedLyrics.Read(new MemoryStream(file)).Texts.Single());
    }

    [Fact]
    public void Mp4_without_lyrics_has_none()
    {
        byte[] moov = Atom("moov", Atom("mvhd", new byte[100]));
        Assert.Empty(EmbeddedLyrics.Read(new MemoryStream([.. Atom("ftyp", "M4A "u8.ToArray()), .. moov])).Texts);
    }

    // ---------- Finder ----------

    private static Func<string, EmbeddedFound> Tags(EmbeddedFound found) => _ => found;

    [Fact]
    public void Lrc_next_to_the_song_comes_first()
    {
        var song = Write("Invented Song.mp3", FakeAudio);
        File.WriteAllText(Path.Combine(_folder, "Invented Song.lrc"), "[00:01.00]From the lrc file");
        var lyrics = LyricsFinder.Find(song, Tags(new EmbeddedFound([new LyricLine(TimeSpan.Zero, "From the tag")], ["Tag text"])))!;
        Assert.Equal(LyricsSource.LrcFile, lyrics.Source);
        Assert.True(lyrics.IsSynced);
        Assert.Equal("From the lrc file", lyrics.Lines.Single().Text);
    }

    [Fact]
    public void Embedded_synced_comes_before_embedded_text()
    {
        var song = Write("a.mp3", FakeAudio);
        var lyrics = LyricsFinder.Find(song, Tags(new EmbeddedFound([new LyricLine(TimeSpan.FromSeconds(2), "Synced tag")], ["Plain tag"])))!;
        Assert.Equal(LyricsSource.EmbeddedSynced, lyrics.Source);
    }

    [Fact]
    public void Lyrics_text_with_lrc_times_counts_as_synced()
    {
        var song = Write("b.flac", FakeAudio);
        var lyrics = LyricsFinder.Find(song, Tags(new EmbeddedFound(null, ["Plain words", "[00:03.00]Timed words"])))!;
        Assert.Equal(LyricsSource.EmbeddedSynced, lyrics.Source);
        Assert.Equal(0, lyrics.IndexAt(TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public void Plain_tag_text_is_unsynced()
    {
        var song = Write("c.m4a", FakeAudio);
        var lyrics = LyricsFinder.Find(song, Tags(new EmbeddedFound(null, ["Line one\nLine two"])))!;
        Assert.Equal(LyricsSource.EmbeddedText, lyrics.Source);
        Assert.False(lyrics.IsSynced);
        Assert.Equal(-1, lyrics.IndexAt(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void An_empty_lrc_falls_through_to_the_tags()
    {
        var song = Write("d.mp3", FakeAudio);
        File.WriteAllText(Path.Combine(_folder, "d.lrc"), "[ar:Nobody]\n");
        Assert.Equal(LyricsSource.EmbeddedText, LyricsFinder.Find(song, Tags(new EmbeddedFound(null, ["Tag words"])))!.Source);
    }

    [Fact]
    public void No_lyrics_and_streams_give_null()
    {
        var song = Write("e.wav", FakeAudio);
        Assert.Null(LyricsFinder.Find(song, Tags(EmbeddedFound.None)));
        Assert.Null(LyricsFinder.Find("https://radio.example/stream", _ => throw new InvalidOperationException("not read")));
        Assert.Null(LyricsFinder.Find(Path.Combine(_folder, "missing.mp3"), Tags(EmbeddedFound.None)));
    }

    [Fact]
    public void Lrc_path_swaps_the_extension()
    {
        Assert.Equal(@"C:\Music\Some Tune.lrc", LyricsFinder.LrcPathFor(@"C:\Music\Some Tune.mp3"));
        Assert.Equal(@"C:\Music\a.b.lrc", LyricsFinder.LrcPathFor(@"C:\Music\a.b.flac"));
    }

    [Fact]
    public void Reads_a_real_file_end_to_end()
    {
        var tag = Tag(3, 0, Frame(3, "USLT", Uslt("[00:00.50]Timed inside the tag\n[00:01.50]Second timed line")));
        var song = Write("real.mp3", [.. tag, .. FakeAudio]);
        var lyrics = LyricsFinder.Find(song)!;
        Assert.Equal(LyricsSource.EmbeddedSynced, lyrics.Source);
        Assert.Equal(["Timed inside the tag", "Second timed line"], lyrics.Lines.Select(l => l.Text));
    }
}
