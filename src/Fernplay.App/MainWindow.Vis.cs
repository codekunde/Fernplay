using System.Windows.Controls;
using Fernplay.Core.Audio;

namespace Fernplay.App;

/// <summary>The display area (spectrum bars, oscilloscope, vectorscope, off) in every look, and its menus.</summary>
public partial class MainWindow
{
    private readonly float[] _waveL = new float[SpectrumTap.WaveFrames], _waveR = new float[SpectrumTap.WaveFrames];

    /// <summary>Called once from the constructor.</summary>
    private void SetUpVis()
    {
        AttachVis(Spectrum);
        AppMenu.Opened += (_, _) => FillDisplayMenu();
        FillDisplayMenu();
    }

    /// <summary>Wires a display area (Fernplay's, or a classic skin's main or windowshade one) to the settings and menus.</summary>
    private void AttachVis<T>(T display) where T : System.Windows.FrameworkElement, IVisDisplay
    {
        display.Apply(_settings.Vis);
        display.Clicked += d =>
        {
            _settings.Vis.Mode = VisCycle.Next(_settings.Vis.Mode, d.HasVectorscope);
            ApplyVis();
        };
        var menu = new ContextMenu();
        display.ContextMenu = menu;
        VisMenu.Fill(menu, _settings.Vis, display.HasVectorscope, ApplyVis);
        display.ContextMenuOpening += (_, _) => VisMenu.Fill(menu, _settings.Vis, display.HasVectorscope, ApplyVis);
    }

    /// <summary>The app menu's Display submenu, for the look in use (a classic skin has no vectorscope).</summary>
    private void FillDisplayMenu() => VisMenu.Fill(DisplayMenuItem, _settings.Vis, _classic is null, ApplyVis);

    /// <summary>The settings changed: every display area takes them (the hidden ones too, so a change of look shows them).</summary>
    private void ApplyVis()
    {
        Spectrum.Apply(_settings.Vis);
        if (_classic is { } v)
        {
            v.Spectrum.Apply(_settings.Vis);
            v.Shade.Spectrum.Apply(_settings.Vis);
        }
    }

    /// <summary>The display area on screen now; Fernplay's rolled-up strip has none.</summary>
    private IVisDisplay? ActiveVis => _classic is { } v ? (_shaded ? v.Shade.Spectrum : v.Spectrum) : _shaded ? null : Spectrum;

    /// <summary>Feeds the visible display area on the display timer; only the data its mode needs is fetched.</summary>
    private void AnimateVis()
    {
        if (ActiveVis is not { } vis) return;
        switch (vis.Mode)
        {
            case VisMode.Bars:
                vis.PushLevels(_c.Engine.GetSpectrum(vis.BandCount));
                break;
            case VisMode.Oscilloscope or VisMode.Vectorscope:
                vis.PushWave(_waveL, _waveR, _c.Engine.GetWave(_waveL, _waveR));
                break;
        }
    }
}
