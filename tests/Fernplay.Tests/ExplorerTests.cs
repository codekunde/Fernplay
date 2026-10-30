using Fernplay.Core;
using Microsoft.Win32;

namespace Fernplay.Tests;

/// <summary>
/// Shell registration runs against a throwaway key (HKCU\Software\CodeKunde\FernplayTests\{guid}), deleted after
/// each test; the real file types are never touched.
/// </summary>
public sealed class ExplorerTests : IDisposable
{
    private const string Exe = @"C:\Apps\Fernplay\Fernplay.exe";
    private const string Icon = @"C:\Apps\Fernplay\Assets\FernplayFile.ico";
    private readonly string _rootPath = $@"Software\CodeKunde\FernplayTests\{Guid.NewGuid():N}";
    private readonly RegistryKey _root;

    public ExplorerTests() => _root = Registry.CurrentUser.CreateSubKey(_rootPath);

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false);
        using var parent = Registry.CurrentUser.OpenSubKey(@"Software\CodeKunde\FernplayTests", writable: true);
        if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(@"Software\CodeKunde\FernplayTests", throwOnMissingSubKey: false);
    }

    private string? Value(string path, string? name = null)
    {
        using var k = _root.OpenSubKey(path);
        return k?.GetValue(name) as string;
    }

    private bool KeyExists(string path) { using var k = _root.OpenSubKey(path); return k is not null; }

    [Fact]
    public void Register_writes_open_with_verbs_icons_and_default_apps_entries()
    {
        var reg = new ShellRegistration(_root, Exe, Icon);
        Assert.False(reg.IsRegistered());
        reg.Register();
        Assert.True(reg.IsRegistered());
        Assert.False(reg.NeedsRefresh());

        Assert.Equal($"\"{Exe}\" \"%1\"", Value(@"Software\Classes\Fernplay.Audio\shell\open\command"));
        Assert.Equal($"\"{Exe}\" /add \"%1\"", Value(@"Software\Classes\Fernplay.Audio\shell\enqueue\command"));
        Assert.Equal("Player", Value(@"Software\Classes\Fernplay.Audio\shell\open", "MultiSelectModel"));
        Assert.Equal($"\"{Icon}\"", Value(@"Software\Classes\Fernplay.Playlist\DefaultIcon"));

        using (var owp = _root.OpenSubKey(@"Software\Classes\.mp3\OpenWithProgids"))
            Assert.Contains("Fernplay.Audio", owp!.GetValueNames());
        using (var owp = _root.OpenSubKey(@"Software\Classes\.m3u8\OpenWithProgids"))
            Assert.Contains("Fernplay.Playlist", owp!.GetValueNames());

        Assert.Equal("Add to Fernplay playlist", Value(@"Software\Classes\SystemFileAssociations\.flac\shell\Fernplay.Enqueue"));
        Assert.Equal($"\"{Exe}\" \"%1\"", Value(@"Software\Classes\Directory\shell\Fernplay.Play\command"));
        Assert.Equal("Fernplay", Value(@"Software\Classes\Applications\Fernplay.exe", "FriendlyAppName"));
        Assert.Equal("Fernplay.Audio", Value(@"Software\CodeKunde\Fernplay\Capabilities\FileAssociations", ".wma"));
        Assert.Equal(@"Software\CodeKunde\Fernplay\Capabilities", Value(@"Software\RegisteredApplications", "Fernplay"));
    }

    [Fact]
    public void Unregister_removes_everything_it_wrote_and_leaves_other_apps_alone()
    {
        // Another app's entries on the same extension must survive.
        using (var other = _root.CreateSubKey(@"Software\Classes\.mp3\OpenWithProgids")) other.SetValue("OtherPlayer.mp3", "");
        using (var other = _root.CreateSubKey(@"Software\RegisteredApplications")) other.SetValue("OtherPlayer", @"Software\Other\Capabilities");

        var reg = new ShellRegistration(_root, Exe, Icon);
        reg.Register();
        reg.Unregister();

        Assert.False(reg.IsRegistered());
        Assert.False(KeyExists(@"Software\Classes\Fernplay.Audio"));
        Assert.False(KeyExists(@"Software\Classes\Fernplay.Playlist"));
        Assert.False(KeyExists(@"Software\Classes\Applications\Fernplay.exe"));
        Assert.False(KeyExists(@"Software\Classes\SystemFileAssociations\.mp3\shell\Fernplay.Enqueue"));
        Assert.False(KeyExists(@"Software\Classes\Directory\shell\Fernplay.Play"));
        Assert.False(KeyExists(@"Software\CodeKunde\Fernplay\Capabilities"));
        using (var owp = _root.OpenSubKey(@"Software\Classes\.mp3\OpenWithProgids"))
            Assert.Equal(["OtherPlayer.mp3"], owp!.GetValueNames());
        using (var apps = _root.OpenSubKey(@"Software\RegisteredApplications"))
            Assert.Equal(["OtherPlayer"], apps!.GetValueNames());
    }

    [Fact]
    public void A_moved_exe_needs_a_refresh()
    {
        new ShellRegistration(_root, Exe, Icon).Register();
        var moved = new ShellRegistration(_root, @"D:\Elsewhere\Fernplay.exe", Icon);
        Assert.True(moved.NeedsRefresh());
        moved.Register();
        Assert.False(moved.NeedsRefresh());
        Assert.Equal("\"D:\\Elsewhere\\Fernplay.exe\" \"%1\"", Value(@"Software\Classes\Fernplay.Audio\shell\open\command"));
    }

    // ---------- launch requests

    [Fact]
    public void Args_become_a_request_with_full_paths()
    {
        var r = OpenRequest.FromArgs(["/add", @"music\a.mp3", @"C:\x\b.flac", ""], @"D:\Lib");
        Assert.True(r.Add);
        Assert.Equal([@"D:\Lib\music\a.mp3", @"C:\x\b.flac"], r.Paths);
        Assert.False(OpenRequest.FromArgs([@"C:\x\b.flac"], @"C:\").Add);
    }

    [Fact]
    public void A_request_survives_the_pipe_message()
    {
        var r = new OpenRequest(true, [@"C:\Music\Café – 01.mp3", @"\\nas\share\b.flac"]);
        var back = OpenRequest.FromMessage(r.ToMessage());
        Assert.NotNull(back);
        Assert.True(back.Add);
        Assert.Equal(r.Paths, back.Paths);
        Assert.Null(OpenRequest.FromMessage("rm -rf\nC:\\x"));
        Assert.Null(OpenRequest.FromMessage(""));
    }

    [Fact]
    public void A_burst_of_launches_becomes_one_list_in_explorer_order()
    {
        var b = new OpenBatch();
        b.Add(new OpenRequest(false, [@"C:\Album\10 - Ten.mp3"]));
        b.Add(new OpenRequest(false, [@"C:\Album\2 - Two.mp3"]));
        b.Add(new OpenRequest(true, [@"C:\Other\x.mp3"]));
        b.Add(new OpenRequest(false, [@"C:\Album\1 - One.mp3", @"C:\album\2 - TWO.mp3"]));
        var (open, add) = b.Take();
        Assert.Equal([@"C:\Album\1 - One.mp3", @"C:\Album\2 - Two.mp3", @"C:\Album\10 - Ten.mp3"], open);
        Assert.Equal([@"C:\Other\x.mp3"], add);
        Assert.True(b.IsEmpty);
    }
}
