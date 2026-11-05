using Fernplay.Core;

namespace Fernplay.Tests;

/// <summary>Where settings live: FERNPLAY_DATA, a portable copy's Data folder, or %APPDATA%. Runs in a temp folder.</summary>
public sealed class DataFolderTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("fernplay-datafolder-").FullName;
    private string Exe => Directory.CreateDirectory(Path.Combine(_temp, "app")).FullName;
    private string AppData => Path.Combine(_temp, "roaming");

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public void Installed_copy_uses_appdata()
    {
        Assert.False(DataFolder.IsPortable(Exe));
        Assert.Equal(Path.Combine(AppData, "CodeKunde", "Fernplay"), DataFolder.Resolve(null, Exe, AppData));
        Assert.Equal(Path.Combine(AppData, "CodeKunde", "Fernplay"), DataFolder.Resolve("", Exe, AppData));
    }

    [Fact]
    public void Portable_marker_keeps_data_next_to_the_exe()
    {
        File.WriteAllText(Path.Combine(Exe, DataFolder.PortableMarker), "");
        Assert.True(DataFolder.IsPortable(Exe));
        Assert.Equal(Path.Combine(Exe, "Data"), DataFolder.Resolve(null, Exe, AppData));
    }

    [Fact]
    public void Fernplay_data_wins_over_the_marker()
    {
        File.WriteAllText(Path.Combine(Exe, DataFolder.PortableMarker), "");
        Assert.Equal(@"X:\Test", DataFolder.Resolve(@"X:\Test", Exe, AppData));
    }

    [Fact]
    public void First_portable_start_copies_settings_library_and_skins_but_not_logs()
    {
        var installed = Directory.CreateDirectory(Path.Combine(AppData, "CodeKunde", "Fernplay")).FullName;
        File.WriteAllText(Path.Combine(installed, "settings.json"), "{\"Volume\":0.4}");
        File.WriteAllText(Path.Combine(installed, "library.json"), "[]");
        File.WriteAllText(Path.Combine(installed, "diagnostics.log"), "log");
        Directory.CreateDirectory(Path.Combine(installed, "Skins"));
        File.WriteAllText(Path.Combine(installed, "Skins", "Base.wsz"), "zip");
        var portable = Path.Combine(Exe, "Data");

        Assert.True(DataFolder.SeedPortable(installed, portable));
        Assert.Equal("{\"Volume\":0.4}", File.ReadAllText(Path.Combine(portable, "settings.json")));
        Assert.True(File.Exists(Path.Combine(portable, "library.json")));
        Assert.True(File.Exists(Path.Combine(portable, "Skins", "Base.wsz")));
        Assert.False(File.Exists(Path.Combine(portable, "diagnostics.log")));

        // Later starts never copy again, even when the installed settings change.
        File.WriteAllText(Path.Combine(installed, "settings.json"), "{\"Volume\":0.9}");
        Assert.False(DataFolder.SeedPortable(installed, portable));
        Assert.Equal("{\"Volume\":0.4}", File.ReadAllText(Path.Combine(portable, "settings.json")));
    }

    [Fact]
    public void Nothing_installed_means_nothing_to_copy()
    {
        var portable = Path.Combine(Exe, "Data");
        Assert.False(DataFolder.SeedPortable(Path.Combine(AppData, "CodeKunde", "Fernplay"), portable));
        Assert.False(Directory.Exists(portable)); // the app creates it on its first save
    }
}
