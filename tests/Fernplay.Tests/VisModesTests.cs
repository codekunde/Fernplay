using System.Text.Json;
using System.Text.Json.Serialization;
using Fernplay.Core.Audio;
using NAudio.Wave;

namespace Fernplay.Tests;

public class VisModesTests
{
    [Fact]
    public void FernplayLookCyclesThroughTheVectorscope()
    {
        var m = VisMode.Bars;
        var seen = new List<VisMode>();
        for (int i = 0; i < 5; i++) { m = VisCycle.Next(m, hasVectorscope: true); seen.Add(m); }
        Assert.Equal([VisMode.Oscilloscope, VisMode.Vectorscope, VisMode.Off, VisMode.Bars, VisMode.Oscilloscope], seen);
    }

    [Fact]
    public void ClassicSkinCycleSkipsTheVectorscope()
    {
        var m = VisMode.Bars;
        var seen = new List<VisMode>();
        for (int i = 0; i < 4; i++) { m = VisCycle.Next(m, hasVectorscope: false); seen.Add(m); }
        Assert.Equal([VisMode.Oscilloscope, VisMode.Off, VisMode.Bars, VisMode.Oscilloscope], seen);
    }

    [Fact]
    public void SavedVectorscopeShowsAsScopeOnAClassicSkinAndStepsToOff()
    {
        Assert.Equal(VisMode.Oscilloscope, VisCycle.Shown(VisMode.Vectorscope, false));
        Assert.Equal(VisMode.Vectorscope, VisCycle.Shown(VisMode.Vectorscope, true));
        Assert.Equal(VisMode.Off, VisCycle.Next(VisMode.Vectorscope, false));
        Assert.DoesNotContain(VisMode.Vectorscope, VisCycle.Modes(false));
        Assert.Equal(4, VisCycle.Modes(true).Count);
    }

    [Fact]
    public void SettingsSurviveAJsonRoundTrip()
    {
        var json = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var s = new VisSettings { Mode = VisMode.Vectorscope, Bars = BarStyle.Thin, Peaks = false, BarFalloff = Falloff.Fast, PeakFalloff = Falloff.Slow, Scope = ScopeStyle.Dots };
        string text = JsonSerializer.Serialize(s, json);
        Assert.Contains("\"Vectorscope\"", text);
        var back = JsonSerializer.Deserialize<VisSettings>(text, json)!;
        Assert.Equal((s.Mode, s.Bars, s.Peaks, s.BarFalloff, s.PeakFalloff, s.Scope), (back.Mode, back.Bars, back.Peaks, back.BarFalloff, back.PeakFalloff, back.Scope));
    }

    [Fact]
    public void MissingOrBadValuesGiveTheDefaults()
    {
        var json = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var empty = JsonSerializer.Deserialize<VisSettings>("{}", json)!;
        Assert.Equal((VisMode.Bars, BarStyle.Thick, true, Falloff.Normal, Falloff.Normal, ScopeStyle.Lines),
            (empty.Mode, empty.Bars, empty.Peaks, empty.BarFalloff, empty.PeakFalloff, empty.Scope));
        var bad = new VisSettings { Mode = (VisMode)9, Bars = (BarStyle)7, BarFalloff = (Falloff)5, PeakFalloff = (Falloff)(-1), Scope = (ScopeStyle)3 }.Clean();
        Assert.Equal((VisMode.Bars, BarStyle.Thick, Falloff.Normal, Falloff.Normal, ScopeStyle.Lines), (bad.Mode, bad.Bars, bad.BarFalloff, bad.PeakFalloff, bad.Scope));
    }

    [Fact]
    public void BarsRiseAtOnceAndFallAtTheChosenSpeed()
    {
        foreach (var f in new[] { Falloff.Slow, Falloff.Normal, Falloff.Fast })
        {
            var s = new SpectrumSmoother();
            s.Push([1f], f, f);
            Assert.Equal(1f, s.Levels[0]);
            s.Push([0f], f, f);
            Assert.Equal(1f - SpectrumSmoother.BarStep(f), s.Levels[0], 5);
            Assert.Equal(1f - SpectrumSmoother.PeakStep(f), s.Peaks[0], 5);
        }
        Assert.True(SpectrumSmoother.BarStep(Falloff.Slow) < SpectrumSmoother.BarStep(Falloff.Normal));
        Assert.True(SpectrumSmoother.BarStep(Falloff.Normal) < SpectrumSmoother.BarStep(Falloff.Fast));
        Assert.True(SpectrumSmoother.PeakStep(Falloff.Slow) < SpectrumSmoother.PeakStep(Falloff.Fast));
    }

    [Fact]
    public void SmootherFollowsAChangeInBandCount()
    {
        var s = new SpectrumSmoother();
        s.Push(new float[19], Falloff.Normal, Falloff.Normal);
        s.Push(new float[76], Falloff.Normal, Falloff.Normal);
        Assert.Equal(76, s.Count);
        Assert.Equal(76, s.Peaks.Length);
    }

    [Theory]
    [InlineData(1f, 16, 0)]
    [InlineData(-1f, 16, 15)]
    [InlineData(0f, 16, 8)]       // 7.5 rounds down the middle to row 8
    [InlineData(2f, 16, 0)]       // clipped
    [InlineData(-3f, 5, 4)]
    [InlineData(0f, 5, 2)]
    [InlineData(float.NaN, 5, 2)]
    public void ScopeMapsSamplesToRows(float sample, int height, int row) => Assert.Equal(row, ScopeMath.Row(sample, height));

