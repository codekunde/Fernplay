using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Fernplay.Core;

/// <summary>
/// Updating an installed Fernplay from About: the release's MSI is downloaded, checked against the SHA-256 GitHub
/// gives for it, and installed by msiexec after Fernplay has closed; Fernplay then starts again.
/// Only for the copy the MSI installed: a portable or zip copy would get a second, installed copy beside it.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>Where the MSI installs Fernplay (per user), relative to %LOCALAPPDATA%.</summary>
    public const string InstallFolder = @"Programs\Fernplay";

    /// <summary>Release files must come from Fernplay's own GitHub releases.</summary>
    private const string AllowedPrefix = "https://github.com/codekunde/Fernplay/releases/download/";

    /// <summary>
    /// True when this exe is the one the MSI installed: it runs from %LOCALAPPDATA%\Programs\Fernplay, is not
    /// portable and no test data folder is set.
    /// </summary>
    public static bool IsInstalledCopy(string exeFolder, string localAppData, bool portable, string? testDataFolder)
    {
        if (portable || testDataFolder is { Length: > 0 } || exeFolder.Length == 0 || localAppData.Length == 0) return false;
        static string Norm(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Norm(exeFolder), Norm(Path.Combine(localAppData, InstallFolder)), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Why this release's MSI cannot be used for an in-app update, or null when it can.</summary>
    public static string? Problem(ReleaseInfo release)
    {
        if (release.Msi is not { } msi) return "This release has no installer (MSI).";
        if (!msi.DownloadUrl.StartsWith(AllowedPrefix, StringComparison.Ordinal)) return "The installer is not on Fernplay's release page.";
        if (msi.Sha256 is null) return "GitHub gave no checksum for the installer, so it cannot be checked.";
        return null;
    }

    /// <summary>
    /// Downloads the MSI into <paramref name="folder"/> and checks its size and SHA-256. Returns the file's path.
    /// Throws <see cref="UpdateException"/> with a message for the user; a file that fails the check is deleted.
    /// </summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, string folder, string currentVersion,
        IProgress<double>? progress = null, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        if (Problem(release) is { } problem) throw new UpdateException(problem);
        var msi = release.Msi!;
        Directory.CreateDirectory(folder);
        // Installers and logs from earlier updates (about 57 MB each) go first; a file in use stays.
        foreach (var old in Directory.EnumerateFiles(folder))
            try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        var target = Path.Combine(folder, Path.GetFileName(msi.Name));
        var part = target + ".part";
        try
        {
            using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            http.Timeout = Timeout.InfiniteTimeSpan; // a slow line may need minutes; the caller can cancel
            using var request = new HttpRequestMessage(HttpMethod.Get, msi.DownloadUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Fernplay", currentVersion.Length > 0 ? currentVersion : "0"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new UpdateException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase} for the installer.");
            long total = response.Content.Headers.ContentLength ?? msi.Size;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long done = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(part))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    sha.AppendData(buffer, 0, n);
                    done += n;
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)done / total));
                }
            }
            if (msi.Size > 0 && done != msi.Size)
                throw new UpdateException($"The download was incomplete ({done:N0} of {msi.Size:N0} bytes). Try again.");
            var hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            if (hash != msi.Sha256)
                throw new UpdateException("The downloaded installer does not match its checksum on GitHub, so it was not used.");
            File.Move(part, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateException(ex is HttpRequestException ? "Could not download the installer. Check the internet connection." : $"Could not save the installer: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The hidden PowerShell script that waits for Fernplay (process id) to close, installs the MSI quietly with a
    /// progress bar (per user: no admin prompt; log beside the MSI), and starts Fernplay again when it worked.
    /// </summary>
    public static string Script(int processId, string msiPath, string exePath)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var log = Path.ChangeExtension(msiPath, ".log");
        // One argument string: Windows PowerShell 5.1 passes an argument array so that msiexec fails (1622) on /l*v.
        string msiArgs = $"'/i \"' + {Q(msiPath)} + '\" /passive /norestart /l*v \"' + {Q(log)} + '\"'";
        return string.Join("; ",
            $"Wait-Process -Id {processId} -Timeout 60 -ErrorAction SilentlyContinue",
            $"$p = Start-Process msiexec.exe -ArgumentList ({msiArgs}) -Wait -PassThru",
            $"if ($p.ExitCode -eq 0 -or $p.ExitCode -eq 3010) {{ Start-Process {Q(exePath)} }}");
    }

    /// <summary>Arguments for powershell.exe that run <see cref="Script"/> hidden (encoded, so no quoting can break it).</summary>
    public static string PowerShellArguments(string script) =>
        "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand " +
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
}

public sealed class UpdateException(string message) : Exception(message);
