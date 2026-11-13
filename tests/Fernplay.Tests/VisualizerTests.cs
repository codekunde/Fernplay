using Fernplay.Core.Audio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Fernplay.Tests;

public sealed class VisualizerTests
{
    [Fact]
    public void Ring_keeps_nothing_while_off()
    {
        var ring = new PcmRing(16);
        ring.Write([0.1f, 0.2f, 0.3f, 0.4f], 2);
        Assert.Empty(ring.Drain());
    }

    [Fact]
    public void Ring_passes_stereo_through_and_empties_on_drain()
    {
        var ring = new PcmRing(16) { Enabled = true };
        ring.Write([0.1f, 0.2f, 0.3f, 0.4f], 2);
        Assert.Equal([0.1f, 0.2f, 0.3f, 0.4f], ring.Drain());
        Assert.Empty(ring.Drain());
    }

    [Fact]
    public void Ring_doubles_mono_and_averages_more_channels()
    {
        var ring = new PcmRing(16) { Enabled = true };
        ring.Write([0.5f, -0.5f], 1);
        Assert.Equal([0.5f, 0.5f, -0.5f, -0.5f], ring.Drain());
        // 4 channels: 0 and 2 left, 1 and 3 right; the partial frame at the end is dropped.
        ring.Write([0.2f, 0.4f, 0.6f, 0.8f, 0.9f], 4);
        var d = ring.Drain();
        Assert.Equal(2, d.Length);
        Assert.Equal(0.4f, d[0], 5);
        Assert.Equal(0.6f, d[1], 5);
    }

    [Fact]
    public void Ring_keeps_the_newest_frames_when_full()
    {
        var ring = new PcmRing(3) { Enabled = true };
        for (int i = 1; i <= 5; i++) ring.Write([i, -i], 2);
        Assert.Equal([3f, -3f, 4f, -4f, 5f, -5f], ring.Drain());
    }

    [Fact]
    public void Switching_off_forgets_what_was_kept()
    {
        var ring = new PcmRing(16) { Enabled = true };
        ring.Write([0.1f, 0.2f], 2);
        ring.Enabled = false;
        ring.Enabled = true;
        Assert.Empty(ring.Drain());
    }

    [Theory]
    [InlineData(20, 3)]   // the default is a step
    [InlineData(5, 0)]
    [InlineData(300, 11)]
    [InlineData(1, 0)]    // below the range: the first step
    [InlineData(1000, 11)] // above it: the last
    [InlineData(50, 6)]   // between 45 and 60: nearer 45
    public void Preset_time_lands_on_the_nearest_step(int seconds, int index) =>
        Assert.Equal(index, Fernplay.Core.VisualizerTimes.StepIndex(seconds));

    [Theory]
    [InlineData(20, "20 S")]
    [InlineData(60, "1 MIN")]
    [InlineData(90, "1.5 MIN")]
    [InlineData(300, "5 MIN")]
    public void Preset_time_reads_in_seconds_or_minutes(int seconds, string text) =>
        Assert.Equal(text, Fernplay.Core.VisualizerTimes.Format(seconds));

    [Fact]
    public void Spectrum_tap_hands_its_samples_to_the_ring_unchanged()
    {
        var ring = new PcmRing(4096) { Enabled = true };
        var sine = new SignalGenerator(44100, 2) { Frequency = 440, Gain = 0.5, Type = SignalGeneratorType.Sin };
        var tap = new SpectrumTap(sine.Take(TimeSpan.FromMilliseconds(20)), ring);
        var buffer = new float[1024];
        int n = tap.Read(buffer);
        Assert.Equal(1024, n);
        Assert.Equal(buffer, ring.Drain());
    }
}
