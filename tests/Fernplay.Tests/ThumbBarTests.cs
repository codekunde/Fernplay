using Fernplay.Core;

namespace Fernplay.Tests;

/// <summary>The taskbar thumbnail buttons: what each shows for a play state, and that the pictures are sharp at every DPI.</summary>
public sealed class ThumbBarTests
{
    private static ThumbButtonView Button(ThumbButtonView[] views, ThumbButton b) => views.Single(v => v.Button == b);

    [Fact]
    public void Three_buttons_in_order_previous_play_next()
    {
        var views = ThumbBar.For(PlayState.Stopped, loaded: false, trackCount: 3);
        Assert.Equal([ThumbButton.Previous, ThumbButton.PlayPause, ThumbButton.Next], views.Select(v => v.Button));
        Assert.Equal(["Previous", "Play", "Next"], views.Select(v => v.Tooltip));
    }

    [Fact]
    public void Empty_list_turns_every_button_off()
    {
        var views = ThumbBar.For(PlayState.Stopped, loaded: false, trackCount: 0);
        Assert.All(views, v => Assert.False(v.Enabled));
        Assert.Equal(ThumbGlyph.Play, Button(views, ThumbButton.PlayPause).Glyph);
    }

    [Theory]
    [InlineData(PlayState.Stopped, false, ThumbGlyph.Play, "Play")]
    [InlineData(PlayState.Stopped, true, ThumbGlyph.Play, "Play")]
    [InlineData(PlayState.Paused, true, ThumbGlyph.Play, "Play")]
    [InlineData(PlayState.Playing, true, ThumbGlyph.Pause, "Pause")]
    public void Play_pause_follows_the_play_state(PlayState state, bool loaded, ThumbGlyph glyph, string tip)
    {
        var views = ThumbBar.For(state, loaded, trackCount: 2);
        var pp = Button(views, ThumbButton.PlayPause);
        Assert.Equal(glyph, pp.Glyph);
        Assert.Equal(tip, pp.Tooltip);
        Assert.All(views, v => Assert.True(v.Enabled));
        Assert.Equal(ThumbGlyph.Previous, Button(views, ThumbButton.Previous).Glyph);
        Assert.Equal(ThumbGlyph.Next, Button(views, ThumbButton.Next).Glyph);
    }

    [Fact]
    public void A_station_alone_in_the_list_keeps_previous_and_next_on_as_the_player_does()
    {
        // The engine state is the same for a station; the player's own buttons are on whenever the list has entries.
        var views = ThumbBar.For(PlayState.Playing, loaded: true, trackCount: 1);
        Assert.All(views, v => Assert.True(v.Enabled));
        Assert.Equal("Pause", Button(views, ThumbButton.PlayPause).Tooltip);
    }

    [Fact]
    public void List_emptied_under_a_playing_song_still_offers_pause()
    {
        var views = ThumbBar.For(PlayState.Playing, loaded: true, trackCount: 0);
        Assert.True(Button(views, ThumbButton.PlayPause).Enabled);
        Assert.False(Button(views, ThumbButton.Previous).Enabled);
        Assert.False(Button(views, ThumbButton.Next).Enabled);
    }

    public static TheoryData<ThumbGlyph, int> GlyphsAndSizes()
    {
        var data = new TheoryData<ThumbGlyph, int>();
        foreach (var g in Enum.GetValues<ThumbGlyph>())
            foreach (int size in new[] { 16, 20, 24, 32 }) // 100, 125, 150, 200 %
                data.Add(g, size);
        return data;
    }

    [Theory]
    [MemberData(nameof(GlyphsAndSizes))]
    public void Glyph_is_centred_and_its_straight_edges_are_whole_pixels(ThumbGlyph glyph, int size)
    {
        var a = ThumbBar.Draw(glyph, size);
        Assert.Equal(size * size, a.Length);
        int Alpha(int x, int y) => a[y * size + x];
        var cols = Enumerable.Range(0, size).Where(x => Enumerable.Range(0, size).Any(y => Alpha(x, y) > 0)).ToList();
        var rows = Enumerable.Range(0, size).Where(y => Enumerable.Range(0, size).Any(x => Alpha(x, y) > 0)).ToList();
        // Vertically: centred exactly and the same above and below the middle.
        Assert.Equal(rows[0], size - 1 - rows[^1]);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                Assert.Equal(Alpha(x, y), Alpha(x, size - 1 - y));
        // About 10/16 of the size high (Windows' own media buttons), never touching the edge.
        Assert.InRange(rows.Count, size * 9 / 16, size * 11 / 16 + 1);
        // Horizontally centred within a pixel (the play triangle sits half a pixel right on purpose).
        Assert.InRange(cols[0] - (size - 1 - cols[^1]), 0, 1);
        // The upright edges are hard: the first and last columns are solid over the middle rows.
        int mid = size / 2;
        if (glyph != ThumbGlyph.Next) Assert.Equal(255, Alpha(cols[0], mid));
        if (glyph != ThumbGlyph.Play && glyph != ThumbGlyph.Previous) Assert.Equal(255, Alpha(cols[^1], mid));
        if (glyph is ThumbGlyph.Pause)
            Assert.All(a, v => Assert.True(v is 0 or 255)); // only straight edges: no grey at all
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void Previous_is_next_mirrored(int size)
    {
        var next = ThumbBar.Draw(ThumbGlyph.Next, size);
        var prev = ThumbBar.Draw(ThumbGlyph.Previous, size);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                Assert.Equal(next[y * size + x], prev[y * size + size - 1 - x]);
    }

    [Fact]
    public void Pause_bars_are_two_solid_columns_with_a_gap()
    {
        var a = ThumbBar.Draw(ThumbGlyph.Pause, 16);
        string row = string.Concat(Enumerable.Range(0, 16).Select(x => a[8 * 16 + x] == 255 ? '#' : '.'));
        Assert.Equal("....###..###....", row);
    }
}
