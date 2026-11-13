using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Fernplay.App.Visualizer;
using Fernplay.Core.Audio;

namespace Fernplay.App;

/// <summary>
/// The visualizer: MilkDrop presets drawn by projectM from what Fernplay plays (after the EQ, before the volume).
/// Its own window, resizable, with full screen. Presets come from a folder the user picks; Fernplay ships none.
/// Keys while it is in front: N next preset, P or Backspace back, L lock, F / F11 / Alt+Enter / double-click full
/// screen, Esc leaves full screen; the player's keys (Z X C V B, Space…) work too.
/// </summary>
public partial class VisualizerWindow : SkinWindow
{
    public const double DefaultWidth = 380, DefaultHeight = 240, MinSkinWidth = 200, MinSkinHeight = 120;

    private readonly PcmRing _pcm;
    private readonly UserSettings _settings;
    private VisualizerView? _view;
    private bool _ready, _full, _failed;
    private Size _sizeBeforeFull;

    public VisualizerWindow(PcmRing pcm, UserSettings settings)
    {
        _pcm = pcm;
        _settings = settings;
        InitializeComponent();
        AttachDragHandle(TitleBar);
        MenuIcon.Source = AppIcon.Frame(16);
        VisualizerSize = new Size(settings.VisualizerWidth, settings.VisualizerHeight);
        LockToggle.IsChecked = settings.VisualizerLocked;
        PreviewKeyDown += OnKey;
        IsVisibleChanged += (_, _) => UpdateRunning();
        StateChanged += (_, _) => UpdateRunning();
        Loaded += (_, _) => { _ready = true; Start(); };
        Closed += (_, _) => _pcm.Enabled = false;
    }

    /// <summary>Size of the window in Fernplay skin pixels (remembered in the settings); 0 = default.</summary>
    public Size VisualizerSize
    {
        get => _full ? _sizeBeforeFull : new Size(Shell.Width / Scale, Shell.Height / Scale);
        set
        {
            double w = value.Width > 0 ? Math.Max(value.Width, MinSkinWidth) : DefaultWidth;
            double h = value.Height > 0 ? Math.Max(value.Height, MinSkinHeight) : DefaultHeight;
            Shell.Width = Math.Round(w * Scale);
            Shell.Height = Math.Round(h * Scale);
        }
    }

    public bool IsFullScreen => _full;

    // ================= Picture =================

    /// <summary>Starts projectM on the saved preset folder, or asks for one.</summary>
    private void Start()
    {
        if (_failed || _view is not null) return;
        if (_settings.VisualizerFolder is not { } folder || !Directory.Exists(folder))
        {
            ShowMessage("Choose a folder with MilkDrop presets (.milk files). Fernplay comes without presets: MilkDrop 2 and 3, " +
                        "Winamp and projectM's preset packs have them.", folderButton: true);
            return;
        }
        var view = _view = new VisualizerView(_pcm, folder, PresetSeconds, LockToggle.IsChecked == true, _settings.VisualizerShuffle);
        view.PresetChanged += (index, name) => { _showing = index; PresetText.Text = name; };
        view.MenuWanted += () => OpenMenu(atMouse: true);
        view.NoPresets += f => PresetText.Text = $"No presets in {Path.GetFileName(f)}";
        view.Failed += reason =>
        {
            _failed = true;
            RemoveView();
            ShowMessage(reason, folderButton: false);
        };
        view.DoubleClicked += () => SetFullScreen(!_full);
        view.FocusWanted += FocusPicture;
        MessagePanel.Visibility = Visibility.Collapsed;
        Picture.Children.Add(view);
        UpdateRunning();
    }

    private void RemoveView()
    {
        if (_view is null) return;
        Picture.Children.Remove(_view);
        _view.Dispose(); // stops the render thread, frees projectM and the GL context
        _view = null;
        UpdateRunning();
    }

    private void ShowMessage(string text, bool folderButton)
    {
        MessageText.Text = text;
        MessageFolderButton.Visibility = folderButton ? Visibility.Visible : Visibility.Collapsed;
        MessagePanel.Visibility = Visibility.Visible;
        PresetText.Text = "";
        foreach (var b in new UIElement[] { BackButton, NextButton, LockToggle }) b.IsEnabled = false;
    }

