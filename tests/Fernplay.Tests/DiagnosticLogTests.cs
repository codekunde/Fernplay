using Fernplay.Core;

namespace Fernplay.Tests;

[CollectionDefinition(nameof(DiagnosticLogTests), DisableParallelization = true)]
public sealed class DiagnosticLogCollection;

/// <summary>The log is one static per process: these tests run alone and give it back unset.</summary>
[Collection(nameof(DiagnosticLogTests))]
public sealed class DiagnosticLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fernplay-diag-" + Guid.NewGuid().ToString("N"));
    private string Log => Path.Combine(_folder, DiagnosticLog.FileName);
    private string Switch => Path.Combine(_folder, DiagnosticLog.SwitchFileName);

    public DiagnosticLogTests()
    {
        Directory.CreateDirectory(_folder);
        DiagnosticLog.Folder = _folder;
        DiagnosticLog.Header = () => "Fernplay test";
    }

    public void Dispose()
    {
        DiagnosticLog.Folder = null;
        DiagnosticLog.Header = null;
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("")]
    [InlineData("song.mp3")]
    [InlineData("/add song.mp3")]
    [InlineData("/diagnosticsx")]
    [InlineData("///diagnostics on")]
    [InlineData("diagnostics on")] // a file or folder of that name opens as before
    public void Parse_leaves_other_arguments_to_opening(string line) =>
        Assert.Null(DiagnosticsCli.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

    [Theory]
    [InlineData(new[] { "/diagnostics", "on" }, DiagnosticsCommand.On)]
    [InlineData(new[] { "--diagnostics", "ON" }, DiagnosticsCommand.On)]
    [InlineData(new[] { "-Diagnostics", "off" }, DiagnosticsCommand.Off)]
    [InlineData(new[] { "/DIAGNOSTICS", "status" }, DiagnosticsCommand.Status)]
    [InlineData(new[] { "/diagnostics" }, DiagnosticsCommand.Status)]
    [InlineData(new[] { "/diagnostics", "maybe" }, DiagnosticsCommand.Usage)]
    [InlineData(new[] { "/diagnostics", "on", "now" }, DiagnosticsCommand.Usage)]
    public void Parse_reads_the_switch(string[] args, DiagnosticsCommand expected) => Assert.Equal(expected, DiagnosticsCli.Parse(args));

    [Fact]
    public void Off_by_default_nothing_is_written()
    {
        DiagnosticLog.Write("state Playing");
        Assert.False(DiagnosticLog.Enabled);
        Assert.False(File.Exists(Log));
        Assert.Equal("", DiagnosticLog.Callers());
    }

    [Fact]
    public void Nothing_is_written_before_the_folder_is_known()
    {
        DiagnosticLog.Folder = null;
        File.WriteAllText(Switch, "");
        DiagnosticLog.Write("early");
        Assert.False(File.Exists(Log));
    }

    [Fact]
    public void On_writes_a_header_then_lines_and_off_stops_but_keeps_the_log()
    {
        var (text, code) = DiagnosticsCli.Run(DiagnosticsCommand.On, _folder);
        Assert.Equal(0, code);
        Assert.Contains("is on", text);
        Assert.True(File.Exists(Switch));

        DiagnosticLog.Write("state Playing song.mp3");
        var (offText, offCode) = DiagnosticsCli.Run(DiagnosticsCommand.Off, _folder);
        Assert.Equal(0, offCode);
        Assert.Contains("is off", offText);
        Assert.False(File.Exists(Switch));
        DiagnosticLog.Write("after off");

        var lines = File.ReadAllLines(Log);
        Assert.Contains("diagnostics log on: Fernplay test", lines[0]);
        Assert.Contains(lines, l => l.EndsWith("diagnostics turned on from the command line", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.EndsWith("state Playing song.mp3", StringComparison.Ordinal));
        Assert.EndsWith("diagnostics turned off from the command line", lines[^1]);
        Assert.DoesNotContain(lines, l => l.Contains("after off", StringComparison.Ordinal));
        Assert.Contains($"[{Environment.ProcessId}]", lines[0]);
    }

    [Fact]
    public void Status_and_repeats_say_what_is_so()
    {
        Assert.StartsWith("Diagnostics log is off.", DiagnosticsCli.Run(DiagnosticsCommand.Status, _folder).Text);
        DiagnosticsCli.Run(DiagnosticsCommand.On, _folder);
        Assert.Contains("already", DiagnosticsCli.Run(DiagnosticsCommand.On, _folder).Text);
        Assert.StartsWith("Diagnostics log is on:", DiagnosticsCli.Run(DiagnosticsCommand.Status, _folder).Text);
        DiagnosticsCli.Run(DiagnosticsCommand.Off, _folder);
        var again = DiagnosticsCli.Run(DiagnosticsCommand.Off, _folder);
        Assert.Contains("already", again.Text);
        Assert.Contains(Log, again.Text); // the earlier log is still there to send
        Assert.Equal((DiagnosticsCli.UsageText, 2), DiagnosticsCli.Run(DiagnosticsCommand.Usage, _folder));
    }

    [Fact]
    public void A_running_Fernplay_notices_the_switch_another_process_made()
    {
        Assert.False(DiagnosticLog.Enabled); // checked once: off
        File.WriteAllText(Switch, ""); // "Fernplay.exe /diagnostics on" from another process
        Thread.Sleep(DiagnosticLog.Recheck + TimeSpan.FromMilliseconds(200));
        DiagnosticLog.Write("picked up");
        Assert.Contains(File.ReadAllLines(Log), l => l.EndsWith("picked up", StringComparison.Ordinal));

        File.Delete(Switch); // "/diagnostics off" from another process
        Thread.Sleep(DiagnosticLog.Recheck + TimeSpan.FromMilliseconds(200));
        DiagnosticLog.Write("not any more");
        Assert.DoesNotContain(File.ReadAllLines(Log), l => l.Contains("not any more", StringComparison.Ordinal));
    }

    [Fact]
    public void Switch_problems_are_reported_not_thrown()
    {
        Directory.CreateDirectory(Switch); // a folder where the switch file should go: cannot be written or deleted as a file
        var (text, code) = DiagnosticsCli.Run(DiagnosticsCommand.On, _folder);
        Assert.Equal(1, code);
        Assert.StartsWith("Could not switch", text);
    }

    [Fact]
    public void Names_never_carry_the_folders()
    {
        Assert.Equal("song.mp3", DiagnosticLog.Name(@"C:\Users\someone\Music\song.mp3"));
        Assert.Equal("https://radio.example/stream", DiagnosticLog.Name("https://radio.example/stream"));
        Assert.Equal("(none)", DiagnosticLog.Name(null));
    }
}
