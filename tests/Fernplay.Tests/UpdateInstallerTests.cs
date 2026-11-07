using System.Net;
using System.Security.Cryptography;
using System.Text;
using Fernplay.Core;

namespace Fernplay.Tests;

public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fernplay-update-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Installer = Encoding.ASCII.GetBytes(new string('M', 300_000));
    private static readonly string InstallerSha = Convert.ToHexStringLower(SHA256.HashData(Installer));
    private const string Url = "https://github.com/codekunde/Fernplay/releases/download/v0.9.0/Fernplay-0.9.0-win-x64.msi";

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class BytesHandler(byte[] body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
        }
    }

    private static ReleaseInfo Release(string? sha = null, long? size = null, string url = Url, string name = "Fernplay-0.9.0-win-x64.msi") =>
        new(new Version(0, 9, 0), "v0.9.0", "https://github.com/codekunde/Fernplay/releases/tag/v0.9.0",
            [
                new ReleaseAsset("Fernplay-0.9.0-win-x64.zip", "https://github.com/codekunde/Fernplay/releases/download/v0.9.0/Fernplay-0.9.0-win-x64.zip", 900_000, new string('a', 64)),
                new ReleaseAsset(name, url, size ?? Installer.Length, sha ?? InstallerSha),
            ]);

    [Fact]
    public void Parse_reads_assets_and_their_sha256()
    {
        const string json = """
            {"tag_name":"v0.9.0","html_url":"https://github.com/codekunde/Fernplay/releases/tag/v0.9.0","assets":[
              {"name":"Fernplay-0.9.0-win-x64.msi","browser_download_url":"https://github.com/codekunde/Fernplay/releases/download/v0.9.0/Fernplay-0.9.0-win-x64.msi","size":58000000,
               "digest":"sha256:ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789"},
              {"name":"Fernplay-0.9.0-win-x64.zip","browser_download_url":"https://x/y.zip","size":900000,"digest":null},
              {"name":"broken"}
            ]}
            """;
        var r = UpdateCheck.Parse(json)!;
        Assert.Equal(2, r.Assets!.Count);
        Assert.Equal("Fernplay-0.9.0-win-x64.msi", r.Msi!.Name);
        Assert.Equal(58_000_000, r.Msi.Size);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", r.Msi.Sha256);
        Assert.Null(r.Assets[1].Sha256);
    }

    [Fact]
    public void A_release_without_assets_has_no_msi() =>
        Assert.Null(UpdateCheck.Parse("""{"tag_name":"v0.9.0"}""")!.Msi);

    [Theory]
    [InlineData(@"C:\Users\a\AppData\Local\Programs\Fernplay", false, null, true)]
    [InlineData(@"C:\Users\a\AppData\Local\Programs\Fernplay\", false, null, true)]
    [InlineData(@"c:\users\A\appdata\local\programs\fernplay", false, null, true)]
    [InlineData(@"C:\Users\a\AppData\Local\Programs\Fernplay", true, null, false)]    // portable.txt beside it
    [InlineData(@"C:\Users\a\AppData\Local\Programs\Fernplay", false, @"D:\t", false)] // test run
    [InlineData(@"D:\Tools\Fernplay", false, null, false)]                              // a zip copy
    public void Only_the_msi_installed_copy_updates_itself(string exeFolder, bool portable, string? data, bool expected) =>
        Assert.Equal(expected, UpdateInstaller.IsInstalledCopy(exeFolder, @"C:\Users\a\AppData\Local", portable, data));

    [Fact]
    public void Problems_are_named()
    {
        Assert.Null(UpdateInstaller.Problem(Release()));
        Assert.Contains("no installer", UpdateInstaller.Problem(Release(name: "Fernplay-0.9.0-win-x64.exe")));
        Assert.Contains("not on Fernplay's release page", UpdateInstaller.Problem(Release(url: "https://example.com/Fernplay-0.9.0-win-x64.msi")));
        Assert.Contains("not on Fernplay's release page", UpdateInstaller.Problem(Release(url: "https://github.com/someone/Fernplay/releases/download/v0.9.0/x.msi")));
        var noSha = Release() with { Assets = [new ReleaseAsset("Fernplay-0.9.0-win-x64.msi", Url, 1, null)] };
        Assert.Contains("no checksum", UpdateInstaller.Problem(noSha));
    }

    [Fact]
    public async Task Download_checks_the_hash_and_keeps_the_file()
    {
        var handler = new BytesHandler(Installer);
        var reports = new List<double>();
        Directory.CreateDirectory(_folder);
        var leftover = Path.Combine(_folder, "Fernplay-0.8.0-win-x64.msi");
        await File.WriteAllTextAsync(leftover, "old installer");
        var path = await UpdateInstaller.DownloadAsync(Release(), _folder, "0.8.0", new SyncProgress(reports), handler);
        Assert.Equal(Path.Combine(_folder, "Fernplay-0.9.0-win-x64.msi"), path);
        Assert.Equal(Installer, await File.ReadAllBytesAsync(path));
        Assert.Equal(Url, handler.Seen!.RequestUri!.ToString());
        Assert.Contains("Fernplay/0.8.0", handler.Seen.Headers.UserAgent.ToString());
        Assert.Equal(1.0, reports[^1], 3);
        Assert.False(File.Exists(path + ".part"));
        Assert.False(File.Exists(leftover)); // the last update's installer is cleared
    }

    [Fact]
    public async Task A_wrong_hash_is_refused_and_nothing_is_kept()
    {
        var bad = (byte[])Installer.Clone();
        bad[1000] ^= 1; // one bit changed, same size
        var ex = await Assert.ThrowsAsync<UpdateException>(() => UpdateInstaller.DownloadAsync(Release(), _folder, "0.8.0", handler: new BytesHandler(bad)));
        Assert.Contains("does not match its checksum", ex.Message);
        Assert.Empty(Directory.EnumerateFiles(_folder));
    }

    [Fact]
    public async Task A_short_download_is_refused()
    {
        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            UpdateInstaller.DownloadAsync(Release(), _folder, "0.8.0", handler: new BytesHandler(Installer[..1000])));
        Assert.Contains("incomplete", ex.Message);
        Assert.Empty(Directory.EnumerateFiles(_folder));
    }

    [Fact]
    public async Task An_http_error_is_reported()
    {
        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            UpdateInstaller.DownloadAsync(Release(), _folder, "0.8.0", handler: new BytesHandler([], HttpStatusCode.NotFound)));
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task A_foreign_address_is_never_fetched()
    {
        var handler = new BytesHandler(Installer);
        await Assert.ThrowsAsync<UpdateException>(() =>
            UpdateInstaller.DownloadAsync(Release(url: "https://evil.example/Fernplay-0.9.0-win-x64.msi"), _folder, "0.8.0", handler: handler));
        Assert.Null(handler.Seen);
    }

    [Fact]
    public void Script_waits_installs_quietly_and_restarts_only_on_success()
    {
        var script = UpdateInstaller.Script(4242, @"C:\Temp\it's\Fernplay-0.9.0-win-x64.msi", @"C:\Users\a\AppData\Local\Programs\Fernplay\Fernplay.exe");
        Assert.StartsWith("Wait-Process -Id 4242", script);
        Assert.Contains(" /passive /norestart /l*v ", script);
        Assert.Contains(@"'C:\Temp\it''s\Fernplay-0.9.0-win-x64.msi'", script); // quote doubled inside single quotes
        Assert.Contains(@"'C:\Temp\it''s\Fernplay-0.9.0-win-x64.log'", script);
        Assert.Contains("-eq 0 -or $p.ExitCode -eq 3010", script);
        var args = UpdateInstaller.PowerShellArguments(script);
        Assert.Contains("-WindowStyle Hidden", args);
        var encoded = args[(args.LastIndexOf(' ') + 1)..];
        Assert.Equal(script, Encoding.Unicode.GetString(Convert.FromBase64String(encoded)));
    }

    /// <summary>
    /// The real thing against GitHub (about 60 MB): only with FERNPLAY_LIVE=1, otherwise it passes without a request.
    /// </summary>
    [Fact]
    public async Task Live_latest_release_msi_downloads_and_matches_its_checksum()
    {
        if (Environment.GetEnvironmentVariable("FERNPLAY_LIVE") != "1") return;
        var check = await UpdateCheck.CheckAsync("0.0.1");
        Assert.Null(check.Error);
        Assert.Null(UpdateInstaller.Problem(check.Latest!));
        var path = await UpdateInstaller.DownloadAsync(check.Latest!, _folder, "0.0.1");
        Assert.Equal(check.Latest!.Msi!.Size, new FileInfo(path).Length);
    }

    /// <summary>
    /// Runs the real script hidden (FERNPLAY_LIVE=1 only): it waits for the process, msiexec fails on a missing MSI,
    /// and the "restart" (here a marker-writing cmd) must not run.
    /// </summary>
    [Fact]
    public async Task Live_script_waits_and_does_not_restart_after_a_failed_install()
    {
        if (Environment.GetEnvironmentVariable("FERNPLAY_LIVE") != "1") return;
        Directory.CreateDirectory(_folder);
        var marker = Path.Combine(_folder, "restarted.txt");
        var restart = Path.Combine(_folder, "restart.cmd");
        await File.WriteAllTextAsync(restart, $"@echo yes> \"{marker}\"");
        using var waitFor = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ping", "-n 3 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        var script = UpdateInstaller.Script(waitFor.Id, Path.Combine(_folder, "missing.msi"), restart);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", UpdateInstaller.PowerShellArguments(script)) { CreateNoWindow = true, UseShellExecute = false })!;
        Assert.True(ps.WaitForExit(60_000));
        Assert.True(waitFor.HasExited);
        Assert.True(sw.Elapsed > TimeSpan.FromSeconds(1.5)); // it waited for the process (ping runs ~2 s)
        Assert.False(File.Exists(marker));
        Assert.True(File.Exists(Path.Combine(_folder, "missing.log"))); // msiexec ran and logged
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; this one records at once.</summary>
    private sealed class SyncProgress(List<double> list) : IProgress<double>
    {
        public void Report(double value) { lock (list) list.Add(value); }
    }
}
