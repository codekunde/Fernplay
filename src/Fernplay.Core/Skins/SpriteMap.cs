namespace Fernplay.Core.Skins;

/// <summary>One rectangle on one sprite sheet (sheet = file name without ".bmp", upper case).</summary>
public sealed record Sprite(string Sheet, int X, int Y, int W, int H);

/// <summary>
/// Where each part of the classic player, equalizer and playlist sits on its sprite sheet.
/// Coordinates follow the classic skin layout as documented publicly and in Webamp's sprite table (MIT, see
/// THIRD-PARTY-NOTICES). A skin whose sheet is smaller than a rectangle simply has no such sprite.
/// </summary>
public static class SpriteMap
{
    private static readonly Dictionary<string, Sprite> _all = [];

    /// <summary>Every sprite by name ("Main.Background", "Buttons.Play.Pressed", ...), for drawing and for the dump tool.</summary>
    public static IReadOnlyDictionary<string, Sprite> All => _all;

    public static Sprite Get(string name) => _all[name];

    private static void Add(string name, string sheet, int x, int y, int w, int h) => _all[name] = new Sprite(sheet, x, y, w, h);

    /// <summary>A button with a normal and a pressed sprite.</summary>
    private static void Pair(string name, string sheet, int x, int y, int w, int h, int px, int py)
    {
        Add(name, sheet, x, y, w, h);
        Add(name + ".Pressed", sheet, px, py, w, h);
    }

    public const int VolumeSteps = 28;
    public const int EqSliderSteps = 28;

    public static Sprite VolumeBackground(int step) => _all[$"Volume.Background.{Math.Clamp(step, 0, VolumeSteps - 1)}"];
    public static Sprite BalanceBackground(int step) => _all[$"Balance.Background.{Math.Clamp(step, 0, VolumeSteps - 1)}"];
    public static Sprite EqSliderBackground(int step) => _all[$"Eq.Slider.{Math.Clamp(step, 0, EqSliderSteps - 1)}"];

