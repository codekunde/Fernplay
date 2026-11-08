using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Fernplay.Core.Audio;

/// <summary>Volume levelling: leave tracks as they are, or play them at the level their ReplayGain tags give.</summary>
public enum LevelMode { Off, Track, Album }

/// <summary>
/// A file's ReplayGain values (gain in dB, peak as a linear sample value, 1 = full scale); null where the tag is missing.
/// Fernplay only reads these tags; a tagger (foobar2000, MusicBee, rsgain, mp3gain) writes them.
/// </summary>
public sealed record ReplayGain(double? TrackGain, double? TrackPeak, double? AlbumGain, double? AlbumPeak)
{
    /// <summary>
    /// Gain in dB for files without ReplayGain tags while levelling is on. Tagged music mostly comes out 6 to 10 dB
    /// quieter than mastered (the ReplayGain reference is 89 dB), so untagged files are turned down too, as Winamp does.
    /// </summary>
    public const double UntaggedDb = -6;

    /// <summary>
    /// The linear gain to play a file at. Off = 1. Track mode falls back to the album gain and Album mode to the track
    /// gain when only one is tagged. The gain never pushes the tagged peak over full scale, and without a peak it never boosts.
    /// </summary>
    public static float GainFor(ReplayGain? rg, LevelMode mode)
    {
        if (mode == LevelMode.Off) return 1f;
        double? db, peak;
        if (mode == LevelMode.Album && rg?.AlbumGain is { } a) (db, peak) = (a, rg.AlbumPeak);
        else if (rg?.TrackGain is { } t) (db, peak) = (t, rg.TrackPeak);
        else if (rg?.AlbumGain is { } a2) (db, peak) = (a2, rg.AlbumPeak);
        else (db, peak) = (UntaggedDb, null);

        double g = Math.Pow(10, db.Value / 20);
        if (peak is > 0) g = Math.Min(g, 1 / peak.Value);
        else g = Math.Min(g, 1);
        return (float)g;
    }

    public bool IsEmpty => TrackGain is null && AlbumGain is null;
}

/// <summary>
/// Reads ReplayGain tags without a tag library (Windows' property system does not expose them): ID3v2 TXXX frames and
/// APEv2 tags (MP3, AAC; mp3gain writes APEv2), Vorbis comments (FLAC) and iTunes freeform atoms (M4A, MP4).
/// WAV and WMA are not read. Never throws: a file it cannot read gives null.
/// </summary>
public static class ReplayGainReader
{
    private const int MaxTagBytes = 64 * 1024 * 1024; // a tag with large cover art still fits; anything bigger is skipped

    public static ReplayGain? TryRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Read(fs, values);
            var rg = new ReplayGain(
                Gain(values, "REPLAYGAIN_TRACK_GAIN"), Peak(values, "REPLAYGAIN_TRACK_PEAK"),
                Gain(values, "REPLAYGAIN_ALBUM_GAIN"), Peak(values, "REPLAYGAIN_ALBUM_PEAK"));
            return rg.IsEmpty ? null : rg;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Collects the REPLAYGAIN_* text values found in any tag of the stream (first one found wins).</summary>
    internal static void Read(Stream s, Dictionary<string, string> values)
    {
        var head = new byte[12];
        s.Position = 0;
        int n = ReadUpTo(s, head);
        long afterId3 = 0;
        if (n >= 10 && head[0] == 'I' && head[1] == 'D' && head[2] == '3')
            afterId3 = ReadId3v2(s, values);

        s.Position = afterId3;
        n = ReadUpTo(s, head);
        if (n >= 4 && Encoding.ASCII.GetString(head, 0, 4) == "fLaC") ReadFlac(s, afterId3 + 4, values);
        else if (n >= 8 && Encoding.ASCII.GetString(head, 4, 4) == "ftyp") ReadMp4(s, values);
        else ReadApe(s, values);
    }

    // ================= ID3v2 =================

