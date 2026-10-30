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
    public bool EqEnabled { get; set; } = true;
    public double Preamp { get; set; }
    public double[] Bands { get; set; } = new double[10];
    public bool Shuffle { get; set; }
    public RepeatMode Repeat { get; set; }
    public bool ShowRemaining { get; set; }
    public List<string> Playlist { get; set; } = [];
    public int CurrentIndex { get; set; } = -1;
    public WindowPlace? Main { get; set; }
    public WindowPlace? Equalizer { get; set; }
    public WindowPlace? PlaylistWindow { get; set; }
    public bool EqualizerVisible { get; set; } = true;
    public bool PlaylistVisible { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool SnapPanels { get; set; } = true;
    public bool RememberPlaylist { get; set; } = true;
    /// <summary>Skin scale the panel positions were saved at; 0 = before this was saved (2.0).</summary>
    public double Scale { get; set; }

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodeKunde", "Fernplay", "settings.json");

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