    static SpriteMap()
    {
        // "General" windows (the library): GEN.BMP. Title pieces 25x20, active row 0 and inactive row 21.
        string[] genTop = ["TopLeft", "TopLeftEnd", "TopCenterFill", "TopRightEnd", "TopFill", "TopRight"];
        for (int i = 0; i < genTop.Length; i++)
        {
            Add($"Gen.{genTop[i]}.Active", "GEN", i * 26, 0, 25, 20);
            Add($"Gen.{genTop[i]}.Inactive", "GEN", i * 26, 21, 25, 20);
        }
        Add("Gen.BottomLeft", "GEN", 0, 42, 125, 14);
        Add("Gen.BottomRight", "GEN", 0, 57, 125, 14);
        Add("Gen.BottomFill", "GEN", 127, 72, 25, 14);
        Add("Gen.MiddleLeft", "GEN", 127, 42, 11, 29);
        Add("Gen.MiddleLeftBottom", "GEN", 158, 42, 11, 24);
        Add("Gen.MiddleRight", "GEN", 139, 42, 8, 29);
        Add("Gen.MiddleRightBottom", "GEN", 170, 42, 8, 24);
        Add("Gen.Close.Pressed", "GEN", 148, 42, 9, 9);

        // Player background.
        Add("Main.Background", "MAIN", 0, 0, 275, 116);

        // Title bar: active / inactive, windowshade, window buttons, clutter bar.
        Add("Title.Active", "TITLEBAR", 27, 0, 275, 14);
        Add("Title.Inactive", "TITLEBAR", 27, 15, 275, 14);
        Add("Title.Shade.Active", "TITLEBAR", 27, 29, 275, 14);
        Add("Title.Shade.Inactive", "TITLEBAR", 27, 42, 275, 14);
        Pair("Title.Options", "TITLEBAR", 0, 0, 9, 9, 0, 9);
        Pair("Title.Minimize", "TITLEBAR", 9, 0, 9, 9, 9, 9);
        Pair("Title.Shade", "TITLEBAR", 0, 18, 9, 9, 9, 18);
        Pair("Title.Close", "TITLEBAR", 18, 0, 9, 9, 18, 9);
        Add("Title.Clutter", "TITLEBAR", 304, 0, 8, 43);
        Add("Title.Clutter.Disabled", "TITLEBAR", 312, 0, 8, 43);

        // Windowshade (the player rolled up): the button that rolls it down again, and the mini position bar whose knob
        // looks different in the left, middle and right third.
        Pair("Title.Unshade", "TITLEBAR", 0, 27, 9, 9, 9, 27);
        Add("Shade.Position.Background", "TITLEBAR", 0, 36, 17, 7);
        Add("Shade.Position.Thumb.Left", "TITLEBAR", 17, 36, 3, 7);
        Add("Shade.Position.Thumb", "TITLEBAR", 20, 36, 3, 7);
        Add("Shade.Position.Thumb.Right", "TITLEBAR", 23, 36, 3, 7);

        // Transport and eject.
        Pair("Buttons.Previous", "CBUTTONS", 0, 0, 23, 18, 0, 18);
        Pair("Buttons.Play", "CBUTTONS", 23, 0, 23, 18, 23, 18);
        Pair("Buttons.Pause", "CBUTTONS", 46, 0, 23, 18, 46, 18);
        Pair("Buttons.Stop", "CBUTTONS", 69, 0, 23, 18, 69, 18);
        Pair("Buttons.Next", "CBUTTONS", 92, 0, 22, 18, 92, 18);
        Pair("Buttons.Eject", "CBUTTONS", 114, 0, 22, 16, 114, 16);

        // Position bar.
        Add("Position.Background", "POSBAR", 0, 0, 248, 10);
        Pair("Position.Thumb", "POSBAR", 248, 0, 29, 10, 278, 0);

        // Volume and balance: 28 background steps, one under the other, then the knob.
        for (int i = 0; i < VolumeSteps; i++)
        {
            Add($"Volume.Background.{i}", "VOLUME", 0, i * 15, 68, 13);
            Add($"Balance.Background.{i}", "BALANCE", 9, i * 15, 38, 13);
        }
        Pair("Volume.Thumb", "VOLUME", 15, 422, 14, 11, 0, 422);
        Pair("Balance.Thumb", "BALANCE", 15, 422, 14, 11, 0, 422);

        // Shuffle, repeat, EQ and PL switches: off / off pressed / on / on pressed.
        Toggle("Repeat", 0, 0, 28, 15, 0, 15, 0, 30, 0, 45);
        Toggle("Shuffle", 28, 0, 47, 15, 28, 15, 28, 30, 28, 45);
        Toggle("EqSwitch", 0, 61, 23, 12, 46, 61, 0, 73, 46, 73);
        Toggle("PlSwitch", 23, 61, 23, 12, 69, 61, 23, 73, 69, 73);

        // Mono / stereo lights.
        Add("Stereo.On", "MONOSTER", 0, 0, 29, 12);
        Add("Stereo.Off", "MONOSTER", 0, 12, 29, 12);
        Add("Mono.On", "MONOSTER", 29, 0, 27, 12);
        Add("Mono.Off", "MONOSTER", 29, 12, 27, 12);

        // Play state, and the thin light beside it: Ok (green in the base skin) or Buffering (red).
        Add("State.Playing", "PLAYPAUS", 0, 0, 9, 9);
        Add("State.Paused", "PLAYPAUS", 9, 0, 9, 9);
        Add("State.Stopped", "PLAYPAUS", 18, 0, 9, 9);
        Add("State.Ok", "PLAYPAUS", 36, 0, 3, 9);
        Add("State.Buffering", "PLAYPAUS", 39, 0, 3, 9);

        // Clock digits: NUMBERS (0-9, blank; the minus is a 5x1 line taken from the digits) or NUMS_EX (0-9, blank, minus).
        for (int d = 0; d < 10; d++)
        {
            Add($"Numbers.{d}", "NUMBERS", d * 9, 0, 9, 13);
            Add($"NumsEx.{d}", "NUMS_EX", d * 9, 0, 9, 13);
        }
        Add("Numbers.Blank", "NUMBERS", 90, 0, 9, 13);
        Add("Numbers.Minus", "NUMBERS", 20, 6, 5, 1);
        Add("Numbers.NoMinus", "NUMBERS", 9, 6, 5, 1);
        Add("NumsEx.Blank", "NUMS_EX", 90, 0, 9, 13);
        Add("NumsEx.Minus", "NUMS_EX", 99, 0, 9, 13);

        // Equalizer.
        Add("Eq.Background", "EQMAIN", 0, 0, 275, 116);
        Add("Eq.Title.Active", "EQMAIN", 0, 134, 275, 14);
        Add("Eq.Title.Inactive", "EQMAIN", 0, 149, 275, 14);
        Pair("Eq.Close", "EQMAIN", 0, 116, 9, 9, 0, 125);
        for (int i = 0; i < EqSliderSteps; i++) Add($"Eq.Slider.{i}", "EQMAIN", 13 + i % 14 * 15, 164 + i / 14 * 65, 14, 63);
        Pair("Eq.Thumb", "EQMAIN", 0, 164, 11, 11, 0, 176);
        Add("Eq.On", "EQMAIN", 10, 119, 26, 12);
        Add("Eq.On.Pressed", "EQMAIN", 128, 119, 26, 12);
        Add("Eq.On.Active", "EQMAIN", 69, 119, 26, 12);
        Add("Eq.On.Active.Pressed", "EQMAIN", 187, 119, 26, 12);
        Add("Eq.Auto", "EQMAIN", 36, 119, 32, 12);
        Add("Eq.Auto.Pressed", "EQMAIN", 154, 119, 32, 12);
        Add("Eq.Auto.Active", "EQMAIN", 95, 119, 32, 12);
        Add("Eq.Auto.Active.Pressed", "EQMAIN", 213, 119, 32, 12);
        Pair("Eq.Presets", "EQMAIN", 224, 164, 44, 12, 224, 176);
        Add("Eq.Graph.Background", "EQMAIN", 0, 294, 113, 19);
        Add("Eq.Graph.Colors", "EQMAIN", 115, 294, 1, 19);
        Add("Eq.Graph.Preamp", "EQMAIN", 0, 314, 113, 1);

        // Playlist frame: corners and tiles (active / inactive title row), sides, bottom, scroll knob.
        Add("Pl.TopLeft.Active", "PLEDIT", 0, 0, 25, 20);
        Add("Pl.Title.Active", "PLEDIT", 26, 0, 100, 20);
        Add("Pl.TopTile.Active", "PLEDIT", 127, 0, 25, 20);
        Add("Pl.TopRight.Active", "PLEDIT", 153, 0, 25, 20);
        Add("Pl.TopLeft.Inactive", "PLEDIT", 0, 21, 25, 20);
        Add("Pl.Title.Inactive", "PLEDIT", 26, 21, 100, 20);
        Add("Pl.TopTile.Inactive", "PLEDIT", 127, 21, 25, 20);
        Add("Pl.TopRight.Inactive", "PLEDIT", 153, 21, 25, 20);
        Add("Pl.LeftTile", "PLEDIT", 0, 42, 12, 29);
        Add("Pl.RightTile", "PLEDIT", 31, 42, 20, 29);
        Add("Pl.BottomLeft", "PLEDIT", 0, 72, 125, 38);
        Add("Pl.BottomRight", "PLEDIT", 126, 72, 150, 38);
        Add("Pl.BottomTile", "PLEDIT", 179, 0, 25, 38);
        Add("Pl.Visualizer", "PLEDIT", 205, 0, 75, 38);
        Pair("Pl.ScrollThumb", "PLEDIT", 52, 53, 8, 18, 61, 53);
        Add("Pl.Close.Pressed", "PLEDIT", 52, 42, 9, 9);
        Add("Pl.Shade.Pressed", "PLEDIT", 62, 42, 9, 9);

        // Playlist button menus: each item normal (left) and pressed (right), and the bar beside the menu.
        MenuItem("Pl.Add.Url", 0, 111); MenuItem("Pl.Add.Dir", 0, 130); MenuItem("Pl.Add.File", 0, 149);
        Add("Pl.Add.Bar", "PLEDIT", 48, 111, 3, 54);
        MenuItem("Pl.Rem.All", 54, 111); MenuItem("Pl.Rem.Crop", 54, 130); MenuItem("Pl.Rem.Selected", 54, 149); MenuItem("Pl.Rem.Misc", 54, 168);
        Add("Pl.Rem.Bar", "PLEDIT", 100, 111, 3, 72);
        MenuItem("Pl.Sel.Invert", 104, 111); MenuItem("Pl.Sel.None", 104, 130); MenuItem("Pl.Sel.All", 104, 149);
        Add("Pl.Sel.Bar", "PLEDIT", 150, 111, 3, 54);
        MenuItem("Pl.Misc.Sort", 154, 111); MenuItem("Pl.Misc.Info", 154, 130); MenuItem("Pl.Misc.Options", 154, 149);
        Add("Pl.Misc.Bar", "PLEDIT", 200, 111, 3, 54);
        MenuItem("Pl.List.New", 204, 111); MenuItem("Pl.List.Save", 204, 130); MenuItem("Pl.List.Load", 204, 149);
        Add("Pl.List.Bar", "PLEDIT", 250, 111, 3, 54);
    }

    private static void Toggle(string name, int x, int y, int w, int h, int px, int py, int ox, int oy, int opx, int opy)
    {
        Add(name, "SHUFREP", x, y, w, h);
        Add(name + ".Pressed", "SHUFREP", px, py, w, h);
        Add(name + ".On", "SHUFREP", ox, oy, w, h);
        Add(name + ".On.Pressed", "SHUFREP", opx, opy, w, h);
    }

    private static void MenuItem(string name, int x, int y) => Pair(name, "PLEDIT", x, y, 22, 18, x + 23, y);
}
