using System.Text.Json;
using Fernplay.Core;
using Fernplay.Core.Audio;

namespace Fernplay.Tests;

/// <summary>The output device choice: the Settings list, which device plays, and switching while a track plays.</summary>
public sealed class OutputDeviceTests : IDisposable
{
    private const string Speakers = "{0.0.0.00000000}.{11111111-aaaa-4bbb-8ccc-000000000001}";
    private const string Headset = "{0.0.0.00000000}.{11111111-aaaa-4bbb-8ccc-000000000002}";
    private const string Hdmi = "{0.0.0.00000000}.{11111111-aaaa-4bbb-8ccc-000000000003}";

    private static readonly AudioDevice[] Devices =
    [
        new(Speakers, "Speakers (Realtek(R) Audio)", true),
        new(Headset, "Headset (USB Audio)", true),
        new(Hdmi, "LG TV (NVIDIA High Definition Audio)", false),
    ];

    /// <summary>A device list from a test, standing in for Windows.</summary>
    private sealed class FakeDevices(params AudioDevice[] devices) : IAudioDeviceSource
    {
        public IReadOnlyList<AudioDevice> List() => devices;
    }

    // ---------------- Choosing and the list ----------------

    [Fact]
    public void A_connected_saved_device_is_used()
    {
        Assert.Equal(Headset, OutputDevices.Resolve(Headset, Devices));
        Assert.NotNull(OutputDevices.Resolve(Headset.ToUpperInvariant(), Devices)); // Windows' IDs ignore case
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{0.0.0.00000000}.{not-a-device}")]
    [InlineData(Hdmi)] // known to Windows but unplugged
    public void Missing_empty_or_unplugged_means_the_Windows_default(string? saved) =>
        Assert.Null(OutputDevices.Resolve(saved, Devices));

    [Fact]
    public void List_has_the_default_first_then_connected_devices_by_name()
    {
        var entries = OutputDevices.Entries(null, new FakeDevices(Devices).List());
        Assert.Equal([OutputDevices.DefaultName, "Headset (USB Audio)", "Speakers (Realtek(R) Audio)"], entries.Select(e => e.Name));
        Assert.Null(entries[0].Id);
        Assert.Equal([Headset, Speakers], entries.Skip(1).Select(e => e.Id));
        Assert.Equal(0, OutputDevices.IndexOf(entries, null));
        Assert.Equal("Headset (USB Audio)", entries[1].ToString()); // what screen readers read
    }

    [Fact]
    public void An_unplugged_saved_device_stays_in_the_list_marked_not_connected()
    {
        var entries = OutputDevices.Entries(Hdmi, Devices);
        Assert.Equal(4, entries.Count);
        Assert.Equal(new OutputDevices.Entry(Hdmi, "LG TV (NVIDIA High Definition Audio) (not connected)"), entries[^1]);
        Assert.Equal(3, OutputDevices.IndexOf(entries, Hdmi));
    }

    [Fact]
    public void A_saved_device_Windows_no_longer_knows_shows_as_saved_device()
    {
        var entries = OutputDevices.Entries("{gone}", Devices);
        Assert.Equal("Saved device (not connected)", entries[^1].Name);
        Assert.Equal(entries.Count - 1, OutputDevices.IndexOf(entries, "{gone}"));
    }

    [Fact]
    public void A_connected_saved_device_is_selected_and_not_added_twice()
    {
        var entries = OutputDevices.Entries(Speakers, Devices);
        Assert.Equal(3, entries.Count);
        Assert.Equal(2, OutputDevices.IndexOf(entries, Speakers));
    }

    [Fact]
    public void No_devices_at_all_still_offers_the_default()
    {
        var entries = OutputDevices.Entries(null, new FakeDevices().List());
        Assert.Equal([OutputDevices.DefaultName], entries.Select(e => e.Name));
    }

    [Fact]
    public void A_device_id_survives_a_settings_round_trip()
    {
        // settings.json is written by System.Text.Json; endpoint IDs carry braces and dots.
        var json = JsonSerializer.Serialize(new { OutputDevice = Headset });
        var back = JsonSerializer.Deserialize<JsonElement>(json).GetProperty("OutputDevice").GetString();
        Assert.Equal(Headset, back);
        Assert.Equal(Headset, OutputDevices.Resolve(back, Devices));
    }

    // ---------------- Switching in the engine ----------------

    private readonly TestAudio _audio = new();
    private readonly List<FakeOutput> _outputs = [];
    private readonly List<string?> _asked = [];
    private Func<string?, FakeOutput> _make = id => new FakeOutput { DeviceId = id };
    private readonly PlayerEngine _engine;

    public OutputDeviceTests()
    {
        _engine = new PlayerEngine(path => new MediaFoundationDecoder(path), id =>
        {
            _asked.Add(id);
            var o = _make(id);
            _outputs.Add(o);
            return Task.FromResult<IAudioOutput>(o);
        });
    }

    public void Dispose()
    {
        _engine.Dispose();
        _audio.Dispose();
    }

    private FakeOutput Output => _outputs[^1];

    [Fact]
    public async Task The_first_output_opens_on_the_chosen_device()
    {
        await _engine.SetOutputDeviceAsync(Headset);
        await _engine.LoadAsync(_audio.Tone("a.wav", 2.0));
        Assert.Equal([Headset], _asked);
        Assert.Equal(Headset, _engine.ActiveOutputDevice);
    }

    [Fact]
    public async Task Choosing_a_device_while_playing_keeps_the_place_and_plays_on()
    {
        await _engine.LoadAsync(_audio.Tone("a.wav", 3.0));
        _engine.Play();
        Output.Pump(1.0);
        var first = Output;

        await _engine.SetOutputDeviceAsync(Speakers);

        Assert.Equal(2, _outputs.Count);
        Assert.True(first.Disposed);
        Assert.Equal(Speakers, _engine.ActiveOutputDevice);
        Assert.True(Output.Playing);
        Assert.Equal(PlayState.Playing, _engine.State);
        Assert.Equal(1.0, _engine.Position.TotalSeconds, 1); // not back to 0
        Output.Pump(0.5);
        Assert.Equal(1.5, _engine.Position.TotalSeconds, 1);
        first.Fail(new InvalidOperationException("late")); // the old output's events are ignored
        Assert.Equal(PlayState.Playing, _engine.State);
    }

    [Fact]
    public async Task Choosing_a_device_while_paused_stays_paused()
    {
        await _engine.LoadAsync(_audio.Tone("a.wav", 3.0));
        _engine.Play();
        Output.Pump(0.5);
        _engine.Pause();

        await _engine.SetOutputDeviceAsync(Headset);

        Assert.Equal(PlayState.Paused, _engine.State);
        Assert.False(Output.Playing);
        Assert.True(Output.Initialised);
        _engine.Play();
        Assert.True(Output.Playing);
        Output.Pump(0.5);
        Assert.Equal(1.0, _engine.Position.TotalSeconds, 1);
    }

    [Fact]
    public async Task The_same_device_again_opens_nothing_new()
    {
        await _engine.SetOutputDeviceAsync(Headset);
        await _engine.LoadAsync(_audio.Tone("a.wav", 2.0));
        await _engine.SetOutputDeviceAsync(Headset);
        Assert.Single(_outputs);
    }

    [Fact]
    public async Task A_device_that_gives_the_same_output_keeps_the_old_one()
    {
        // The wanted device could not be opened and the factory gave the default, which was playing already.
        _make = _ => new FakeOutput();
        await _engine.LoadAsync(_audio.Tone("a.wav", 2.0));
        _engine.Play();
        var first = Output;
        await _engine.SetOutputDeviceAsync(Headset);
        Assert.False(first.Disposed);
        Assert.True(Output.Disposed);
        Assert.True(first.Playing);
    }

    [Fact]
    public async Task Without_an_output_the_choice_waits_for_the_next_load()
    {
        await _engine.SetOutputDeviceAsync(Speakers);
        Assert.Empty(_outputs);
        Assert.Equal(Speakers, _engine.OutputDevice);
    }

    [Fact]
    public async Task An_unplugged_chosen_device_moves_to_the_default_and_keeps_playing()
    {
        Exception? failed = null;
        _engine.PlaybackFailed += (_, e) => failed = e;
        await _engine.SetOutputDeviceAsync(Headset);
        await _engine.LoadAsync(_audio.Tone("a.wav", 3.0));
        _engine.Play();
        Output.Pump(1.0);
        var lost = Output;

        lost.Fail(new InvalidOperationException("AUDCLNT_E_DEVICE_INVALIDATED"));
        await _engine.OutputRecovery;

        Assert.Null(failed);
        Assert.Equal(2, _outputs.Count);
        Assert.Null(Output.DeviceId);
        Assert.True(lost.Disposed);
        Assert.Equal(PlayState.Playing, _engine.State);
        Assert.True(Output.Playing);
        Assert.Equal(Headset, _engine.OutputDevice); // the choice is kept for when it is back
        Output.Pump(0.5);
        Assert.Equal(1.5, _engine.Position.TotalSeconds, 1);

        await _engine.SetOutputDeviceAsync(Headset); // plugged in again
        Assert.Equal(Headset, _engine.ActiveOutputDevice);
        Assert.Equal(PlayState.Playing, _engine.State);
    }

    [Fact]
    public async Task When_the_default_cannot_take_over_either_playback_fails_as_before()
    {
        Exception? failed = null;
        _engine.PlaybackFailed += (_, e) => failed = e;
        await _engine.SetOutputDeviceAsync(Headset);
        await _engine.LoadAsync(_audio.Tone("a.wav", 3.0));
        _engine.Play();
        _make = id => id is null ? throw new InvalidOperationException("no device at all") : new FakeOutput { DeviceId = id };

        Output.Fail(new InvalidOperationException("gone"));
        await _engine.OutputRecovery;

        Assert.Equal("gone", failed?.Message);
        Assert.Equal(PlayState.Stopped, _engine.State);
        _make = id => new FakeOutput { DeviceId = id };
        await _engine.LoadAsync(_audio.Tone("b.wav", 1.0)); // the next load opens a new output
        Assert.Equal(PlayState.Stopped, _engine.State);
        Assert.False(Output.Disposed);
    }

    [Fact]
    public async Task An_error_on_the_Windows_default_still_stops_with_PlaybackFailed()
    {
        Exception? failed = null;
        _engine.PlaybackFailed += (_, e) => failed = e;
        await _engine.LoadAsync(_audio.Tone("a.wav", 1.0));
        _engine.Play();
        Output.Fail(new InvalidOperationException("device gone"));
        Assert.Equal("device gone", failed?.Message);
        Assert.Single(_outputs);
    }
}
