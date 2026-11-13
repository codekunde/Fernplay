using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace Fernplay.App;

/// <summary>
/// Text for a command-line switch (Fernplay.exe /diagnostics …). Fernplay is a Windows app with no console of its
/// own: output that is piped or redirected goes there; otherwise it borrows the console of the shell that started it;
/// started from Run (Win+R) or a shortcut, with no console at all, it shows a message box.
/// </summary>
internal static class CliConsole
{
    private const int StdOutput = -11;
    private const int AttachParentProcess = -1;
    private const uint FileTypeChar = 2;

    public static void WriteLine(string text, bool error = false)
    {
        try
        {
            var handle = GetStdHandle(StdOutput);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1) && GetFileType(handle) is not (0 or FileTypeChar))
            {
                using var stdout = Console.OpenStandardOutput();
                var bytes = Encoding.UTF8.GetBytes(text + Environment.NewLine);
                stdout.Write(bytes);
                return;
            }
            if (AttachConsole(AttachParentProcess))
            {
                // The shell has already printed its prompt (it does not wait for a Windows app): start on a new line.
                var console = GetStdHandle(StdOutput);
                string s = Environment.NewLine + text + Environment.NewLine;
                WriteConsoleW(console, s, (uint)s.Length, out _, IntPtr.Zero);
                FreeConsole();
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        MessageBox.Show(text, "Fernplay", MessageBoxButton.OK, error ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WriteConsoleW(IntPtr console, string text, uint length, out uint written, IntPtr reserved);
}
