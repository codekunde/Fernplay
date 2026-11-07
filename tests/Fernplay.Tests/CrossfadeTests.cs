using Fernplay.Core;
using Fernplay.Core.Audio;
using NAudio.Wave;

namespace Fernplay.Tests;

/// <summary>Decoder made of a constant level per channel, so the mix can be read sample by sample.</summary>
internal sealed class LevelDecoder(double seconds, float level, int rate = 1000, int channels = 2) : ITrackDecoder
{
    private readonly long _total = (long)(seconds * rate);
    private long _pos;
    public bool Disposed { get; private set; }
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
    public TimeSpan TotalTime => TimeSpan.FromSeconds((double)_total / rate);
    public TimeSpan CurrentTime => TimeSpan.FromSeconds((double)_pos / rate);
    public AudioFormatInfo Format => new("TEST", rate, channels);

    public int Read(Span<float> buffer)
    {
        int frames = (int)Math.Min(buffer.Length / channels, _total - _pos);
        buffer[..(frames * channels)].Fill(level);
        _pos += frames;
        return frames * channels;
    }

    public void Seek(TimeSpan position) => _pos = Math.Clamp((long)(position.TotalSeconds * WaveFormat.SampleRate), 0, _total);
    public void Dispose() => Disposed = true;
}

public sealed class TrackMixerTests
{
    private const int Rate = 1000; // 1 frame = 1 ms, easy to count

    private static float[] Pull(TrackMixer m, int frames, out int got)
    {
        var buf = new float[frames * 2];
        got = m.Read(buf) / 2;
        return buf;
    }

    [Fact]
    public void Gapless_hand_over_has_no_gap_and_no_overlap()
    {
        var m = new TrackMixer(Rate);
        var a = new LevelDecoder(0.5, 0.25f);
        var b = new LevelDecoder(0.5, 0.75f);
        var switched = new List<ITrackDecoder>();
        m.Switched += (_, d) => switched.Add(d);
        m.SetCurrent(a);
        Assert.True(m.Queue(b, TimeSpan.Zero, m.CurrentId));
        var buf = Pull(m, 800, out int got);
        Assert.Equal(800, got);
        Assert.All(buf[..1000], s => Assert.Equal(0.25f, s));   // frames 0..499: a
        Assert.All(buf[1000..], s => Assert.Equal(0.75f, s));   // frames 500..799: b, the very next frame
        Assert.Equal([b], switched);
        Assert.Same(b, m.Current);
        Assert.False(m.ReachedEnd);
    }

    [Fact]
    public void Crossfade_starts_on_the_exact_frame_and_keeps_equal_power()
    {
        var m = new TrackMixer(Rate);
        var a = new LevelDecoder(2.0, 1f);
        var b = new LevelDecoder(2.0, 1f);
        int switchedAt = -1, pulled = 0;
        m.Switched += (_, _) => switchedAt = pulled;
        m.SetCurrent(a);
        m.Queue(b, TimeSpan.FromSeconds(0.5), m.CurrentId);
        var all = new List<float>();
        for (int i = 0; i < 25; i++) { all.AddRange(Pull(m, 100, out _)); pulled += 100; } // 2.5 s in 100 ms reads
        // The fade starts 0.5 s before a's end (frame 1500): a alone until then.
        Assert.Equal(1500, switchedAt); // raised in the read that starts at frame 1500
        for (int f = 0; f < 1500; f++) Assert.Equal(1f, all[2 * f]);
        // During the fade both tracks are 1.0: sum = sin + cos, and power sin² + cos² = 1.
        for (int f = 1500; f < 2000; f++)
        {
            var (gin, gout) = TrackMixer.FadeGains((f - 1500) / 500.0);
            Assert.Equal(gin + gout, all[2 * f], 3);
            Assert.Equal(1.0, gin * gin + gout * gout, 6);
        }
        // After the fade: b alone, full level; b's clock started at the fade.
        for (int f = 2000; f < 2500; f++) Assert.Equal(1f, all[2 * f]);
        Assert.Same(b, m.Current);
        Assert.Equal(1.0, b.CurrentTime.TotalSeconds, 2);
        Assert.False(m.IsFading);
        Assert.True(a.Disposed || SpinUntil(() => a.Disposed));
    }

