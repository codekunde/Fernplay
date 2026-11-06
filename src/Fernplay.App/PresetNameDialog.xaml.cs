using System.Windows;
using Fernplay.Core.Audio;

namespace Fernplay.App;

/// <summary>Asks for the name of a new EQ preset; says when the name replaces a saved one or cannot be used.</summary>
public partial class PresetNameDialog : SkinDialog
{
    private readonly IReadOnlyList<EqPreset> _saved;

    public PresetNameDialog(IReadOnlyList<EqPreset> saved)
    {
        _saved = saved;
        InitializeComponent();
        AttachDragHandle(TitleBar);
        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>The name typed (trimmed), once Save was pressed.</summary>
    public string PresetName => NameBox.Text.Trim();

    private void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var problem = UserEqPresets.NameProblem(NameBox.Text);
        bool replaces = problem is null && _saved.Any(p => string.Equals(p.Name, PresetName, StringComparison.OrdinalIgnoreCase));
        // An empty box needs no message: the disabled Save says enough.
        string? note = NameBox.Text.Trim().Length == 0 ? null : problem ?? (replaces ? $"Replaces your preset {PresetName}." : null);
        NoteText.Text = note ?? "";
        NoteText.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.IsEnabled = problem is null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (UserEqPresets.NameProblem(NameBox.Text) is not null) return;
        DialogResult = true;
    }
}
