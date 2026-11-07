using System.IO;
using System.Windows;
using Fernplay.Core;

namespace Fernplay.App;

public partial class AboutWindow : SkinDialog
{
    private string _downloadUrl = UpdateCheck.ReleasesPage;
    private ReleaseInfo? _latest;
    private CancellationTokenSource? _download;

    public AboutWindow()
    {
        InitializeComponent();
        AttachDragHandle(TitleBar);
        Logo.Source = AppIcon.Frame(64);
        VersionText.Text = $"Version {AppIcon.Version}";
        Closed += (_, _) => _download?.Cancel(); // closing About stops a download
    }

#if DEBUG
    // Debug builds only, for test runs: pretend to be an older version and/or the MSI-installed copy.
    private static string CurrentVersion => Environment.GetEnvironmentVariable("FERNPLAY_TEST_VERSION") is { Length: > 0 } v ? v : AppIcon.Version;
    private static bool TestInstalled => Environment.GetEnvironmentVariable("FERNPLAY_TEST_INSTALLED") == "1";
#else
    private static string CurrentVersion => AppIcon.Version;
    private static bool TestInstalled => false;
#endif

    /// <summary>This exe is the one the MSI installed, so the MSI can update it in place.</summary>
    private static bool IsInstalledCopy => TestInstalled || UpdateInstaller.IsInstalledCopy(AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), UserSettings.IsPortable,
        Environment.GetEnvironmentVariable("FERNPLAY_DATA"));

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        DownloadButton.Visibility = InstallButton.Visibility = Visibility.Collapsed;
        ShowUpdateText("Checking…");
        var result = await UpdateCheck.CheckAsync(CurrentVersion);
        if (!IsLoaded) return; // closed meanwhile
        UpdateButton.IsEnabled = true;
        if (result.Error is { } error)
        {
            ShowUpdateText($"Could not check for an update. {error}");
            return;
        }
        var latest = _latest = result.Latest!;
        if (!result.IsNewer)
        {
            ShowUpdateText($"You have the newest version ({CurrentVersion}).");
            return;
        }
        _downloadUrl = latest.PageUrl;
        UpdateButton.Visibility = Visibility.Collapsed; // the row has room for two buttons
        string news = $"Fernplay {latest.Version.ToString(3)} is out (you have {CurrentVersion}).";
        if (IsInstalledCopy && UpdateInstaller.Problem(latest) is null)
        {
            ShowUpdateText($"{news} UPDATE downloads it ({latest.Msi!.Size / 1048576.0:0} MB), closes Fernplay, installs it and starts Fernplay again. Your settings stay.");
            InstallButton.Visibility = Visibility.Visible;
            InstallButton.Focus();
        }
        else
        {
            // A portable or zip copy (an MSI would install a second copy), or a release without a usable installer.
            ShowUpdateText(IsInstalledCopy ? $"{news} {UpdateInstaller.Problem(latest)} DOWNLOAD opens the release page." : news);
            DownloadButton.Visibility = Visibility.Visible;
            DownloadButton.Focus();
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_latest is null) return;
        InstallButton.IsEnabled = false;
        var cts = _download = new CancellationTokenSource();
        var progress = new Progress<double>(f => ShowUpdateText($"Downloading Fernplay {_latest.Version.ToString(3)}… {f * 100:0} %"));
        string msi;
        try
        {
            ShowUpdateText($"Downloading Fernplay {_latest.Version.ToString(3)}…");
            msi = await UpdateInstaller.DownloadAsync(_latest, Path.Combine(Path.GetTempPath(), "Fernplay-update"), CurrentVersion, progress, ct: cts.Token);
        }
        catch (OperationCanceledException) { return; } // About was closed
        catch (UpdateException ex)
        {
            if (!IsLoaded) return;
            ShowUpdateText($"{ex.Message} DOWNLOAD opens the release page instead.");
            InstallButton.Visibility = Visibility.Collapsed;
            DownloadButton.Visibility = Visibility.Visible;
            return;
        }
        if (!IsLoaded) return;

        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Fernplay.exe");
        var script = UpdateInstaller.Script(Environment.ProcessId, msi, exe);
        DiagnosticLog.Write($"update: installing {Path.GetFileName(msi)} after Fernplay closes");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", UpdateInstaller.PowerShellArguments(script))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowUpdateText($"Could not start the installer ({ex.Message}). The installer is in {msi}.");
            InstallButton.IsEnabled = true;
            return;
        }
        ShowUpdateText("Installing… Fernplay closes now and starts again when the update is in.");
        // Closing the player saves the settings, playlist and panel places as usual; the installer waits for it.
        // About is modal: the player closes once this dialog has returned.
        var player = Application.Current.MainWindow;
        Close();
        player?.Dispatcher.BeginInvoke(() => player.Close());
    }

    private void ShowUpdateText(string text)
    {
        UpdateText.Text = text;
        UpdateText.Visibility = Visibility.Visible;
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_downloadUrl) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowUpdateText($"Could not open the browser. The page is {_downloadUrl}");
        }
    }
}
