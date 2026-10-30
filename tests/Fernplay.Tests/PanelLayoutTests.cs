using Fernplay.Core;

namespace Fernplay.Tests;

public sealed class PanelLayoutTests
{
    private static readonly Box Screen = new(0, 0, 1920, 1040);
    // The default stack: main 560x192, EQ 560x220 under it, playlist 560x302 under that.
    private static readonly Box Main = new(1000, 393, 560, 192);
    private static readonly Box Eq = new(1000, 585, 560, 220);
    private static readonly Box Pl = new(1000, 805, 560, 302);

    [Fact]
    public void The_default_stack_is_one_docked_group()
    {
        Assert.True(PanelLayout.Touching(Main, Eq));
        Assert.True(PanelLayout.Touching(Eq, Pl));
        Assert.False(PanelLayout.Touching(Main, Pl));
        Assert.Equal([0, 1, 2], PanelLayout.Group([Main, Eq, Pl], 0).Order());
    }

    [Fact]
    public void A_detached_panel_leaves_the_group_and_takes_nothing_with_it()
    {
        var eqMoved = Eq with { Left = 200 };
        // The playlist only touched the EQ, so with the EQ gone it is no longer connected to the main panel.
        Assert.Equal([0], PanelLayout.Group([Main, eqMoved, Pl], 0));
    }

    [Fact]
    public void Side_by_side_panels_dock_too_but_corners_do_not()
    {
        var right = new Box(Main.Right, Main.Top + 50, 300, 200);
        Assert.True(PanelLayout.Touching(Main, right));
        var corner = new Box(Main.Right, Main.Bottom, 300, 200); // only the corners meet
        Assert.False(PanelLayout.Touching(Main, corner));
    }

    [Fact]
    public void Dragging_near_a_panel_edge_snaps_onto_it()
    {
        // EQ dragged to 7 px below the main panel and 4 px to the right: snaps flush under it and aligned left.
        var dragged = Eq with { Left = 1004, Top = Main.Bottom + 7 };
        var (x, y) = PanelLayout.SnapOffset(dragged, [Main], Screen);
        Assert.Equal(-4, x);
        Assert.Equal(-7, y);
    }

    [Fact]
    public void Far_away_panels_do_not_snap_but_screen_edges_do()
    {
        var dragged = new Box(300, 300, 560, 220);
        Assert.Equal((0d, 0d), PanelLayout.SnapOffset(dragged, [Main], Screen));
        var nearEdge = dragged with { Left = 6, Top = Screen.Bottom - 220 + 8 };
        Assert.Equal((-6d, -8d), PanelLayout.SnapOffset(nearEdge, [Main], Screen));
    }

    [Fact]
    public void The_nearest_edge_wins()
    {
        // 3 px from the main panel's bottom, 9 px from the screen's top: the panel edge is closer.
        var dragged = new Box(1000, Main.Bottom + 3, 560, 220);
        var (_, y) = PanelLayout.SnapOffset(dragged, [Main], Screen with { Top = Main.Bottom + 12 });
        Assert.Equal(-3, y);
    }
}
