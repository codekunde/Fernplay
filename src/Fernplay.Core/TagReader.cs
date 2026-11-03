using System.Runtime.InteropServices;

namespace Fernplay.Core;

/// <summary>
/// Title, artist, album, duration and bitrate from the Windows property system (IPropertyStore).
/// No tag library needed: Windows reads ID3, FLAC, MP4 and WMA tags itself.
/// </summary>
internal static class TagReader
{
    private static readonly Guid SummaryInfo = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");
    private static readonly Guid Music = new("56A3372E-CE9C-11D2-9F0E-006097C686F6");
    private static readonly Guid Media = new("64440490-4C8B-11D1-8B70-080036B11A03");

    internal static readonly PropertyKey Title = new(SummaryInfo, 2);
    internal static readonly PropertyKey Artist = new(Music, 2);
    internal static readonly PropertyKey Album = new(Music, 4);
    internal static readonly PropertyKey Duration = new(Media, 3);        // 100 ns units
    internal static readonly PropertyKey EncodingBitrate = new(Media, 4); // bits per second
    internal static readonly PropertyKey TrackNumber = new(Music, 7);     // System.Music.TrackNumber
    internal static readonly PropertyKey AlbumArtist = new(Music, 13);    // System.Music.AlbumArtist
    internal static readonly PropertyKey Genre = new(Music, 11);          // System.Music.Genre
    internal static readonly PropertyKey Year = new(Music, 5);            // System.Media.Year (Media, 5 is System.Audio.SampleRate)
    internal static readonly PropertyKey Rating = new(new Guid("64440492-4C8B-11D1-8B70-080036B11A03"), 9); // System.Rating, 1-99

    public static Track Read(string path) => ReadTags(path).ToTrack(path);

    /// <summary>Everything Fernplay reads from a file's tags (the library uses the extra fields).</summary>
    public static TagInfo ReadTags(string path)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out var store));
        try
        {
            ulong ticks = GetUInt64(store, Duration);
            uint bps = (uint)GetUInt64(store, EncodingBitrate);
            ulong number = GetUInt64(store, TrackNumber);
            ulong year = GetUInt64(store, Year);
            return new TagInfo(
                GetString(store, Title),
                GetString(store, Artist),
                GetString(store, Album),
                ticks > 0 ? TimeSpan.FromTicks((long)ticks) : null,
                bps > 0 ? (int)Math.Round(bps / 1000.0) : null,
                number is > 0 and < 10000 ? (int)number : null,
                GetString(store, AlbumArtist),
                GetString(store, Genre),
                year is > 0 and < 10000 ? (int)year : null,
                TagInfo.Stars((int)GetUInt64(store, Rating)));
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    internal static string? GetString(IPropertyStore store, PropertyKey key)
    {
        var k = key;
        if (store.GetValue(ref k, out var pv) != 0) return null;
        try
        {
            if (pv.vt == 0) return null; // VT_EMPTY
            // Handles single strings and string vectors (several artists are joined with "; ").
            if (PropVariantToStringAlloc(ref pv, out var text) != 0 || text == IntPtr.Zero) return null;
            try { var s = Marshal.PtrToStringUni(text); return string.IsNullOrWhiteSpace(s) ? null : s.Trim(); }
            finally { Marshal.FreeCoTaskMem(text); }
        }
        finally { PropVariantClear(ref pv); }
    }

    private static ulong GetUInt64(IPropertyStore store, PropertyKey key)
    {
        var k = key;
        if (store.GetValue(ref k, out var pv) != 0) return 0;
        try { return pv.vt == 0 || PropVariantToUInt64(ref pv, out var v) != 0 ? 0 : v; }
        finally { PropVariantClear(ref pv); }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct PropertyKey(Guid fmtid, uint pid)
    {
        public readonly Guid FormatId = fmtid;
        public readonly uint PropertyId = pid;
    }

    // PROPVARIANT: 16 bytes on x86, 24 on x64; only vt is read directly.
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort vt;
        private ushort r1, r2, r3;
        private IntPtr p1, p2;

        /// <summary>A VT_LPWSTR holding a CoTaskMem copy of text; PropVariantClear frees it.</summary>
        public static PropVariant FromString(string text) => new() { vt = 31, p1 = Marshal.StringToCoTaskMemUni(text) };
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, int flags, ref Guid iid, out IPropertyStore store);

    [DllImport("propsys.dll")]
    internal static extern int PropVariantToStringAlloc(ref PropVariant pv, out IntPtr text);

    [DllImport("propsys.dll")]
    internal static extern int PropVariantToUInt64(ref PropVariant pv, out ulong value);

    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(ref PropVariant pv);
}
