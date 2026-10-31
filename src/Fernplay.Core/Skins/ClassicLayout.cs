namespace Fernplay.Core.Skins;

/// <summary>
/// Where things sit on the classic player (skin pixels, 275x116) and the small calculations behind its controls.
/// Positions follow the classic skin layout (see <see cref="SpriteMap"/>).
/// </summary>
public static class ClassicLayout
{
    public const int Width = 275, Height = 116;

    public static readonly (int X, int Y) TitleBar = (0, 0), Options = (6, 3), Minimize = (244, 3), Shade = (254, 3), Close = (264, 3),
        Clutter = (10, 22), StateLight = (24, 28), State = (26, 28), Minus = (36, 26), Spectrum = (24, 43),
        Title = (111, 27), Kbps = (111, 43), Khz = (156, 43), Mono = (212, 41), Stereo = (239, 41),
        Volume = (107, 57), Balance = (177, 57), EqSwitch = (219, 58), PlSwitch = (242, 58), Position = (16, 72),
        Previous = (16, 88), Play = (39, 88), Pause = (62, 88), Stop = (85, 88), Next = (108, 88), Eject = (136, 89),
        Shuffle = (164, 89), Repeat = (210, 89), About = (253, 91);

    /// <summary>X of the four clock digits (minutes tens, minutes ones, seconds tens, seconds ones), all at y 26.</summary>
    public static readonly int[] DigitX = [48, 60, 78, 90];
    public const int DigitY = 26;

    public const int SpectrumWidth = 76, SpectrumHeight = 16, SpectrumBars = 19;
    public const int TitleWidth = 154, KbpsChars = 3, KhzChars = 2;

    /// <summary>
    /// The clock's four digits for a time: minutes (two digits, wrapping past 99) and seconds. Time left shows a minus.
    /// </summary>
    public static (bool Minus, int[] Digits) Clock(TimeSpan t)
    {
        bool minus = t < TimeSpan.Zero;
        long total = (long)Math.Abs(Math.Floor(t.TotalSeconds));
        long minutes = total / 60 % 100, seconds = total % 60;
        return (minus, [(int)(minutes / 10), (int)(minutes % 10), (int)(seconds / 10), (int)(seconds % 10)]);
    }

    /// <summary>Background step (0-27) of the volume slider for 0..1.</summary>
    public static int VolumeStep(double volume) => (int)Math.Round(Math.Clamp(volume, 0, 1) * (SpriteMap.VolumeSteps - 1));

    /// <summary>Background step (0-27) of the balance slider: 0 in the centre, 27 at either end.</summary>
    public static int BalanceStep(double balance) => (int)Math.Round(Math.Min(Math.Abs(balance), 1) * (SpriteMap.VolumeSteps - 1));

    /// <summary>The balance snaps to the centre within this distance, so it is easy to set back.</summary>
    public const double BalanceSnap = 0.15;

    /// <summary>Where a step away from the centre lands (just outside the snap zone), so keys and the wheel never stick at 0.</summary>
    public const double BalanceStepOut = 0.2;

    public static double SnapBalance(double balance) => Math.Abs(balance) < BalanceSnap ? 0 : Math.Clamp(balance, -1, 1);

    /// <summary>
    /// The balance after a click, key or wheel step from <paramref name="from"/> to <paramref name="to"/>: inside the
    /// centre zone it snaps to 0, except that a key or wheel step (<paramref name="step"/>) from the centre itself moves
    /// out to <see cref="BalanceStepOut"/>.
    /// </summary>
    public static double SettleBalance(double from, double to, double step = 0.1)
    {
        if (to == 0 || Math.Abs(to) >= BalanceSnap) return Math.Clamp(to, -1, 1);
        return from == 0 && Math.Abs(Math.Abs(to) - step) < 1e-9 ? Math.Sign(to) * BalanceStepOut : 0;
    }