    [Fact]
    public void Fade_curve_runs_from_old_to_new()
    {
        var m = new TrackMixer(Rate);
        m.SetCurrent(new LevelDecoder(1.0, 1f));
        m.Queue(new LevelDecoder(1.0, 0f), TimeSpan.FromSeconds(0.4), m.CurrentId); // silent new track shows the old one's curve
        var buf = Pull(m, 1000, out _);
        Assert.Equal(1f, buf[2 * 599]);                  // just before the fade
        Assert.Equal(1.0, buf[2 * 600], 3);              // fade start: old at full
        Assert.Equal(Math.Cos(Math.PI / 4), buf[2 * 800], 2); // half way: -3 dB
        Assert.True(buf[2 * 999] < 0.01);                // end: old gone
    }

    [Fact]
    public void Natural_end_with_nothing_queued_returns_short_then_nothing()
    {
        var m = new TrackMixer(Rate);
        m.SetCurrent(new LevelDecoder(0.3, 0.5f));
        Pull(m, 500, out int got);
        Assert.Equal(300, got);
        Assert.True(m.ReachedEnd);
        Pull(m, 100, out got);
        Assert.Equal(0, got);
    }

    [Fact]
    public void NearEnd_fires_once_at_the_lead_and_again_after_a_cancel()
    {
        var m = new TrackMixer(Rate) { NearEndLead = TimeSpan.FromSeconds(0.3) };
        var fired = new List<int>();
        m.NearEnd += (_, id) => fired.Add(id);
        m.SetCurrent(new LevelDecoder(1.0, 0.5f));
        Pull(m, 600, out _);
        Assert.Empty(fired);
        Pull(m, 100, out _); // 700 read, 300 left
        Assert.Equal([m.CurrentId], fired);
        Pull(m, 100, out _);
        Assert.Single(fired);
        m.CancelQueued(); // e.g. the playlist changed: ask again
        Pull(m, 10, out _);
        Assert.Equal(2, fired.Count);
    }

    [Fact]
    public void Queue_for_a_track_no_longer_playing_is_refused()
    {
        var m = new TrackMixer(Rate);
        m.SetCurrent(new LevelDecoder(1.0, 0.5f));
        int old = m.CurrentId;
        m.SetCurrent(new LevelDecoder(1.0, 0.5f)); // the user picked another track meanwhile
        Assert.False(m.Queue(new LevelDecoder(1.0, 0.5f), TimeSpan.Zero, old));
        Assert.False(m.HasQueued);
    }

    [Fact]
    public void Seeking_into_the_fade_zone_starts_the_fade_from_there()
    {
        var m = new TrackMixer(Rate);
        var a = new LevelDecoder(5.0, 1f);
        var b = new LevelDecoder(5.0, 0f);
        m.SetCurrent(a);
        m.Queue(b, TimeSpan.FromSeconds(1), m.CurrentId);
        Pull(m, 100, out _);
        m.Seek(TimeSpan.FromSeconds(4.6)); // 0.4 s left, fade was 1 s
        var buf = Pull(m, 500, out _);
        Assert.Same(b, m.Current);
        Assert.Equal(1.0, buf[0], 3);            // the fade begins at once, from full
        Assert.True(buf[2 * 399] < 0.01);        // and ends with a's real end, 0.4 s later
        Assert.Equal(0f, buf[2 * 450]);
    }

    [Fact]
    public void Seeking_back_keeps_the_queued_track_and_does_not_ask_again()
    {
        var m = new TrackMixer(Rate) { NearEndLead = TimeSpan.FromSeconds(0.5) };
        int asked = 0;
        m.NearEnd += (_, _) => asked++;
        var a = new LevelDecoder(2.0, 0.5f);
        var b = new LevelDecoder(2.0, 0.5f);
        m.SetCurrent(a);
        Pull(m, 1600, out _);
        Assert.Equal(1, asked);
        m.Queue(b, TimeSpan.Zero, m.CurrentId);
        m.Seek(TimeSpan.FromSeconds(0.2));
        Pull(m, 2000, out _); // past a's end again (1800 frames of a, then b)
        Assert.Equal(1, asked);
        Assert.Same(b, m.Current);
    }

    [Fact]
    public void Repeat_one_loops_the_same_file_gapless()
    {
        // Repeat track with XFADE on: the same file is queued again, without a fade.
        var m = new TrackMixer(Rate);
        m.SetCurrent(new LevelDecoder(0.2, 0.4f));
        m.Queue(new LevelDecoder(0.2, 0.4f), TimeSpan.Zero, m.CurrentId);
        var buf = Pull(m, 400, out int got);
        Assert.Equal(400, got);
        Assert.All(buf, s => Assert.Equal(0.4f, s));
    }

