using System.Runtime.InteropServices;

namespace Fernplay.Core;

/// <summary>One file in the playlist, with what Windows knows about it.</summary>
public sealed record Track(string Path, string? Title, string? Artist, string? Album, TimeSpan? Duration, int? BitrateKbps)
{
    /// <summary>"Artist - Title" like the classic players; the file name when there are no tags.</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Title)
            ? (string.IsNullOrWhiteSpace(Artist) ? Title! : $"{Artist} - {Title}")
            : System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>Reads tags through the Windows property system (the same data Explorer shows). Never throws.</summary>
    public static Track FromFile(string path)
    {
        try { return TagReader.Read(path); }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new Track(path, null, null, null, null, null);
        }
    }
}
