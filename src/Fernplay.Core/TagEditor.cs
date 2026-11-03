using System.Runtime.InteropServices;
using static Fernplay.Core.TagReader;

namespace Fernplay.Core;

/// <summary>The tag fields Fernplay can edit, as text; "" = not set.</summary>
public sealed record TagFields(
    string Title = "", string Artist = "", string Album = "", string AlbumArtist = "", string TrackNumber = "",
    string Year = "", string Genre = "", string Composer = "", string Comment = "");

/// <summary>Writing a file's tags failed; the message is ready to show.</summary>
public sealed class TagEditException(string message, bool inUse = false, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Another program (or Fernplay's own playback) has the file open.</summary>
    public bool InUse { get; } = inUse;
}

/// <summary>
/// Reads and writes tags through the Windows property system, the same way Explorer's Details pane edits them
/// (ID3 in MP3, Vorbis comments in FLAC, MP4 atoms in M4A, WMA attributes). No tag library needed. Ratings are not
/// written: Fernplay keeps them in its library only.
/// </summary>
public static class TagEditor
{
    // Keys from propkey.h (Windows Kits 10.0.19041.0, um\propkey.h).
    private static readonly PropertyKey Comment = new(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 6);  // PKEY_Comment
    private static readonly PropertyKey Composer = new(new Guid("64440492-4C8B-11D1-8B70-080036B11A03"), 19); // PKEY_Music_Composer

    private const int GpsReadWrite = 0x2; // GETPROPERTYSTOREFLAGS.GPS_READWRITE

    /// <summary>The fields, the order they are shown in, and whether Windows keeps several values (Artist; Artist).</summary>
    private static readonly (string Name, PropertyKey Key, bool Multi, Func<TagFields, string> Get)[] Fields =
    [
        ("Title", TagReader.Title, false, f => f.Title),
        ("Artist", TagReader.Artist, true, f => f.Artist),
        ("Album", TagReader.Album, false, f => f.Album),
        ("Album artist", TagReader.AlbumArtist, false, f => f.AlbumArtist),
        ("Track number", TagReader.TrackNumber, false, f => f.TrackNumber),
        ("Year", TagReader.Year, false, f => f.Year),
        ("Genre", TagReader.Genre, true, f => f.Genre),
        ("Composer", Composer, true, f => f.Composer),
        ("Comment", Comment, false, f => f.Comment),
    ];

    /// <summary>The file's editable tags. Throws <see cref="TagEditException"/> when Windows cannot read the file.</summary>
    public static TagFields Read(string path)
    {
        var iid = typeof(IPropertyStore).GUID;
        int hr = SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out var store);
        if (hr < 0) throw Failure(hr, path, reading: true);
        try
        {
            string S(PropertyKey k) => GetString(store, k) ?? "";
            return new TagFields(S(TagReader.Title), S(TagReader.Artist), S(TagReader.Album), S(TagReader.AlbumArtist),
                S(TagReader.TrackNumber), S(TagReader.Year), S(TagReader.Genre), S(Composer), S(Comment));
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    /// <summary>What is wrong with the fields before saving, or null. Track number and year are whole numbers.</summary>
    public static string? Validate(TagFields f)
    {
        if (f.TrackNumber.Trim() is { Length: > 0 } n && !(uint.TryParse(n, out var t) && t is >= 1 and <= 999))
            return "Track number: a whole number from 1 to 999, or empty.";
        if (f.Year.Trim() is { Length: > 0 } y && !(uint.TryParse(y, out var v) && v is >= 1 and <= 9999))
            return "Year: four digits, such as 1987, or empty.";
        return null;
    }

    /// <summary>Names of the fields that differ (spaces at the ends ignored).</summary>
    public static IReadOnlyList<string> Changed(TagFields before, TagFields after) =>
        [.. Fields.Where(x => x.Get(before).Trim() != x.Get(after).Trim()).Select(x => x.Name)];

    /// <summary>
    /// Writes the fields that changed from <paramref name="before"/> to <paramref name="after"/> (others stay exactly as
    /// they are in the file); an emptied field is removed. Throws <see cref="TagEditException"/>.
    /// </summary>
    public static void Write(string path, TagFields before, TagFields after)
    {
        if (Validate(after) is { } problem) throw new TagEditException(problem);
        var changed = Fields.Where(x => x.Get(before).Trim() != x.Get(after).Trim()).ToList();
        if (changed.Count == 0) return;

        var iid = typeof(IPropertyStore).GUID;
        int hr = SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, GpsReadWrite, ref iid, out var store);
        if (hr < 0) throw Failure(hr, path, reading: false);
        try
        {
            foreach (var (name, key, multi, get) in changed)
            {
                string text = get(after).Trim();
                var k = key;
                var pv = default(PropVariant); // VT_EMPTY removes the field
                if (text.Length > 0)
                {
                    // "A; B" becomes two artists, as Explorer stores them; numbers become the property's own type.
                    hr = multi ? InitPropVariantFromStringAsVector(text, out pv) : 0;
                    if (!multi) pv = PropVariant.FromString(text);
                    if (hr >= 0) hr = PSCoerceToCanonicalValue(ref k, ref pv);
                    if (hr < 0) { PropVariantClear(ref pv); throw new TagEditException($"{name}: Windows can't store \"{text}\" there."); }
                }
                hr = store.SetValue(ref k, ref pv);
                PropVariantClear(ref pv);
                if (hr < 0) throw Failure(hr, path, reading: false, field: name);
            }
            hr = store.Commit();
            if (hr < 0) throw Failure(hr, path, reading: false);
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    private static TagEditException Failure(int hr, string path, bool reading, string? field = null)
    {
        string file = Path.GetFileName(path);
        string ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var inner = Marshal.GetExceptionForHR(hr);
        return (uint)hr switch
        {
            0x80070020 or 0x80030020 or 0x80070021 or 0x80030021 => // sharing or lock violation
                new TagEditException($"{file} is open in another program, so its tags can't be saved now. Close it there and try again.", inUse: true, inner),
            // Access denied: also what a read-only property handler (WAV) answers, so check the file itself.
            0x80070005 or 0x80030005 when !reading && CanOpenForWriting(path) =>
                new TagEditException($"Windows can't write tags to {ext} files. Nothing was saved.", inner: inner),
            0x80070005 or 0x80030005 =>
                new TagEditException($"No permission to change {file}: it may be read-only or in a protected folder.", inner: inner),
            0x80070002 or 0x80070003 or 0x80030002 or 0x80030003 => // not found
                new TagEditException($"{file} is no longer there.", inner: inner),
            0x80070070 => new TagEditException($"The disk is full: {file} was not changed.", inner: inner),
            _ when reading => new TagEditException($"Windows can't read the tags of {file} (error 0x{hr:X8}).", inner: inner),
            _ when field is not null => new TagEditException($"Windows can't write {field.ToLowerInvariant()} to {ext} files (error 0x{hr:X8}). Nothing was saved.", inner: inner),
            _ => new TagEditException($"Windows can't write tags to {ext} files like {file} (error 0x{hr:X8}). Nothing was saved.", inner: inner),
        };
    }

    private static bool CanOpenForWriting(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    [DllImport("propsys.dll", CharSet = CharSet.Unicode)]
    private static extern int InitPropVariantFromStringAsVector(string text, out PropVariant pv);

    [DllImport("propsys.dll")]
    private static extern int PSCoerceToCanonicalValue(ref PropertyKey key, ref PropVariant pv);
}
