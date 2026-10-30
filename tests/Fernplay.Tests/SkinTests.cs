using Fernplay.Core.Skins;
using static Fernplay.Tests.SyntheticSkin;

namespace Fernplay.Tests;

public sealed class BmpDecoderTests
{
    private static uint Rgb(uint rgb) => 0xFF000000u | rgb;

    [Fact]
    public void Reads_24_bit_bottom_up_and_top_down_the_same_way()
    {
        foreach (bool topDown in new[] { false, true })
        {
            var img = BmpDecoder.Decode(Bmp24(5, 3, PositionColor, topDown)); // 5 px: rows need padding
            Assert.Equal((5, 3), (img.Width, img.Height));
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 5; x++) Assert.Equal(PositionColor(x, y), img[x, y]);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void Reads_indexed_bitmaps(int bits)
    {
        uint[] palette = bits == 1 ? [0x000000, 0xFFFFFF] : [0x000000, 0xFF0000, 0x00FF00, 0x0000FF];
        var img = BmpDecoder.Decode(BmpIndexed(7, 2, bits, palette, (x, y) => (x + y) % palette.Length));
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 7; x++) Assert.Equal(Rgb(palette[(x + y) % palette.Length]), img[x, y]);
    }

    [Fact]
    public void Palette_entries_the_file_leaves_out_are_black()
    {
        var img = BmpDecoder.Decode(BmpIndexed(2, 1, 8, [0xFF0000], (x, _) => x * 200)); // index 200 not in the palette
        Assert.Equal(Rgb(0xFF0000), img[0, 0]);
        Assert.Equal(Rgb(0x000000), img[1, 0]);
    }

    [Fact]
    public void Reads_16_bit_bit_fields_and_scales_to_full_range()
    {
        // 565: white, pure red, pure green (6 bits), pure blue. One row of 4 pixels = 8 bytes (no padding).
        ushort[] px = [0xFFFF, 0xF800, 0x07E0, 0x001F];
        var bytes = px.SelectMany(BitConverter.GetBytes).ToArray();
        var img = BmpDecoder.Decode(Wrap(4, 1, 16, 3, [], bytes, [0xF800, 0x07E0, 0x001F]));
        Assert.Equal(Rgb(0xFFFFFF), img[0, 0]);
        Assert.Equal(Rgb(0xFF0000), img[1, 0]);
        Assert.Equal(Rgb(0x00FF00), img[2, 0]);
        Assert.Equal(Rgb(0x0000FF), img[3, 0]);
    }

    [Fact]
    public void Reads_32_bit_as_opaque()
    {
        var bytes = new byte[] { 0x10, 0x20, 0x30, 0x00, 0x40, 0x50, 0x60, 0x00 }; // alpha 0 in the file
        var img = BmpDecoder.Decode(Wrap(2, 1, 32, 0, [], bytes));
        Assert.Equal(Rgb(0x302010), img[0, 0]);
        Assert.Equal(Rgb(0x605040), img[1, 0]);
    }

    [Fact]
    public void Reads_RLE8_runs_literals_and_line_ends()
    {
        // Bottom row: run of 3 x index 1, literal [2, 3, 1] (odd count padded), end of line.
        // Top row: run of 2 x index 3, end of bitmap (the rest stays black).
        byte[] rle = [3, 1, 0, 3, 2, 3, 1, 0, 0, 0, 2, 3, 0, 1];
        var img = BmpDecoder.Decode(Wrap(6, 2, 8, 1, [0x000000, 0xFF0000, 0x00FF00, 0x0000FF], rle));
        uint k = Rgb(0), r = Rgb(0xFF0000), g = Rgb(0x00FF00), b = Rgb(0x0000FF);
        Assert.Equal([r, r, r, g, b, r], Enumerable.Range(0, 6).Select(x => img[x, 1]));
        Assert.Equal([b, b, k, k, k, k], Enumerable.Range(0, 6).Select(x => img[x, 0]));
    }

    [Fact]
    public void Rejects_files_that_are_not_bitmaps_or_are_too_big_or_cut_short()
    {
        Assert.Throws<InvalidDataException>(() => BmpDecoder.Decode("PNG not a bitmap at all.."u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => BmpDecoder.Decode(Wrap(5000, 1, 24, 0, [], new byte[15000])));
        var full = Bmp24(10, 10, PositionColor);
        Assert.Throws<InvalidDataException>(() => BmpDecoder.Decode(full.AsSpan(0, full.Length - 20)));
        Assert.Throws<InvalidDataException>(() => BmpDecoder.Decode(Wrap(2, 2, 24, 4, [], new byte[16]))); // JPEG inside a BMP
    }

    [Fact]
    public void Crop_returns_null_outside_the_image()
    {
        var img = BmpDecoder.Decode(Bmp24(10, 8, PositionColor));
        var part = img.Crop(2, 3, 4, 5)!;
        Assert.Equal(PositionColor(2, 3), part[0, 0]);
        Assert.Equal(PositionColor(5, 7), part[3, 4]);
        Assert.Null(img.Crop(8, 0, 3, 1));
        Assert.Null(img.Crop(0, 7, 1, 2));
        Assert.Null(img.Crop(-1, 0, 1, 1));
    }
}

public sealed class ClassicSkinTests
{
    private static ClassicSkin Load(Dictionary<string, object> files, string name = "test") => ClassicSkin.Load(Zip(files), name);

