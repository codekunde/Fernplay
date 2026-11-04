using System.Windows;
using Fernplay.Core;

namespace Fernplay.App;

public partial class AboutWindow : SkinDialog
{
    private string _downloadUrl = UpdateCheck.ReleasesPage;

    public AboutWindow()
    {
        InitializeComponent();
        AttachDragHandle(TitleBar);
        Logo.Source = AppIcon.Frame(64);
        VersionText.Text = $"Version {AppIcon.Version}";
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        DownloadButton.Visibility = Visibility.Collapsed;
        ShowUpdateText("Checking…");
        var result = await UpdateCheck.CheckAsync(AppIcon.Version);
        if (!IsLoaded) return; // closed meanwhile
        UpdateButton.IsEnabled = true;
        if (result.Error is { } error)
        {
            ShowUpdateText($"Could not check for an update. {error}");
            return;
        }
        var latest = result.Latest!;
        if (result.IsNewer)
        {
            _downloadUrl = latest.PageUrl;
            ShowUpdateText($"Fernplay {latest.Version.ToString(3)} is out (you have {AppIcon.Version}).");
            UpdateButton.Visibility = Visibility.Collapsed; // the row has room for two buttons
            DownloadButton.Visibility = Visibility.Visible;
            DownloadButton.Focus();
        }
        else ShowUpdateText($"You have the newest version ({AppIcon.Version}).");
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
