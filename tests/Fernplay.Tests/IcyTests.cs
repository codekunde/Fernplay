using System.Text;
using Fernplay.Core.Audio;
using NAudio.Wave;

namespace Fernplay.Tests;

public sealed class IcyMetadataTests
{
    [Theory]
    [InlineData("StreamTitle='Level 42 - Lessons In love';", "Level 42 - Lessons In love")]
    [InlineData("StreamTitle='Guns N' Roses - Paradise City';StreamUrl='';", "Guns N' Roses - Paradise City")]
    [InlineData("StreamTitle='';json='{}';", "")]
    [InlineData("StreamTitle='  *** Wetter ';", "*** Wetter")]
    [InlineData("StreamTitle='No end quote", "No end quote")]
    public void Reads_the_stream_title(string block, string expected) => Assert.Equal(expected, IcyMetadata.StreamTitle(block));

    [Fact]
    public void A_block_without_a_title_gives_null() => Assert.Null(IcyMetadata.StreamTitle("StreamUrl='http://x';"));

    [Fact]
    public void Utf8_is_read_and_padding_dropped()
    {
        var bytes = Encoding.UTF8.GetBytes("StreamTitle='Motörhead - Ace';").Concat(new byte[9]).ToArray();
        Assert.Equal("StreamTitle='Motörhead - Ace';", IcyMetadata.Decode(bytes));
    }

    [Fact]
    public void Latin1_is_read_when_not_utf8()
    {
        var bytes = Encoding.Latin1.GetBytes("StreamTitle='Motörhead - Ace';");
        Assert.Equal("Motörhead - Ace", IcyMetadata.StreamTitle(IcyMetadata.Decode(bytes)));
    }
}

public sealed class IcyStreamTests
{
    [Fact]
    public void Hands_out_only_the_audio_and_keeps_the_titles()
    {
        var audio = Enumerable.Range(0, 1000).Select(i => (byte)(i % 251)).ToArray();
        var wire = Icy.Wrap(audio, 100, new() { [0] = "A - One", [5] = "B - Two" });
        using var icy = new IcyStream(new TrickleStream(wire, 7), 100);

        var got = new List<byte>();
        var titles = new List<string>();
        var buf = new byte[64];
        int n;
        while ((n = icy.Read(buf, 0, buf.Length)) > 0)
        {
            got.AddRange(buf.Take(n));
            if (icy.TryTakeTitle(out var t)) titles.Add(t);
        }
        Assert.Equal(audio, got.ToArray());
        Assert.Equal(["A - One", "B - Two"], titles);
    }

    [Fact]
    public void Fills_each_read_although_the_network_returns_little()
    {
        var wire = Icy.Wrap(new byte[500], 100, []);
        using var icy = new IcyStream(new TrickleStream(wire, 3), 100);
        Assert.Equal(417, icy.Read(new byte[417], 0, 417));
    }
}

[Collection(WindowsEncoder.Name)]
public sealed class IcyMp3DecoderTests : IDisposable
{
    private readonly TestAudio _audio = new();
    public void Dispose() => _audio.Dispose();

    /// <summary>An 8-second tone as a 128 kbit/s MP3 (Windows' own encoder), the bytes only.</summary>
    private byte[] Mp3(double seconds = 8)
    {
        var wav = _audio.Tone("tone.wav", seconds);
        var mp3 = Path.Combine(_audio.Folder, "tone.mp3");
        using (var reader = new WaveFileReader(wav)) MediaFoundationEncoder.EncodeToMp3(reader, mp3, 128000);
        return File.ReadAllBytes(mp3);
    }

    private static float[] ReadSeconds(ISampleProvider p, double seconds, out int read)
    {
        var buf = new float[(int)(seconds * p.WaveFormat.SampleRate) * p.WaveFormat.Channels];
        read = p.Read(buf);
        return buf;
    }

    /// <summary>Reads 0.1 s at a time, like an audio thread, until <paramref name="seconds"/> of the station's sound has played (silence while it waits does not count) or it ends.</summary>
    private static void PlayUntil(IcyMp3Decoder d, double seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (d.CurrentTime.TotalSeconds < seconds && DateTime.UtcNow < deadline)
        {
            ReadSeconds(d, 0.1, out int n);
            if (n == 0) return;
            Thread.Sleep(1);
        }
    }

    [Fact]
    public void Plays_the_mp3_and_reports_the_format_and_station()
    {
        var mp3 = Mp3();
        using var d = new IcyMp3Decoder(new MemoryStream(Icy.Wrap(mp3, 8000, [])), 8000, " Test FM ");
        Assert.Equal("Test FM", d.StationName);
        Assert.Equal(44100, d.WaveFormat.SampleRate);
        Assert.Equal(2, d.WaveFormat.Channels);
        Assert.Equal(128, d.Format.BitrateKbps);
        Assert.Equal(TimeSpan.Zero, d.TotalTime);

        ReadSeconds(d, 1, out int n);
        Assert.Equal(44100 * 2, n);
        Assert.InRange(d.CurrentTime.TotalSeconds, 0.99, 1.01);
    }

