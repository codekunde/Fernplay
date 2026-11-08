using System.Buffers.Binary;
using System.Text;
using Fernplay.Core;
using Fernplay.Core.Audio;

namespace Fernplay.Tests;

public sealed class ReplayGainTests : IDisposable
{
    private readonly TestAudio _audio = new();
    public void Dispose() => _audio.Dispose();

    private static readonly byte[] SomeAudio = Enumerable.Repeat((byte)0x55, 300).ToArray();

    // ================= the gain rule =================

    [Fact]
    public void Off_never_changes_the_level()
    {
        Assert.Equal(1f, ReplayGain.GainFor(new ReplayGain(-8, 0.5, -9, 0.5), LevelMode.Off));
        Assert.Equal(1f, ReplayGain.GainFor(null, LevelMode.Off));
    }

    [Fact]
    public void Track_and_album_use_their_own_gain_and_fall_back_to_the_other()
    {
        var both = new ReplayGain(-6.0206, 0.5, -12.0412, 0.5);
        Assert.Equal(0.5f, ReplayGain.GainFor(both, LevelMode.Track), 3);
        Assert.Equal(0.25f, ReplayGain.GainFor(both, LevelMode.Album), 3);
        Assert.Equal(0.5f, ReplayGain.GainFor(new ReplayGain(-6.0206, null, null, null), LevelMode.Album), 3);
        Assert.Equal(0.25f, ReplayGain.GainFor(new ReplayGain(null, null, -12.0412, null), LevelMode.Track), 3);
    }

    [Fact]
    public void Untagged_files_play_6_dB_down()
    {
        Assert.Equal(0.501f, ReplayGain.GainFor(null, LevelMode.Track), 3);
        Assert.Equal(0.501f, ReplayGain.GainFor(null, LevelMode.Album), 3);
    }

    [Fact]
    public void A_boost_stops_at_the_peak_and_never_boosts_without_one()
    {
        Assert.Equal(1.25f, ReplayGain.GainFor(new ReplayGain(6, 0.8, null, null), LevelMode.Track), 3); // +6 dB would be 2x; peak 0.8 allows 1.25x
        Assert.Equal(1.995f, ReplayGain.GainFor(new ReplayGain(6, 0.25, null, null), LevelMode.Track), 2);
        Assert.Equal(1f, ReplayGain.GainFor(new ReplayGain(6, null, null, null), LevelMode.Track));
        Assert.Equal(0.5f, ReplayGain.GainFor(new ReplayGain(-6.0206, 1.3, null, null), LevelMode.Track), 3);
    }

    [Theory]
    [InlineData("-7.23 dB", -7.23)]
    [InlineData("+1.50 dB", 1.5)]
    [InlineData(" -3,4 DB ", -3.4)]
    [InlineData("0.987654", 0.987654)]
    [InlineData("-7.23 dB\0", -7.23)]
    public void Values_are_read_in_the_usual_spellings(string text, double expected) =>
        Assert.Equal(expected, ReplayGainReader.Number(text)!.Value, 6);

    [Theory]
    [InlineData("")]
    [InlineData("loud")]
    [InlineData("NaN")]
    public void Nonsense_is_no_value(string text) => Assert.Null(ReplayGainReader.Number(text));

    // ================= ID3v2 =================

    [Fact]
    public void Id3v23_txxx_frames_in_latin1()
    {
        var path = Write("a.mp3", Id3(3, 0,
            Frame3("TXXX", Txxx(0, "replaygain_track_gain", "-7.50 dB")),
            Frame3("TIT2", [0, .. "Song"u8]),
            Frame3("TXXX", Txxx(0, "REPLAYGAIN_TRACK_PEAK", "0.912345")),
            Frame3("TXXX", Txxx(0, "REPLAYGAIN_ALBUM_GAIN", "-8.10 dB"))), SomeAudio);
        var rg = ReplayGainReader.TryRead(path)!;
        Assert.Equal(-7.5, rg.TrackGain);
        Assert.Equal(0.912345, rg.TrackPeak);
        Assert.Equal(-8.1, rg.AlbumGain);
        Assert.Null(rg.AlbumPeak);
    }

