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
    public const double BalanceSnap = 0.08;

    public static double SnapBalance(double balance) => Math.Abs(balance) < BalanceSnap ? 0 : Math.Clamp(balance, -1, 1);

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

    /// <summary>Text for the classic number boxes, right-aligned to the box width ("128", " 44"); too long keeps the right end.</summary>
    public static string Box(string? text, int chars)
    {
        text ??= "";
        return text.Length >= chars ? text[^chars..] : text.PadLeft(chars);
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
