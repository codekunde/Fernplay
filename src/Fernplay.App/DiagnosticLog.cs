using System.IO;

namespace Fernplay.App;

/// <summary>
/// A short local log next to settings.json (diagnostics.log, at most two files of 256 KB): playback state changes,
/// who asked for a pause, Windows media buttons, lock/unlock and display changes with the panel positions.
/// It never leaves the PC; it is there so a fault that happens now and then can be traced afterwards.
/// </summary>
public static class DiagnosticLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(Path.GetDirectoryName(UserSettings.FilePath)!, "diagnostics.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // logging must never break playback
    }

    /// <summary>The calling methods (nearest first), to see which button, key or Windows event asked for something.</summary>
    public static string Callers(int skip = 1, int count = 6)
    {
        var frames = new System.Diagnostics.StackTrace(skip + 1, false).GetFrames();
        return string.Join(" < ", frames
            .Select(f => f.GetMethod())
            .Where(m => m?.DeclaringType is { } t && t.Namespace?.StartsWith("Fernplay", StringComparison.Ordinal) == true)
            .Take(count)
            .Select(m => $"{m!.DeclaringType!.Name}.{m.Name}"));
    }
}
