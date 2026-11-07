using Fernplay.Core;
using Fernplay.Core.Audio;

namespace Fernplay.Tests;

public sealed class PlayerEngineTests : IDisposable
{
    private readonly TestAudio _audio = new();
    private readonly List<FakeOutput> _outputs = [];
    private readonly PlayerEngine _engine;

    public PlayerEngineTests()
    {
        _engine = new PlayerEngine(path => new MediaFoundationDecoder(path), () =>
        {
            var o = new FakeOutput();
            _outputs.Add(o);
            return Task.FromResult<IAudioOutput>(o);
        });
    }

    private FakeOutput Output => _outputs[^1];

    public void Dispose()
    {
        _engine.Dispose();
        _audio.Dispose();
    }

    [Fact]
    public async Task Load_reads_duration_and_format()
    {
        await _engine.LoadAsync(_audio.Tone("a.wav", 2.0, sampleRate: 48000, channels: 1));
        Assert.Equal(PlayState.Stopped, _engine.State);
        Assert.Equal(2.0, _engine.Duration.TotalSeconds, 2);
        Assert.Equal(new AudioFormatInfo("WAV", 48000, 1), _engine.Format);
    }

    [Fact]
    public async Task A_reloaded_track_can_show_paused_and_continues_from_its_place()
    {
        // File info saved while paused: the file is let go of, loaded again, set back to its place and shown paused.
        _engine.ShowPaused(); // nothing loaded: no change
        Assert.Equal(PlayState.Stopped, _engine.State);
        await _engine.LoadAsync(_audio.Tone("a.wav", 3.0));
        _engine.Seek(TimeSpan.FromSeconds(1.5));
        _engine.ShowPaused();
        Assert.Equal(PlayState.Paused, _engine.State);
        Assert.False(Output.Playing); // no sound while paused
        _engine.Play();
        Assert.True(Output.Playing);
        Output.Pump(0.5);
        Assert.Equal(2.0, _engine.Position.TotalSeconds, 1);
    }

    [Fact]
    public async Task Play_pause_stop_change_state_and_stop_rewinds()
    {
        await _engine.LoadAsync(_audio.Tone("a.wav", 3.0));
        _engine.Play();
        Assert.Equal(PlayState.Playing, _engine.State);
        Output.Pump(1.0);
        Assert.Equal(1.0, _engine.Position.TotalSeconds, 1);

        _engine.Pause();
        Assert.Equal(PlayState.Paused, _engine.State);
        Assert.False(Output.Playing);

        _engine.Stop();
        Assert.Equal(PlayState.Stopped, _engine.State);
        Assert.Equal(TimeSpan.Zero, _engine.Position);
    }

    [Fact]
    public async Task Natural_end_raises_TrackEnded_but_Stop_does_not()
    {
        int ended = 0;
        _engine.TrackEnded += (_, _) => ended++;
        await _engine.LoadAsync(_audio.Tone("a.wav", 0.5));

        _engine.Play();
        _engine.Stop();
        Assert.Equal(0, ended);

        _engine.Play();
        Output.Pump(1.0);
        Assert.Equal(1, ended);
        Assert.Equal(PlayState.Stopped, _engine.State);
    }

    [Fact]
    public async Task Play_after_natural_end_starts_from_the_beginning()
    {
        await _engine.LoadAsync(_audio.Tone("a.wav", 0.5));
        _engine.Play();
        Output.Pump(1.0);
        _engine.Play();
        Assert.True(_engine.Position < TimeSpan.FromSeconds(0.1));
    }

    [Fact]
    public async Task Seek_moves_the_position_and_clamps()
    {
        await _engine.LoadAsync(_audio.Tone("a.wav", 3.0));
        _engine.Seek(TimeSpan.FromSeconds(2));
        Assert.Equal(2.0, _engine.Position.TotalSeconds, 1);
        _engine.Seek(TimeSpan.FromSeconds(-5));
        Assert.Equal(TimeSpan.Zero, _engine.Position);
        _engine.Seek(TimeSpan.FromMinutes(5));
        Assert.Equal(3.0, _engine.Position.TotalSeconds, 1);
    }

    [Fact]
    public async Task Volume_scales_the_samples()
    {
        await _engine.LoadAsync(_audio.Tone("a.wav", 2.0, amplitude: 0.5f));
        _engine.Volume = 1f;
        _engine.Play();
        Output.Pump(0.2);
        Assert.Equal(0.5, Output.LastPeak, 2);

        _engine.Volume = 0.5f; // curve: 0.5 → gain 0.25
        Output.Pump(0.2);
        Assert.Equal(0.125, Output.LastPeak, 2);

        _engine.Volume = 0f;
        Output.Pump(0.2);
        Assert.Equal(0, Output.LastPeak);
    }

    [Fact]
    public async Task Loading_a_new_track_keeps_the_output_open_and_does_not_count_as_an_end()
    {
        int ended = 0;
        _engine.TrackEnded += (_, _) => ended++;
        await _engine.LoadAsync(_audio.Tone("a.wav", 0.5));
        _engine.Play();
        Output.Pump(0.2);
        await _engine.LoadAsync(_audio.Tone("b.wav", 0.5));
        Assert.Single(_outputs);
        Assert.False(Output.Disposed);
        Assert.Equal(0, ended);
        Assert.EndsWith("b.wav", _engine.CurrentPath);
        Assert.Equal(PlayState.Stopped, _engine.State);
        _engine.Play();
        Output.Pump(0.2);
        Assert.Equal(0.2, _engine.Position.TotalSeconds, 1);
    }

    [Fact]
    public async Task After_a_device_error_the_next_load_opens_a_new_output()
    {
        await _engine.LoadAsync(_audio.Tone("a.wav", 1.0));
        _engine.Play();
        var first = Output;
        first.Fail(new InvalidOperationException("device gone"));
        await _engine.LoadAsync(_audio.Tone("b.wav", 1.0));
        Assert.Equal(2, _outputs.Count);
        Assert.True(first.Disposed);
        first.Fail(new InvalidOperationException()); // a late event from the old output is ignored
        Assert.Equal(PlayState.Stopped, _engine.State);
    }

    [Fact]
    public async Task Device_error_raises_PlaybackFailed()
    {
        Exception? failed = null;
        _engine.PlaybackFailed += (_, e) => failed = e;
        await _engine.LoadAsync(_audio.Tone("a.wav", 1.0));
        _engine.Play();
        Output.Fail(new InvalidOperationException("device gone"));
        Assert.Equal("device gone", failed?.Message);
        Assert.Equal(PlayState.Stopped, _engine.State);
    }

    [Fact]
    public async Task Unreadable_file_throws_and_leaves_nothing_loaded()
    {
        var bad = Path.Combine(_audio.Folder, "bad.mp3");
        File.WriteAllText(bad, "not audio");
        await _engine.LoadAsync(_audio.Tone("a.wav", 1.0));
        await Assert.ThrowsAnyAsync<Exception>(() => _engine.LoadAsync(bad));
        Assert.False(_engine.IsLoaded);
        Assert.Equal(PlayState.Stopped, _engine.State);
    }
}
