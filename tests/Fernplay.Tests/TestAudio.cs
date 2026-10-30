using Fernplay.Core.Audio;
using NAudio.Wave;

namespace Fernplay.Tests;

/// <summary>Writes short WAV files for the tests; deleted with the test folder.</summary>
public sealed class TestAudio : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "fernplay-tests-" + Guid.NewGuid().ToString("N"));

    public TestAudio() => Directory.CreateDirectory(Folder);

    /// <summary>A sine tone, 16-bit PCM.</summary>
    public string Tone(string name, double seconds, int sampleRate = 44100, int channels = 2, double hz = 440, float amplitude = 0.5f)
    {
        var path = Path.Combine(Folder, name);
        using var writer = new WaveFileWriter(path, new WaveFormat(sampleRate, 16, channels));
        int frames = (int)(seconds * sampleRate);
        var frame = new float[channels];
        for (int i = 0; i < frames; i++)
        {
            Array.Fill(frame, amplitude * (float)Math.Sin(2 * Math.PI * hz * i / sampleRate));
            writer.WriteSamples(frame, 0, channels);
        }
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { }
    }
}

/// <summary>Stands in for the sound card: the test pulls samples with Pump, like the audio thread would.</summary>
public sealed class FakeOutput : IAudioOutput
{
    private ISampleProvider? _source;
    public bool Playing { get; private set; }
    public bool Disposed { get; private set; }
    public float LastPeak { get; private set; }

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public void Init(ISampleProvider source) => _source = source;
    public void Play() => Playing = true;
    public void Pause() => Playing = false;

    public void Stop()
    {
        Playing = false;
        PlaybackStopped?.Invoke(this, new StoppedEventArgs());
    }

    public void Dispose() => Disposed = true;

    /// <summary>Reads the given seconds of audio; raises PlaybackStopped when the source runs dry, as WASAPI does.</summary>
    public void Pump(double seconds)
    {
        if (!Playing || _source is null) return;
        var f = _source.WaveFormat;
        var buf = new float[(int)(seconds * f.SampleRate) * f.Channels];
        int n = _source.Read(buf);
        LastPeak = n == 0 ? 0 : buf.AsSpan(0, n).ToArray().Max(Math.Abs);
        if (n < buf.Length)
        {
            // Ask once more like the real player, so the end is seen.
            if (n > 0) _source.Read(new float[16]);
            Playing = false;
            PlaybackStopped?.Invoke(this, new StoppedEventArgs());
        }
    }

    public void Fail(Exception ex)
    {
        Playing = false;
        PlaybackStopped?.Invoke(this, new StoppedEventArgs(ex));
    }
}
