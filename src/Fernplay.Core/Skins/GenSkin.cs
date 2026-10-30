namespace Fernplay.Core.Skins;

/// <summary>
/// Colours of the classic "general" windows (the media library), read from GENEX.BMP: row 0 holds one colour every 2 px
/// from x = 48 (the documented order below); the button colours come from the normal and pressed button drawn at the
/// top left of the sheet.
/// </summary>
public sealed record GenColors(
    uint ItemBackground, uint ItemText, uint WindowBackground, uint ButtonText, uint WindowText, uint Divider,
    uint SelectionBackground, uint HeaderBackground, uint HeaderText, uint HeaderLight, uint HeaderDark,
    uint ScrollbarBackground,
    uint ButtonLight, uint ButtonFace, uint ButtonShadow, uint ButtonOutline, uint ButtonPressedFace)
{
    /// <summary>The classic base skin's values: used for anything a skin's GENEX.BMP does not have.</summary>
    public static readonly GenColors Default = new(
        0xFF000000, 0xFF00FF00, 0xFF383757, 0xFF393942, 0xFFFFFFFF, 0xFF75748B,
        0xFF0000C6, 0xFF484878, 0xFFFFFFFF, 0xFF6C6CB4, 0xFF24243C,
        0xFF24243C,
        0xFFEFFFFF, 0xFFBDCED6, 0xFF7B8494, 0xFF4A5A6B, 0xFF7B8C9C);

    /// <summary>Reads the colours; a missing sheet or a sheet too small for a colour leaves that colour at its default.</summary>
    public static GenColors From(SkinImage? genex)
    {
        var d = Default;
        if (genex is null) return d;
        uint Row(int index, uint fallback) => At(48 + index * 2, 0, fallback);
        uint At(int x, int y, uint fallback) => genex.Contains(x, y, 1, 1) ? genex[x, y] | 0xFF000000 : fallback;
        return new GenColors(
            Row(0, d.ItemBackground), Row(1, d.ItemText), Row(2, d.WindowBackground), Row(3, d.ButtonText), Row(4, d.WindowText),
            Row(5, d.Divider), Row(6, d.SelectionBackground), Row(7, d.HeaderBackground), Row(8, d.HeaderText),
            Row(9, d.HeaderLight), Row(10, d.HeaderDark), Row(14, d.ScrollbarBackground),
            At(1, 1, d.ButtonLight), At(23, 7, d.ButtonFace), At(45, 13, d.ButtonShadow), At(46, 14, d.ButtonOutline), At(23, 23, d.ButtonPressedFace));
    }
}

/// <summary>
/// A classic skin's frame for "general" windows (GEN.BMP): title bar pieces, sides, bottom corners, close button, and the
/// A-Z letters of the window title. Layout maths only; drawing is the app's job.
/// </summary>
public sealed class GenSkin
{
    public const int TitleHeight = 20, BottomHeight = 14, LeftWidth = 11, RightWidth = 8, Piece = 25;
    public const int LetterHeight = 7, ActiveLettersY = 88, InactiveLettersY = 96, SpaceWidth = 5;
    /// <summary>The title letters sit this far below the top of the title bar.</summary>
    public const int LettersTop = 4;
    /// <summary>The close button: 9x9, its right edge 2 px in from the window's right edge, 3 px down.</summary>
    public const int CloseSize = 9, CloseRight = 11, CloseTop = 3;

    private static readonly string[] FrameSprites =
    [
        "Gen.TopLeft.Active", "Gen.TopLeftEnd.Active", "Gen.TopCenterFill.Active", "Gen.TopRightEnd.Active", "Gen.TopFill.Active", "Gen.TopRight.Active",
        "Gen.TopLeft.Inactive", "Gen.TopLeftEnd.Inactive", "Gen.TopCenterFill.Inactive", "Gen.TopRightEnd.Inactive", "Gen.TopFill.Inactive", "Gen.TopRight.Inactive",
        "Gen.BottomLeft", "Gen.BottomRight", "Gen.BottomFill", "Gen.MiddleLeft", "Gen.MiddleLeftBottom", "Gen.MiddleRight", "Gen.MiddleRightBottom",
    ];

    public ClassicSkin Skin { get; }
    public GenColors Colors { get; }

