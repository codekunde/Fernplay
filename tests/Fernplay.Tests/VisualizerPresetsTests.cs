using System.Text.RegularExpressions;
using Fernplay.Core;

namespace Fernplay.Tests;

public sealed class VisualizerPresetsTests
{
    private const string Own = @"C:\Fernplay\Assets\Presets", Mine = @"D:\Milk\presets";

    [Fact]
    public void Without_a_picked_folder_Fernplays_own_presets_show() =>
        Assert.Equal((Own, true), VisualizerPresets.Choose(null, false, Own, _ => true));

    [Fact]
    public void A_picked_folder_shows_while_it_exists() =>
        Assert.Equal((Mine, false), VisualizerPresets.Choose(Mine, false, Own, _ => true));

    [Fact]
    public void Choosing_Fernplay_presets_keeps_the_picked_folder_aside() =>
        Assert.Equal((Own, true), VisualizerPresets.Choose(Mine, true, Own, _ => true));

    [Fact]
    public void A_picked_folder_that_is_gone_falls_back_to_Fernplays_own() =>
        Assert.Equal((Own, true), VisualizerPresets.Choose(Mine, false, Own, p => p != Mine));

    [Fact]
    public void Own_folder_is_Assets_Presets_next_to_the_exe() =>
        Assert.Equal(Path.Combine(@"C:\Apps\Fernplay", "Assets", "Presets"), VisualizerPresets.OwnFolder(@"C:\Apps\Fernplay"));

    // ================= The shipped files =================

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Fernplay.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Fernplay.slnx not found above the test folder");
    }

    private static string[] PresetFiles() =>
        Directory.GetFiles(Path.Combine(RepoRoot(), "src", "Fernplay.App", "Assets", "Presets"), "*.milk").Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void Eleven_presets_named_Fernplay_dash_name()
    {
        var names = PresetFiles().Select(Path.GetFileName).ToArray();
        Assert.Equal(11, names.Length);
        Assert.All(names, n => Assert.Matches(@"^Fernplay - [A-Z][A-Za-z ]+\.milk$", n));
    }

    /// <summary>
    /// Plain MilkDrop 2 text that projectM 4.1.8 loads: ASCII key=value lines and one [preset00] section, declared as
    /// MilkDrop 2 with shader model 2 but without shader code. As a MilkDrop 1 preset (no version) projectM draws through
    /// its old video-echo path, which tints the corners with a random drifting hue and turns the picture upside down;
    /// preset shader code fell back silently to the default in testing, so there is none.
    /// </summary>
    [Fact]
    public void Each_preset_is_milkdrop_2_text_without_shader_code()
    {
        foreach (var file in PresetFiles())
        {
            var lines = File.ReadAllLines(file);
            Assert.Equal(["MILKDROP_PRESET_VERSION=201", "PSVERSION=2", "PSVERSION_WARP=2", "PSVERSION_COMP=2", "[preset00]"], lines[..5]);
            Assert.All(File.ReadAllBytes(file), b => Assert.True(b < 128, $"{Path.GetFileName(file)}: not ASCII"));
            foreach (var line in lines.Skip(5).Where(l => l.Length > 0))
            {
                Assert.Matches(@"^[A-Za-z_0-9]+=", line);
                Assert.DoesNotMatch(@"^(warp|comp)_\d+=", line);
            }
            Assert.Contains(lines, l => l.StartsWith("per_frame_1=", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_installer_lists_every_preset_and_no_other()
    {
        var wxs = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "Package.wxs"));
        var listed = Regex.Matches(wxs, @"Source=""\$\(PublishDir\)Assets\\Presets\\([^""]+)""").Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal);
        Assert.Equal(PresetFiles().Select(Path.GetFileName), listed);
    }
}
