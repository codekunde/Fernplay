using Fernplay.Core;
using Fernplay.Core.Audio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Fernplay.Tests;

public sealed class EqAndPlaylistTests
{
    // ---------- EQ: measure the gain of a sine after the filter

    private static double GainAt(double hz, Equalizer eq, int rate = 44100)
    {
        var sine = new SignalGenerator(rate, 1) { Frequency = hz, Gain = 0.1, Type = SignalGeneratorType.Sin };
        var stage = new EqualizerStage(sine.Take(TimeSpan.FromSeconds(1)), eq);
        var buf = new float[rate];
        int n = stage.Read(buf);
        // Skip the first 0.25 s while the filter settles.
        double peak = buf.AsSpan(rate / 4, n - rate / 4).ToArray().Max(Math.Abs);
        return 20 * Math.Log10(peak / 0.1);
    }

    [Fact]
    public void Flat_eq_changes_nothing()
    {
        var eq = new Equalizer();
        Assert.Equal(0, GainAt(1000, eq), 1);
    }

    [Theory]
    [InlineData(4, 1000)]
    [InlineData(0, 60)]
    [InlineData(9, 16000)]
    public void A_band_boosts_its_own_frequency_by_its_gain(int band, double hz)
    {
        var eq = new Equalizer();
        eq.SetBand(band, 12);
        Assert.Equal(12, GainAt(hz, eq), 0);
    }

    [Fact]
    public void A_band_leaves_far_frequencies_alone()
    {
        var eq = new Equalizer();
        eq.SetBand(0, 12); // 60 Hz
        Assert.InRange(GainAt(6000, eq), -0.5, 0.5);
    }

    [Fact]
    public void Preamp_and_off_switch()
    {
        var eq = new Equalizer { Preamp = -6 };
        Assert.Equal(-6, GainAt(1000, eq), 1);
        eq.SetBand(4, 12);
        eq.Enabled = false;
        Assert.Equal(0, GainAt(1000, eq), 1);
    }

    [Fact]
    public void Settings_are_clamped_and_presets_apply()
    {
        var eq = new Equalizer();
        eq.SetBand(3, 40);
        Assert.Equal(12, eq.GetBand(3));
        eq.Apply(EqPreset.BuiltIn.First(p => p.Name == "Bass boost"));
        Assert.Equal(8, eq.GetBand(0));
        Assert.Equal(-4, eq.Preamp);
        int v = eq.Version;
        eq.SetBand(0, 8); // no change, no new version
        Assert.Equal(v, eq.Version);
    }

    [Fact]
    public void Bands_above_nyquist_are_skipped_at_low_sample_rates()
    {
        var eq = new Equalizer();
        eq.SetBand(9, 12); // 16 kHz on a 22.05 kHz file
        Assert.Equal(0, GainAt(1000, eq, rate: 22050), 1);
    }

    // ---------- Spectrum

    [Fact]
    public void Spectrum_peaks_in_the_band_of_the_tone()
    {
        int rate = 44100;
        var samples = new float[1024];
        for (int i = 0; i < samples.Length; i++) samples[i] = 0.8f * (float)Math.Sin(2 * Math.PI * 1000 * i / rate);
        var levels = SpectrumTap.Analyse(samples, rate, 20);
        int loudest = Array.IndexOf(levels, levels.Max());
        // 1 kHz on a 40 Hz..16 kHz log scale with 20 bands sits in band 10 (0-based).
        Assert.InRange(loudest, 9, 11);
        Assert.True(levels[loudest] > 0.8f);
        Assert.True(levels[0] < 0.5f);
        Assert.All(SpectrumTap.Analyse(new float[1024], rate, 20), l => Assert.Equal(0f, l));
    }

    // ---------- Playlist: shuffle, repeat, remove

    private static Playlist List(int n, int seed = 1)
    {
        var p = new Playlist(new Random(seed));
        p.AddRange(Enumerable.Range(0, n).Select(i => new Track($"{i}.mp3", null, null, null, TimeSpan.FromMinutes(1), null)));
        return p;
    }

    [Fact]
    public void Repeat_all_wraps_and_repeat_one_only_when_the_track_ended()
    {
        var p = List(3);
        p.MoveTo(2);
        Assert.Null(p.NextIndex(trackEnded: true));
        p.Repeat = RepeatMode.All;
        Assert.Equal(0, p.NextIndex(trackEnded: true));
        p.Repeat = RepeatMode.One;
        Assert.Equal(2, p.NextIndex(trackEnded: true));
        Assert.Equal(0, p.NextIndex(trackEnded: false)); // the Next button still moves on, wrapping like repeat all
    }

    [Fact]
    public void Shuffle_plays_every_track_once_starting_from_the_current()
    {
        var p = List(8, seed: 42);
        p.MoveTo(5);
        p.Shuffle = true;
        var seen = new List<int> { p.CurrentIndex };
        while (p.NextIndex(trackEnded: true) is { } i) { p.MoveTo(i); seen.Add(i); }
        Assert.Equal(8, seen.Count);
        Assert.Equal(Enumerable.Range(0, 8), seen.Order());
        Assert.Equal(5, seen[0]);
        Assert.NotEqual(Enumerable.Range(0, 8).ToList(), seen); // actually shuffled
    }

    [Fact]
    public void Removing_keeps_the_current_track_or_its_follower_next()
    {
        var p = List(5);
        p.MoveTo(2);
        p.RemoveAt([0]);
        Assert.Equal(1, p.CurrentIndex);
        Assert.Equal("2.mp3", p.Current!.Path);
        p.RemoveAt([1]); // remove the current one
        Assert.Equal("3.mp3", p.Tracks[p.NextIndex(false)!.Value].Path);
        Assert.Equal(TimeSpan.FromMinutes(3), p.TotalDuration);
    }

    [Fact]
    public void Update_replaces_details_only_for_the_same_file()
    {
        var p = List(2);
        p.Update(0, new Track("0.mp3", "Title", "Artist", null, null, 320));
        p.Update(1, new Track("other.mp3", "X", null, null, null, null));
        Assert.Equal("Artist - Title", p.Tracks[0].DisplayName);
        Assert.Equal("1", p.Tracks[1].DisplayName);
    }
}
