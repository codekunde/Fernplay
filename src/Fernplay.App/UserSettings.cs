using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fernplay.Core;

namespace Fernplay.App;

public sealed record WindowPlace(double Left, double Top);

/// <summary>
/// Everything Fernplay remembers, in %APPDATA%\CodeKunde\Fernplay\settings.json.
/// A missing or damaged file means defaults; it never stops the app from starting.
/// </summary>
public sealed class UserSettings
{
    public double Volume { get; set; } = 0.8;
    /// <summary>-1 (left) to 1 (right); set on a classic skin's balance slider.</summary>
    public double Balance { get; set; }
    /// <summary>File name of the classic skin in <see cref="SkinsFolder"/>; null = Fernplay's own look.</summary>
    public string? Skin { get; set; }
    /// <summary>Classic skin size: 1x, 2x or 3x the skin's pixels.</summary>
    public int SkinSize { get; set; } = 2;
    /// <summary>The player rolled up to its strip (windowshade), in Fernplay's look or a classic skin's.</summary>
    public bool PlayerShaded { get; set; }
    public bool EqEnabled { get; set; } = true;
    public double Preamp { get; set; }
    public double[] Bands { get; set; } = new double[10];
    public bool Shuffle { get; set; }
    public RepeatMode Repeat { get; set; }
    public bool ShowRemaining { get; set; }
    public List<string> Playlist { get; set; } = [];
    /// <summary>Names of the radio stations in <see cref="Playlist"/> (address → name).</summary>
    public Dictionary<string, string> StationNames { get; set; } = [];
    public int CurrentIndex { get; set; } = -1;
    public WindowPlace? Main { get; set; }
    public WindowPlace? Equalizer { get; set; }
    public WindowPlace? PlaylistWindow { get; set; }
    public bool EqualizerVisible { get; set; } = true;
    public bool PlaylistVisible { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool SnapPanels { get; set; } = true;
    public bool RememberPlaylist { get; set; } = true;
    /// <summary>Height of the playlist's list area in skin pixels.</summary>
    public double PlaylistHeight { get; set; } = Fernplay.Core.PlaylistHeight.Default;
    /// <summary>Height of the playlist on a classic skin, in 29 px tiles (at least 2).</summary>
    public int PlaylistTiles { get; set; } = Fernplay.Core.Skins.ClassicPlaylist.DefaultTiles;
    /// <summary>Width of the playlist on a classic skin, in 25 px steps beyond 275.</summary>
    public int PlaylistWidthSteps { get; set; }
    /// <summary>The library window: shown at the last close, its place, its size in skin pixels (0 = default).</summary>
    public bool LibraryVisible { get; set; }
    public WindowPlace? Library { get; set; }
    public double LibraryWidth { get; set; }
    public double LibraryHeight { get; set; }
    /// <summary>False: the library is as tall as the player's stack and follows it; true once resized with its grip.</summary>
    public bool LibraryHeightByHand { get; set; }
    /// <summary>Skin scale the panel positions were saved at; 0 = before this was saved (2.0).</summary>
    public double Scale { get; set; }

    /// <summary>
    /// The FERNPLAY_DATA environment variable points it at another folder (test runs: the owner's settings stay
    /// untouched). Changing APPDATA does not work for this: GetFolderPath asks Windows, not the environment.
    /// </summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("FERNPLAY_DATA") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodeKunde", "Fernplay"),
        "settings.json");

    /// <summary>Classic skins (.wsz) Fernplay can use, next to settings.json.</summary>
    public static string SkinsFolder => Path.Combine(Path.GetDirectoryName(FilePath)!, "Skins");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath), Json) ?? new UserSettings();
                if (s.Bands.Length != 10) s.Bands = new double[10];
                return s;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        return new UserSettings();
    }

    /// <summary>Writes a temp file first, so a crash mid-write never leaves a half file. Failures are ignored.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
