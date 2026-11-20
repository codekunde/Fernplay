namespace Fernplay.Core;

/// <summary>
/// Which presets the visualizer shows: Fernplay's own (shipped in <c>Assets\Presets</c> next to the exe) or a folder the
/// user picked. Fernplay's own until a folder is picked; the user can go back to them and keep the folder for later.
/// </summary>
public static class VisualizerPresets
{
    /// <summary>Fernplay's own presets, relative to the exe's folder.</summary>
    public static readonly string RelativeFolder = Path.Combine("Assets", "Presets");

    /// <summary>The folder of Fernplay's own presets for an app in <paramref name="appFolder"/>.</summary>
    public static string OwnFolder(string appFolder) => Path.Combine(appFolder, RelativeFolder);

    /// <summary>
    /// The folder to load: the user's folder when one was picked, Fernplay's presets are not chosen instead, and it
    /// still exists; otherwise Fernplay's own (a picked folder that is gone, say an unplugged drive, is kept in the
    /// settings and used again once it is back). <c>Own</c> tells which one it is.
    /// </summary>
    public static (string Folder, bool Own) Choose(string? userFolder, bool useOwn, string ownFolder, Func<string, bool> exists) =>
        !useOwn && !string.IsNullOrWhiteSpace(userFolder) && exists(userFolder) ? (userFolder, false) : (ownFolder, true);
}
