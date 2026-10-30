using Fernplay.Core.Audio;
using Fernplay.Core.Skins;
using static Fernplay.Tests.SyntheticSkin;

namespace Fernplay.Tests;

public sealed class ClassicLayoutTests
{
    [Theory]
    [InlineData(0, false, new[] { 0, 0, 0, 0 })]
    [InlineData(65.9, false, new[] { 0, 1, 0, 5 })]
    [InlineData(-230, true, new[] { 0, 3, 5, 0 })]
    [InlineData(5999, false, new[] { 9, 9, 5, 9 })]
    [InlineData(6000, false, new[] { 0, 0, 0, 0 })] // 100 minutes wrap to 00:00, as the classic clock has two minute digits
    public void Clock_digits(double seconds, bool minus, int[] digits)
    {
        var (m, d) = ClassicLayout.Clock(TimeSpan.FromSeconds(seconds));
        Assert.Equal(minus, m);
        Assert.Equal(digits, d);
    }

    [Fact]
    public void Playlist_height_grows_in_29_px_tiles_from_116()
    {
        Assert.Equal(116, ClassicPlaylist.Height(2));
        Assert.Equal(116, ClassicPlaylist.Height(0)); // never below two tiles
        Assert.Equal(174, ClassicPlaylist.Height(4));
        Assert.Equal(243, ClassicPlaylist.ListWidth(275));
        Assert.Equal(5, ClassicPlaylist.TilesAfterDrag(4, 20, 10));   // 20 px rounds to one tile
        Assert.Equal(4, ClassicPlaylist.TilesAfterDrag(4, 14, 10));   // under half a tile: no change
        Assert.Equal(2, ClassicPlaylist.TilesAfterDrag(4, -200, 10)); // at least two
        Assert.Equal(6, ClassicPlaylist.TilesAfterDrag(4, 500, 6));   // not past the screen bottom
    }

    [Fact]
    public void Playlist_width_grows_in_25_px_steps_with_the_parts_following_the_right_edge()
    {
        Assert.Equal(275, ClassicPlaylist.Width(0));
        Assert.Equal(275, ClassicPlaylist.Width(-3));
        Assert.Equal(400, ClassicPlaylist.Width(5));
        Assert.Equal(125, ClassicPlaylist.BottomRightX(275));
        Assert.Equal(250, ClassicPlaylist.BottomRightX(400));
        Assert.Equal(88, ClassicPlaylist.TitleX(275));
        Assert.Equal(150, ClassicPlaylist.TitleX(400));
        Assert.Null(ClassicPlaylist.VisualizerX(325));      // no room yet
        Assert.Equal(125, ClassicPlaylist.VisualizerX(350)); // right after the bottom-left piece
        Assert.Equal(2, ClassicPlaylist.WidthStepsAfterDrag(0, 60, 10));  // 60 px rounds to two steps
        Assert.Equal(0, ClassicPlaylist.WidthStepsAfterDrag(2, -400, 10)); // never narrower than 275
        Assert.Equal(3, ClassicPlaylist.WidthStepsAfterDrag(0, 400, 3));   // not past the screen edge
    }

    [Fact]
    public void Playlist_running_time_and_mini_clock()
    {
        Assert.Equal("0:00/0:00", ClassicPlaylist.RunningTimeText(TimeSpan.Zero, TimeSpan.Zero, false));
        Assert.Equal("3:12/125:01+", ClassicPlaylist.RunningTimeText(TimeSpan.FromSeconds(192), TimeSpan.FromSeconds(7501), true));
        Assert.Equal((" 1", "05"), ClassicPlaylist.MiniClock(TimeSpan.FromSeconds(65.9)));
        Assert.Equal(("12", "00"), ClassicPlaylist.MiniClock(TimeSpan.FromMinutes(112)));
    }

    [Fact]
    public void Eq_slider_step_runs_from_green_at_the_bottom_to_red_at_the_top()
    {
        Assert.Equal(0, ClassicLayout.EqSliderStep(-12));
        Assert.Equal(14, ClassicLayout.EqSliderStep(0)); // 13.5 rounds to the even 14
        Assert.Equal(27, ClassicLayout.EqSliderStep(12));
        Assert.Equal(27, ClassicLayout.EqSliderStep(40)); // clamped
        Assert.Equal(0, ClassicLayout.EqSliderStep(-40));
    }