    /// <summary>
    /// Layout scale for a size setting (1x, 2x, 3x) on a screen with the given DPI scale, chosen so one skin pixel is a
    /// whole number of screen pixels (crisp), rounding half up: 2x at 100 % → 2.0; at 125 % → 2.4 (3 screen px); at 150 % → 2.0 (3 screen px).
    /// </summary>
    public static double LayoutScale(int size, double dpiScale)
    {
        size = Math.Clamp(size, 1, 3);
        dpiScale = dpiScale > 0 ? dpiScale : 1;
        double device = Math.Max(1, Math.Round(size * dpiScale, MidpointRounding.AwayFromZero));
        return device / dpiScale;
    }

    // ===== Windowshade: the player rolled up to a 275x14 strip (TITLEBAR.BMP "Title.Shade.*") =====

    public const int ShadeHeight = 14;

    /// <summary>
    /// Mini spectrum, the time (sign, minutes, seconds; the colon at x 144 is drawn in the skin, so the text sits either
    /// side of it, y 4 like the font's own colon) and the mini position bar.
    /// </summary>
    public static readonly (int X, int Y) ShadeSpectrum = (79, 5), ShadeSign = (128, 4), ShadeMinutes = (133, 4), ShadeSeconds = (148, 4),
        ShadePosition = (226, 4);

    public const int ShadeSpectrumWidth = 38, ShadeSpectrumHeight = 5, ShadePositionWidth = 17, ShadePositionHeight = 7, ShadeThumbWidth = 3;

    /// <summary>Mini transport hit areas over the buttons drawn in the strip: previous, play, pause, stop, next, eject.</summary>
    public static readonly (int X, int W)[] ShadeButtons = [(168, 9), (177, 10), (187, 10), (197, 9), (206, 9), (216, 9)];
    public const int ShadeButtonY = 2, ShadeButtonHeight = 10;

    /// <summary>The mini position knob's sprite for a position 0..1: its left, middle or right look by third.</summary>
    public static string ShadeThumb(double fraction) =>
        fraction < 1 / 3.0 ? "Shade.Position.Thumb.Left" : fraction < 2 / 3.0 ? "Shade.Position.Thumb" : "Shade.Position.Thumb.Right";

    /// <summary>The time in the strip: sign ("-" for time left), minutes right-aligned in two characters (wrapping past 99), seconds.</summary>
    public static (string Sign, string Minutes, string Seconds) ShadeTime(TimeSpan t)
    {
        var (m, s) = ClassicPlaylist.MiniClock(t);
        return (t < TimeSpan.Zero ? "-" : " ", m, s);
    }

    // ===== Equalizer window (275x116, EQMAIN.BMP) =====

    public static readonly (int X, int Y) EqClose = (264, 3), EqOn = (14, 18), EqAuto = (40, 18), EqGraph = (86, 17),
        EqPresets = (217, 18), EqPreamp = (21, 38);

    /// <summary>X of the 10 band sliders (60 Hz to 16 kHz), all at <see cref="EqSliderY"/>.</summary>
    public static readonly int[] EqBandX = [78, 96, 114, 132, 150, 168, 186, 204, 222, 240];
    public const int EqSliderY = 38, EqSliderWidth = 14, EqSliderHeight = 63, EqGraphWidth = 113, EqGraphHeight = 19;

    /// <summary>Background step (0 = green at -12 dB, 27 = red at +12 dB) of an EQ slider.</summary>
    public static int EqSliderStep(double db, double maxDb = 12) =>
        (int)Math.Round((Math.Clamp(db, -maxDb, maxDb) + maxDb) / (2 * maxDb) * (SpriteMap.EqSliderSteps - 1));

    /// <summary>Row of the EQ graph (0 = top, +12 dB; 18 = bottom, -12 dB) for a gain.</summary>
    public static int EqGraphY(double db, double maxDb = 12) =>
        (int)Math.Round((maxDb - Math.Clamp(db, -maxDb, maxDb)) / (2 * maxDb) * (EqGraphHeight - 1));

