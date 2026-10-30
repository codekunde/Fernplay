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

    [Fact]
    public void Four_rows_even_when_the_screen_is_too_short() =>
        Assert.Equal(4 * Row + Inset, PlaylistHeight.Snap(300, Row, Inset, max: 10), 6);
}
