using Fernplay.Core.Skins;
using static Fernplay.Tests.SyntheticSkin;

namespace Fernplay.Tests;

/// <summary>The library's classic frame (GEN.BMP) and colours (GENEX.BMP), on sheets drawn by the tests.</summary>
public sealed class GenSkinTests
{
    private const uint Marker = 0xFF00C6FF, Ink = 0xFFFFFFFF, Paper = 0xFF101010;

    /// <summary>A GEN-sized sheet whose letter rows have marker columns at <paramref name="markers"/>.</summary>
    private static SkinImage LetterSheet(int width, params int[] markers)
    {
        var px = new uint[width * 109];
        for (int i = 0; i < px.Length; i++) px[i] = Paper;
        foreach (int y in new[] { GenSkin.ActiveLettersY, GenSkin.InactiveLettersY })
            for (int dy = 0; dy < GenSkin.LetterHeight; dy++)
            {
                for (int x = 0; x < width; x++) px[(y + dy) * width + x] = Ink;
                foreach (int m in markers) px[(y + dy) * width + m] = Marker;
            }
        return new SkinImage(width, 109, px);
    }

    [Fact]
    public void Letters_are_the_cells_between_markers_and_the_last_takes_the_previous_width()
    {
        var letters = GenSkin.ReadLetters(LetterSheet(60, 0, 7, 15));
        Assert.Equal(['A', 'B', 'C'], letters.Keys.Order());
        Assert.Equal((1, 6), letters['A']);
        Assert.Equal((8, 7), letters['B']);
        Assert.Equal((16, 7), letters['C']);
    }

    [Fact]
    public void Markers_side_by_side_make_no_letter_and_cells_past_the_sheet_are_dropped()
    {
        // As in Bento: 26 cells, then the rest of the row is all marker colour.
        int[] markers = [.. Enumerable.Range(0, 27).Select(i => i * 6), .. Enumerable.Range(157, 21)];
        var letters = GenSkin.ReadLetters(LetterSheet(178, markers));
        Assert.Equal(26, letters.Count);
        Assert.Equal((151, 5), letters['Z']);
        // A sheet too short for the letter rows has no letters at all.
        Assert.Empty(GenSkin.ReadLetters(new SkinImage(194, 90, new uint[194 * 90])));
    }

    [Theory]
    [InlineData(300, 96)]
    [InlineData(200, 40)]
    [InlineData(197, 96)]  // too narrow for the title: fill only
    [InlineData(40, 96)]
    [InlineData(1000, 0)]
    public void The_title_bar_covers_the_whole_width_without_gaps(int width, int textWidth)
    {
        var (pieces, textX) = GenSkin.TitleRow(width, textWidth);
        int x = 0;
        foreach (var p in pieces)
        {
            Assert.Equal(x, p.X);
            Assert.InRange(p.W, 1, GenSkin.Piece);
            x += p.W;
        }
        Assert.Equal(width, x);
        if (textX is { } tx)
        {
            Assert.Equal((width - textWidth) / 2, tx);
            Assert.Contains(pieces, p => p.Sprite == "TopLeftEnd" && p.X + p.W == tx);
            Assert.Contains(pieces, p => p.Sprite == "TopRightEnd" && p.X == tx + textWidth);
        }
        else Assert.True(textWidth == 0 || width < textWidth + 4 * GenSkin.Piece);
    }

    [Fact]
    public void Title_text_is_laid_out_letter_by_letter_with_gaps_for_spaces()
    {
        var files = FullSkin();
        var skin = ClassicSkin.Load(Zip(files), "test");
        var gen = GenSkin.From(skin)!;
        Assert.Empty(gen.Letters); // the position-coded sheet has no marker columns: no letters, nothing breaks
        Assert.Equal(GenSkin.SpaceWidth * 3, gen.TextWidth("A B")); // unknown letters and spaces are gaps
    }

    [Fact]
    public void Colours_come_from_row_0_and_the_button_drawings_with_defaults_for_the_rest()
    {
        var px = new uint[130 * 75];
        for (int i = 0; i < px.Length; i++) px[i] = 0xFF00C6FF;
        void Set(int x, int y, uint c) => px[y * 130 + x] = c;
        Set(48, 0, 0x111111); Set(50, 0, 0x22FF22); Set(60, 0, 0x0000AA); Set(76, 0, 0x333333);
        Set(23, 7, 0xCCCCCC); Set(23, 23, 0x777777);
        var c = GenColors.From(new SkinImage(130, 75, px));
        Assert.Equal(0xFF111111, c.ItemBackground);
        Assert.Equal(0xFF22FF22, c.ItemText);
        Assert.Equal(0xFF0000AA, c.SelectionBackground);
        Assert.Equal(0xFF333333, c.ScrollbarBackground);
        Assert.Equal(0xFFCCCCCC, c.ButtonFace);
        Assert.Equal(0xFF777777, c.ButtonPressedFace);

        var tiny = GenColors.From(new SkinImage(40, 5, new uint[200]));
        Assert.Equal(GenColors.Default.ItemText, tiny.ItemText); // row too short: default
        Assert.Equal(GenColors.Default, GenColors.From(null));
    }

    [Fact]
    public void A_skin_without_GEN_keeps_Fernplays_look_and_says_so()
    {
        var files = FullSkin();
        files.Remove("GEN.BMP");
        var skin = ClassicSkin.Load(Zip(files), "test");
        Assert.Null(GenSkin.From(skin));
        Assert.Contains(skin.Warnings, w => w.StartsWith("GEN.BMP is missing"));
        // A GEN.BMP too small for the frame pieces counts as none.
        files["GEN.BMP"] = Bmp24(120, 40, PositionColor);
        Assert.Null(GenSkin.From(ClassicSkin.Load(Zip(files), "test")));
    }

    [Fact]
    public void Frame_sprites_sit_where_the_classic_sheet_has_them()
    {
        var skin = ClassicSkin.Load(Zip(FullSkin()), "test");
        var gen = GenSkin.From(skin);
        Assert.NotNull(gen);
        // Position-coded pixels: a crop's first pixel tells where it was taken from.
        Assert.Equal(PositionColor(104, 21), skin.GetSprite("Gen.TopFill.Inactive")![0, 0]);
        Assert.Equal(PositionColor(0, 57), skin.GetSprite("Gen.BottomRight")![0, 0]);
        Assert.Equal(PositionColor(148, 42), skin.GetSprite("Gen.Close.Pressed")![0, 0]);
    }
}