    [Fact]
    public void A_new_track_that_ends_during_the_fade_lets_the_old_one_go_and_the_mix_ends()
    {
        var m = new TrackMixer(Rate);
        m.SetCurrent(new LevelDecoder(1.0, 1f));
        m.Queue(new LevelDecoder(0.1, 1f), TimeSpan.FromSeconds(0.5), m.CurrentId);
        Pull(m, 2000, out int got);
        Assert.Equal(600, got); // 500 of a alone, then the 100 ms track
        Assert.True(m.ReachedEnd);
        Assert.False(m.IsFading);
    }

    [Fact]
    public void Mono_and_other_rates_are_brought_to_the_mixer_format()
    {
        var m = new TrackMixer(48000);
        var mono = new LevelDecoder(0.5, 0.5f, rate: 48000, channels: 1);
        m.SetCurrent(mono);
        var buf = Pull(m, 4800, out int got);
        Assert.Equal(4800, got);
        Assert.Equal(0.5f, buf[0]); Assert.Equal(0.5f, buf[1]); // both sides

        var other = new LevelDecoder(1.0, 0.5f, rate: 44100);
        m.SetCurrent(other);
        int total = 0;
        do { Pull(m, 4800, out got); total += got; } while (got > 0);
        Assert.InRange(total, 47800, 48200); // one second at 48 kHz, within a resampler block
    }

    [Fact]
    public void Five_one_is_mixed_down_without_clipping()
    {
        var m = new TrackMixer(Rate);
        m.SetCurrent(new LevelDecoder(0.1, 1f, channels: 6));
        var buf = Pull(m, 50, out _);
        Assert.Equal(1.0, buf[0], 3);
        Assert.Equal(1.0, buf[1], 3);
    }

    private static bool SpinUntil(Func<bool> done) => SpinWait.SpinUntil(done, TimeSpan.FromSeconds(2));
}

public sealed class CrossfadeEngineTests : IDisposable
{
    private readonly TestAudio _audio = new();
    private readonly List<FakeOutput> _outputs = [];
    private readonly PlayerEngine _engine;

    public CrossfadeEngineTests()
    {
        _engine = new PlayerEngine(path => new MediaFoundationDecoder(path), () =>
        {
            var o = new FakeOutput();
            _outputs.Add(o);
            return Task.FromResult<IAudioOutput>(o);
        });
    }

    public void Dispose()
    {
        _engine.Dispose();
        _audio.Dispose();
    }

    private FakeOutput Output => _outputs[^1];

    [Fact]
    public async Task Queued_track_follows_gapless_on_one_output_and_reports_the_change()
    {
        int ended = 0, near = -1;
        string? changed = null;
        _engine.TrackEnded += (_, _) => ended++;
        _engine.NearEnd += (_, id) => near = id;
        _engine.TrackChanged += (_, p) => changed = p;
        await _engine.LoadAsync(_audio.Tone("a.wav", 4.0));
        _engine.Play();
        Output.Pump(0.9);
        Assert.Equal(-1, near); // 3.1 s left, lead 3 s
        Output.Pump(0.2);
        Assert.True(near >= 0);
        var b = _audio.Tone("b.wav", 2.0, amplitude: 0.25f);
        Assert.True(await _engine.QueueNextAsync(b, near));
        Output.Pump(3.4); // 4.5 s: through a's end, 0.5 s into b
        Assert.Equal(b, changed);
        Assert.Equal(b, _engine.CurrentPath);
        Assert.Equal(PlayState.Playing, _engine.State);
        Assert.Equal(0, ended);
        Assert.Single(_outputs);
        Assert.Equal(0.5, _engine.Position.TotalSeconds, 1);
        Output.Pump(3.0);
        Assert.Equal(1, ended); // b ends with nothing queued
    }

    [Fact]
    public async Task Crossfade_is_used_only_when_both_tracks_are_long_enough()
    {
        _engine.Crossfade = TimeSpan.FromSeconds(2);
        int near = -1;
        _engine.NearEnd += (_, id) => near = id;
        await _engine.LoadAsync(_audio.Tone("a.wav", 10.0));
        _engine.Play();
        Output.Pump(4.9);
        Assert.Equal(-1, near); // lead = 2 s fade + 3 s to open
        Output.Pump(0.2);
        Assert.True(near >= 0);
        // 3 s next track is shorter than twice the fade: gapless instead.
        Assert.True(await _engine.QueueNextAsync(_audio.Tone("short.wav", 3.0), near));
        string? changed = null;
        _engine.TrackChanged += (_, p) => changed = p;
        Output.Pump(3.0); // 8.1 s: a fade would have started at 8 s
        Assert.Null(changed);
        Output.Pump(2.0);
        Assert.EndsWith("short.wav", changed);
    }

