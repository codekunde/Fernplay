using Fernplay.Core;
using Fernplay.Core.Audio;
using Fernplay.Core.Library;
using NAudio.Wave;
using Xunit;

namespace Fernplay.Tests;

/// <summary>
/// Test classes that encode MP3/AAC with Windows' encoder (NAudio MediaFoundationEncoder) run one at a time: two classes
/// encoding at once crashed the test host now and then (fail-fast 0xc0000409, always with an ICY test running).
/// </summary>
[CollectionDefinition(Name)]
public sealed class WindowsEncoder
{
    public const string Name = "Windows encoder";
}

[Collection(WindowsEncoder.Name)]
public sealed class TagEditorTests : IDisposable
{
    private readonly TestAudio _audio = new();

    public void Dispose() => _audio.Dispose();

    private string Mp3(string name = "tone.mp3")
    {
        var wav = _audio.Tone(Path.ChangeExtension(name, ".wav"), 3);
        var mp3 = Path.Combine(_audio.Folder, name);
        using (var reader = new WaveFileReader(wav)) MediaFoundationEncoder.EncodeToMp3(reader, mp3, 128000);
        return mp3;
    }

    private static readonly TagFields Full = new(
        Title: "Spore Song", Artist: "The Spore Prints", Album: "Moss", AlbumArtist: "The Spore Prints",
        TrackNumber: "7", Year: "1987", Genre: "Rock", Composer: "F. Ern", Comment: "Ünïcode ✓ works");

    [Fact]
    public void Mp3_round_trip_of_every_field()
    {
        var path = Mp3();
        TagEditor.Write(path, TagEditor.Read(path), Full);

        Assert.Equal(Full, TagEditor.Read(path));
        var t = Track.FromFile(path);
        Assert.Equal("07 - The Spore Prints - Spore Song", t.DisplayName);
        var info = TagInfo.FromFile(path)!;
        Assert.Equal(1987, info.Year);
        Assert.Equal("Rock", info.Genre);
        Assert.Equal("The Spore Prints", info.AlbumArtist);
    }

    [Fact]
    public void Only_changed_fields_are_written_and_emptied_ones_removed()
    {
        var path = Mp3();
        TagEditor.Write(path, TagEditor.Read(path), Full);
        var before = TagEditor.Read(path);

        TagEditor.Write(path, before, before with { Title = "New Title", Comment = "" });

        Assert.Equal(before with { Title = "New Title", Comment = "" }, TagEditor.Read(path));
    }

    [Fact]
    public void Several_artists_are_kept_apart_and_shown_joined()
    {
        var path = Mp3();
        TagEditor.Write(path, TagEditor.Read(path), new TagFields(Artist: "Alpha; Beta"));
        Assert.Equal("Alpha; Beta", TagEditor.Read(path).Artist);
    }

    [Fact]
    public void Audio_still_plays_after_writing()
    {
        var path = Mp3();
        TimeSpan length;
        using (var d = new MediaFoundationDecoder(path)) length = d.TotalTime;
        TagEditor.Write(path, TagEditor.Read(path), Full with { Comment = new string('x', 5000) }); // grows the tag

        using var after = new MediaFoundationDecoder(path);
        Assert.InRange(after.TotalTime.TotalMilliseconds, length.TotalMilliseconds - 50, length.TotalMilliseconds + 50);
        var buffer = new float[4410];
        Assert.True(after.Read(buffer) > 0);
    }

    [Fact]
    public void M4a_round_trip()
    {
        var wav = _audio.Tone("aac.wav", 3);
        var m4a = Path.Combine(_audio.Folder, "aac.m4a");
        using (var reader = new WaveFileReader(wav)) MediaFoundationEncoder.EncodeToAac(reader, m4a, 128000);
        var edit = new TagFields(Title: "Fern", Artist: "Moss", Album: "Forest", TrackNumber: "3", Year: "2001", Genre: "Ambient");
        TagEditor.Write(m4a, TagEditor.Read(m4a), edit);
        var read = TagEditor.Read(m4a);
        Assert.Equal(("Fern", "Moss", "Forest", "3", "2001", "Ambient"), (read.Title, read.Artist, read.Album, read.TrackNumber, read.Year, read.Genre));
    }

    [Fact]
    public void A_file_held_open_reports_in_use_and_stays_unchanged()
    {
        var path = Mp3();
        var bytes = File.ReadAllBytes(path);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var ex = Assert.Throws<TagEditException>(() => TagEditor.Write(path, new TagFields(), Full));
            Assert.True(ex.InUse, ex.Message);
        }
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Media_foundation_does_not_lock_the_playing_file()
    {
        // Why PlayerController lets go of the playing file before saving: Windows would rewrite it under the decoder.
        var path = Mp3();
        using var playing = new MediaFoundationDecoder(path);
        TagEditor.Write(path, new TagFields(), new TagFields(Title: "Under the decoder"));
        Assert.Equal("Under the decoder", TagEditor.Read(path).Title);
    }

