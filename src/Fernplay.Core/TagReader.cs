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

    private static readonly PropertyKey Title = new(SummaryInfo, 2);
    private static readonly PropertyKey Artist = new(Music, 2);
    private static readonly PropertyKey Album = new(Music, 4);
    private static readonly PropertyKey Duration = new(Media, 3);        // 100 ns units
    private static readonly PropertyKey EncodingBitrate = new(Media, 4); // bits per second
    private static readonly PropertyKey TrackNumber = new(Music, 7);     // System.Music.TrackNumber

    public static Track Read(string path)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out var store));
        try
        {
            ulong ticks = GetUInt64(store, Duration);
            uint bps = (uint)GetUInt64(store, EncodingBitrate);
            ulong number = GetUInt64(store, TrackNumber);
            return new Track(
                path,
                GetString(store, Title),
                GetString(store, Artist),
                GetString(store, Album),
                ticks > 0 ? TimeSpan.FromTicks((long)ticks) : null,
                bps > 0 ? (int)Math.Round(bps / 1000.0) : null,
                number is > 0 and < 10000 ? (int)number : null);
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    private static string? GetString(IPropertyStore store, PropertyKey key)
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
    private readonly struct PropertyKey(Guid fmtid, uint pid)
    {
        public readonly Guid FormatId = fmtid;
        public readonly uint PropertyId = pid;
    }

    // PROPVARIANT: 16 bytes on x86, 24 on x64; only vt is read directly.
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        private ushort r1, r2, r3;
        private IntPtr p1, p2;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, int flags, ref Guid iid, out IPropertyStore store);

    [DllImport("propsys.dll")]
    private static extern int PropVariantToStringAlloc(ref PropVariant pv, out IntPtr text);

    [DllImport("propsys.dll")]
    private static extern int PropVariantToUInt64(ref PropVariant pv, out ulong value);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pv);
}
