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
        GeneralTab.IsChecked = true;
        OnTopCheck.IsChecked = settings.AlwaysOnTop;
        SnapCheck.IsChecked = settings.SnapPanels;
        RememberCheck.IsChecked = settings.RememberPlaylist;
        ExplorerCheck.IsChecked = App.Shell.IsRegistered();
        DefaultButton.IsEnabled = ExplorerCheck.IsChecked == true;
        (settings.SkinSize switch { 1 => Size1, 3 => Size3, _ => Size2 }).IsChecked = true;
        XfadeSlider.Value = Player?.CrossfadeSeconds ?? settings.CrossfadeSeconds;
        ShowXfadeLength();
        ((Player?.Levelling ?? settings.Levelling) switch
        {
            Fernplay.Core.Audio.LevelMode.Track => LevelTrack,
            Fernplay.Core.Audio.LevelMode.Album => LevelAlbum,
            _ => LevelOff,
        }).IsChecked = true;
        ShowSkin();
        RemoteCheck.IsChecked = App.Remote?.IsOn == true;
        _pairTimer.Tick += (_, _) => ShowPairing();
        if (App.Remote is { } remote)
        {
            remote.StatusChanged += OnRemoteStatus;
            remote.Pairing.Closed += OnPairingClosed;
            Closed += (_, _) =>
            {
                remote.StatusChanged -= OnRemoteStatus;
                remote.Pairing.Closed -= OnPairingClosed;
                _pairTimer.Stop();
                remote.Pairing.Close(); // a code on screen only while Settings is open
            };
        }
        ShowRemote();
        _ready = true;
    }

    // ================= Phone remote =================

    private readonly System.Windows.Threading.DispatcherTimer _pairTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _pairUntil;
    private string? _pairNote;
    private string? _shownPhones;

    private sealed record PhoneRow(string Id, string Text, string RemoveName);

    private void OnRemoteStatus(object? sender, EventArgs e) => ShowRemote();

    private void OnPairingClosed(object? sender, Fernplay.Core.Remote.PairingResult result) => Dispatcher.BeginInvoke(() =>
    {
        _pairTimer.Stop();
        _pairNote = result switch
        {
            Fernplay.Core.Remote.PairingResult.Paired => "Paired. The phone can now control Fernplay.",
            Fernplay.Core.Remote.PairingResult.TooManyTries => "Five wrong codes: pairing stopped. Try again with a new code.",
            Fernplay.Core.Remote.PairingResult.Expired => "The code ran out. Pair again for a new one.",
            _ => null,
        };
        ShowRemote();
    });

    // Checked/Unchecked (not Click): UI Automation toggles without a click.
    private void Remote_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || App.Remote is not { } remote) return;
        bool on = RemoteCheck.IsChecked == true;
        if (on && !remote.TurnOn())
        {
            _ready = false;
            RemoteCheck.IsChecked = false;
            _ready = true;
        }
        else if (!on) remote.TurnOff();
        _settings.RemoteOn = remote.IsOn;
        _pairNote = null;
        ShowRemote();
    }

    private void Pair_Click(object sender, RoutedEventArgs e)
    {
        if (App.Remote is not { IsOn: true } remote) return;
        if (remote.Pairing.IsOpen) { remote.Pairing.Close(); _pairTimer.Stop(); _pairNote = null; ShowRemote(); return; }
        var code = remote.Pairing.Open(RemoteControl.PairingTime);
        _pairUntil = DateTime.UtcNow + RemoteControl.PairingTime;
        PairCode.Text = $"{code[..3]} {code[3..]}";
        _pairNote = null;
        _pairTimer.Start();
        ShowRemote();
    }

    private void RemovePhone_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) App.Remote?.RemovePhone(id);
    }

    private void ShowPairing()
    {
        var left = _pairUntil - DateTime.UtcNow;
        if (left <= TimeSpan.Zero || App.Remote?.Pairing.IsOpen != true)
        {
            _pairTimer.Stop();
            if (App.Remote?.Pairing.IsOpen != true && _pairNote is null && left <= TimeSpan.Zero) _pairNote = "The code ran out. Pair again for a new one.";
            ShowRemote();
            return;
        }
        PairTime.Text = $"{(int)left.TotalMinutes}:{left.Seconds:00} LEFT";
    }

    private void ShowRemote()
    {
        var remote = App.Remote;
        bool on = remote?.IsOn == true;
        bool pairing = on && remote!.Pairing.IsOpen;
        PairButton.IsEnabled = on;
        PairButton.Content = pairing ? "CANCEL PAIRING" : "PAIR A PHONE…";
        PairCode.Visibility = PairTime.Visibility = pairing ? Visibility.Visible : Visibility.Collapsed;
        if (pairing) ShowPairingTime();

        string text;
        if (remote?.Problem is { } problem) text = problem;
        else if (!on) text = "Off. When on, phones on the same Wi-Fi that you pair here can play, pause, skip, change the volume and pick from the playlist.";
        else if (pairing) text = "In the Fernplay app on your phone: Home → Fernplay on your PC → this PC, then type the code.";
        else
        {
            var addresses = RemoteControl.Addresses();
            var connected = remote!.Connected;
            text = $"On. This PC: {(addresses.Count > 0 ? string.Join(", ", addresses) : "no network")}, port {remote.Port}."
                + (connected.Count > 0 ? $" Connected: {string.Join(", ", connected)}." : "");
        }
        RemoteStatus.Text = _pairNote is null ? text : $"{_pairNote} {text}";

        // The rows are rebuilt only when the phones change: a new list of equal rows on every status update makes
        // UI Automation (and so Narrator) lose the rows and their REMOVE buttons.
        var phones = remote?.Phones ?? [];
        var key = string.Join("|", phones.Select(p => p.Id + p.Name));
        if (key != _shownPhones)
        {
            _shownPhones = key;
            PhonesList.ItemsSource = phones.Select(p => new PhoneRow(p.Id, $"{p.Name} (paired {p.Paired.ToLocalTime():d MMM yyyy})", $"Remove {p.Name}")).ToList();
        }
        PhonesList.Visibility = phones.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowPairingTime()
    {
        var left = _pairUntil - DateTime.UtcNow;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        PairTime.Text = $"{(int)left.TotalMinutes}:{left.Seconds:00} LEFT";
    }

    private void Xfade_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (XfadeText is null) return; // during InitializeComponent
        ShowXfadeLength();
        if (_ready) Player?.SetCrossfadeSeconds((int)Math.Round(XfadeSlider.Value));
    }

    private void ShowXfadeLength()
    {
        XfadeText.Text = $"{(int)Math.Round(XfadeSlider.Value)} S";
        System.Windows.Automation.AutomationProperties.SetItemStatus(XfadeSlider, $"{(int)Math.Round(XfadeSlider.Value)} seconds");
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

    // Checked (not Click): UI Automation selects without a click. The other page is Hidden, so the window keeps its height.
    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (GeneralPage is null || AdvancedPage is null) return; // during InitializeComponent
        bool general = GeneralTab.IsChecked == true;
        GeneralPage.Visibility = general ? Visibility.Visible : Visibility.Hidden;
        AdvancedPage.Visibility = general ? Visibility.Hidden : Visibility.Visible;
    }

    // Checked (not Click): UI Automation selects without a click.
    private void Level_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || Player is not { } player || sender is not FrameworkElement { Tag: string tag }) return;
        var mode = Enum.Parse<Fernplay.Core.Audio.LevelMode>(tag);
        player.Levelling = mode;
        _settings.Levelling = mode;
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