    [Fact]
    public void Loads_a_full_skin_and_cuts_sprites_from_the_right_place()
    {
        var skin = Load(FullSkin());
        Assert.Empty(skin.Warnings);
        Assert.Equal("test", skin.Name);
        foreach (var (name, sprite) in SpriteMap.All)
        {
            if (sprite.Sheet == "NUMS_EX") continue; // this skin has the plain digits
            var img = skin.GetSprite(sprite);
            Assert.True(img is not null, $"{name} missing");
            Assert.Equal(PositionColor(sprite.X, sprite.Y), img![0, 0]);
            Assert.Equal(PositionColor(sprite.X + sprite.W - 1, sprite.Y + sprite.H - 1), img[sprite.W - 1, sprite.H - 1]);
        }
        Assert.Equal("NUMBERS", skin.DigitSheet);
        Assert.Equal(0xFF112233u, skin.Playlist.Normal);
        Assert.Equal("Tahoma", skin.Playlist.Font);
        Assert.Equal(0xFF020406u, skin.VisColors[2]);
        Assert.Single(skin.Regions.For("Normal")!);
    }

    [Fact]
    public void Every_sprite_fits_the_full_size_sheets()
    {
        var sizes = new Dictionary<string, (int W, int H)>(FullSizes) { ["NUMS_EX"] = (108, 13) };
        foreach (var (name, s) in SpriteMap.All)
        {
            var (w, h) = sizes[s.Sheet];
            Assert.True(s.X + s.W <= w && s.Y + s.H <= h, $"{name} ({s.X},{s.Y} {s.W}x{s.H}) lies outside {s.Sheet} {w}x{h}");
        }
    }

    [Fact]
    public void File_names_are_found_in_any_case_and_inside_one_top_folder()
    {
        var files = FullSkin(s => "MySkin/" + s.ToLowerInvariant() + ".Bmp");
        files["__MACOSX/MySkin/._main.bmp"] = new byte[] { 1, 2, 3 }; // Mac resource fork: ignored
        var skin = Load(files);
        Assert.Empty(skin.Warnings);
        Assert.NotNull(skin.GetSprite("Main.Background"));
    }

    [Fact]
    public void Extended_digits_are_preferred_when_present()
    {
        var files = FullSkin();
        files.Remove("NUMBERS.BMP");
        files["nums_ex.bmp"] = Bmp24(108, 13, PositionColor);
        var skin = Load(files);
        Assert.Empty(skin.Warnings);
        Assert.Equal("NUMS_EX", skin.DigitSheet);
        Assert.Equal(new Sprite("NUMS_EX", 27, 0, 9, 13), skin.Digit(3));
    }

    [Fact]
    public void A_short_sheet_just_lacks_the_sprites_outside_it()
    {
        var files = FullSkin();
        files["VOLUME.BMP"] = Bmp24(68, 419, PositionColor); // like Bento: no knob row
        files["EQMAIN.BMP"] = Bmp24(275, 294, PositionColor); // no curve graph
        var skin = Load(files);
        Assert.Empty(skin.Warnings);
        Assert.Null(skin.GetSprite("Volume.Thumb"));
        Assert.False(skin.Has(SpriteMap.Get("Eq.Graph.Background")));
        Assert.NotNull(skin.GetSprite(SpriteMap.VolumeBackground(27)));
        Assert.NotNull(skin.GetSprite("Eq.Presets"));
    }

    [Fact]
    public void Balance_comes_from_the_volume_sheet_when_missing()
    {
        var files = FullSkin();
        files.Remove("BALANCE.BMP");
        var skin = Load(files);
        Assert.Empty(skin.Warnings);
        Assert.Same(skin.Sheets["VOLUME"], skin.Sheets["BALANCE"]);
    }

    [Fact]
    public void Missing_or_damaged_optional_files_are_warnings()
    {
        var files = FullSkin();
        files.Remove("PLEDIT.TXT"); files.Remove("VISCOLOR.TXT"); files.Remove("REGION.TXT"); files.Remove("TEXT.BMP");
        files["CBUTTONS.BMP"] = "this is not a bitmap";
        var skin = Load(files);
        Assert.Contains(skin.Warnings, w => w.StartsWith("TEXT.BMP is missing"));
        Assert.Contains(skin.Warnings, w => w.StartsWith("CBUTTONS.BMP cannot be read"));
        Assert.Null(skin.GetSprite("Buttons.Play"));
        Assert.Equal(PlaylistStyle.Default, skin.Playlist);
        Assert.Equal(VisColors.Default, skin.VisColors);
        Assert.Null(skin.Regions.For("normal"));
    }