    /// <summary>
    /// The EQ curve: one row per graph column (113), a Catmull-Rom curve through the 10 bands spaced evenly across the
    /// graph, clamped to the graph.
    /// </summary>
    public static int[] EqCurve(IReadOnlyList<double> bands, double maxDb = 12)
    {
        var rows = new int[EqGraphWidth];
        int n = bands.Count;
        double span = (EqGraphWidth - 1.0) / (n - 1);
        for (int x = 0; x < EqGraphWidth; x++)
        {
            double pos = x / span;
            int i = Math.Min((int)pos, n - 2);
            double t = pos - i;
            double p0 = bands[Math.Max(i - 1, 0)], p1 = bands[i], p2 = bands[i + 1], p3 = bands[Math.Min(i + 2, n - 1)];
            double db = 0.5 * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (-p0 + 3 * p1 - 3 * p2 + p3) * t * t * t);
            rows[x] = EqGraphY(db, maxDb);
        }
        return rows;
    }

    /// <summary>Text for the classic number boxes, right-aligned to the box width ("128", " 44"); too long keeps the right end.</summary>
    public static string Box(string? text, int chars)
    {
        text ??= "";
        return text.Length >= chars ? text[^chars..] : text.PadLeft(chars);
    }
}

/// <summary>
/// The classic playlist window (PLEDIT.BMP): a 20 px title row, side tiles of 29 px, a 38 px bottom bar; 275 px wide
/// or wider in 25 px steps. The list sits inside the frame. Parts of the bottom-right piece (150 px, always at the right
/// edge) are given relative to it; Y positions in the bottom bar are from its top.
/// </summary>
public static class ClassicPlaylist
{
    public const int MinWidth = 275, WidthStep = 25, TopHeight = 20, BottomHeight = 38, TileHeight = 29, MinTiles = 2, DefaultTiles = 4;
    public const int ListLeft = 12, ListRight = 20, BottomLeftWidth = 125, BottomRightWidth = 150, VisualizerWidth = 75, RowHeight = 13;

    public static int Width(int widthSteps) => MinWidth + WidthStep * Math.Max(widthSteps, 0);
    public static int Height(int tiles) => TopHeight + TileHeight * Math.Max(tiles, MinTiles) + BottomHeight;
    public static int ListWidth(int width) => width - ListLeft - ListRight;
    public static int BottomRightX(int width) => width - BottomRightWidth;

    /// <summary>The title sprite (100 px) centred in the top row: x 88 at 275 px.</summary>
    public static int TitleX(int width) => (width - 100 + 1) / 2;

    /// <summary>The visualizer piece of the bottom bar, left of the bottom-right piece, once the window is wide enough (350 px).</summary>
    public static int? VisualizerX(int width) =>
        width - BottomRightWidth - VisualizerWidth >= BottomLeftWidth ? width - BottomRightWidth - VisualizerWidth : null;

    /// <summary>Title row, from the right edge: close button (its normal look is part of the title sprite) and shade.</summary>
    public const int CloseFromRight = 11, ShadeFromRight = 20, ButtonsY = 3;

    /// <summary>Bottom bar menu buttons on the left piece (left edges; 22x18 at y 8).</summary>
    public const int AddX = 13, RemX = 42, SelX = 71, MiscX = 100;
    public const int ButtonY = 8, ButtonWidth = 22, ButtonHeight = 18;

    // ----- In the bottom-right piece (x relative to it) -----

    /// <summary>LIST OPTS button.</summary>
    public const int ListX = 105;

    /// <summary>Mini transport (previous, play, pause, stop, next, eject): hit areas, y 22, 9 tall.</summary>
    public static readonly (int X, int W)[] MiniButtons = [(5, 8), (13, 10), (23, 10), (33, 9), (42, 9), (51, 9)];
    public const int MiniButtonY = 22, MiniButtonHeight = 9;

    /// <summary>Running time "selected/total" (TEXT.BMP font) and the mini clock either side of the colon drawn in the skin.</summary>
    public static readonly (int X, int Y) RunningTime = (7, 10), MiniMinutes = (73, 23), MiniSeconds = (86, 23);
    public const int RunningTimeChars = 14;