    [Fact]
    public void Id3v24_in_utf8_and_utf16_with_padding_after()
    {
        var path = Write("b.mp3", Id3(4, 0,
            Frame4("TXXX", Txxx(3, "REPLAYGAIN_TRACK_GAIN", "-4.00 dB")),
            Frame4("TXXX", Txxx(1, "REPLAYGAIN_ALBUM_GAIN", "-5.25 dB")),
            new byte[64]), SomeAudio);
        var rg = ReplayGainReader.TryRead(path)!;
        Assert.Equal(-4.0, rg.TrackGain);
        Assert.Equal(-5.25, rg.AlbumGain);
    }

    [Fact]
    public void Id3v23_unsynchronised_tag_is_read()
    {
        // A frame whose size field holds 0xFF gets a 0x00 after it in an unsynchronised tag.
        var big = Txxx(0, "REPLAYGAIN_TRACK_GAIN", "-2.00 dB" + new string(' ', 255 - 31));
        var frame = Frame3("TXXX", big);
        Assert.Contains((byte)0xFF, frame);
        var body = new List<byte>();
        foreach (var b in frame) { body.Add(b); if (b == 0xFF) body.Add(0); }
        var path = Write("c.mp3", Id3(3, 0x80, [.. body]), SomeAudio);
        Assert.Equal(-2.0, ReplayGainReader.TryRead(path)!.TrackGain);
    }

    [Fact]
    public void A_file_without_tags_gives_null()
    {
        Assert.Null(ReplayGainReader.TryRead(Write("d.mp3", SomeAudio)));
        Assert.Null(ReplayGainReader.TryRead(Write("e.mp3", Id3(3, 0, Frame3("TIT2", [0, .. "Song"u8])), SomeAudio)));
        Assert.Null(ReplayGainReader.TryRead(_audio.Tone("f.wav", 0.1)));
        Assert.Null(ReplayGainReader.TryRead(Path.Combine(_audio.Folder, "missing.mp3")));
    }

    [Fact]
    public void A_cut_off_tag_gives_null_not_an_error()
    {
        var whole = Id3(3, 0, Frame3("TXXX", Txxx(0, "REPLAYGAIN_TRACK_GAIN", "-7.50 dB")));
        Assert.Null(ReplayGainReader.TryRead(Write("g.mp3", whole[..20])));
    }

    // ================= APEv2 (mp3gain) =================

    [Fact]
    public void Ape_tag_at_the_end_with_or_without_id3v1_after_it()
    {
        var ape = Ape(("REPLAYGAIN_TRACK_GAIN", "-9.10 dB"), ("REPLAYGAIN_TRACK_PEAK", "1.020000"), ("Artist", "X"));
        Assert.Equal(-9.1, ReplayGainReader.TryRead(Write("h.mp3", SomeAudio, ape))!.TrackGain);
        var id3v1 = new byte[128]; "TAG"u8.CopyTo(id3v1);
        var rg = ReplayGainReader.TryRead(Write("i.mp3", SomeAudio, ape, id3v1))!;
        Assert.Equal(-9.1, rg.TrackGain);
        Assert.Equal(1.02, rg.TrackPeak);
    }

    // ================= FLAC =================

    [Fact]
    public void Flac_vorbis_comments()
    {
        var streamInfo = Block(0, false, new byte[34]);
        var comments = Block(4, true, Vorbis("reference libFLAC", "TITLE=Song", "REPLAYGAIN_TRACK_GAIN=-6.48 dB", "REPLAYGAIN_ALBUM_GAIN=-7.02 dB", "REPLAYGAIN_ALBUM_PEAK=0.998"));
        var rg = ReplayGainReader.TryRead(Write("j.flac", "fLaC"u8.ToArray(), streamInfo, comments, SomeAudio))!;
        Assert.Equal(-6.48, rg.TrackGain);
        Assert.Equal(-7.02, rg.AlbumGain);
        Assert.Equal(0.998, rg.AlbumPeak);
    }

    // ================= MP4 =================

