namespace Fernplay.Core;

/// <summary>
/// Where Fernplay keeps settings, the library, skins and the diagnostics log.
/// FERNPLAY_DATA wins (test runs); else a <see cref="PortableMarker"/> file next to the exe keeps everything in
/// <c>Data</c> beside it (the portable zip ships with one); else %APPDATA%\CodeKunde\Fernplay (the MSI and older zips).
/// </summary>
public static class DataFolder
{
    public const string PortableMarker = "portable.txt";
    public const string PortableFolderName = "Data";

    public static string Resolve(string? fernplayData, string exeFolder, string appData) =>
        fernplayData is { Length: > 0 } dir ? dir
        : IsPortable(exeFolder) ? Path.Combine(exeFolder, PortableFolderName)
        : Path.Combine(appData, "CodeKunde", "Fernplay");

    public static bool IsPortable(string exeFolder) => File.Exists(Path.Combine(exeFolder, PortableMarker));

    /// <summary>
    /// First start of a portable copy: when its data folder does not exist yet, copy the settings, library and
    /// skins from the installed (%APPDATA%) folder, so a move to the portable zip keeps the user's setup.
    /// Logs are not copied. Returns true when something was copied. Never throws for file errors.
    /// </summary>
    public static bool SeedPortable(string appDataFolder, string portableFolder)
    {
        if (Directory.Exists(portableFolder) || !Directory.Exists(appDataFolder)) return false;
        try
        {
            bool copied = false;
            Directory.CreateDirectory(portableFolder);
            foreach (var name in new[] { "settings.json", "library.json" })
            {
                var src = Path.Combine(appDataFolder, name);
                if (!File.Exists(src)) continue;
                File.Copy(src, Path.Combine(portableFolder, name));
                copied = true;
            }
            var skins = Path.Combine(appDataFolder, "Skins");
            if (Directory.Exists(skins))
            {
                var target = Directory.CreateDirectory(Path.Combine(portableFolder, "Skins")).FullName;
                foreach (var file in Directory.EnumerateFiles(skins))
                {
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
                    copied = true;
                }
            }
            return copied;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