    // Returns where the audio starts (after the tag).
    private static long ReadId3v2(Stream s, Dictionary<string, string> values)
    {
        var h = new byte[10];
        s.Position = 0;
        if (ReadUpTo(s, h) < 10) return 0;
        int major = h[3], flags = h[5];
        int size = Syncsafe(h.AsSpan(6, 4));
        long end = 10 + size + ((flags & 0x10) != 0 ? 10 : 0);
        if (major is < 2 or > 4 || size <= 0 || size > MaxTagBytes) return Math.Max(0, end);

        var tag = new byte[size];
        if (ReadUpTo(s, tag) < size) return end;
        ReadOnlySpan<byte> body = tag;
        if (major < 4 && (flags & 0x80) != 0) body = Unsync(body); // v2.2/2.3: unsynchronised as a whole

        int pos = 0;
        if ((flags & 0x40) != 0 && major >= 3)
        {
            if (body.Length < 4) return end;
            pos = major == 3 ? 4 + BinaryPrimitives.ReadInt32BigEndian(body) : Syncsafe(body[..4]);
            if (pos < 0 || pos > body.Length) return end;
        }

        int headerLen = major == 2 ? 6 : 10;
        while (pos + headerLen <= body.Length && body[pos] != 0)
        {
            string id;
            int frameSize, frameFlags = 0;
            if (major == 2)
            {
                id = Encoding.ASCII.GetString(body.Slice(pos, 3));
                frameSize = (body[pos + 3] << 16) | (body[pos + 4] << 8) | body[pos + 5];
            }
            else
            {
                id = Encoding.ASCII.GetString(body.Slice(pos, 4));
                frameSize = major == 4 ? Syncsafe(body.Slice(pos + 4, 4)) : BinaryPrimitives.ReadInt32BigEndian(body.Slice(pos + 4, 4));
                frameFlags = body[pos + 9];
            }
            pos += headerLen;
            if (frameSize <= 0 || pos + frameSize > body.Length) break;
            var data = body.Slice(pos, frameSize);
            pos += frameSize;

            if (id is not ("TXXX" or "TXX")) continue;
            if (major == 4)
            {
                if ((frameFlags & 0x0C) != 0) continue;          // compressed or encrypted
                if ((frameFlags & 0x01) != 0) data = data.Length >= 4 ? data[4..] : default; // data length indicator
                if ((frameFlags & 0x02) != 0) data = Unsync(data);
            }
            if (ParseTxxx(data) is var (key, value)) Add(values, key, value);
        }
        return end;
    }

    private static (string Key, string Value)? ParseTxxx(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return null;
        int enc = data[0];
        var rest = data[1..];
        bool wide = enc is 1 or 2;
        int term = FindTerminator(rest, wide);
        if (term < 0) return null;
        string key = Decode(rest[..term], enc);
        string value = Decode(rest[(term + (wide ? 2 : 1))..], enc);
        return (key, value);
    }

    private static int FindTerminator(ReadOnlySpan<byte> b, bool wide)
    {
        if (!wide) return b.IndexOf((byte)0);
        for (int i = 0; i + 1 < b.Length; i += 2)
            if (b[i] == 0 && b[i + 1] == 0) return i;
        return -1;
    }

    private static string Decode(ReadOnlySpan<byte> b, int enc)
    {
        string text = enc switch
        {
            1 => b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF ? Encoding.BigEndianUnicode.GetString(b[2..])
                : b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE ? Encoding.Unicode.GetString(b[2..])
                : Encoding.Unicode.GetString(b),
            2 => Encoding.BigEndianUnicode.GetString(b),
            3 => Encoding.UTF8.GetString(b),
            _ => Encoding.Latin1.GetString(b),
        };
        return text.TrimEnd('\0');
    }

    private static byte[] Unsync(ReadOnlySpan<byte> b)
    {
        var o = new List<byte>(b.Length);
        for (int i = 0; i < b.Length; i++)
        {
            o.Add(b[i]);
            if (b[i] == 0xFF && i + 1 < b.Length && b[i + 1] == 0) i++;
        }
        return [.. o];
    }

    private static int Syncsafe(ReadOnlySpan<byte> b) => (b[0] & 0x7F) << 21 | (b[1] & 0x7F) << 14 | (b[2] & 0x7F) << 7 | (b[3] & 0x7F);

    // ================= APEv2 (at the end of the file, before an ID3v1 tag if there is one) =================

