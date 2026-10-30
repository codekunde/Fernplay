using System.IO.Compression;
using System.Text;

namespace Fernplay.Tests;

/// <summary>
/// Builds BMP files and .wsz skins in memory for the skin tests (own drawing; no third-party skin in the repo).
/// Sheet pixels encode their own position, so a cropped sprite can be checked exactly: colour = x &lt;&lt; 12 | y.
/// </summary>
internal static class SyntheticSkin
{
    /// <summary>Sheet sizes of a complete classic skin.</summary>
    public static readonly Dictionary<string, (int W, int H)> FullSizes = new()
    {
        ["MAIN"] = (275, 116), ["TITLEBAR"] = (344, 87), ["CBUTTONS"] = (136, 36), ["POSBAR"] = (307, 10),
        ["VOLUME"] = (68, 433), ["BALANCE"] = (68, 433), ["SHUFREP"] = (92, 85), ["NUMBERS"] = (99, 13),
        ["TEXT"] = (155, 18), ["MONOSTER"] = (58, 24), ["PLAYPAUS"] = (42, 9), ["EQMAIN"] = (275, 315), ["PLEDIT"] = (280, 186),
        ["GEN"] = (194, 109), ["GENEX"] = (130, 75),
    };

    public static uint PositionColor(int x, int y) => 0xFF000000u | (uint)x << 12 | (uint)y;

    /// <summary>24-bit bottom-up BMP whose pixels come from <paramref name="color"/>.</summary>
    public static byte[] Bmp24(int w, int h, Func<int, int, uint> color, bool topDown = false)
    {
        int stride = (w * 3 + 3) / 4 * 4;
        var px = new byte[stride * h];
        for (int y = 0; y < h; y++)
        {
            int row = topDown ? y : h - 1 - y;
            for (int x = 0; x < w; x++)
            {
                uint c = color(x, y);
                int o = row * stride + x * 3;
                px[o] = (byte)c; px[o + 1] = (byte)(c >> 8); px[o + 2] = (byte)(c >> 16);
            }
        }
        return Wrap(w, topDown ? -h : h, 24, 0, [], px);
    }

    /// <summary>Indexed BMP (1, 4 or 8 bits) from palette indexes; <paramref name="paletteCount"/> entries written.</summary>
    public static byte[] BmpIndexed(int w, int h, int bits, uint[] palette, Func<int, int, int> index)
    {
        int stride = (w * bits + 31) / 32 * 4;
        var px = new byte[stride * h];
        for (int y = 0; y < h; y++)
        {
            int row = h - 1 - y;
            for (int x = 0; x < w; x++)
            {
                int i = index(x, y), bit = x * bits;
                px[row * stride + bit / 8] |= (byte)(i << (8 - bits - bit % 8));
            }
        }
        return Wrap(w, h, bits, 0, palette, px);
    }

    /// <summary>A BMP with the given header values and raw pixel bytes (for RLE, bit fields and broken files).</summary>
    public static byte[] Wrap(int w, int h, int bits, int compression, uint[] palette, byte[] pixels, uint[]? masks = null)
    {
        int extra = masks is null ? 0 : masks.Length * 4;
        int offset = 14 + 40 + extra + palette.Length * 4;
        var b = new byte[offset + pixels.Length];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BitConverter.GetBytes(b.Length).CopyTo(b, 2);
        BitConverter.GetBytes(offset).CopyTo(b, 10);
        BitConverter.GetBytes(40).CopyTo(b, 14);
        BitConverter.GetBytes(w).CopyTo(b, 18);
        BitConverter.GetBytes(h).CopyTo(b, 22);
        BitConverter.GetBytes((short)1).CopyTo(b, 26);
        BitConverter.GetBytes((short)bits).CopyTo(b, 28);
        BitConverter.GetBytes(compression).CopyTo(b, 30);
        BitConverter.GetBytes(pixels.Length).CopyTo(b, 34);
        BitConverter.GetBytes(palette.Length).CopyTo(b, 46);
        int at = 54;
        foreach (var m in masks ?? []) { BitConverter.GetBytes(m).CopyTo(b, at); at += 4; }
        foreach (var p in palette) { BitConverter.GetBytes(p & 0xFFFFFF).CopyTo(b, at); at += 4; }
        pixels.CopyTo(b, offset);
        return b;
    }

    /// <summary>A .wsz in memory: file name → content (string = UTF-8 text, byte[] as is).</summary>
    public static MemoryStream Zip(IEnumerable<KeyValuePair<string, object>> files)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in files)
            {
                using var s = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
                var bytes = content as byte[] ?? Encoding.UTF8.GetBytes((string)content);
                s.Write(bytes);
            }
        ms.Position = 0;
        return ms;
    }

    /// <summary>All sheets of a full skin at their usual sizes, with position-coded pixels, plus the text files.</summary>
    public static Dictionary<string, object> FullSkin(Func<string, string>? name = null)
    {
        name ??= s => s + ".BMP";
        var files = new Dictionary<string, object>();
        foreach (var (sheet, (w, h)) in FullSizes) files[name(sheet)] = Bmp24(w, h, PositionColor);
        files["PLEDIT.TXT"] = "[Text]\r\nNormal=#112233\r\nCurrent=#445566\r\nNormalBG=#000000\r\nSelectedBG=#0000C6\r\nFont=Tahoma\r\n";
        files["VISCOLOR.TXT"] = string.Join("\r\n", Enumerable.Range(0, 24).Select(i => $"{i},{i * 2},{i * 3}, // colour {i}"));
        files["REGION.TXT"] = "[Normal]\r\nNumPoints=4\r\nPointList=0,0 275,0 275,116 0,116\r\n";
        return files;
    }
}
