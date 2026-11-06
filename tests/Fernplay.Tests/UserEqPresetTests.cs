using System.Text.Json;
using Fernplay.Core.Audio;

namespace Fernplay.Tests;

public sealed class UserEqPresetTests
{
    private static EqPreset P(string name, double first = 0) => new(name, -1, [first, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

    [Fact]
    public void From_current_takes_the_eq_as_it_is_and_trims_the_name()
    {
        var eq = new Equalizer { Preamp = -3 };
        eq.SetBand(0, 6);
        eq.SetBand(9, -4.5);
        var p = UserEqPresets.FromCurrent("  Car  ", eq);
        Assert.Equal("Car", p.Name);
        Assert.Equal(-3, p.Preamp);
        Assert.Equal(6, p.Bands[0]);
        Assert.Equal(-4.5, p.Bands[9]);
        eq.SetBand(0, 0);
        Assert.Equal(6, p.Bands[0]); // a copy, not the live bands
    }

    [Theory]
    [InlineData("", "Type a name")]
    [InlineData("   ", "Type a name")]
    [InlineData("rock", "built-in")]
    [InlineData("Bass Boost", "built-in")]
    [InlineData("1234567890123456789012345678901", "30 characters")]
    public void Names_that_cannot_be_used_say_why(string name, string because) =>
        Assert.Contains(because, UserEqPresets.NameProblem(name));

    [Fact]
    public void A_name_already_saved_is_fine_it_replaces() =>
        Assert.Null(UserEqPresets.NameProblem("Car"));

    [Fact]
    public void Save_adds_and_replaces_the_same_name_in_its_place()
    {
        var list = new List<EqPreset> { P("Car"), P("Night") };
        Assert.False(UserEqPresets.Save(list, P("Gym")));
        Assert.True(UserEqPresets.Save(list, P("car", 5)));
        Assert.Equal(["car", "Night", "Gym"], list.Select(p => p.Name));
        Assert.Equal(5, list[0].Bands[0]);
    }

    [Fact]
    public void Delete_removes_by_name_any_case()
    {
        var list = new List<EqPreset> { P("Car"), P("Night") };
        Assert.True(UserEqPresets.Delete(list, "CAR"));
        Assert.False(UserEqPresets.Delete(list, "Car"));
        Assert.Equal(["Night"], list.Select(p => p.Name));
    }

    [Fact]
    public void Clean_drops_broken_entries_from_a_settings_file()
    {
        var list = UserEqPresets.Clean([P("Car"), new EqPreset("", 0, new double[10]), new EqPreset("Short", 0, [1, 2]), P("car"), null!]);
        Assert.Equal(["Car"], list.Select(p => p.Name));
        Assert.Empty(UserEqPresets.Clean(null));
    }

    [Fact]
    public void Presets_survive_the_settings_file()
    {
        var json = JsonSerializer.Serialize(new List<EqPreset> { P("Car", 4.5) });
        var back = JsonSerializer.Deserialize<List<EqPreset>>(json)!;
        Assert.Equal("Car", back[0].Name);
        Assert.Equal(-1, back[0].Preamp);
        Assert.Equal(4.5, back[0].Bands[0]);
        Assert.Equal(10, back[0].Bands.Length);
    }
}
