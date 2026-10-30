using Microsoft.Win32;

namespace Fernplay.Core;

/// <summary>
/// Registers Fernplay with Windows for the current user only (HKCU, no admin): "Open with" and Default apps
/// list it for music files and playlists, its file icon shows where it is the default, and Explorer's
/// right-click menu gets "Add to Fernplay playlist" (files, folders) and "Play in Fernplay" (folders).
/// Windows does not let an app make itself the default; the user picks Fernplay once in Windows' own dialog.
/// All keys and values written here are Fernplay's own and are removed again by <see cref="Unregister"/>.
/// </summary>
public sealed class ShellRegistration(RegistryKey root, string exePath, string fileIconPath)
{
    public const string AppName = "Fernplay";
    public const string AudioProgId = "Fernplay.Audio";
    public const string PlaylistProgId = "Fernplay.Playlist";
    private const string Classes = @"Software\Classes";
    private const string CapabilitiesPath = @"Software\CodeKunde\Fernplay\Capabilities";
    private const string RegisteredApps = @"Software\RegisteredApplications";
    private const string ExeName = "Fernplay.exe";
    private const string EnqueueVerb = "Fernplay.Enqueue";
    private const string PlayVerb = "Fernplay.Play";

    public static readonly IReadOnlyList<string> AudioExtensions = [.. Playlist.SupportedExtensions.Order()];
    public static readonly IReadOnlyList<string> PlaylistExtensions = [".m3u", ".m3u8"];

    /// <summary>The current user's registry; use another key for tests.</summary>
    public static ShellRegistration ForCurrentUser(string exePath, string fileIconPath) => new(Registry.CurrentUser, exePath, fileIconPath);

    private string OpenCommand => $"\"{exePath}\" \"%1\"";
    private string AddCommand => $"\"{exePath}\" /add \"%1\"";
    private string AppIcon => $"\"{exePath}\",0";

    public bool IsRegistered()
    {
        using var k = root.OpenSubKey($@"{Classes}\{AudioProgId}\shell\open\command");
        return k is not null;
    }

    /// <summary>True when registered for another exe path (the app was moved): call <see cref="Register"/> again.</summary>
    public bool NeedsRefresh()
    {
        using var k = root.OpenSubKey($@"{Classes}\{AudioProgId}\shell\open\command");
        return k is not null && !string.Equals(k.GetValue(null) as string, OpenCommand, StringComparison.OrdinalIgnoreCase);
    }

    public void Register()
    {
        WriteProgId(AudioProgId, "Music file");
        WriteProgId(PlaylistProgId, "Playlist");

        foreach (var ext in AudioExtensions) WriteExtension(ext, AudioProgId);
        foreach (var ext in PlaylistExtensions) WriteExtension(ext, PlaylistProgId);

        // Applications\Fernplay.exe: the "Open with" list entry.
        using (var app = root.CreateSubKey($@"{Classes}\Applications\{ExeName}"))
        {
            app.SetValue("FriendlyAppName", AppName);
            using (var icon = app.CreateSubKey("DefaultIcon")) icon.SetValue(null, AppIcon);
            using (var cmd = app.CreateSubKey(@"shell\open\command")) cmd.SetValue(null, OpenCommand);
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var ext in AudioExtensions.Concat(PlaylistExtensions)) types.SetValue(ext, "");
        }

        // Folders: play or add a whole folder.
        WriteVerb($@"{Classes}\Directory\shell\{PlayVerb}", "Play in Fernplay", OpenCommand);
        WriteVerb($@"{Classes}\Directory\shell\{EnqueueVerb}", "Add to Fernplay playlist", AddCommand);

        // Default apps (Settings): capabilities plus the RegisteredApplications pointer.
        using (var cap = root.CreateSubKey(CapabilitiesPath))
        {
            cap.SetValue("ApplicationName", AppName);
            cap.SetValue("ApplicationDescription", "A small music player in the classic style: player, equalizer and playlist.");
            cap.SetValue("ApplicationIcon", AppIcon);
            using var assoc = cap.CreateSubKey("FileAssociations");
            foreach (var ext in AudioExtensions) assoc.SetValue(ext, AudioProgId);
            foreach (var ext in PlaylistExtensions) assoc.SetValue(ext, PlaylistProgId);
        }
        using (var reg = root.CreateSubKey(RegisteredApps)) reg.SetValue(AppName, CapabilitiesPath);
    }

    public void Unregister()
    {
        DeleteTree($@"{Classes}\{AudioProgId}");
        DeleteTree($@"{Classes}\{PlaylistProgId}");
        foreach (var ext in AudioExtensions.Concat(PlaylistExtensions))
        {
            using (var owp = root.OpenSubKey($@"{Classes}\{ext}\OpenWithProgids", writable: true))
            {
                owp?.DeleteValue(AudioProgId, throwOnMissingValue: false);
                owp?.DeleteValue(PlaylistProgId, throwOnMissingValue: false);
            }
            DeleteTree($@"{Classes}\SystemFileAssociations\{ext}\shell\{EnqueueVerb}");
        }
        DeleteTree($@"{Classes}\Applications\{ExeName}");
        DeleteTree($@"{Classes}\Directory\shell\{PlayVerb}");
        DeleteTree($@"{Classes}\Directory\shell\{EnqueueVerb}");
        DeleteTree(@"Software\CodeKunde\Fernplay\Capabilities");
        using var reg = root.OpenSubKey(RegisteredApps, writable: true);
        reg?.DeleteValue(AppName, throwOnMissingValue: false);
    }

    private void WriteProgId(string progId, string typeName)
    {
        using var k = root.CreateSubKey($@"{Classes}\{progId}");
        k.SetValue(null, $"{typeName} (Fernplay)");
        k.SetValue("FriendlyTypeName", $"{typeName} (Fernplay)");
        using (var icon = k.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{fileIconPath}\"");
        using (var shell = k.CreateSubKey("shell")) shell.SetValue(null, "open");
        WriteVerb($@"{Classes}\{progId}\shell\open", "&Play in Fernplay", OpenCommand);
        WriteVerb($@"{Classes}\{progId}\shell\enqueue", "&Add to Fernplay playlist", AddCommand);
    }

    private void WriteExtension(string ext, string progId)
    {
        using (var owp = root.CreateSubKey($@"{Classes}\{ext}\OpenWithProgids"))
            owp.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
        // "Add to Fernplay playlist" on every music file, whichever app is its default.
        WriteVerb($@"{Classes}\SystemFileAssociations\{ext}\shell\{EnqueueVerb}", "Add to Fernplay playlist", AddCommand);
    }

    /// <summary>A verb: menu text, Fernplay's icon, all selected items handed over at once ("Player").</summary>
    private void WriteVerb(string path, string text, string command)
    {
        using var verb = root.CreateSubKey(path);
        verb.SetValue(null, text);
        verb.SetValue("Icon", AppIcon);
        verb.SetValue("MultiSelectModel", "Player");
        using var cmd = verb.CreateSubKey("command");
        cmd.SetValue(null, command);
    }

    private void DeleteTree(string path) => root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
}