    [Fact]
    public void M4a_freeform_atoms_after_the_audio()
    {
        byte[] Free(string name, string value) => Box("----",
            Box("mean", [0, 0, 0, 0, .. "com.apple.iTunes"u8]),
            Box("name", [0, 0, 0, 0, .. Encoding.UTF8.GetBytes(name)]),
            Box("data", [0, 0, 0, 1, 0, 0, 0, 0, .. Encoding.UTF8.GetBytes(value)]));
        var ilst = Box("ilst", Box("©nam", Box("data", [0, 0, 0, 1, 0, 0, 0, 0, .. "Song"u8])),
            Free("replaygain_track_gain", "-3.30 dB"), Free("replaygain_track_peak", "0.750000"));
        var moov = Box("moov", Box("mvhd", new byte[100]), Box("udta", Box("meta", [0, 0, 0, 0, .. Box("hdlr", new byte[25]), .. ilst])));
        var rg = ReplayGainReader.TryRead(Write("k.m4a", Box("ftyp", [.. "M4A "u8, 0, 0, 0, 0]), Box("mdat", SomeAudio), moov))!;
        Assert.Equal(-3.3, rg.TrackGain);
        Assert.Equal(0.75, rg.TrackPeak);
    }

    [Fact]
    public void M4a_quicktime_keys_as_ffmpeg_writes_them()
    {
        byte[] Key(string name) { var b = Encoding.UTF8.GetBytes(name); var h = new byte[8]; BinaryPrimitives.WriteInt32BigEndian(h, b.Length + 8); "mdta"u8.CopyTo(h.AsSpan(4)); return [.. h, .. b]; }
        var keys = Box("keys", [0, 0, 0, 0, 0, 0, 0, 2, .. Key("replaygain_track_gain"), .. Key("encoder")]);
        var item1 = Box("\0\0\0\u0001", Box("data", [0, 0, 0, 1, 0, 0, 0, 0, .. "-4.40 dB"u8]));
        var item2 = Box("\0\0\0\u0002", Box("data", [0, 0, 0, 1, 0, 0, 0, 0, .. "Lavf"u8]));
        var meta = Box("meta", Box("hdlr", new byte[25]), keys, Box("ilst", item1, item2)); // not a full box here
        var moov = Box("moov", Box("mvhd", new byte[100]), Box("udta", meta));
        var rg = ReplayGainReader.TryRead(Write("l.m4a", Box("ftyp", [.. "M4A "u8, 0, 0, 0, 0]), Box("mdat", SomeAudio), moov))!;
        Assert.Equal(-4.4, rg.TrackGain);
    }

    // ================= mixer and engine =================

    [Fact]
    public void The_mixer_applies_each_tracks_gain_also_across_a_crossfade()
    {
        var m = new TrackMixer(1000);
        var a = new LevelDecoder(1, 0.8f, rate: 1000);
        var b = new LevelDecoder(1, 0.8f, rate: 1000);
        m.SetCurrent(a, 0.5f);
        var buf = new float[200];
        m.Read(buf);
        Assert.All(buf, s => Assert.Equal(0.4f, s, 5));
        Assert.True(m.SetGain(a, 0.25f));
        m.Read(buf);
        Assert.All(buf, s => Assert.Equal(0.2f, s, 5));
        Assert.True(m.Queue(b, TimeSpan.Zero, m.CurrentId, 1f));
        var rest = new float[2 * 1000];
        int n = m.Read(rest);
        Assert.Equal(0.2f, rest[0], 5);        // still a
        Assert.Equal(0.8f, rest[n - 1], 5);    // b at its own gain after the hand-over
        Assert.False(m.SetGain(new LevelDecoder(1, 0f), 1f));
    }

    [Fact]
    public async Task The_engine_levels_by_tags_and_a_change_applies_to_the_playing_track()
    {
        var outputs = new List<FakeOutput>();
        string? asked = null;
        using var engine = new PlayerEngine(p => new MediaFoundationDecoder(p), () =>
        {
            var o = new FakeOutput(); outputs.Add(o); return Task.FromResult<IAudioOutput>(o);
        }, p => { asked = p; return new ReplayGain(-6.0206, 0.5, -12.0412, 0.5); });
        var path = _audio.Tone("t.wav", 3, amplitude: 0.5f);
        await engine.LoadAsync(path);
        Assert.Equal(path, asked);
        Assert.Equal(-6.0206, engine.CurrentGainTags!.TrackGain);
        engine.Play();
        var o = outputs[^1];
        o.Pump(0.3); float off = o.LastPeak;
        engine.Levelling = LevelMode.Track;
        o.Pump(0.3); Assert.Equal(off / 2, o.LastPeak, 2);
        engine.Levelling = LevelMode.Album;
        o.Pump(0.3); Assert.Equal(off / 4, o.LastPeak, 2);
        engine.Levelling = LevelMode.Off;
        o.Pump(0.3); Assert.Equal(off, o.LastPeak, 2);
    }

