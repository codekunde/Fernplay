using NAudio.Wave;

namespace Fernplay.Core.Audio;

/// <summary>
/// First part of the chain (decoder → EQ → output): turns one file into float samples.
/// Read and Seek may be called from different threads; implementations make them safe together.
/// </summary>
public interface ITrackDecoder : ISampleProvider, IDisposable
{
    TimeSpan TotalTime { get; }

    /// <summary>Read position in the file (slightly ahead of what is heard, by the output's buffer).</summary>
    TimeSpan CurrentTime { get; }

    /// <summary>Moves the read position; clamped to the track.</summary>
    void Seek(TimeSpan position);

    AudioFormatInfo Format { get; }
}

/// <summary>A live station that reports what it is playing.</summary>
public interface ILiveStream
{
    /// <summary>The station's own name (icy-name), when it sends one.</summary>
    string? StationName { get; }

    /// <summary>The song now playing ("Artist - Title" on most stations), or null.</summary>
    string? Title { get; }

    /// <summary>Raised on the audio thread when the playing song changes.</summary>
    event EventHandler<string?>? TitleChanged;
}

/// <summary>What the player shows under the title. BitrateKbps is set for a station (files carry it in their tags).</summary>
public sealed record AudioFormatInfo(string Codec, int SampleRate, int Channels, int? BitrateKbps = null)
{
    public string SampleRateText => $"{SampleRate / 1000.0:0.#} kHz";
    public string ChannelsText => Channels switch { 1 => "mono", 2 => "stereo", _ => $"{Channels} channels" };
}
