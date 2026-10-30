using System.Runtime.InteropServices;

namespace Fernplay.Core;

/// <summary>Turns a load or playback failure into one sentence the user can act on.</summary>
public static class PlaybackErrors
{
    private const int UnsupportedByteStream = unchecked((int)0xC00D36C4); // MF_E_UNSUPPORTED_BYTESTREAM_TYPE
    private const int ClassNotRegistered = unchecked((int)0x80040154);    // REGDB_E_CLASSNOTREG

    public static string Describe(Exception ex, string path)
    {
        var name = Path.GetFileName(path);
        return ex switch
        {
            FileNotFoundException or DirectoryNotFoundException => $"Can't find {name}: it was moved or deleted.",
            UnauthorizedAccessException => $"Can't open {name}: Windows denied access to the file.",
            DllNotFoundException or TypeInitializationException => MissingMediaFeatures,
            COMException { HResult: ClassNotRegistered } => MissingMediaFeatures,
            _ when ex.HResult == UnsupportedByteStream || ex.Message.Contains("byte stream type is not supported", StringComparison.OrdinalIgnoreCase)
                => $"Can't play {name}: Windows has no decoder for {Path.GetExtension(path).TrimStart('.').ToUpperInvariant()} files.",
            // Any other Media Foundation error (facility 0xC00D): the decoder could not make sense of the data.
            _ when IsMediaFoundationError(ex) => $"Can't play {name}: the file is damaged or is not a music file.",
            _ => $"Can't play {name}: {ex.Message.TrimEnd('.')}.",
        };
    }

    private static bool IsMediaFoundationError(Exception ex) =>
        (ex.HResult & 0xFFFF0000) == 0xC00D0000 || ex.Message.Contains("0xC00D", StringComparison.OrdinalIgnoreCase);

    public const string MissingMediaFeatures =
        "Windows media features are missing. On a Windows \"N\" edition, install the Media Feature Pack from Microsoft, then restart Fernplay.";
}