    /// <summary>Samples are kept and frames drawn only while the picture can be seen.</summary>
    private void UpdateRunning()
    {
        bool seen = IsVisible && WindowState != WindowState.Minimized && _view is not null;
        _pcm.Enabled = seen;
        if (_view is not null) _view.Paused = !seen;
        foreach (var b in new UIElement[] { BackButton, NextButton, LockToggle }) b.IsEnabled = _view is not null;
    }

    private double PresetSeconds => Fernplay.Core.VisualizerTimes.Clamp(_settings.VisualizerPresetSeconds);

    /// <summary>Settings changed the time each preset shows: the next change comes after the new time.</summary>
    public void ApplyPresetSeconds() => _view?.SetPresetSeconds(PresetSeconds);

    // ================= Controls =================

    private void Next_Click(object sender, RoutedEventArgs e) => _view?.Next();
    private void Back_Click(object sender, RoutedEventArgs e) => _view?.Back();
    private void Full_Click(object sender, RoutedEventArgs e) => SetFullScreen(!_full);

    // Checked/Unchecked rather than Click: Narrator and other UI Automation clients toggle without a click.
    private void Lock_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.VisualizerLocked = LockToggle.IsChecked == true;
        _view?.SetLocked(_settings.VisualizerLocked);
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Folder with MilkDrop presets (.milk); its subfolders are included",
            InitialDirectory = _settings.VisualizerFolder is { } f && Directory.Exists(f) ? f : KnownPresetFolder() ?? "",
        };
        if (dialog.ShowDialog(this) != true) return;
        _settings.VisualizerFolder = dialog.FolderName;
        _settings.Save();
        PresetText.Text = "";
        _showing = -1;
        if (_view is not null) _view.SetFolder(dialog.FolderName);
        else Start();
    }

    /// <summary>A preset folder of an installed Winamp/MilkDrop, to open the picker there; null if none is found.</summary>
    private string? KnownPresetFolder()
    {
        var candidates = new List<string>();
        if (_settings.VisualizerPath is { } milkdrop && Path.GetDirectoryName(milkdrop) is { } md) candidates.Add(Path.Combine(md, "presets"));
        foreach (var pf in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            candidates.Add(Path.Combine(Environment.GetFolderPath(pf), "Winamp", "Plugins", "Milkdrop2", "presets"));
        return candidates.FirstOrDefault(Directory.Exists);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        // The chooser's keys come through here too (a popup's events go on to the window); while it is open, keys are
        // for it: the search box takes letters and Space, so the player's keys are left alone (App skips a text box).
        if (Chooser.IsOpen)
        {
            ChooserKey(e, key, mods);
            return;
        }
        bool used = true;
        if (key == Key.F && mods == ModifierKeys.Control) OpenChooser();
        else if (key == Key.Enter && mods == ModifierKeys.Alt) SetFullScreen(!_full);
        else if (key == Key.Escape && mods == ModifierKeys.None && _full) SetFullScreen(false);
        else if (mods != ModifierKeys.None) used = false;
        else switch (key)
        {
            case Key.N: _view?.Next(); break;
            case Key.P or Key.Back: _view?.Back(); break;
            case Key.L when _view is not null: LockToggle.IsChecked = LockToggle.IsChecked != true; break;
            case Key.F or Key.F11: SetFullScreen(!_full); break;
            default: used = false; break;
        }
        if (used)
        {
            e.Handled = true; // the rest goes on to the player's keys (App)
            FocusRing.KeyWasShortcut(this);
        }
    }

    // ================= Menu =================

    private bool _syncingMenu;

    private void MenuButton_Click(object sender, RoutedEventArgs e) => OpenMenu(atMouse: false);

    private void Picture_RightClick(object sender, MouseButtonEventArgs e)
    {
        OpenMenu(atMouse: true); // the message in place of the picture (WPF); the picture itself is the child window
        e.Handled = true;
    }

    private void OpenMenu(bool atMouse)
    {
        VisMenu.PlacementTarget = atMouse ? PictureFrame : MenuButton;
        VisMenu.Placement = atMouse ? PlacementMode.MousePoint : PlacementMode.Bottom;
        VisMenu.IsOpen = true;
    }

    private void VisMenu_Opened(object sender, RoutedEventArgs e)
    {
        _syncingMenu = true;
        ShuffleMenuItem.IsChecked = _settings.VisualizerShuffle;
        LockMenuItem.IsChecked = LockToggle.IsChecked == true;
        OnTopMenuItem.IsChecked = _settings.VisualizerOnTop;
        _syncingMenu = false;
        foreach (var item in new[] { ChooseMenuItem, ShuffleMenuItem, LockMenuItem }) item.IsEnabled = _view is not null;
        FullMenuItem.Header = _full ? "Leave full screen   Esc" : "Full screen   F";
    }

    // Checked/Unchecked rather than Click: Narrator and other UI Automation clients toggle without a click.
    private void ShuffleMenu_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingMenu) return;
        _settings.VisualizerShuffle = ShuffleMenuItem.IsChecked;
        _view?.SetShuffle(_settings.VisualizerShuffle);
    }

    private void LockMenu_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingMenu) return;
        LockToggle.IsChecked = LockMenuItem.IsChecked; // Lock_Changed saves it and tells projectM
    }

    private void OnTopMenu_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingMenu) return;
        _settings.VisualizerOnTop = OnTopMenuItem.IsChecked;
        if (!_full) Topmost = _settings.VisualizerOnTop; // full screen is always on top; leaving it applies the setting
    }

    private void Choose_Click(object sender, RoutedEventArgs e) => OpenChooser();

    // ================= Preset chooser =================

    /// <summary>A preset in the chooser: its place in projectM's playlist (A–Z), name without .milk, full path.</summary>
    private sealed record PresetEntry(int Index, string Name, string Path);

    private List<PresetEntry> _presets = [];
    private System.ComponentModel.ICollectionView? _presetView;
    private int _showing = -1;       // place of the preset showing (from projectM's switched callback)
    private bool _quietSelection;    // a selection made by the code (filtering, opening) shows nothing

    private void OpenChooser()
    {
        if (_view is null || Chooser.IsOpen) return;
        _view.RequestPresets((items, position) =>
        {
            if (_view is null) return;
            _presets = items.Select((p, i) => new PresetEntry(i, System.IO.Path.GetFileNameWithoutExtension(p), p)).ToList();
            _presetView = System.Windows.Data.CollectionViewSource.GetDefaultView(_presets);
            _presetView.Filter = o => Matches((PresetEntry)o, SearchBox.Text);
            _quietSelection = true;
            PresetList.ItemsSource = _presets;
            SearchBox.Text = "";
            _showing = position;
            SelectQuietly(_presets.ElementAtOrDefault(position));
            UpdateCount();

            // Over the picture's top left corner, as tall as the picture allows.
            double pictureHeight = PictureFrame.ActualHeight / Scale; // skin pixels
            PresetList.Height = Math.Clamp(pictureHeight - 40, 60, 320);
            Chooser.PlacementTarget = PictureFrame;
            Chooser.HorizontalOffset = 4 * Scale;
            Chooser.VerticalOffset = 4 * Scale;
            Chooser.IsOpen = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                Keyboard.Focus(SearchBox);
                if (PresetList.SelectedItem is { } current) PresetList.ScrollIntoView(current);
            });
        });
    }

    /// <summary>Every word typed is in the name (any order, any case).</summary>
    private static bool Matches(PresetEntry p, string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase));

    private void Search_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_presetView is null) return;
        var keep = PresetList.SelectedItem as PresetEntry;
        _presetView.Refresh();
        // Keep the selection when it still matches; otherwise the first match, ready for Enter (nothing shown yet).
        SelectQuietly(keep is not null && Matches(keep, SearchBox.Text) ? keep : _presetView.Cast<PresetEntry>().FirstOrDefault());
        UpdateCount();
    }

    private void UpdateCount()
    {
        int shown = _presetView?.Cast<object>().Count() ?? 0;
        ChooserCount.Text = shown == _presets.Count ? $"{_presets.Count}" : $"{shown} of {_presets.Count}";
    }

    private void SelectQuietly(PresetEntry? entry)
    {
        _quietSelection = true;
        PresetList.SelectedItem = entry;
        if (entry is not null) PresetList.ScrollIntoView(entry);
        _quietSelection = false;
    }

    private void PresetList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_quietSelection || PresetList.SelectedItem is not PresetEntry p) return;
        ShowPreset(p);
    }

    private void ShowPreset(PresetEntry p)
    {
        if (p.Index == _showing) return;
        _showing = p.Index;
        _view?.Play(p.Index);
    }

    private void PresetList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PresetList.SelectedItem is PresetEntry p) ShowPreset(p);
        Chooser.IsOpen = false;
    }

    private void ChooserKey(KeyEventArgs e, Key key, ModifierKeys mods)
    {
        if (mods != ModifierKeys.None && !(mods == ModifierKeys.Control && key == Key.F)) return;
        switch (key)
        {
            case Key.Escape:
            case Key.F when mods == ModifierKeys.Control:
                Chooser.IsOpen = false;
                break;
            case Key.Enter:
                if (PresetList.SelectedItem is PresetEntry p) ShowPreset(p);
                Chooser.IsOpen = false;
                break;
            case Key.Up or Key.Down or Key.PageUp or Key.PageDown:
                int count = PresetList.Items.Count;
                if (count == 0) break;
                int page = Math.Max(1, (int)(PresetList.ActualHeight / 9) - 1);
                int step = key switch { Key.Up => -1, Key.Down => 1, Key.PageUp => -page, _ => page };
                int at = PresetList.SelectedIndex < 0 ? (step > 0 ? -1 : count) : PresetList.SelectedIndex;
                PresetList.SelectedIndex = Math.Clamp(at + step, 0, count - 1); // shows it (SelectionChanged)
                PresetList.ScrollIntoView(PresetList.SelectedItem);
                break;
            default:
                // Typing while the list has the focus (after a click): the letters go to the search box.
                if (!SearchBox.IsKeyboardFocused && key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or Key.Space or Key.Back)
                    Keyboard.Focus(SearchBox);
                return; // not handled: the text box (or the list) gets the key
        }
        e.Handled = true;
    }

    private void Chooser_Closed(object sender, EventArgs e)
    {
        PresetList.ItemsSource = null; // free the list until next time
        _presetView = null;
        _presets = [];
        FocusPicture();
    }

    // ================= Full screen =================

    /// <summary>Full screen on the screen the window is on: maximised without the frame and bars, over the taskbar.</summary>
    public void SetFullScreen(bool on)
    {
        if (on == _full) return;
        if (on)
        {
            _sizeBeforeFull = VisualizerSize;
            _full = true;
            TitleBar.Visibility = BottomBar.Visibility = Visibility.Collapsed;
            PictureFrame.Margin = new Thickness(0);
            PictureFrame.BorderThickness = Root.BorderThickness = new Thickness(0);
            SizeToContent = SizeToContent.Manual;
            Shell.Width = Shell.Height = double.NaN;
            Topmost = true;
            WindowState = WindowState.Maximized; // borderless and not resizable: covers the taskbar
            Activate();
        }
        else
        {
            _full = false;
            WindowState = WindowState.Normal;
            Topmost = _settings.VisualizerOnTop;
            TitleBar.Visibility = BottomBar.Visibility = Visibility.Visible;
            PictureFrame.Margin = new Thickness(4, 3, 4, 0);
            PictureFrame.BorderThickness = Root.BorderThickness = new Thickness(0.5);
            VisualizerSize = _sizeBeforeFull;
            SizeToContent = SizeToContent.WidthAndHeight;
        }
        // A button that had the focus (FULL SCREEN, NEXT…) is hidden now or was: WPF would be left with no focused
        // element and drop every key, Esc included.
        FocusPicture();
        DiagnosticLog.Write($"visualizer full screen {(on ? "on" : "off")}");
    }

    /// <summary>Keyboard focus to the picture's frame (also gives the window the Win32 focus back from the child).</summary>
    private void FocusPicture() => Keyboard.Focus(PictureFrame);

    // ================= Resize =================

    private Point _gripStart;
    private Size _gripStartSize; // DIPs

    private void Grip_DragStarted(object sender, DragStartedEventArgs e)
    {
        _gripStart = Mouse.GetPosition(this); // window coordinates: the top left stays put
        _gripStartSize = new Size(Shell.Width, Shell.Height);
        if (_view is not null) _view.Resizing = true; // no frames while dragging: the size is taken at the end
    }

    private void Grip_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_view is not null) _view.Resizing = false;
    }

    private void Grip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var p = Mouse.GetPosition(this);
        // Limit: the whole desktop (all screens); never forced below the size at the start of the drag.
        var desk = Desktop;
        double minW = MinSkinWidth * Scale, minH = MinSkinHeight * Scale;
        double maxW = Math.Max(Math.Max(minW, _gripStartSize.Width), desk.Right - Left);
        double maxH = Math.Max(Math.Max(minH, _gripStartSize.Height), desk.Bottom - Top);
        Shell.Width = Math.Round(Math.Clamp(_gripStartSize.Width + p.X - _gripStart.X, minW, maxW));
        Shell.Height = Math.Round(Math.Clamp(_gripStartSize.Height + p.Y - _gripStart.Y, minH, maxH));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
