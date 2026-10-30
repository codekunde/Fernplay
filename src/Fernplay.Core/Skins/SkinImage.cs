using System.Buffers.Binary;

namespace Fernplay.Core.Skins;

/// <summary>A decoded sprite sheet or sprite: opaque pixels as 0xAARRGGBB, rows top to bottom.</summary>
public sealed class SkinImage
{
    public int Width { get; }
    public int Height { get; }
    public uint[] Pixels { get; }

    public SkinImage(int width, int height, uint[] pixels)
    {
        if (width <= 0 || height <= 0 || pixels.Length != width * height) throw new ArgumentException("Size does not match the pixels.");
        Width = width; Height = height; Pixels = pixels;
    }

    public uint this[int x, int y] => Pixels[y * Width + x];

    /// <summary>True when the rectangle lies completely inside the image.</summary>
    public bool Contains(int x, int y, int w, int h) => x >= 0 && y >= 0 && w > 0 && h > 0 && x + w <= Width && y + h <= Height;

    /// <summary>A copy of one rectangle, or null when it does not lie completely inside the image.</summary>
    public SkinImage? Crop(int x, int y, int w, int h)
    {
        if (!Contains(x, y, w, h)) return null;
        var p = new uint[w * h];
        for (int row = 0; row < h; row++) Array.Copy(Pixels, (y + row) * Width + x, p, row * w, w);
        return new SkinImage(w, h, p);
    }
}

/// <summary>
/// Reads Windows BMP files as classic skins use them: 1, 4, 8, 16, 24 and 32 bits per pixel, uncompressed,
/// RLE8, RLE4 and bit fields; bottom-up and top-down. Everything comes out opaque (skins have no alpha).
/// </summary>
public static class BmpDecoder
{
    public const int MaxSide = 4096;

    public static SkinImage Decode(ReadOnlySpan<byte> file)
    {
        if (file.Length < 26 || file[0] != (byte)'B' || file[1] != (byte)'M') throw new InvalidDataException("Not a BMP file.");
        int dataOffset = BinaryPrimitives.ReadInt32LittleEndian(file[10..]);
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(file[14..]);
        int width, height, bpp, compression = 0, colorsUsed = 0, paletteEntrySize = 4;
        if (headerSize == 12) // OS/2 BITMAPCOREHEADER
        {
            width = BinaryPrimitives.ReadInt16LittleEndian(file[18..]);
            height = BinaryPrimitives.ReadInt16LittleEndian(file[20..]);
            bpp = BinaryPrimitives.ReadInt16LittleEndian(file[24..]);
            paletteEntrySize = 3;
        }
        else if (headerSize >= 40 && file.Length >= 14 + 40)
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(file[18..]);
            height = BinaryPrimitives.ReadInt32LittleEndian(file[22..]);
            bpp = BinaryPrimitives.ReadInt16LittleEndian(file[28..]);
            compression = BinaryPrimitives.ReadInt32LittleEndian(file[30..]);
            colorsUsed = BinaryPrimitives.ReadInt32LittleEndian(file[46..]);
        }
        else throw new InvalidDataException($"Unknown BMP header ({headerSize} bytes).");

