using System.IO.Compression;
using System.Text;

namespace Fernplay.Core.Skins;

/// <summary>A skin file that cannot be used; the message says why in plain words.</summary>
public sealed class SkinLoadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A classic (Winamp 2.x style) skin read from a .wsz/.zip file: sprite sheets, playlist colours, spectrum colours
/// and window outlines. Everything is read into memory; nothing is extracted to disk. A skin needs MAIN.BMP; every
/// other file is optional, and a sprite that is missing or outside its sheet is simply not drawn.
/// </summary>
public sealed class ClassicSkin
{
    // Limits for a file nobody checked: classic skins are well under 1 MB.
    public const long MaxFileBytes = 16L << 20;
    public const int MaxEntries = 512;
    public const long MaxEntryBytes = 8L << 20;
    public const long MaxTotalBytes = 32L << 20;

    /// <summary>Sheets Fernplay draws from (file name without ".bmp").</summary>
    public static readonly string[] SheetNames =
        ["MAIN", "TITLEBAR", "CBUTTONS", "POSBAR", "VOLUME", "BALANCE", "SHUFREP", "NUMBERS", "NUMS_EX", "TEXT", "MONOSTER", "PLAYPAUS", "EQMAIN", "PLEDIT",
         "GEN", "GENEX"]; // GEN/GENEX: the library window (see GenSkin)

    public string Name { get; }
    public IReadOnlyDictionary<string, SkinImage> Sheets { get; }
    public PlaylistStyle Playlist { get; }
    /// <summary>24 colours, see <see cref="VisColors"/>.</summary>
    public IReadOnlyList<uint> VisColors { get; }
    public SkinRegions Regions { get; }
    /// <summary>Files that were missing or unreadable; the skin still works without them.</summary>
    public IReadOnlyList<string> Warnings { get; }
    /// <summary>"NUMS_EX" when the skin has the extended digits (preferred, as the classic player does), else "NUMBERS".</summary>
    public string DigitSheet => Sheets.ContainsKey("NUMS_EX") ? "NUMS_EX" : "NUMBERS";

    private ClassicSkin(string name, Dictionary<string, SkinImage> sheets, PlaylistStyle playlist, IReadOnlyList<uint> vis, SkinRegions regions, List<string> warnings)
    {
        Name = name; Sheets = sheets; Playlist = playlist; VisColors = vis; Regions = regions; Warnings = warnings;
    }

    /// <summary>The pixels of one sprite, or null when the skin does not have it.</summary>
    public SkinImage? GetSprite(Sprite s) => Sheets.TryGetValue(s.Sheet, out var sheet) ? sheet.Crop(s.X, s.Y, s.W, s.H) : null;

    public SkinImage? GetSprite(string name) => GetSprite(SpriteMap.Get(name));

    public bool Has(Sprite s) => Sheets.TryGetValue(s.Sheet, out var sheet) && sheet.Contains(s.X, s.Y, s.W, s.H);

    /// <summary>A clock digit 0-9 from the skin's digit sheet.</summary>
    public Sprite Digit(int d) => SpriteMap.Get($"{(DigitSheet == "NUMS_EX" ? "NumsEx" : "Numbers")}.{Math.Clamp(d, 0, 9)}");