    [Fact]
    public void ScopeSpreadsSamplesOverTheColumns()
    {
        Assert.Equal(0, ScopeMath.SampleIndex(0, 76, ScopeMath.Samples));
        Assert.Equal(ScopeMath.Samples * 75 / 76, ScopeMath.SampleIndex(75, 76, ScopeMath.Samples));
        Assert.Equal(ScopeMath.Samples / 2, ScopeMath.SampleIndex(38, 76, ScopeMath.Samples));
        Assert.Equal(0, ScopeMath.SampleIndex(5, 0, 10));
    }

    [Fact]
    public void ScopeColoursAreBrightestInTheMiddle()
    {
        Assert.Equal(18, ScopeMath.ColorIndex(7, 16));
        Assert.Equal(18, ScopeMath.ColorIndex(8, 16));
        Assert.Equal(22, ScopeMath.ColorIndex(0, 16));
        Assert.Equal(22, ScopeMath.ColorIndex(15, 16));
        Assert.Equal(18, ScopeMath.ColorIndex(2, 5));
        Assert.Equal(22, ScopeMath.ColorIndex(4, 5));
        for (int y = 0; y < 16; y++) Assert.InRange(ScopeMath.ColorIndex(y, 16), 18, 22);
    }

    [Fact]
    public void ScopeLinesJoinNeighbouringColumns()
    {
        Assert.Equal((5, 5), ScopeMath.Span(5, 5));
        Assert.Equal((3, 10), ScopeMath.Span(10, 2)); // falling from row 2 to 10: rows 3..10
        Assert.Equal((2, 9), ScopeMath.Span(2, 10));  // rising from 10 to 2: rows 2..9
    }

    [Fact]
    public void VectorscopeShowsMonoAsAVerticalLine()
    {
        var (x, y) = VectorscopeMath.Rotate(0.6f, 0.6f);
        Assert.Equal(0f, x);
        Assert.Equal(0.6f, y, 5);
        var (xi, yi) = VectorscopeMath.Rotate(0.6f, -0.6f); // out of phase: sideways
        Assert.Equal(0f, yi);
        Assert.Equal(-0.6f, xi, 5);
    }

    [Fact]
    public void VectorscopeLeansLeftForLeftAndRightForRight()
    {
        var (lx, ly) = VectorscopeMath.Rotate(1f, 0f);
        var (rx, ry) = VectorscopeMath.Rotate(0f, 1f);
        Assert.True(lx < 0 && ly > 0);
        Assert.True(rx > 0 && ry > 0);
        Assert.Equal(-lx, ly, 5); // exactly 45°
        Assert.Equal(rx, ry, 5);
    }

    [Fact]
    public void VectorscopeKeepsFullScaleInsideTheDiamond()
    {
        foreach (var (l, r) in new[] { (1f, 1f), (1f, -1f), (-1f, 1f), (-1f, -1f), (0.3f, -0.9f) })
        {
            var (x, y) = VectorscopeMath.Rotate(l, r);
            Assert.True(Math.Abs(x) + Math.Abs(y) <= 1.0001f);
        }
    }

    [Fact]
    public void VectorscopePixelsCentreSilenceAndClampOutliers()
    {
        Assert.Equal((40, 40), VectorscopeMath.ToPixel(0, 0, 81));
        Assert.Equal((40, 0), VectorscopeMath.ToPixel(0, 1, 81));   // up is row 0
        Assert.Equal((80, 40), VectorscopeMath.ToPixel(1, 0, 81));
        Assert.Equal((0, 80), VectorscopeMath.ToPixel(-5, -5, 81));
    }

    [Fact]
    public void VectorscopeGainLiftsQuietMusicWithinLimits()
    {
        Assert.Equal(1f, VectorscopeMath.Gain(1f));
        Assert.Equal(4f, VectorscopeMath.Gain(0.01f));
        Assert.Equal(4f, VectorscopeMath.Gain(0f));
        Assert.Equal(0.85f / 0.5f, VectorscopeMath.Gain(0.5f), 5);
        // The envelope follows a peak at once and lets go slowly.
        Assert.Equal(0.9f, VectorscopeMath.Envelope(0.2f, 0.9f));
        Assert.Equal(0.9f * 0.96f, VectorscopeMath.Envelope(0.9f, 0.1f), 5);
    }

    [Fact]
    public void TapKeepsLeftAndRightApartForTheScopes()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var tap = new SpectrumTap(new Frames(format, [0.5f, -0.25f, 0.1f, 0.2f]));
        tap.Read(new float[4].AsSpan());
        var l = new float[2]; var r = new float[2];
        Assert.Equal(2, tap.CopyWave(l, r));
        Assert.Equal([0.5f, 0.1f], l);   // oldest first
        Assert.Equal([-0.25f, 0.2f], r);
    }

    [Fact]
    public void TapSpreadsMonoToBothSides()
    {
        var tap = new SpectrumTap(new Frames(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1), [0.4f]));
        tap.Read(new float[1].AsSpan());
        var l = new float[1]; var r = new float[1];
        tap.CopyWave(l, r);
        Assert.Equal(0.4f, l[0]);
        Assert.Equal(0.4f, r[0]);
    }

    private sealed class Frames(WaveFormat format, float[] data) : ISampleProvider
    {
        private int _at;
        public WaveFormat WaveFormat => format;
        public int Read(Span<float> buffer)
        {
            int n = Math.Min(buffer.Length, data.Length - _at);
            data.AsSpan(_at, n).CopyTo(buffer);
            _at += n;
            return n;
        }
    }
}