        bool topDown = height < 0;
        height = Math.Abs(height);
        if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide) throw new InvalidDataException($"BMP size {width}x{height} is out of range.");

        // Bit-field masks: inside the header from version 4 on, else right after the 40-byte header.
        uint rMask = 0, gMask = 0, bMask = 0;
        int paletteStart = 14 + headerSize;
        if (compression == 3 || compression == 6)
        {
            int at = headerSize >= 52 ? 14 + 40 : 14 + headerSize;
            if (file.Length < at + 12) throw new InvalidDataException("BMP bit fields are cut short.");
            rMask = BinaryPrimitives.ReadUInt32LittleEndian(file[at..]);
            gMask = BinaryPrimitives.ReadUInt32LittleEndian(file[(at + 4)..]);
            bMask = BinaryPrimitives.ReadUInt32LittleEndian(file[(at + 8)..]);
            if (headerSize == 40) paletteStart += compression == 6 ? 16 : 12;
        }
        else if (bpp == 16) { rMask = 0x7C00; gMask = 0x03E0; bMask = 0x001F; }

        uint[] palette = [];
        if (bpp <= 8)
        {
            int count = colorsUsed > 0 && colorsUsed <= 1 << bpp ? colorsUsed : 1 << bpp;
            palette = new uint[1 << bpp];
            Array.Fill(palette, 0xFF000000u); // entries the file leaves out draw black, as Windows does
            for (int i = 0; i < count; i++)
            {
                int at = paletteStart + i * paletteEntrySize;
                if (at + 3 > file.Length) break;
                palette[i] = 0xFF000000u | (uint)file[at + 2] << 16 | (uint)file[at + 1] << 8 | file[at];
            }
        }

        if (dataOffset <= 0 || dataOffset > file.Length) throw new InvalidDataException("BMP pixel data is missing.");
        var data = file[dataOffset..];
        var pixels = new uint[width * height];
        // Row r of the file is image row (topDown ? r : height - 1 - r).
        int Row(int r) => topDown ? r : height - 1 - r;

        switch (compression)
        {
            case 0 or 3 or 6 when bpp is 1 or 4 or 8 or 16 or 24 or 32:
            {
                int stride = (width * bpp + 31) / 32 * 4;
                if ((long)stride * height > data.Length) throw new InvalidDataException("BMP pixel data is cut short.");
                for (int r = 0; r < height; r++)
                {
                    var line = data.Slice(r * stride, stride);
                    int o = Row(r) * width;
                    for (int x = 0; x < width; x++) pixels[o + x] = ReadPixel(line, x, bpp, palette, rMask, gMask, bMask);
                }
                break;
            }
            case 1 when bpp == 8: Rle(data, width, height, 8, palette, pixels, Row); break;
            case 2 when bpp == 4: Rle(data, width, height, 4, palette, pixels, Row); break;
            default: throw new InvalidDataException($"BMP format not supported ({bpp} bits, compression {compression}).");
        }
        return new SkinImage(width, height, pixels);
    }

    private static uint ReadPixel(ReadOnlySpan<byte> line, int x, int bpp, uint[] palette, uint rMask, uint gMask, uint bMask)
    {
        switch (bpp)
        {
            case 1: return palette[(line[x >> 3] >> (7 - (x & 7))) & 1];
            case 4: return palette[(x & 1) == 0 ? line[x >> 1] >> 4 : line[x >> 1] & 0xF];
            case 8: return palette[line[x]];
            case 16: return Masked(BinaryPrimitives.ReadUInt16LittleEndian(line[(x * 2)..]), rMask, gMask, bMask);
            case 24: return 0xFF000000u | (uint)line[x * 3 + 2] << 16 | (uint)line[x * 3 + 1] << 8 | line[x * 3];
            default:
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(line[(x * 4)..]);
                return rMask == 0 ? 0xFF000000u | (v & 0xFFFFFF) : Masked(v, rMask, gMask, bMask);
        }
    }

    private static uint Masked(uint v, uint rMask, uint gMask, uint bMask) =>
        0xFF000000u | Channel(v, rMask) << 16 | Channel(v, gMask) << 8 | Channel(v, bMask);

    /// <summary>Scales a masked channel to 0..255.</summary>
    private static uint Channel(uint v, uint mask)
    {
        if (mask == 0) return 0;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        return ((v & mask) >> shift) * 255 / max;
    }

    /// <summary>RLE8 / RLE4: runs, literal blocks, end of line, end of bitmap, delta. Pixels never written stay black.</summary>
    private static void Rle(ReadOnlySpan<byte> d, int width, int height, int bits, uint[] palette, uint[] pixels, Func<int, int> row)
    {
        for (int i = 0; i < pixels.Length; i++) pixels[i] = 0xFF000000u;
        int x = 0, r = 0, i2 = 0;
        void Put(int index) { if (x < width && r < height) pixels[row(r) * width + x] = palette[index]; x++; }
        while (i2 + 1 < d.Length && r < height)
        {
            int count = d[i2], value = d[i2 + 1];
            i2 += 2;
            if (count > 0)
            {
                for (int k = 0; k < count; k++) Put(bits == 8 ? value : (k & 1) == 0 ? value >> 4 : value & 0xF);
                continue;
            }
            switch (value)
            {
                case 0: x = 0; r++; break;            // end of line
                case 1: return;                       // end of bitmap
                case 2:                               // delta
                    if (i2 + 1 >= d.Length) return;
                    x += d[i2]; r += d[i2 + 1]; i2 += 2; break;
                default:                              // literal block of `value` pixels, padded to 2 bytes
                {
                    int bytes = bits == 8 ? value : (value + 1) / 2;
                    if (i2 + bytes > d.Length) return;
                    for (int k = 0; k < value; k++) Put(bits == 8 ? d[i2 + k] : (k & 1) == 0 ? d[i2 + k / 2] >> 4 : d[i2 + k / 2] & 0xF);
                    i2 += bytes + (bytes & 1);
                    break;
                }
            }
        }
    }
}