    /// <summary>Letter cells on the sheet: x and width (the row is <see cref="ActiveLettersY"/> or <see cref="InactiveLettersY"/>).</summary>
    public IReadOnlyDictionary<char, (int X, int W)> Letters { get; }

    private GenSkin(ClassicSkin skin, GenColors colors, Dictionary<char, (int X, int W)> letters)
    {
        Skin = skin; Colors = colors; Letters = letters;
    }

    /// <summary>The frame of this skin, or null when it has no usable GEN.BMP (the library then keeps Fernplay's look).</summary>
    public static GenSkin? From(ClassicSkin skin)
    {
        if (!skin.Sheets.TryGetValue("GEN", out var gen)) return null;
        if (!FrameSprites.All(n => skin.Has(SpriteMap.Get(n)))) return null;
        skin.Sheets.TryGetValue("GENEX", out var genex);
        return new GenSkin(skin, GenColors.From(genex), ReadLetters(gen));
    }

    /// <summary>
    /// A-Z between marker columns on the letter row; the marker colour is the row's first pixel. The last letter has no
    /// marker after it and takes the width of the one before. Cells outside the sheet or of no width are left out.
    /// </summary>
    public static Dictionary<char, (int X, int W)> ReadLetters(SkinImage gen)
    {
        var letters = new Dictionary<char, (int X, int W)>();
        if (!gen.Contains(0, InactiveLettersY, 1, LetterHeight)) return letters;
        uint marker = gen[0, ActiveLettersY];
        var cuts = Enumerable.Range(0, gen.Width).Where(x => gen[x, ActiveLettersY] == marker).ToList();
        char c = 'A';
        for (int i = 0; i < cuts.Count && c <= 'Z'; i++)
        {
            int x = cuts[i] + 1;
            int w = i + 1 < cuts.Count ? cuts[i + 1] - x : (letters.Count > 0 ? letters[(char)(c - 1)].W : 0);
            if (w <= 0) continue; // two markers side by side: no cell
            if (x + w > gen.Width) break;
            letters[c++] = (x, w);
        }
        return letters;
    }

    /// <summary>Where each title letter goes (x from the text's start) and which cell it uses; spaces and unknown characters leave a gap.</summary>
    public List<(int X, int SheetX, int W)> TextLayout(string text)
    {
        var parts = new List<(int, int, int)>();
        int x = 0;
        foreach (char ch in text.ToUpperInvariant())
        {
            if (Letters.TryGetValue(ch, out var cell)) { parts.Add((x, cell.X, cell.W)); x += cell.W; }
            else x += SpaceWidth;
        }
        return parts;
    }

    public int TextWidth(string text) => text.ToUpperInvariant().Sum(ch => Letters.TryGetValue(ch, out var cell) ? cell.W : SpaceWidth);

    /// <summary>
    /// The title bar for a window <paramref name="width"/> skin px wide: corner pieces at both ends, the title (on the
    /// centre fill, between its two end caps) in the middle, the fill piece tiled in between. Returns the pieces left to
    /// right with the width each covers (tiles are cut at the last one), and the x of the text (null = no room for it).
    /// </summary>
    public static (List<(string Sprite, int X, int W)> Pieces, int? TextX) TitleRow(int width, int textWidth)
    {
        var pieces = new List<(string, int, int)>();
        void Tile(string sprite, int from, int to)
        {
            for (int x = from; x < to; x += Piece) pieces.Add((sprite, x, Math.Min(Piece, to - x)));
        }
        int? textX = null;
        pieces.Add(("TopLeft", 0, Math.Min(Piece, width)));
        if (textWidth > 0 && width >= textWidth + 4 * Piece)
        {
            int tx = (width - textWidth) / 2;
            textX = tx;
            Tile("TopFill", Piece, tx - Piece);
            pieces.Add(("TopLeftEnd", tx - Piece, Piece));
            Tile("TopCenterFill", tx, tx + textWidth);
            pieces.Add(("TopRightEnd", tx + textWidth, Piece));
            Tile("TopFill", tx + textWidth + Piece, width - Piece);
        }
        else Tile("TopFill", Piece, width - Piece);
        if (width > Piece) pieces.Add(("TopRight", Math.Max(Piece, width - Piece), Math.Min(Piece, width - Piece)));
        return (pieces, textX);
    }
}
