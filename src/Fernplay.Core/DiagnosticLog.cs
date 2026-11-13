namespace Fernplay.Core;

/// <summary>
/// A short local log next to settings.json (diagnostics.log, at most two files of 256 KB) to trace a fault that happens
/// now and then: playback, stations, the audio device, podcasts, updates, the phone remote, errors. It never leaves the PC.
/// <para>
/// Off by default. "Fernplay.exe /diagnostics on" creates <see cref="SwitchFileName"/> in the data folder and
/// "/diagnostics off" deletes it; a running Fernplay notices within <see cref="Recheck"/>, no restart needed. A file
/// (not a setting in settings.json) because the running player rewrites settings.json when it closes, and the switch
/// must work whether or not it runs.
/// </para>
/// </summary>
public static class DiagnosticLog
{
    public const string FileName = "diagnostics.log";
    public const string SwitchFileName = "diagnostics.on";
    private const long MaxBytes = 256 * 1024;
    /// <summary>How long a running Fernplay takes at most to notice the switch file appear or go.</summary>
    public static readonly TimeSpan Recheck = TimeSpan.FromSeconds(2);

    private static readonly object Gate = new();
    private static string? _folder;
    private static bool _on;
    private static long _checkedAt;
    private static bool _checkedOnce;

    /// <summary>The data folder (where settings.json is); nothing is written until it is set.</summary>
    public static string? Folder
    {
        get => _folder;
        set { lock (Gate) { _folder = value; _checkedOnce = false; _on = false; } }
    }

    /// <summary>One line written first whenever logging (re)starts in this process: version, Windows, data folder kind.</summary>
    public static Func<string>? Header { get; set; }

    public static string? FilePath => _folder is null ? null : Path.Combine(_folder, FileName);

    /// <summary>Logging is switched on for this data folder (checked at most every <see cref="Recheck"/>).</summary>
    public static bool Enabled { get { lock (Gate) return EnabledLocked(); } }

    public static bool IsOn(string folder) => File.Exists(Path.Combine(folder, SwitchFileName));

    /// <summary>Creates or deletes the switch file. Throws IOException / UnauthorizedAccessException (the caller says so).</summary>
    public static void SetOn(string folder, bool on)
    {
        var marker = Path.Combine(folder, SwitchFileName);
        if (on)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(marker, "While this file is here Fernplay writes diagnostics.log next to it. Run \"Fernplay.exe /diagnostics off\" (or delete this file) to stop.\r\n");
        }
        else File.Delete(marker);
        lock (Gate) if (string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase)) _checkedOnce = false; // this process sees it at once
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                if (!EnabledLocked()) return;
                Append(message);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // logging must never break playback
    }

    /// <summary>Writes even while off (the "turned off" line just before the switch file goes).</summary>
    public static void WriteAlways(string message)
    {
        try { lock (Gate) if (_folder is not null) Append(message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The calling methods (nearest first), to see which button, key or Windows event asked for something; empty while off.</summary>
    public static string Callers(int skip = 1, int count = 6)
    {
        if (!Enabled) return "";
        var frames = new System.Diagnostics.StackTrace(skip + 1, false).GetFrames();
        return string.Join(" < ", frames
            .Select(f => f.GetMethod())
            .Where(m => m?.DeclaringType is { } t && t.Namespace?.StartsWith("Fernplay", StringComparison.Ordinal) == true)
            .Take(count)
            .Select(m => $"{m!.DeclaringType!.Name}.{m.Name}"));
    }

    /// <summary>A file name for the log (never the folders above it, which hold the user's name).</summary>
    public static string Name(string? path) =>
        path is null ? "(none)" : Track.IsStreamPath(path) ? path : Path.GetFileName(path);

    private static bool EnabledLocked()
    {
        if (_folder is null) return false;
        long now = Environment.TickCount64;
        if (_checkedOnce && now - _checkedAt < Recheck.TotalMilliseconds) return _on;
        bool was = _on;
        _on = IsOn(_folder);
        _checkedAt = now;
        _checkedOnce = true;
        if (_on && !was)
        {
            try { Append($"diagnostics log on: {Header?.Invoke() ?? "Fernplay"}"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return _on;
    }

    private static void Append(string message)
    {
        var path = Path.Combine(_folder!, FileName);
        Directory.CreateDirectory(_folder!);
        var info = new FileInfo(path);
        if (info.Exists && info.Length > MaxBytes) File.Move(path, path + ".old", overwrite: true);
        File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");
    }
}

/// <summary>"Fernplay.exe /diagnostics on|off|status": switches the diagnostics log; no window, no playback.</summary>
public enum DiagnosticsCommand { On, Off, Status, Usage }

public static class DiagnosticsCli
{
    /// <summary>
    /// The command when the arguments are a diagnostics switch (<c>/diagnostics</c>, <c>--diagnostics</c> or
    /// <c>-diagnostics</c>, any case, then on, off or status; alone it means status); otherwise null (files to open).
    /// Anything else after the switch is <see cref="DiagnosticsCommand.Usage"/>.
    /// </summary>
    public static DiagnosticsCommand? Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || !IsSwitch(args[0])) return null;
        if (args.Count == 1) return DiagnosticsCommand.Status;
        if (args.Count > 2) return DiagnosticsCommand.Usage;
        return args[1].ToLowerInvariant() switch
        {
            "on" => DiagnosticsCommand.On,
            "off" => DiagnosticsCommand.Off,
            "status" => DiagnosticsCommand.Status,
            _ => DiagnosticsCommand.Usage,
        };
    }

    private static bool IsSwitch(string a) =>
        a.TrimStart('/', '-') is var name && a.Length > name.Length && a.Length - name.Length <= 2
        && name.Equals("diagnostics", StringComparison.OrdinalIgnoreCase);

    public const string UsageText = "Usage: Fernplay.exe /diagnostics on | off | status";

    /// <summary>
    /// Carries out the command for the data folder; returns the text to print and the exit code (0 done, 1 failed,
    /// 2 usage). The log keeps what it has when switched off (to send along with a fault report).
    /// </summary>
    public static (string Text, int ExitCode) Run(DiagnosticsCommand command, string folder)
    {
        string log = Path.Combine(folder, DiagnosticLog.FileName);
        try
        {
            switch (command)
            {
                case DiagnosticsCommand.On:
                    bool already = DiagnosticLog.IsOn(folder);
                    DiagnosticLog.SetOn(folder, true);
                    if (!already) DiagnosticLog.Write("diagnostics turned on from the command line");
                    return ($"Diagnostics log is on{(already ? " (it already was)" : "")}: {log}{Environment.NewLine}Turn it off with: Fernplay.exe /diagnostics off", 0);
                case DiagnosticsCommand.Off:
                    bool wasOn = DiagnosticLog.IsOn(folder);
                    if (wasOn) DiagnosticLog.WriteAlways("diagnostics turned off from the command line");
                    DiagnosticLog.SetOn(folder, false);
                    return ($"Diagnostics log is off{(wasOn ? "" : " (it already was)")}.{(File.Exists(log) ? $" What it wrote so far stays in {log}" : "")}", 0);
                case DiagnosticsCommand.Status:
                    return DiagnosticLog.IsOn(folder)
                        ? ($"Diagnostics log is on: {log}", 0)
                        : ($"Diagnostics log is off.{(File.Exists(log) ? $" An earlier log is in {log}" : "")}", 0);
                default:
                    return (UsageText, 2);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ($"Could not switch the diagnostics log: {ex.Message}", 1);
        }
    }
}
