using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fernplay.Core.Audio;

namespace Fernplay.App;

public partial class EqWindow : SkinWindow
{
    private readonly Equalizer _eq;
    private readonly Slider[] _bands = new Slider[10];
    private bool _syncing;

    private readonly List<EqPreset> _userPresets;

    /// <summary>A preset was saved or deleted (the list given to the constructor changed): save the settings.</summary>
    public event Action? PresetsChanged;

    public EqWindow(Equalizer eq, List<EqPreset> userPresets)
    {
        _eq = eq;
        _userPresets = userPresets;
        InitializeComponent();
        AttachDragHandle(TitleBar);

        for (int i = 0; i < 10; i++)
        {
            int band = i;
            double hz = Equalizer.BandFrequencies[i];
            string label = hz >= 1000 ? $"{hz / 1000:0}K" : $"{hz:0}";
            var s = new Slider { Style = (Style)FindResource("EqSlider"), HorizontalAlignment = HorizontalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(s, hz >= 1000 ? $"{hz / 1000:0} kHz band" : $"{hz:0} Hz band");
            s.ValueChanged += (_, e) => { if (!_syncing) _eq.SetBand(band, e.NewValue); };
            SliderReset.OnDoubleClick(s, 0);
            _bands[i] = s;
            BandSliders.Children.Add(s);
            BandLabels.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("SkinLabel"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom });
        }

        SliderReset.OnDoubleClick(PreampSlider, 0);

        // Built again after a save or delete (both looks share this menu).
        PresetsChanged += () => Dispatcher.BeginInvoke(BuildPresetsMenu); // not while the menu is still closing
        BuildPresetsMenu();

        _eq.Changed += (_, _) => Sync();
        Sync();
        Activated += (_, _) => ClassicActivation(true);
        Deactivated += (_, _) => ClassicActivation(false);
        DpiChanged += (_, _) => ApplyClassicScale();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    // Checked/Unchecked rather than Click, so a screen reader's toggle works too.
    private void On_Changed(object sender, RoutedEventArgs e)
    {
        if (!_syncing) _eq.Enabled = OnToggle.IsChecked == true;
    }

    private void Presets_Click(object sender, RoutedEventArgs e)
    {
        PresetsMenu.PlacementTarget = PresetsButton;
        PresetsMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        PresetsMenu.IsOpen = true;
    }

    /// <summary>Your presets, then the built-in ones, then Save current as… and Delete (your presets only).</summary>
    private void BuildPresetsMenu()
    {
        PresetsMenu.Items.Clear();
        MenuItem Item(string header, Action click)
        {
            // Menu headers take "_" as an access-key marker (my_rock showed as "myrock" with the r underlined):
            // doubled, a preset name shows and is read as it is.
            var item = new MenuItem { Header = header.Replace("_", "__") };
            item.Click += (_, _) => click();
            return item;
        }
        foreach (var preset in _userPresets) PresetsMenu.Items.Add(Item(preset.Name, () => _eq.Apply(preset)));
        if (_userPresets.Count > 0) PresetsMenu.Items.Add(new Separator());
        foreach (var preset in EqPreset.BuiltIn) PresetsMenu.Items.Add(Item(preset.Name, () => _eq.Apply(preset)));
        PresetsMenu.Items.Add(new Separator());
        PresetsMenu.Items.Add(Item("Save current as…", SaveCurrentPreset));
        var delete = new MenuItem { Header = "Delete preset", IsEnabled = _userPresets.Count > 0 };
        foreach (var preset in _userPresets)
            delete.Items.Add(Item(preset.Name, () =>
            {
                if (UserEqPresets.Delete(_userPresets, preset.Name)) PresetsChanged?.Invoke();
            }));
        PresetsMenu.Items.Add(delete);
    }

    private void SaveCurrentPreset()
    {
        var dialog = new PresetNameDialog(_userPresets) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        UserEqPresets.Save(_userPresets, UserEqPresets.FromCurrent(dialog.PresetName, _eq));
        PresetsChanged?.Invoke();
    }

    private void Preamp_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncing) _eq.Preamp = e.NewValue;
    }

    /// <summary>Shows the EQ's values on the sliders, colours each bar by its gain, and redraws the curve.</summary>
    private void Sync()
    {
        _syncing = true;
        OnToggle.IsChecked = _eq.Enabled;
        PreampSlider.Value = _eq.Preamp;
        Paint(PreampSlider);
        for (int i = 0; i < 10; i++)
        {
            _bands[i].Value = _eq.GetBand(i);
            Paint(_bands[i]);
        }
        Curve.Show(_eq);
        ClassicSync();
        _syncing = false;
    }

    /// <summary>Bar colour by gain: green when cut, yellow around 0 dB, orange to red when boosted.</summary>
    private static void Paint(Slider s)
    {
        double t = (s.Value + Equalizer.MaxDb) / (2 * Equalizer.MaxDb); // 0..1
        Color c = t < 0.5
            ? Mix(Color.FromRgb(0x2F, 0xB8, 0x45), Color.FromRgb(0xE3, 0xCF, 0x4A), t * 2)
            : Mix(Color.FromRgb(0xE3, 0xCF, 0x4A), Color.FromRgb(0xE8, 0x4A, 0x2E), (t - 0.5) * 2);
        s.Foreground = new SolidColorBrush(c);
        s.ToolTip = $"{(s.Value > 0 ? "+" : "")}{s.Value:0.#} dB";
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
