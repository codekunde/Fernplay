using Fernplay.Core;

namespace Fernplay.Tests;

public sealed class PlaylistAndFormatTests : IDisposable
{
    private readonly TestAudio _audio = new();
    public void Dispose() => _audio.Dispose();

    private static Track T(string name) => new(name, null, null, null, null, null);

    [Fact]
    public void Next_and_previous_walk_the_list_and_stop_at_the_ends()
    {
        var list = new Playlist();
        Assert.Equal(0, list.AddRange([T("a.mp3"), T("b.mp3"), T("c.mp3")]));
        Assert.Null(list.Current);
        Assert.Equal("a.mp3", list.MoveTo(0)!.Path);
        Assert.Equal("b.mp3", list.Next()!.Path);
        Assert.Equal("c.mp3", list.Next()!.Path);
        Assert.Null(list.Next());
        Assert.Equal("c.mp3", list.Current!.Path); // stays on the last one
        list.MoveTo(0);
        Assert.Equal("a.mp3", list.Previous()!.Path); // stays on the first one
        Assert.Equal(3, list.AddRange([T("d.mp3")]));
    }

    [Fact]
    public void ExpandPaths_takes_supported_files_and_walks_folders_in_order()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_audio.Folder, "Album"));
        File.WriteAllText(Path.Combine(sub.FullName, "02.mp3"), "");
        File.WriteAllText(Path.Combine(sub.FullName, "01.FLAC"), "");
        File.WriteAllText(Path.Combine(sub.FullName, "cover.jpg"), "");
        File.WriteAllText(Path.Combine(sub.FullName, "x.ogg"), "");
        var single = Path.Combine(_audio.Folder, "single.wav");
        File.WriteAllText(single, "");

        var result = Playlist.ExpandPaths([sub.FullName, single, Path.Combine(_audio.Folder, "missing.mp3")])
            .Select(Path.GetFileName).ToList();

        Assert.Equal(["01.FLAC", "02.mp3", "single.wav"], result);
    }

    [Fact]
    public void Display_name_uses_tags_then_the_file_name()
    {
        Assert.Equal("Song - Artist", new Track(@"C:\m\x.mp3", "Song", "Artist", null, null, null).DisplayName);
        Assert.Equal("Song", new Track(@"C:\m\x.mp3", "Song", null, null, null, null).DisplayName);
        Assert.Equal("01 - Intro", new Track(@"C:\m\01 - Intro.mp3", " ", "Artist", null, null, null).DisplayName);
    }

    [Fact]
    public void Display_name_is_track_number_title_artist()
    {
        Assert.Equal("03 - Inverted - Mark Absence", new Track("x.mp3", "Inverted", "Mark Absence", null, null, null, 3).DisplayName);
        Assert.Equal("12 - Song", new Track("x.mp3", "Song", null, null, null, null, 12).DisplayName);
        // The number is already in the title (as on the owner's album): not added twice.
        Assert.Equal("03 - Inverted - Mark Absence", new Track("x.mp3", "03 - Inverted", "Mark Absence", null, null, null, 3).DisplayName);
        Assert.Equal("03 - Inverted - Mark Absence", new Track("x.mp3", "03 - Inverted", "Mark Absence", null, null, null).DisplayName);
        // M3U keeps the usual "Artist - Title".
        Assert.Equal("Mark Absence - Inverted", new Track("x.mp3", "Inverted", "Mark Absence", null, null, null, 3).ArtistTitle);
    }

    [Fact]
    public void FromFile_reads_duration_of_a_wav_and_never_throws()
    {
        var t = Track.FromFile(_audio.Tone("tone.wav", 1.5));
        Assert.NotNull(t.Duration);
        Assert.Equal(1.5, t.Duration!.Value.TotalSeconds, 1);
        Assert.Equal("tone", t.DisplayName);

        var missing = Track.FromFile(Path.Combine(_audio.Folder, "nope.mp3"));
        Assert.Null(missing.Duration);
    }

    [Theory]
    [InlineData(0, false, "0:00")]
    [InlineData(65, false, "1:05")]
    [InlineData(3723, false, "1:02:03")]
    [InlineData(42, true, "-0:42")]
    [InlineData(-3, false, "0:00")]
    public void Clock_formats_like_a_player(int seconds, bool remaining, string expected) =>
        Assert.Equal(expected, TimeFormat.Clock(TimeSpan.FromSeconds(seconds), remaining));

    [Fact]
    public void Errors_say_what_failed_and_what_to_do()
    {
        var mp3 = @"C:\m\song.ogg";
        Assert.Equal("Can't find song.ogg: it was moved or deleted.", PlaybackErrors.Describe(new FileNotFoundException(), mp3));
        Assert.Equal("Can't play song.ogg: Windows has no decoder for OGG files.",
            PlaybackErrors.Describe(new System.Runtime.InteropServices.COMException("x", unchecked((int)0xC00D36C4)), mp3));
        Assert.Equal(PlaybackErrors.MissingMediaFeatures, PlaybackErrors.Describe(new DllNotFoundException("mfplat.dll"), mp3));
        Assert.Equal("Can't play song.ogg: boom.", PlaybackErrors.Describe(new InvalidOperationException("boom."), mp3));
        Assert.Equal("Can't play song.ogg: the file is damaged or is not a music file.",
            PlaybackErrors.Describe(new InvalidOperationException("Media Foundation error 0xC00D3E84"), mp3));
    }

    [Fact]
    public void Format_texts()
    {
        var f = new Fernplay.Core.Audio.AudioFormatInfo("FLAC", 44100, 2);
        Assert.Equal("44.1 kHz", f.SampleRateText);
        Assert.Equal("stereo", f.ChannelsText);
        Assert.Equal("48 kHz", (f with { SampleRate = 48000 }).SampleRateText);
    }

    [Fact]
    public void Volume_curve_is_quadratic()
    {
        Assert.Equal(0f, Fernplay.Core.Audio.GainStage.ToGain(0f));
        Assert.Equal(0.25f, Fernplay.Core.Audio.GainStage.ToGain(0.5f));
        Assert.Equal(1f, Fernplay.Core.Audio.GainStage.ToGain(1f));
    }
}
