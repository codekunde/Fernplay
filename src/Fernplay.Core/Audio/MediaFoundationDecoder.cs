using NAudio.Wave;

namespace Fernplay.Core.Audio;

/// <summary>
/// Decodes with Windows' own Media Foundation codecs: MP3, WAV, AAC/M4A, WMA, FLAC (Windows 10 and later).
/// A lock keeps the audio thread's Read and the UI's Seek from running at the same time.
/// </summary>
public sealed class MediaFoundationDecoder : ITrackDecoder
{
    private readonly MediaFoundationReader _reader;
    private readonly ISampleProvider _samples;
    private readonly Lock _gate = new();

    public MediaFoundationDecoder(string path)
    {
        _reader = new MediaFoundationReader(path);
        _samples = _reader.ToSampleProvider();
        Format = new AudioFormatInfo(CodecName(path), _reader.WaveFormat.SampleRate, _reader.WaveFormat.Channels);
    }

    public WaveFormat WaveFormat => _samples.WaveFormat;
    public AudioFormatInfo Format { get; }
    public TimeSpan TotalTime => _reader.TotalTime;

    public TimeSpan CurrentTime
    {
        get { lock (_gate) return _reader.CurrentTime; }
    }

    public int Read(Span<float> buffer)
    {
        lock (_gate) return _samples.Read(buffer);
    }

    public void Seek(TimeSpan position)
    {
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (position > TotalTime) position = TotalTime;
        lock (_gate) _reader.CurrentTime = position;
    }

    public void Dispose()
    {
        lock (_gate) _reader.Dispose();
    }

    internal static string CodecName(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "MP3",
        ".flac" => "FLAC",
        ".wav" => "WAV",
        ".wma" => "WMA",
        ".m4a" or ".aac" or ".mp4" => "AAC",
        var other => other.TrimStart('.').ToUpperInvariant(),
    };
}