    [Fact]
    public void Wav_says_the_format_takes_no_tags()
    {
        var wav = _audio.Tone("plain.wav", 1);
        var bytes = File.ReadAllBytes(wav);
        var ex = Assert.Throws<TagEditException>(() => TagEditor.Write(wav, TagEditor.Read(wav), new TagFields(Title: "Wave")));
        Assert.Equal("Windows can't write tags to WAV files. Nothing was saved.", ex.Message);
        Assert.Equal(bytes, File.ReadAllBytes(wav));
    }

    [Fact]
    public void Read_only_file_says_no_permission()
    {
        var path = Mp3();
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var ex = Assert.Throws<TagEditException>(() => TagEditor.Write(path, new TagFields(), Full));
            Assert.StartsWith("No permission to change tone.mp3", ex.Message);
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Theory]
    [InlineData("0", null, "Track number")]
    [InlineData("3/12", null, "Track number")]
    [InlineData("1000", null, "Track number")]
    [InlineData(" 12 ", "1987", null)]
    [InlineData(null, "87a", "Year")]
    [InlineData(null, "", null)]
    public void Validate(string? track, string? year, string? problemStarts)
    {
        var f = new TagFields(TrackNumber: track ?? "", Year: year ?? "");
        var problem = TagEditor.Validate(f);
        if (problemStarts is null) Assert.Null(problem);
        else Assert.StartsWith(problemStarts, problem);
        if (problem is not null) Assert.Throws<TagEditException>(() => TagEditor.Write("nothing.mp3", new TagFields(), f));
    }

    [Fact]
    public void Changed_lists_the_edited_fields()
    {
        Assert.Equal(["Title", "Year"], TagEditor.Changed(new TagFields(Title: "a"), new TagFields(Title: "b ", Year: "2000")));
        Assert.Empty(TagEditor.Changed(new TagFields(Title: "a"), new TagFields(Title: " a ")));
    }

    [Fact]
    public void Missing_file_is_explained()
    {
        var ex = Assert.Throws<TagEditException>(() => TagEditor.Read(Path.Combine(_audio.Folder, "gone.mp3")));
        Assert.Contains("gone.mp3", ex.Message);
    }

    [Fact]
    public void Playlist_refresh_updates_every_entry_of_the_file_without_moving()
    {
        var list = new Playlist();
        list.AddRange([new Track(@"C:\m\a.mp3", "Old", null, null, null, null), new Track(@"C:\m\b.mp3", "B", null, null, null, null), new Track(@"C:\M\A.mp3", "Old", null, null, null, null)]);
        list.MoveTo(1);
        var updated = new List<int>();
        list.ItemUpdated += (_, i) => updated.Add(i);
        bool changed = false;
        list.Changed += (_, _) => changed = true;

        list.Refresh(new Track(@"c:\m\a.mp3", "New", "Artist", null, TimeSpan.FromSeconds(3), null));

        Assert.Equal([0, 2], updated);
        Assert.False(changed);
        Assert.Equal(1, list.CurrentIndex);
        Assert.Equal(["New", "B", "New"], list.Tracks.Select(t => t.Title));
        Assert.Equal(@"C:\M\A.mp3", list.Tracks[2].Path); // the entry keeps its own spelling
    }

    [Fact]
    public void Library_refresh_keeps_stats_and_rating()
    {
        var index = new LibraryIndex();
        index.AddFolder(@"C:\Music");
        index.Apply(new ScanChanges([new ScannedFile(@"C:\Music\Moss\Forest\01 - Fern.mp3", new TagInfo("Fern", "Moss", "Forest", null, null, 1), 10, DateTime.UnixEpoch)],
            [], [], [@"C:\Music"], [], 0, DateTime.UnixEpoch, TimeSpan.Zero));
        index.SetRating(@"C:\Music\Moss\Forest\01 - Fern.mp3", 4);
        index.RecordPlay(@"C:\Music\Moss\Forest\01 - Fern.mp3", DateTime.UnixEpoch);

        Assert.True(index.Refresh(new ScannedFile(@"C:\Music\Moss\Forest\01 - Fern.mp3", new TagInfo("Fern (Live)", null, null, null, null, 1, Year: 1999), 12, DateTime.UnixEpoch.AddDays(1))));
        Assert.False(index.Refresh(new ScannedFile(@"C:\Elsewhere\x.mp3", null, 1, DateTime.UnixEpoch)));

        var t = index.Find(@"C:\Music\Moss\Forest\01 - Fern.mp3")!;
        Assert.Equal(("Fern (Live)", "Moss", "Forest", 1999, 4, 1, 12L), (t.Title, t.Artist, t.Album, t.Year, t.Rating, t.PlayCount, t.Size)); // artist/album from folders
    }
}
