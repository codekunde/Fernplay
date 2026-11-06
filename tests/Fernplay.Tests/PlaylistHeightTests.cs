using Fernplay.Core;

namespace Fernplay.Tests;

public sealed class PlaylistHeightTests
{
    private const double Row = 9, Inset = 1;

    [Theory]
    [InlineData(118, 118)]   // exactly 13 rows
    [InlineData(121, 118)]   // a bit more: back to 13
    [InlineData(124, 127)]   // past half a row: 14
    [InlineData(200, 199)]   // 22 rows
    public void Snaps_to_whole_rows(double raw, double expected) =>
        Assert.Equal(expected, PlaylistHeight.Snap(raw, Row, Inset, max: 1000), 6);

    [Fact]
    public void Never_below_four_rows() =>
        Assert.Equal(4 * Row + Inset, PlaylistHeight.Snap(5, Row, Inset, max: 1000), 6);

    [Fact]
    public void Never_past_the_bottom_of_the_screen()
    {
        // Room for 20.5 rows: 20 whole rows.
        Assert.Equal(20 * Row + Inset, PlaylistHeight.Snap(5000, Row, Inset, max: 20.5 * Row + Inset), 6);
    }

    [Theory]
    [InlineData(119, 121.5)] // 13 rows would be 118: the other panel's bottom (121.5) is nearer than half a row
    [InlineData(125, 121.5)]
    [InlineData(126.5, 127)] // more than half a row from the edge: whole rows again
    [InlineData(117, 118)]   // 4.5 from the edge: whole rows
    public void Snaps_to_another_panels_bottom_within_half_a_row(double raw, double expected) =>
        Assert.Equal(expected, PlaylistHeight.Snap(raw, Row, Inset, max: 1000, edges: [121.5]), 6);

    [Fact]
    public void An_edge_past_the_screen_bottom_is_ignored() =>
        Assert.Equal(118, PlaylistHeight.Snap(120, Row, Inset, max: 119, edges: [121.5]), 6);

    [Theory]
    [InlineData(118, 118, 0)]
    [InlineData(121.5, 118, 3.5)]
    [InlineData(126.9, 118, 8.9)]
    public void Split_keeps_whole_rows_in_the_list(double height, double list, double spare)
    {
        var (l, s) = PlaylistHeight.Split(height, Row, Inset);
        Assert.Equal(list, l, 6);
        Assert.Equal(spare, s, 6);
    }

    [Fact]
    public void Four_rows_even_when_the_screen_is_too_short() =>
        Assert.Equal(4 * Row + Inset, PlaylistHeight.Snap(300, Row, Inset, max: 10), 6);
}
