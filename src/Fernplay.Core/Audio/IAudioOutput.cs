using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Fernplay.Core.Audio;

/// <summary>
/// Last part of the chain (decoder → EQ → output): pulls samples and sends them to the sound card.
/// PlaybackStopped fires when the source runs dry, when Stop is called, or on a device error.
/// </summary>
public interface IAudioOutput : IDisposable
{
    void Init(ISampleProvider source);
    void Play();
    void Pause();
    void Stop();
    event EventHandler<StoppedEventArgs>? PlaybackStopped;
    /// <summary>The playback device this output is fixed to; null when it follows the Windows default.</summary>
    string? DeviceId => null;
}

/// <summary>
/// WASAPI shared mode, either on the Windows default device (following Windows when the default changes: headphones
/// in or out) or on one chosen device. A chosen device that is not connected gives the default instead.
/// </summary>
public sealed class WasapiAudioOutput : IAudioOutput
{
    private readonly WasapiPlayer _player;
    private readonly MMDevice? _device;

    private WasapiAudioOutput(WasapiPlayer player, MMDevice? device)
    {
        (_player, _device) = (player, device);
        DeviceId = device?.ID;
        _player.PlaybackStopped += (s, e) => PlaybackStopped?.Invoke(this, e);
    }

    /// <summary>The Windows default device (following changes).</summary>
    public static Task<IAudioOutput> CreateAsync() => CreateAsync(null);

    /// <summary>
    /// On the device with this ID (from <see cref="SystemAudioDevices"/>); null, or a device that is not connected or
    /// cannot be opened, gives the Windows default. A fixed device that is unplugged while playing stops with an error
    /// (WASAPI reports the device as invalidated); <see cref="PlayerEngine"/> then moves to the default by itself.
    /// </summary>
    public static async Task<IAudioOutput> CreateAsync(string? deviceId)
    {
        if (deviceId is not null && OpenDevice(deviceId) is { } device)
        {
            try
            {
                var player = new WasapiPlayerBuilder().WithDevice(device).WithLatency(100).Build();
                var fixedOutput = new WasapiAudioOutput(player, device);
                if (DiagnosticLog.Enabled) DiagnosticLog.Write($"audio output: WASAPI shared, chosen device \"{device.FriendlyName}\"");
                return fixedOutput;
            }
            catch (Exception ex)
            {
                // Only the default is left to try: never let a chosen device stop playback altogether.
                DiagnosticLog.Write($"audio output: chosen device could not be opened, using the Windows default: {PlaybackErrors.Detail(ex)}");
                device.Dispose();
            }
        }
        var output = new WasapiAudioOutput(await new WasapiPlayerBuilder()
            .WithDefaultDeviceStreamRouting()
            .WithLatency(100)
            .BuildAsync(), null);
        if (DiagnosticLog.Enabled) DiagnosticLog.Write($"audio output: WASAPI shared, default device \"{DefaultDeviceName()}\"");
        return output;
    }

    /// <summary>The device if it is connected (active); null and a log line otherwise.</summary>
    private static MMDevice? OpenDevice(string deviceId)
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            var device = devices.GetDevice(deviceId);
            if (device.State == DeviceState.Active) return device;
            if (DiagnosticLog.Enabled) DiagnosticLog.Write($"audio output: chosen device \"{SafeName(device)}\" is {device.State}, using the Windows default");
            device.Dispose();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"audio output: chosen device not found, using the Windows default: {ex.GetType().Name}");
        }
        return null;
    }

    private static string SafeName(MMDevice device)
    {
        try { return device.FriendlyName; }
        catch (Exception ex) { return $"unknown ({ex.GetType().Name})"; }
    }

    /// <summary>The Windows default playback device's name, for the log.</summary>
    private static string DefaultDeviceName()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.FriendlyName;
        }
        catch (Exception ex) { return $"unknown ({ex.GetType().Name})"; } // only for the log: never let it stop playback
    }

    public string? DeviceId { get; }
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public void Init(ISampleProvider source) => _player.Init(source.ToWaveProvider());
    public void Play() => _player.Play();
    public void Pause() => _player.Pause();
    public void Stop() => _player.Stop();

    public void Dispose()
    {
        _player.Dispose();
        _device?.Dispose();
    }
}
