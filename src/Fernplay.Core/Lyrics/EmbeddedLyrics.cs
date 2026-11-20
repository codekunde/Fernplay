using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Fernplay.Core.Lyrics;

/// <summary>Lyrics found in a file's tags: synced lines (ID3 SYLT) and lyrics texts (which may hold LRC times).</summary>
public sealed record EmbeddedFound(IReadOnlyList<LyricLine>? Synced, IReadOnlyList<string> Texts)
{
    public static readonly EmbeddedFound None = new(null, []);
}

/// <summary>
/// Reads lyrics from a song's own tags. Own small readers, no tag library: ID3v2.2 to 2.4 (USLT and SYLT, in MP3 and
/// AAC), FLAC Vorbis comments (LYRICS, UNSYNCEDLYRICS) and MP4 atoms (©lyr). Anything else (WMA's WM/Lyrics) comes
/// from the Windows property system's System.Music.Lyrics, as the other tags do (<see cref="TagReader"/>).
/// </summary>
public static class EmbeddedLyrics
{
    /// <summary>Tags bigger than this are not read (cover pictures make tags large; lyrics never do).</summary>
    private const int MaxTagBytes = 64 * 1024 * 1024;

    /// <summary>What the file's tags hold. Never throws for a bad tag; IO errors are the caller's.</summary>
    public static EmbeddedFound Read(string path)
    {
        EmbeddedFound found;
        using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess))
            found = Read(s);
        if (found.Synced is null && found.Texts.Count == 0 && WindowsLyrics(path) is { } text)
            found = new EmbeddedFound(null, [text]);
        return found;
    }

    /// <summary>The tags in a stream: ID3v2 at the start (any file), then FLAC or MP4 by their signature.</summary>
    public static EmbeddedFound Read(Stream s)
    {
        try
        {
            var head = new byte[12];
            if (ReadFull(s, head) < 10) return EmbeddedFound.None;
            if (head.AsSpan(0, 3).SequenceEqual("ID3"u8))
            {
                int size = SyncSafe(head.AsSpan(6, 4)) + ((head[5] & 0x10) != 0 ? 10 : 0); // + footer
                if (size > MaxTagBytes) return EmbeddedFound.None;
                var tag = new byte[size];
                int got = ReadFull(s, tag.AsSpan(0, Math.Max(0, size)), from: 10);
                var id3 = ReadId3(head.AsSpan(0, 10), tag.AsSpan(0, got));
                if (id3.Synced is not null || id3.Texts.Count > 0) return id3;
                // FLAC with an ID3 tag in front of it (some taggers do that): look behind the tag.
                s.Position = 10 + size;
                if (ReadFull(s, head.AsSpan(0, 4)) == 4 && head.AsSpan(0, 4).SequenceEqual("fLaC"u8)) return ReadFlac(s);
                return EmbeddedFound.None;
            }
            if (head.AsSpan(0, 4).SequenceEqual("fLaC"u8)) { s.Position = 4; return ReadFlac(s); }
            if (head.AsSpan(4, 4).SequenceEqual("ftyp"u8)) { s.Position = 0; return ReadMp4(s); }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or EndOfStreamException or DecoderFallbackException) { }
        return EmbeddedFound.None;
    }

    // ================= ID3v2 =================

    /// <summary>The frames of an ID3v2 tag: <paramref name="header"/> is its 10-byte header, <paramref name="body"/> the rest.</summary>
    internal static EmbeddedFound ReadId3(ReadOnlySpan<byte> header, ReadOnlySpan<byte> body)
    {
        int version = header[3];
        byte flags = header[5];
        if (version is < 2 or > 4) return EmbeddedFound.None;
        if (version == 2 && (flags & 0x40) != 0) return EmbeddedFound.None; // v2.2 compression: never defined
        byte[] data = body.ToArray();
        bool tagUnsync = (flags & 0x80) != 0;
        if (tagUnsync && version < 4) data = Resync(data); // v2.4 marks it on each frame instead
        int pos = 0;
        if (version >= 3 && (flags & 0x40) != 0 && data.Length >= 4) // extended header: skipped
            pos = version == 3 ? 4 + (int)BinaryPrimitives.ReadUInt32BigEndian(data) : SyncSafe(data.AsSpan(0, 4));

        IReadOnlyList<LyricLine>? synced = null;
        var texts = new List<string>();
        int idLength = version == 2 ? 3 : 4, headerLength = version == 2 ? 6 : 10;
        while (pos >= 0 && pos + headerLength <= data.Length && data[pos] != 0)
        {
            string id = Encoding.ASCII.GetString(data, pos, idLength);
            int size = version switch
            {
                2 => data[pos + 3] << 16 | data[pos + 4] << 8 | data[pos + 5],
                3 => (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 4))),
                _ => SyncSafe(data.AsSpan(pos + 4, 4)),
            };
            byte format = version >= 3 ? data[pos + 9] : (byte)0;
            int start = pos + headerLength;
            if (size < 0 || start + size > data.Length) break;
            pos = start + size;
            if (id is not ("USLT" or "ULT" or "SYLT" or "SLT")) continue;

            var frame = data.AsSpan(start, size);
            if (version == 3)
            {
                if ((format & 0xC0) != 0) continue;      // compressed or encrypted
                if ((format & 0x20) != 0) frame = frame[1..]; // group id
            }
            else if (version == 4)
            {
                if ((format & 0x0C) != 0) continue;      // compressed or encrypted
                if ((format & 0x40) != 0) frame = frame[1..]; // group id
                if ((format & 0x01) != 0 && frame.Length >= 4) frame = frame[4..]; // data length indicator
                if ((format & 0x02) != 0 || tagUnsync) frame = Resync(frame.ToArray());
            }
            if (id is "USLT" or "ULT")
            {
                if (Unsynced(frame) is { } text) texts.Add(text);
            }
            else synced ??= Synced(frame);
        }
        return new EmbeddedFound(synced, texts);
    }

    /// <summary>USLT: encoding, language, description, text.</summary>
    private static string? Unsynced(ReadOnlySpan<byte> f)
    {
        if (f.Length < 5) return null;
        int enc = f[0], pos = 4;
        ReadText(f, enc, ref pos, terminated: true); // description
        string text = ReadText(f, enc, ref pos, terminated: false);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// SYLT: encoding, language, time format, content type, description, then (text, time) pairs. Only times in
    /// milliseconds are used (format 2; MPEG frame counts cannot be turned into time here). Entries that start with a
    /// line break begin a new line; when none does, each entry is a line of its own.
    /// </summary>
    private static IReadOnlyList<LyricLine>? Synced(ReadOnlySpan<byte> f)
    {
        if (f.Length < 7 || f[4] != 2) return null;
        int enc = f[0], pos = 6;
        ReadText(f, enc, ref pos, terminated: true); // description
        var entries = new List<(string Text, TimeSpan Time)>();
        while (pos < f.Length)
        {
            string text = ReadText(f, enc, ref pos, terminated: true);
            if (pos + 4 > f.Length) break;
            entries.Add((text, TimeSpan.FromMilliseconds(BinaryPrimitives.ReadUInt32BigEndian(f[pos..]))));
            pos += 4;
        }
        if (entries.Count == 0) return null;

        var lines = new List<(TimeSpan Time, StringBuilder Text)>();
        bool breaks = entries.Skip(1).Any(e => e.Text.StartsWith('\n') || e.Text.StartsWith('\r'));
        foreach (var (text, time) in entries)
        {
            bool newLine = !breaks || lines.Count == 0 || text.StartsWith('\n') || text.StartsWith('\r');
            if (newLine) lines.Add((time, new StringBuilder(text.TrimStart('\r', '\n'))));
            else lines[^1].Text.Append(text);
        }
        var result = lines.Select(l => new LyricLine(l.Time, l.Text.ToString().Trim())).OrderBy(l => l.Time).ToList();
        return result.Any(l => !l.IsGap) ? result : null;
    }

    /// <summary>Text in an ID3 encoding (0 Latin-1, 1 UTF-16 with a mark, 2 UTF-16BE, 3 UTF-8), to its end mark or the end.</summary>
    private static string ReadText(ReadOnlySpan<byte> f, int enc, ref int pos, bool terminated)
    {
        bool wide = enc is 1 or 2;
        int end = f.Length;
        if (terminated)
        {
            end = -1;
            for (int i = pos; i + (wide ? 1 : 0) < f.Length; i += wide ? 2 : 1)
                if (f[i] == 0 && (!wide || f[i + 1] == 0)) { end = i; break; }
            if (end < 0) end = f.Length;
        }
        var bytes = f[pos..end];
        pos = Math.Min(f.Length, end + (terminated ? (wide ? 2 : 1) : 0));
        string text = enc switch
        {
            1 when bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]) => Encoding.BigEndianUnicode.GetString(bytes[2..]),
            1 when bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) => Encoding.Unicode.GetString(bytes[2..]),
            1 => Encoding.Unicode.GetString(bytes),
            2 => Encoding.BigEndianUnicode.GetString(bytes),
            3 => Encoding.UTF8.GetString(bytes),
            _ => Encoding.Latin1.GetString(bytes),
        };
        return text.TrimEnd('\0');
    }

    /// <summary>Undoes ID3 unsynchronisation (FF 00 back to FF).</summary>
    private static byte[] Resync(byte[] data)
    {
        var output = new List<byte>(data.Length);
        for (int i = 0; i < data.Length; i++)
        {
            output.Add(data[i]);
            if (data[i] == 0xFF && i + 1 < data.Length && data[i + 1] == 0) i++;
        }
        return [.. output];
    }

    private static int SyncSafe(ReadOnlySpan<byte> b) => (b[0] & 0x7F) << 21 | (b[1] & 0x7F) << 14 | (b[2] & 0x7F) << 7 | (b[3] & 0x7F);

    // ================= FLAC =================

    /// <summary>FLAC metadata blocks (the stream stands just after "fLaC"): LYRICS and UNSYNCEDLYRICS from the Vorbis comment.</summary>
    private static EmbeddedFound ReadFlac(Stream s)
    {
        var header = new byte[4];
        for (int guard = 0; guard < 128 && ReadFull(s, header) == 4; guard++)
        {
            bool last = (header[0] & 0x80) != 0;
            int type = header[0] & 0x7F, length = header[1] << 16 | header[2] << 8 | header[3];
            if (type == 4)
            {
                var block = new byte[length];
                if (ReadFull(s, block) < length) break;
                return new EmbeddedFound(null, VorbisLyrics(block));
            }
            if (last) break;
            s.Seek(length, SeekOrigin.Current);
        }
        return EmbeddedFound.None;
    }

    /// <summary>The lyrics fields of a Vorbis comment block (little-endian lengths, "KEY=value" in UTF-8).</summary>
    internal static List<string> VorbisLyrics(ReadOnlySpan<byte> block)
    {
        var texts = new List<string>();
        if (block.Length < 8) return texts;
        long pos = 4 + (long)BinaryPrimitives.ReadUInt32LittleEndian(block); // vendor
        if (pos + 4 > block.Length) return texts;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(block[(int)pos..]);
        pos += 4;
        for (uint i = 0; i < count && pos + 4 <= block.Length; i++)
        {
            long length = BinaryPrimitives.ReadUInt32LittleEndian(block[(int)pos..]);
            pos += 4;
            if (pos + length > block.Length) break;
            string field = Encoding.UTF8.GetString(block.Slice((int)pos, (int)length));
            pos += length;
            int eq = field.IndexOf('=');
            if (eq <= 0) continue;
            string key = field[..eq];
            if ((key.Equals("LYRICS", StringComparison.OrdinalIgnoreCase) || key.Equals("UNSYNCEDLYRICS", StringComparison.OrdinalIgnoreCase))
                && !string.IsNullOrWhiteSpace(field[(eq + 1)..]))
                texts.Add(field[(eq + 1)..]);
        }
        return texts;
    }

    // ================= MP4 =================

    /// <summary>moov / udta / meta / ilst / ©lyr / data, read without loading the audio.</summary>
    private static EmbeddedFound ReadMp4(Stream s)
    {
        // Top level: find moov (often after the audio), read it whole.
        long at = 0, length = s.Length;
        var head = new byte[16];
        while (at + 8 <= length)
        {
            s.Position = at;
            if (ReadFull(s, head.AsSpan(0, 8)) < 8) break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(head);
            int headerSize = 8;
            if (size == 1)
            {
                if (ReadFull(s, head.AsSpan(8, 8)) < 8) break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(8));
                headerSize = 16;
            }
            else if (size == 0) size = length - at;
            if (size < headerSize) break;
            if (head.AsSpan(4, 4).SequenceEqual("moov"u8))
            {
                long body = size - headerSize;
                if (body > MaxTagBytes) break;
                var moov = new byte[body];
                int got = ReadFull(s, moov);
                return Mp4Lyrics(moov.AsSpan(0, got)) is { } text ? new EmbeddedFound(null, [text]) : EmbeddedFound.None;
            }
            at += size;
        }
        return EmbeddedFound.None;
    }

    /// <summary>The ©lyr text inside a moov atom's body.</summary>
    internal static string? Mp4Lyrics(ReadOnlySpan<byte> moov)
    {
        var udta = Child(moov, "udta"u8);
        if (udta.IsEmpty) return null;
        var meta = Child(udta, "meta"u8);
        if (meta.Length < 4) return null;
        // "meta" is a full box (4 bytes version and flags) in MP4; QuickTime files leave those out.
        if (!(meta.Length >= 8 && meta.Slice(4, 4).SequenceEqual("hdlr"u8))) meta = meta[4..];
        var ilst = Child(meta, "ilst"u8);
        var lyr = Child(ilst, [0xA9, (byte)'l', (byte)'y', (byte)'r']);
        var data = Child(lyr, "data"u8);
        if (data.Length < 8) return null;
        string text = Encoding.UTF8.GetString(data[8..]); // type and locale first
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>The body of the first child atom of that type (empty if none).</summary>
    private static ReadOnlySpan<byte> Child(ReadOnlySpan<byte> parent, ReadOnlySpan<byte> type)
    {
        int pos = 0;
        while (pos + 8 <= parent.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(parent[pos..]);
            int header = 8;
            if (size == 1 && pos + 16 <= parent.Length) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(parent[(pos + 8)..]); header = 16; }
            else if (size == 0) size = parent.Length - pos;
            if (size < header || pos + size > parent.Length) break;
            if (parent.Slice(pos + 4, 4).SequenceEqual(type)) return parent.Slice(pos + header, (int)size - header);
            pos += (int)size;
        }
        return [];
    }

    // ================= Helpers =================

    private static int ReadFull(Stream s, Span<byte> buffer, long? from = null)
    {
        if (from is { } p) s.Position = p;
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    // ================= Windows property system =================

    private static readonly TagReader.PropertyKey LyricsKey = new(new Guid("56A3372E-CE9C-11D2-9F0E-006097C686F6"), 12); // System.Music.Lyrics

    /// <summary>System.Music.Lyrics as Windows reads it (WMA's WM/Lyrics and whatever else its handlers know), or null.</summary>
    internal static string? WindowsLyrics(string path)
    {
        try
        {
            var iid = typeof(TagReader.IPropertyStore).GUID;
            if (TagReader.SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out var store) < 0) return null;
            try { return TagReader.GetString(store, LyricsKey); }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException) { return null; }
    }
}