    /// <summary>Scroll arrows and the resize grip (bottom-right corner).</summary>
    public static readonly (int X, int Y) ScrollUp = (133, 1), ScrollDown = (133, 7), Grip = (130, 18);

    /// <summary>The scroll knob runs in the right tile, 15 px from the right edge.</summary>
    public const int ScrollFromRight = 15;

    /// <summary>
    /// Steps after dragging the grip by <paramref name="delta"/> skin px: whole steps of <paramref name="stepSize"/>,
    /// between <paramref name="min"/> and <paramref name="max"/> (the screen edge).
    /// </summary>
    public static int StepsAfterDrag(int start, double delta, int stepSize, int min, int max) =>
        Math.Clamp(start + (int)Math.Round(delta / stepSize), min, Math.Max(min, max));

    /// <summary>Tiles after dragging the grip by <paramref name="dy"/> skin px: whole 29 px steps, at least 2, at most <paramref name="maxTiles"/>.</summary>
    public static int TilesAfterDrag(int startTiles, double dy, int maxTiles) => StepsAfterDrag(startTiles, dy, TileHeight, MinTiles, maxTiles);

    /// <summary>Width steps after dragging the grip by <paramref name="dx"/> skin px: whole 25 px steps, at least 0 (275 px).</summary>
    public static int WidthStepsAfterDrag(int startSteps, double dx, int maxSteps) => StepsAfterDrag(startSteps, dx, WidthStep, 0, maxSteps);

    /// <summary>"3:12/45:01": length of the selected tracks / of the whole list, minutes not wrapped; "+" when some lengths are unknown.</summary>
    public static string RunningTimeText(TimeSpan selected, TimeSpan total, bool someUnknown)
    {
        static string M(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        return $"{M(selected)}/{M(total)}{(someUnknown ? "+" : "")}";
    }

    /// <summary>The mini clock's two boxes: minutes right-aligned in two characters (wrapping past 99), seconds.</summary>
    public static (string Minutes, string Seconds) MiniClock(TimeSpan t)
    {
        long total = (long)Math.Abs(Math.Floor(t.TotalSeconds));
        return (((total / 60) % 100).ToString().PadLeft(2), (total % 60).ToString("00"));
    }
}

/// <summary>The folder of classic skins Fernplay knows (one .wsz per skin) and copying new ones into it.</summary>
public sealed class SkinLibrary(string folder)
{
    public string Folder { get; } = folder;

    /// <summary>Skin files in the folder, by name.</summary>
    public IReadOnlyList<string> List()
    {
        try
        {
            return Directory.Exists(Folder)
                ? Directory.EnumerateFiles(Folder).Where(IsSkinFile).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public static bool IsSkinFile(string path) =>
        Path.GetExtension(path).Equals(".wsz", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>The path of a skin by its file name (as saved in the settings), or null when it is no longer there.</summary>
    public string? Find(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        var path = Path.Combine(Folder, fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Checks a skin file and copies it into the folder (unless it is there already). A different file with the same name
    /// gets a number ("Bento (2).wsz"). Returns the path inside the folder. Throws <see cref="SkinLoadException"/> when the
    /// file is not a usable skin; nothing is copied then.
    /// </summary>
    public string Import(string path)
    {
        ClassicSkin.Load(path); // refuse before copying
        var full = Path.GetFullPath(path);
        if (string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(Folder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return full;
        try
        {
            Directory.CreateDirectory(Folder);
            string name = Path.GetFileNameWithoutExtension(full), ext = Path.GetExtension(full).ToLowerInvariant();
            var target = Path.Combine(Folder, name + ext);
            for (int n = 2; File.Exists(target); n++)
            {
                if (SameContent(target, full)) return target;
                target = Path.Combine(Folder, $"{name} ({n}){ext}");
            }
            File.Copy(full, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SkinLoadException($"The skin could not be copied to {Folder}: {ex.Message}", ex);
        }
    }

    private static bool SameContent(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        return fa.Length == fb.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }
}