    private static void ReadApe(Stream s, Dictionary<string, string> values)
    {
        foreach (long footerAt in new[] { s.Length - 32, s.Length - 128 - 32 })
        {
            if (footerAt < 0) continue;
            var f = new byte[32];
            s.Position = footerAt;
            if (ReadUpTo(s, f) < 32 || Encoding.ASCII.GetString(f, 0, 8) != "APETAGEX") continue;
            int size = BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(12)); // items + footer
            int count = BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(16));
            if (size < 32 || size > MaxTagBytes || footerAt + 32 - size < 0) return;
            var items = new byte[size - 32];
            s.Position = footerAt + 32 - size;
            if (ReadUpTo(s, items) < items.Length) return;
            int pos = 0;
            for (int i = 0; i < count && pos + 8 < items.Length; i++)
            {
                int len = BinaryPrimitives.ReadInt32LittleEndian(items.AsSpan(pos));
                int itemFlags = BinaryPrimitives.ReadInt32LittleEndian(items.AsSpan(pos + 4));
                int keyEnd = Array.IndexOf(items, (byte)0, pos + 8);
                if (len < 0 || keyEnd < 0 || keyEnd + 1 + len > items.Length) return;
                if (((itemFlags >> 1) & 3) == 0) // text
                    Add(values, Encoding.ASCII.GetString(items, pos + 8, keyEnd - pos - 8), Encoding.UTF8.GetString(items, keyEnd + 1, len));
                pos = keyEnd + 1 + len;
            }
            return;
        }
    }

    // ================= FLAC =================

    private static void ReadFlac(Stream s, long firstBlock, Dictionary<string, string> values)
    {
        long pos = firstBlock;
        var h = new byte[4];
        while (true)
        {
            s.Position = pos;
            if (ReadUpTo(s, h) < 4) return;
            bool last = (h[0] & 0x80) != 0;
            int type = h[0] & 0x7F;
            int len = (h[1] << 16) | (h[2] << 8) | h[3];
            if (type == 4)
            {
                var block = new byte[len];
                if (ReadUpTo(s, block) < len) return;
                ReadVorbisComments(block, values);
                return;
            }
            if (last || type == 127) return;
            pos += 4 + len;
        }
    }

    internal static void ReadVorbisComments(ReadOnlySpan<byte> b, Dictionary<string, string> values)
    {
        if (b.Length < 8) return;
        int vendor = BinaryPrimitives.ReadInt32LittleEndian(b);
        if (vendor < 0 || 8 + vendor > b.Length) return;
        int pos = 4 + vendor;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(b[pos..]);
        pos += 4;
        for (uint i = 0; i < count && pos + 4 <= b.Length; i++)
        {
            int len = BinaryPrimitives.ReadInt32LittleEndian(b[pos..]);
            pos += 4;
            if (len < 0 || pos + len > b.Length) return;
            string c = Encoding.UTF8.GetString(b.Slice(pos, len));
            pos += len;
            int eq = c.IndexOf('=');
            if (eq > 0) Add(values, c[..eq], c[(eq + 1)..]);
        }
    }

    // ================= MP4 (moov/udta/meta/ilst/----) =================

    private static void ReadMp4(Stream s, Dictionary<string, string> values)
    {
        // Top level: find moov without reading mdat (the audio).
        long pos = 0;
        var h = new byte[16];
        while (pos + 8 <= s.Length)
        {
            s.Position = pos;
            if (ReadUpTo(s, h) < 8) return;
            long size = BinaryPrimitives.ReadUInt32BigEndian(h);
            string type = Encoding.ASCII.GetString(h, 4, 4);
            int header = 8;
            if (size == 1) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(h.AsSpan(8)); header = 16; }
            else if (size == 0) size = s.Length - pos;
            if (size < header) return;
            if (type == "moov")
            {
                if (size > MaxTagBytes) return;
                var moov = new byte[(int)(size - header)];
                s.Position = pos + header;
                if (ReadUpTo(s, moov) < moov.Length) return;
                // iTunes and most taggers: moov/udta/meta. QuickTime style (ffmpeg): moov/meta, or udta/meta with a keys list.
                var udta = Child(moov, "udta");
                foreach (var meta in new[] { udta.IsEmpty ? [] : Child(udta, "meta").ToArray(), Child(moov, "meta").ToArray() })
                {
                    if (meta.Length < 4) continue;
                    // iTunes' meta is a full box (version and flags first); QuickTime's is not.
                    var inner = meta.AsSpan(Encoding.ASCII.GetString(meta, 4, Math.Min(4, meta.Length - 4)) == "hdlr" ? 0 : 4);
                    ReadIlst(Child(inner, "ilst"), KeyNames(Child(inner, "keys")), values);
                }
                return;
            }
            pos += size;
        }
    }

    // QuickTime keys box: version/flags, count, then (size, namespace, name) per key; ilst items refer to them by 1-based index.
    private static List<string> KeyNames(ReadOnlySpan<byte> keys)
    {
        var names = new List<string>();
        if (keys.Length < 8) return names;
        uint count = BinaryPrimitives.ReadUInt32BigEndian(keys[4..]);
        int pos = 8;
        for (uint i = 0; i < count && pos + 8 <= keys.Length; i++)
        {
            int size = BinaryPrimitives.ReadInt32BigEndian(keys[pos..]);
            if (size < 8 || pos + size > keys.Length) break;
            names.Add(Encoding.UTF8.GetString(keys.Slice(pos + 8, size - 8)));
            pos += size;
        }
        return names;
    }

    private static void ReadIlst(ReadOnlySpan<byte> ilst, List<string> keyNames, Dictionary<string, string> values)
    {
        foreach (var (type, item) in Boxes(ilst))
        {
            if (type != "----")
            {
                // QuickTime style: the item type is an index into the keys list.
                var raw = Encoding.Latin1.GetBytes(type);
                int index = (int)BinaryPrimitives.ReadUInt32BigEndian(raw);
                if (index < 1 || index > keyNames.Count) continue;
                var box = Child(item, "data");
                if (box.Length >= 8) Add(values, keyNames[index - 1], Encoding.UTF8.GetString(box[8..]));
                continue;
            }
            string? name = null, data = null;
            foreach (var (t, box) in Boxes(item))
            {
                if (t == "name" && box.Length >= 4) name = Encoding.UTF8.GetString(box[4..]);
                else if (t == "data" && box.Length >= 8) data = Encoding.UTF8.GetString(box[8..]);
            }
            if (name is not null && data is not null) Add(values, name, data);
        }
    }

    private static ReadOnlySpan<byte> Child(ReadOnlySpan<byte> parent, string type)
    {
        foreach (var (t, body) in Boxes(parent))
            if (t == type) return body;
        return default;
    }

    private static List<(string Type, byte[] Body)> Boxes(ReadOnlySpan<byte> b)
    {
        var list = new List<(string, byte[])>();
        int pos = 0;
        while (pos + 8 <= b.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(b[pos..]);
            string type = Encoding.Latin1.GetString(b.Slice(pos + 4, 4));
            int header = 8;
            if (size == 1)
            {
                if (pos + 16 > b.Length) break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(b[(pos + 8)..]);
                header = 16;
            }
            else if (size == 0) size = b.Length - pos;
            if (size < header || pos + size > b.Length) break;
            list.Add((type, b.Slice(pos + header, (int)size - header).ToArray()));
            pos += (int)size;
        }
        return list;
    }

    // ================= values =================

    private static void Add(Dictionary<string, string> values, string key, string value)
    {
        key = key.Trim();
        if (key.StartsWith("REPLAYGAIN_", StringComparison.OrdinalIgnoreCase)) values.TryAdd(key, value);
    }

    private static double? Gain(Dictionary<string, string> v, string key) =>
        v.TryGetValue(key, out var s) && Number(s) is { } d && d is >= -60 and <= 60 ? d : null;

    private static double? Peak(Dictionary<string, string> v, string key) =>
        v.TryGetValue(key, out var s) && Number(s) is { } d && d is > 0 and <= 10 ? d : null;

    /// <summary>"-7.23 dB", "+1.5 dB", "0.987654"; a comma is taken as the decimal point when there is no dot.</summary>
    internal static double? Number(string text)
    {
        var t = text.Trim().TrimEnd('\0').Trim();
        if (t.EndsWith("dB", StringComparison.OrdinalIgnoreCase)) t = t[..^2].Trim();
        if (!t.Contains('.')) t = t.Replace(',', '.');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;
    }

    private static int ReadUpTo(Stream s, Span<byte> buffer)
    {
        int n = 0;
        while (n < buffer.Length)
        {
            int got = s.Read(buffer[n..]);
            if (got <= 0) break;
            n += got;
        }
        return n;
    }
}