    [Fact]
    public void A_title_changes_when_its_part_of_the_sound_is_played()
    {
        var mp3 = Mp3();
        // 128 kbit/s = 16,000 bytes a second. A block follows every 8,000 audio bytes: block 0 after 0.5 s of
        // sound, block 6 after 3.5 s (7 × 8,000 bytes). Times are read after the 0.1 s read that reported them.
        var wire = Icy.Wrap(mp3, 8000, new() { [0] = "Artist - First", [6] = "Artist - Second" });
        using var d = new IcyMp3Decoder(new MemoryStream(wire), 8000, null);
        var seen = new List<(double At, string? Title)>();
        d.TitleChanged += (_, t) => seen.Add((d.CurrentTime.TotalSeconds, t));

        PlayUntil(d, 5);

        Assert.Equal(2, seen.Count);
        Assert.Equal("Artist - First", seen[0].Title);
        Assert.InRange(seen[0].At, 0.45, 0.65);
        Assert.Equal("Artist - Second", seen[1].Title);
        Assert.InRange(seen[1].At, 3.45, 3.65);
        Assert.Equal("Artist - Second", d.Title);
    }

    [Fact]
    public void An_empty_title_means_no_song()
    {
        var wire = Icy.Wrap(Mp3(2), 8000, new() { [0] = "Artist - Song", [1] = "" });
        using var d = new IcyMp3Decoder(new MemoryStream(wire), 8000, null);
        PlayUntil(d, 1.5);
        Assert.Null(d.Title);
    }

    [Fact]
    public void The_end_of_the_stream_is_reported_after_the_sound_is_played()
    {
        using var d = new IcyMp3Decoder(new MemoryStream(Icy.Wrap(Mp3(2), 8000, [])), 8000, null);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        int n;
        do { ReadSeconds(d, 0.5, out n); Thread.Sleep(1); } while (n > 0 && DateTime.UtcNow < deadline);
        Assert.Equal(0, n);
        Assert.InRange(d.CurrentTime.TotalSeconds, 1.9, 2.2);
        Assert.Null(d.Error);
    }

    [Fact]
    public void Silence_fills_a_slow_station_and_a_stalled_one_ends()
    {
        var old = IcyMp3Decoder.StallLimit;
        IcyMp3Decoder.StallLimit = TimeSpan.FromMilliseconds(300);
        try
        {
            var mp3 = Mp3(1);
            using var d = new IcyMp3Decoder(new HangingStream(Icy.Wrap(mp3, 8000, [])), 8000, null);
            var buf = ReadSeconds(d, 3, out int n); // more than the station sent: the rest is silence
            Assert.Equal(buf.Length, n);
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while ((n = d.Read(new float[8820])) > 0 && DateTime.UtcNow < deadline) Thread.Sleep(20); // 0.1 s per read
            Assert.Equal(0, n);
            Assert.IsType<TimeoutException>(d.Error);
        }
        finally { IcyMp3Decoder.StallLimit = old; }
    }
}

/// <summary>Builds an ICY stream: audio with a metadata block after every metaInt bytes (titles by block number).</summary>
internal static class Icy
{
    public static byte[] Wrap(byte[] audio, int metaInt, Dictionary<int, string> titles)
    {
        var o = new MemoryStream();
        for (int pos = 0, block = 0; pos < audio.Length; pos += metaInt, block++)
        {
            o.Write(audio, pos, Math.Min(metaInt, audio.Length - pos));
            if (pos + metaInt > audio.Length) break; // no block after a partial chunk
            if (titles.TryGetValue(block, out var t))
            {
                var text = Encoding.UTF8.GetBytes($"StreamTitle='{t}';");
                int len = (text.Length + 15) / 16;
                o.WriteByte((byte)len);
                o.Write(text);
                o.Write(new byte[len * 16 - text.Length]);
            }
            else o.WriteByte(0);
        }
        return o.ToArray();
    }
}

/// <summary>Returns at most a few bytes per read, as a network stream may.</summary>
internal sealed class TrickleStream(byte[] data, int max) : MemoryStream(data)
{
    public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, max));
    public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, max)]);
}

/// <summary>Sends its data, then never answers again until disposed (a station that went quiet).</summary>
internal sealed class HangingStream(byte[] data) : MemoryStream(data)
{
    private readonly ManualResetEventSlim _closed = new();
    // Only the array overload: MemoryStream sends span reads of a subclass to it.
    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = base.Read(buffer, offset, count);
        if (n > 0) return n;
        _closed.Wait();
        throw new ObjectDisposedException(nameof(HangingStream));
    }
    protected override void Dispose(bool disposing) { _closed.Set(); base.Dispose(disposing); }
}