    [Fact]
    public void Unusable_files_say_why()
    {
        string Message(Dictionary<string, object> f) => Assert.Throws<SkinLoadException>(() => Load(f)).Message;

        var noMain = FullSkin(); noMain.Remove("MAIN.BMP");
        Assert.Contains("MAIN.BMP is missing", Message(noMain));

        var badMain = FullSkin(); badMain["MAIN.BMP"] = "garbage";
        Assert.Contains("MAIN.BMP cannot be read", Message(badMain));

        Assert.Contains("modern skin", Message(new() { ["skin.xml"] = "<WinampAbstractionLayer/>" }));

        var notZip = new MemoryStream("just text"u8.ToArray());
        Assert.Contains("not a zip", Assert.Throws<SkinLoadException>(() => ClassicSkin.Load(notZip, "x")).Message);

        Assert.Contains("not found", Assert.Throws<SkinLoadException>(() => ClassicSkin.Load(@"C:\nowhere\none.wsz")).Message);
    }

    [Fact]
    public void Refuses_a_file_that_unpacks_to_far_too_much()
    {
        var bomb = FullSkin();
        bomb["MAIN.BMP"] = new byte[ClassicSkin.MaxEntryBytes + 1]; // zeros: tiny when zipped
        Assert.Contains("too big", Assert.Throws<SkinLoadException>(() => Load(bomb)).Message);

        var many = FullSkin();
        for (int i = 0; i <= ClassicSkin.MaxEntries; i++) many[$"junk{i}.txt"] = "x";
        Assert.Contains("too many files", Assert.Throws<SkinLoadException>(() => Load(many)).Message);
    }

    [Fact]
    public void Loads_from_a_file_path()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fernplay-skin-{Guid.NewGuid():N}.wsz");
        try
        {
            using (var f = File.Create(path)) Zip(FullSkin()).CopyTo(f);
            var skin = ClassicSkin.Load(path);
            Assert.Equal(Path.GetFileNameWithoutExtension(path), skin.Name);
        }
        finally { File.Delete(path); }
    }
}

public sealed class SkinTextFileTests
{
    [Fact]
    public void Playlist_colours_with_or_without_hash_and_defaults_for_the_rest()
    {
        var s = PlaylistStyle.Parse("; comment\r\n[text]\r\nnormal=00ff00\r\nCurrent=#FFFFFF // white\r\nSelectedBG=nonsense\r\n");
        Assert.Equal(0xFF00FF00u, s.Normal);
        Assert.Equal(0xFFFFFFFFu, s.Current);
        Assert.Equal(PlaylistStyle.Default.SelectedBackground, s.SelectedBackground);
        Assert.Equal("Arial", s.Font);
        Assert.Equal(PlaylistStyle.Default, PlaylistStyle.Parse(null));
    }

    [Fact]
    public void Vis_colours_skip_comments_clamp_and_keep_defaults_for_missing_lines()
    {
        var c = VisColors.Parse("// header\n\n0,0,0, // black\n300,20,30\n  5 , 6 , 7\n");
        Assert.Equal(24, c.Length);
        Assert.Equal(0xFF000000u, c[0]);
        Assert.Equal(0xFFFF141Eu, c[1]);
        Assert.Equal(0xFF050607u, c[2]);
        Assert.Equal(VisColors.Default[3], c[3]);
    }

    [Fact]
    public void Regions_read_several_polygons_and_wrapped_point_lists()
    {
        var r = SkinRegions.Parse("[Normal]\nNumPoints=4,3\nPointList=0,0 10,0 10,10\n 0,10  20,20 30,20 25,30\n[Equalizer]\nNumPoints=4\nPointList=1,1 2,2\n");
        var normal = r.For("normal")!;
        Assert.Equal(2, normal.Count);
        Assert.Equal((0, 10), normal[0][3]);
        Assert.Equal((25, 30), normal[1][2]);
        Assert.Null(r.For("equalizer")); // 4 points announced, 2 given
        Assert.Single(r.Warnings);
        Assert.Empty(SkinRegions.Parse("; only comments\n; here\n").Windows);
    }

    [Fact]
    public void Skin_font_maps_letters_digits_accents_and_unknowns()
    {
        Assert.Equal(SkinFont.Cell('a'), SkinFont.Cell('A'));
        Assert.Equal((0, 0), SkinFont.Cell('A'));
        Assert.Equal((1, 7), SkinFont.Cell('7'));
        Assert.Equal(SkinFont.Cell('e'), SkinFont.Cell('é'));
        Assert.Equal(SkinFont.Cell('u'), SkinFont.Cell('Ü'));
        Assert.Equal((2, 1), SkinFont.Cell('ö'));
        Assert.Equal(SkinFont.Cell(' '), SkinFont.Cell('€'));
        Assert.Equal(new Sprite("TEXT", 15 * 5, 6, 5, 6), SkinFont.Glyph('-'));
    }
}