    [Fact]
    public void Eq_graph_rows_and_curve()
    {
        Assert.Equal(0, ClassicLayout.EqGraphY(12));
        Assert.Equal(9, ClassicLayout.EqGraphY(0));
        Assert.Equal(18, ClassicLayout.EqGraphY(-12));

        var flat = ClassicLayout.EqCurve(new double[10]);
        Assert.Equal(ClassicLayout.EqGraphWidth, flat.Length);
        Assert.All(flat, y => Assert.Equal(9, y));

        // The curve passes through each band (columns are whole pixels, so within a row) and stays inside the graph.
        double[] bands = [12, -12, 6, 0, -6, 12, 12, -12, 3, -3];
        var curve = ClassicLayout.EqCurve(bands);
        Assert.Equal(ClassicLayout.EqGraphY(12), curve[0]);
        Assert.Equal(ClassicLayout.EqGraphY(-3), curve[112]);
        for (int i = 0; i < 10; i++)
            Assert.InRange(curve[(int)Math.Round(i * 112 / 9.0)] - ClassicLayout.EqGraphY(bands[i]), -1, 1);
        Assert.All(curve, y => Assert.InRange(y, 0, ClassicLayout.EqGraphHeight - 1));
    }

    [Fact]
    public void Slider_steps_cover_the_28_backgrounds()
    {
        Assert.Equal(0, ClassicLayout.VolumeStep(0));
        Assert.Equal(27, ClassicLayout.VolumeStep(1));
        Assert.Equal(14, ClassicLayout.VolumeStep(0.52));
        Assert.Equal(27, ClassicLayout.VolumeStep(7)); // out of range is clamped
        Assert.Equal(0, ClassicLayout.BalanceStep(0));
        Assert.Equal(27, ClassicLayout.BalanceStep(-1));
        Assert.Equal(ClassicLayout.BalanceStep(0.5), ClassicLayout.BalanceStep(-0.5));
    }

    [Fact]
    public void Balance_snaps_to_the_centre_and_stays_in_range()
    {
        Assert.Equal(0, ClassicLayout.SnapBalance(0.05));
        Assert.Equal(0, ClassicLayout.SnapBalance(-0.07));
        Assert.Equal(-0.4, ClassicLayout.SnapBalance(-0.4));
        Assert.Equal(1, ClassicLayout.SnapBalance(3));
        Assert.Equal(0, ClassicLayout.SnapBalance(0.14)); // zone wide enough to hit with a drag
        Assert.Equal(0.15, ClassicLayout.SnapBalance(0.15));
    }

    [Fact]
    public void Balance_steps_snap_to_the_centre_but_never_stick_there()
    {
        Assert.Equal(0, ClassicLayout.SettleBalance(0.2, 0.1));     // a step down from 20 % lands in the centre
        Assert.Equal(0.2, ClassicLayout.SettleBalance(0, 0.1));     // a step up from the centre leaves the zone
        Assert.Equal(-0.2, ClassicLayout.SettleBalance(0, -0.1));
        Assert.Equal(0, ClassicLayout.SettleBalance(0.5, 0.12));    // a click near the centre snaps
        Assert.Equal(0, ClassicLayout.SettleBalance(0, 0.07));      // ... also when already centred
        Assert.Equal(-0.6, ClassicLayout.SettleBalance(0, -0.6));   // outside the zone: as asked
        Assert.Equal(1, ClassicLayout.SettleBalance(0.9, 1.3));
    }

    [Theory]
    [InlineData(2, 1.0, 2.0)]
    [InlineData(2, 1.25, 2.4)]  // 3 screen pixels per skin pixel
    [InlineData(2, 1.5, 2.0)]   // 3 screen pixels
    [InlineData(1, 1.75, 1.142857)] // 2 screen pixels
    [InlineData(3, 1.0, 3.0)]
    [InlineData(9, 1.0, 3.0)]   // sizes are 1 to 3
    public void Layout_scale_gives_whole_screen_pixels(int size, double dpi, double expected)
    {
        double s = ClassicLayout.LayoutScale(size, dpi);
        Assert.Equal(expected, s, 5);
        double device = s * dpi;
        Assert.Equal(Math.Round(device), device, 6);
    }

