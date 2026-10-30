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
}

/// <summary>WASAPI shared mode on the default device; follows Windows when the default device changes (headphones in or out).</summary>
public sealed class WasapiAudioOutput : IAudioOutput
{
    private readonly WasapiPlayer _player;

    private WasapiAudioOutput(WasapiPlayer player)
    {
        _player = player;
        _player.PlaybackStopped += (s, e) => PlaybackStopped?.Invoke(this, e);
    }

    public static async Task<IAudioOutput> CreateAsync() =>
        new WasapiAudioOutput(await new WasapiPlayerBuilder()
            .WithDefaultDeviceStreamRouting()
            .WithLatency(100)
            .BuildAsync());

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public void Init(ISampleProvider source) => _player.Init(source.ToWaveProvider());
    public void Play() => _player.Play();
    public void Pause() => _player.Pause();
    public void Stop() => _player.Stop();
    public void Dispose() => _player.Dispose();
}
