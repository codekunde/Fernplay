using System.Windows;

namespace Fernplay.App;

public partial class SettingsWindow : SkinDialog
{
    private readonly UserSettings _settings;
    private readonly bool _ready; // false while the constructor sets the boxes from the saved settings

    public SettingsWindow(UserSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        AttachDragHandle(TitleBar);
        OnTopCheck.IsChecked = settings.AlwaysOnTop;
        SnapCheck.IsChecked = settings.SnapPanels;
        RememberCheck.IsChecked = settings.RememberPlaylist;
        _ready = true;
    }

    // Checked/Unchecked rather than Click: Narrator and other UI Automation clients toggle without a click.
    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.AlwaysOnTop = OnTopCheck.IsChecked == true;
        _settings.SnapPanels = SnapCheck.IsChecked == true;
        _settings.RememberPlaylist = RememberCheck.IsChecked == true;
        App.ApplyWindowSettings(_settings);
    }
}
