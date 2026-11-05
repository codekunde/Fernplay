using System.Windows;

namespace Fernplay.App;

public partial class SettingsWindow : SkinDialog
{
    private readonly UserSettings _settings;
    private bool _ready; // false while the constructor (or a rollback) sets the boxes

    public SettingsWindow(UserSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        AttachDragHandle(TitleBar);
        OnTopCheck.IsChecked = settings.AlwaysOnTop;
        SnapCheck.IsChecked = settings.SnapPanels;
        RememberCheck.IsChecked = settings.RememberPlaylist;
        ExplorerCheck.IsChecked = App.Shell.IsRegistered();
        DefaultButton.IsEnabled = ExplorerCheck.IsChecked == true;
        (settings.SkinSize switch { 1 => Size1, 3 => Size3, _ => Size2 }).IsChecked = true;
        ShowSkin();
        _ready = true;
    }

    private static MainWindow? Player => Application.Current.MainWindow as MainWindow;

    private void ShowSkin()
    {
        var skin = Player?.Skin;
        SkinText.Text = skin is null
            ? "Fernplay's own look. Choose a classic skin (.wsz), or drop one on the player."
            : $"{skin.Name} (classic skin)" + (skin.Warnings.Count > 0 ? $"; {skin.Warnings.Count} part(s) missing in the skin" : "");
        FernplayLookButton.IsEnabled = skin is not null;
    }

    private void ChooseSkin_Click(object sender, RoutedEventArgs e)
    {
        try { System.IO.Directory.CreateDirectory(UserSettings.SkinsFolder); } catch (System.IO.IOException) { } catch (UnauthorizedAccessException) { }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a classic skin",
            Filter = "Classic skins (*.wsz, *.zip)|*.wsz;*.zip",
            InitialDirectory = UserSettings.SkinsFolder,
        };
        if (dialog.ShowDialog(this) == true && Player?.ApplySkinFile(dialog.FileName) == false)
            SkinText.Text = "That file is not a classic skin; the reason shows in the player's title display.";
        else ShowSkin();
    }

    private void FernplayLook_Click(object sender, RoutedEventArgs e)
    {
        Player?.UseFernplayLook();
        ShowSkin();
    }

    // Checked (not Click): UI Automation selects without a click.
    private void Size_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not FrameworkElement { Tag: string tag }) return;
        Player?.SetSkinSize(int.Parse(tag));
    }

    // Checked/Unchecked rather than Click (see Option_Changed).
    private void Explorer_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        bool on = ExplorerCheck.IsChecked == true;
        try
        {
            if (on) App.Shell.Register(); else App.Shell.Unregister();
            App.Shell.TurnedOff = !on; // the installer's /register on an update respects an untick
            ShellNotify.AssociationsChanged();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            _ready = false;
            ExplorerCheck.IsChecked = App.Shell.IsRegistered();
            _ready = true;
        }
        DefaultButton.IsEnabled = ExplorerCheck.IsChecked == true;
    }

    private void Default_Click(object sender, RoutedEventArgs e) => ShellNotify.OpenDefaultApps();

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