    public static ClassicSkin Load(string path)
    {
        FileInfo info;
        try { info = new FileInfo(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { throw new SkinLoadException("The skin path is not valid.", ex); }
        if (!info.Exists) throw new SkinLoadException($"The skin file was not found: {path}");
        if (info.Length > MaxFileBytes) throw new SkinLoadException($"The skin file is too big ({info.Length / (1 << 20)} MB); classic skins are well under 1 MB.");
        try
        {
            using var fs = info.OpenRead();
            return Load(fs, Path.GetFileNameWithoutExtension(path));
        }
        catch (IOException ex) { throw new SkinLoadException($"The skin file could not be read: {ex.Message}", ex); }
        catch (UnauthorizedAccessException ex) { throw new SkinLoadException("Windows did not allow reading the skin file.", ex); }
    }

    public static ClassicSkin Load(Stream stream, string name)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException ex) { throw new SkinLoadException("This is not a skin: the file is not a zip archive (.wsz).", ex); }
        using (zip)
        {
            if (zip.Entries.Count > MaxEntries) throw new SkinLoadException($"The skin has too many files ({zip.Entries.Count}).");
            var files = Index(zip);
            if (files.ContainsKey("SKIN.XML") && !files.ContainsKey("MAIN.BMP"))
                throw new SkinLoadException("This is a modern skin (.wal); Fernplay reads classic skins (.wsz) only.");
            if (!files.ContainsKey("MAIN.BMP"))
                throw new SkinLoadException("This is not a classic skin: MAIN.BMP is missing.");

            long total = 0;
            byte[]? Read(string file)
            {
                if (!files.TryGetValue(file, out var e)) return null;
                if (e.Length > MaxEntryBytes) throw new SkinLoadException($"{file} in the skin is too big.");
                using var s = e.Open();
                using var ms = new MemoryStream();
                var buf = new byte[81920];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) // the declared size can lie: count what really comes out
                {
                    ms.Write(buf, 0, n);
                    total += n;
                    if (ms.Length > MaxEntryBytes || total > MaxTotalBytes) throw new SkinLoadException("The skin unpacks to far more data than a skin needs.");
                }
                return ms.ToArray();
            }
            string? Text(string file) => Read(file) is { } b ? DecodeText(b) : null;

            var warnings = new List<string>();
            var sheets = new Dictionary<string, SkinImage>();
            foreach (var sheet in SheetNames)
            {
                byte[]? data;
                try { data = Read(sheet + ".BMP"); }
                catch (InvalidDataException ex) { warnings.Add($"{sheet}.BMP is damaged: {ex.Message}"); continue; }
                if (data is null)
                {
                    if (sheet is not ("NUMBERS" or "NUMS_EX")) warnings.Add($"{sheet}.BMP is missing.");
                    continue;
                }
                try { sheets[sheet] = BmpDecoder.Decode(data); }
                catch (InvalidDataException ex)
                {
                    if (sheet == "MAIN") throw new SkinLoadException($"MAIN.BMP cannot be read: {ex.Message}", ex);
                    warnings.Add($"{sheet}.BMP cannot be read: {ex.Message}");
                }
            }
            if (!sheets.ContainsKey("NUMBERS") && !sheets.ContainsKey("NUMS_EX")) warnings.Add("NUMBERS.BMP is missing.");
            // The classic player draws the balance slider from VOLUME.BMP when a skin has no BALANCE.BMP.
            if (!sheets.ContainsKey("BALANCE") && sheets.TryGetValue("VOLUME", out var volume))
            {
                sheets["BALANCE"] = volume;
                warnings.Remove("BALANCE.BMP is missing.");
            }

            var regions = SkinRegions.Parse(Text("REGION.TXT"));
            warnings.AddRange(regions.Warnings);
            return new ClassicSkin(name, sheets, PlaylistStyle.Parse(Text("PLEDIT.TXT")), Skins.VisColors.Parse(Text("VISCOLOR.TXT")), regions, warnings);
        }
    }

    /// <summary>
    /// File name (upper case) → entry, for files at the top or inside one top folder (some skins are zipped with
    /// their folder). Top-level files win; Mac resource folders are skipped.
    /// </summary>
    private static Dictionary<string, ZipArchiveEntry> Index(ZipArchive zip)
    {
        var top = new Dictionary<string, ZipArchiveEntry>();
        var nested = new Dictionary<string, ZipArchiveEntry>();
        foreach (var e in zip.Entries)
        {
            if (e.Name.Length == 0) continue; // folder
            var parts = e.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 2 || parts[0].StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase)) continue;
            var key = parts[^1].ToUpperInvariant();
            (parts.Length == 1 ? top : nested).TryAdd(key, e);
        }
        foreach (var (k, e) in nested) top.TryAdd(k, e);
        return top;
    }

    /// <summary>Skin text files are ANSI from the 90s or UTF-8; a UTF-8 BOM or valid UTF-8 wins, else Latin-1.</summary>
    private static string DecodeText(byte[] b)
    {
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(b).TrimStart('﻿'); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(b); }
    }
}