    [Fact]
    public async Task Stations_are_never_read_for_tags()
    {
        bool asked = false;
        var dec = new LevelDecoder(0, 0.5f);
        using var engine = new PlayerEngine(_ => dec, () => Task.FromResult<IAudioOutput>(new FakeOutput()), _ => { asked = true; return null; });
        await engine.LoadAsync("http://example.invalid/stream");
        Assert.False(asked);
        Assert.Null(engine.CurrentGainTags);
    }

    // ================= builders =================

    private string Write(string name, params byte[][] parts)
    {
        var path = Path.Combine(_audio.Folder, name);
        File.WriteAllBytes(path, parts.SelectMany(p => p).ToArray());
        return path;
    }

    private static byte[] Id3(int major, byte flags, params byte[][] frames)
    {
        var body = frames.SelectMany(f => f).ToArray();
        int n = body.Length;
        byte[] h = [(byte)'I', (byte)'D', (byte)'3', (byte)major, 0, flags,
            (byte)((n >> 21) & 0x7F), (byte)((n >> 14) & 0x7F), (byte)((n >> 7) & 0x7F), (byte)(n & 0x7F)];
        return [.. h, .. body];
    }

    private static byte[] Frame3(string id, byte[] data)
    {
        var h = new byte[10];
        Encoding.ASCII.GetBytes(id).CopyTo(h, 0);
        BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(4), data.Length);
        return [.. h, .. data];
    }

    private static byte[] Frame4(string id, byte[] data)
    {
        int n = data.Length;
        var h = new byte[10];
        Encoding.ASCII.GetBytes(id).CopyTo(h, 0);
        h[4] = (byte)((n >> 21) & 0x7F); h[5] = (byte)((n >> 14) & 0x7F); h[6] = (byte)((n >> 7) & 0x7F); h[7] = (byte)(n & 0x7F);
        return [.. h, .. data];
    }

    private static byte[] Txxx(byte enc, string key, string value) => enc switch
    {
        1 => [1, 0xFF, 0xFE, .. Encoding.Unicode.GetBytes(key), 0, 0, 0xFF, 0xFE, .. Encoding.Unicode.GetBytes(value)],
        3 => [3, .. Encoding.UTF8.GetBytes(key), 0, .. Encoding.UTF8.GetBytes(value)],
        _ => [0, .. Encoding.Latin1.GetBytes(key), 0, .. Encoding.Latin1.GetBytes(value)],
    };

    private static byte[] Ape(params (string Key, string Value)[] items)
    {
        var body = new List<byte>();
        foreach (var (k, v) in items)
        {
            var val = Encoding.UTF8.GetBytes(v);
            var h = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(h, val.Length);
            body.AddRange(h); body.AddRange(Encoding.ASCII.GetBytes(k)); body.Add(0); body.AddRange(val);
        }
        var footer = new byte[32];
        "APETAGEX"u8.CopyTo(footer);
        BinaryPrimitives.WriteInt32LittleEndian(footer.AsSpan(8), 2000);
        BinaryPrimitives.WriteInt32LittleEndian(footer.AsSpan(12), body.Count + 32);
        BinaryPrimitives.WriteInt32LittleEndian(footer.AsSpan(16), items.Length);
        return [.. body, .. footer];
    }

    private static byte[] Block(int type, bool last, byte[] data) =>
        [(byte)(type | (last ? 0x80 : 0)), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length, .. data];

    private static byte[] Vorbis(string vendor, params string[] comments)
    {
        var o = new List<byte>();
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s); var n = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(n, b.Length); o.AddRange(n); o.AddRange(b); }
        Str(vendor);
        var c = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(c, comments.Length); o.AddRange(c);
        foreach (var s in comments) Str(s);
        return [.. o];
    }

    private static byte[] Box(string type, params byte[][] children)
    {
        var body = children.SelectMany(c => c).ToArray();
        var h = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(h, body.Length + 8);
        Encoding.Latin1.GetBytes(type).CopyTo(h, 4);
        return [.. h, .. body];
    }
}