    [Fact]
    public void Number_boxes_right_align_and_keep_the_right_end()
    {
        Assert.Equal("128", ClassicLayout.Box("128", 3));
        Assert.Equal(" 44", ClassicLayout.Box("44", 3));
        Assert.Equal("   ", ClassicLayout.Box(null, 3));
        Assert.Equal("320", ClassicLayout.Box("1320", 3));
    }

    [Fact]
    public void Balance_turns_down_the_far_side_only()
    {
        Assert.Equal((1f, 1f), GainStage.BalanceGains(0));
        Assert.Equal((0.5f, 1f), GainStage.BalanceGains(0.5f));
        Assert.Equal((1f, 0f), GainStage.BalanceGains(-1));
    }

    private sealed class Ones(int channels) : NAudio.Wave.ISampleProvider
    {
        public NAudio.Wave.WaveFormat WaveFormat { get; } = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(44100, channels);
        public int Read(Span<float> buffer) { buffer.Fill(1f); return buffer.Length; }
    }

    [Fact]
    public void Balance_changes_the_samples_of_stereo_and_leaves_mono_alone()
    {
        var stereo = new GainStage(new Ones(2)) { Volume = 1f, Balance = -0.75f }; // towards the left
        var buf = new float[8];
        stereo.Read(buf);
        Assert.All(buf.Where((_, i) => i % 2 == 0), l => Assert.Equal(1f, l));
        Assert.All(buf.Where((_, i) => i % 2 == 1), r => Assert.Equal(0.25f, r, 5));

        var withVolume = new GainStage(new Ones(2)) { Volume = 0.5f, Balance = 0.5f };
        withVolume.Read(buf);
        Assert.Equal(0.5f * 0.25f, buf[0], 5); // left: balance 0.5 × volume gain 0.25
        Assert.Equal(0.25f, buf[1], 5);

        var mono = new GainStage(new Ones(1)) { Volume = 1f, Balance = 1f };
        mono.Read(buf);
        Assert.All(buf, s => Assert.Equal(1f, s));
    }
}

public sealed class SkinLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fernplay-skins-{Guid.NewGuid():N}");
    private string Lib => Path.Combine(_root, "Skins");
    private string Outside => Path.Combine(_root, "Downloads");

    public SkinLibraryTests() => Directory.CreateDirectory(Outside);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string WriteSkin(string name, Action<Dictionary<string, object>>? change = null)
    {
        var files = FullSkin();
        change?.Invoke(files);
        var path = Path.Combine(Outside, name);
        using var f = File.Create(path);
        Zip(files).CopyTo(f);
        return path;
    }

    [Fact]
    public void Import_copies_once_and_numbers_a_different_file_with_the_same_name()
    {
        var lib = new SkinLibrary(Lib);
        Assert.Empty(lib.List()); // folder does not exist yet
        var a = WriteSkin("Cool.wsz");
        var first = lib.Import(a);
        Assert.Equal(Path.Combine(Lib, "Cool.wsz"), first);
        Assert.Equal(first, lib.Import(a));       // same file again: no copy
        Assert.Equal(first, lib.Import(first));   // already in the folder
        File.Delete(a);
        var b = WriteSkin("Cool.wsz", f => f["PLEDIT.TXT"] = "[Text]\nNormal=#FF0000");
        Assert.Equal(Path.Combine(Lib, "Cool (2).wsz"), lib.Import(b));
        Assert.Equal(["Cool (2).wsz", "Cool.wsz"], lib.List().Select(Path.GetFileName));
        Assert.Equal(first, lib.Find("Cool.wsz"));
    }

    [Fact]
    public void A_file_that_is_not_a_skin_is_refused_and_not_copied()
    {
        var lib = new SkinLibrary(Lib);
        var bad = WriteSkin("Broken.wsz", f => f.Remove("MAIN.BMP"));
        Assert.Throws<SkinLoadException>(() => lib.Import(bad));
        Assert.Empty(lib.List());
    }

    [Fact]
    public void Find_ignores_missing_files_and_paths()
    {
        var lib = new SkinLibrary(Lib);
        Assert.Null(lib.Find(null));
        Assert.Null(lib.Find("nope.wsz"));
        Assert.Null(lib.Find(@"..\Downloads\x.wsz"));
        Assert.True(SkinLibrary.IsSkinFile(@"C:\a\B.WSZ"));
        Assert.False(SkinLibrary.IsSkinFile(@"C:\a\song.mp3"));
    }
}