    [Fact]
    public async Task Crossfade_switches_at_fade_start()
    {
        _engine.Crossfade = TimeSpan.FromSeconds(2);
        int near = -1;
        string? changed = null;
        _engine.NearEnd += (_, id) => near = id;
        _engine.TrackChanged += (_, p) => changed = p;
        await _engine.LoadAsync(_audio.Tone("a.wav", 8.0));
        _engine.Play();
        Output.Pump(3.5);
        Assert.True(await _engine.QueueNextAsync(_audio.Tone("b.wav", 8.0), near));
        Output.Pump(2.4); // 5.9 s
        Assert.Null(changed);
        Output.Pump(0.2); // 6.1 s: fade began at 6 s
        Assert.EndsWith("b.wav", changed);
        Assert.Equal(0.1, _engine.Position.TotalSeconds, 1);
    }

    [Fact]
    public async Task Gapless_flag_overrides_the_crossfade()
    {
        _engine.Crossfade = TimeSpan.FromSeconds(1);
        int near = -1;
        string? changed = null;
        _engine.NearEnd += (_, id) => near = id;
        _engine.TrackChanged += (_, p) => changed = p;
        var a = _audio.Tone("a.wav", 6.0);
        await _engine.LoadAsync(a);
        _engine.Play();
        Output.Pump(2.5);
        Assert.True(await _engine.QueueNextAsync(a, near, gapless: true)); // repeat track
        Output.Pump(3.4);
        Assert.Null(changed);
        Output.Pump(0.2);
        Assert.Equal(a, changed);
    }

    [Fact]
    public async Task A_next_file_that_fails_to_open_throws_and_the_track_ends_normally()
    {
        int ended = 0, near = -1;
        _engine.TrackEnded += (_, _) => ended++;
        _engine.NearEnd += (_, id) => near = id;
        await _engine.LoadAsync(_audio.Tone("a.wav", 1.0));
        _engine.Play();
        Output.Pump(0.1);
        var bad = Path.Combine(_audio.Folder, "bad.mp3");
        File.WriteAllText(bad, "not audio");
        await Assert.ThrowsAnyAsync<Exception>(() => _engine.QueueNextAsync(bad, near));
        Output.Pump(2.0);
        Assert.Equal(1, ended);
    }

    [Fact]
    public async Task A_manual_load_drops_the_queued_track()
    {
        int near = -1;
        string? changed = null;
        _engine.NearEnd += (_, id) => near = id;
        _engine.TrackChanged += (_, p) => changed = p;
        await _engine.LoadAsync(_audio.Tone("a.wav", 2.0));
        _engine.Play();
        Output.Pump(0.1);
        Assert.True(await _engine.QueueNextAsync(_audio.Tone("b.wav", 2.0), near));
        await _engine.LoadAsync(_audio.Tone("c.wav", 1.0)); // Next pressed: cut, no fade, queue gone
        Assert.False(_engine.HasQueued);
        _engine.Play();
        Output.Pump(3.0);
        Assert.Null(changed);
    }

    [Fact]
    public async Task Stop_cuts_a_fade_and_rewinds_the_new_track()
    {
        _engine.Crossfade = TimeSpan.FromSeconds(2);
        _engine.Volume = 1f;
        int near = -1;
        _engine.NearEnd += (_, id) => near = id;
        await _engine.LoadAsync(_audio.Tone("a.wav", 6.0, amplitude: 0.5f));
        _engine.Play();
        Output.Pump(1.5);
        Assert.True(await _engine.QueueNextAsync(_audio.Tone("b.wav", 6.0, amplitude: 0.25f), near));
        Output.Pump(3.0); // 4.5 s: fading for 0.5 s
        _engine.Stop();
        Assert.EndsWith("b.wav", _engine.CurrentPath);
        Assert.Equal(TimeSpan.Zero, _engine.Position);
        _engine.Play();
        Output.Pump(0.2);
        Assert.Equal(0.25, Output.LastPeak, 2); // b alone, nothing of a left
    }
}
