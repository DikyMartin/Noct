using System.Text;
using Noct.Core.Lyrics;
using Noct.Core.Settings;

namespace Noct.Tests;

public sealed class LyricsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noct-lyrics-" + Guid.NewGuid().ToString("N"));

    public LyricsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Fractional_seconds_of_one_two_and_three_digits_are_decimal_fractions()
    {
        var doc = LrcParser.Parse("[00:01.5]a\n[00:02.50]b\n[00:03.500]c\n[00:04]d");

        Assert.Equal(
            [1500, 2500, 3500, 4000],
            doc.Lines.Select(l => (int)l.Time!.Value.TotalMilliseconds));
    }

    [Fact]
    public void A_line_with_several_timestamps_becomes_one_line_per_timestamp_in_time_order()
    {
        var doc = LrcParser.Parse("[00:10.00][01:20.00]chorus\n[00:30.00]verse");

        Assert.Equal(["chorus", "verse", "chorus"], doc.Lines.Select(l => l.Text));
        Assert.Equal([10, 30, 80], doc.Lines.Select(l => (int)l.Time!.Value.TotalSeconds));
    }

    [Fact]
    public void Metadata_tags_are_read_and_are_not_shown_as_lyrics()
    {
        var doc = LrcParser.Parse("[ti:Song]\n[ar:Someone]\n[al:Album]\n[by:me]\n[length: 03:20]\n[00:01.00]hello");

        Assert.Equal("Song", doc.Title);
        Assert.Equal("Someone", doc.Artist);
        Assert.Equal("Album", doc.Album);
        Assert.Single(doc.Lines);
    }

    [Fact]
    public void A_positive_offset_shows_lyrics_earlier_and_a_negative_one_later()
    {
        var earlier = LrcParser.Parse("[offset:+500]\n[00:10.00]x");
        var later = LrcParser.Parse("[offset:-500]\n[00:10.00]x");

        Assert.Equal(9500, earlier.Lines[0].Time!.Value.TotalMilliseconds);
        Assert.Equal(10500, later.Lines[0].Time!.Value.TotalMilliseconds);
    }

    [Fact]
    public void An_offset_larger_than_the_stamp_clamps_to_zero_instead_of_going_negative()
    {
        var doc = LrcParser.Parse("[offset:5000]\n[00:01.00]x");

        Assert.Equal(TimeSpan.Zero, doc.Lines[0].Time);
    }

    [Fact]
    public void Enhanced_word_timestamps_are_removed_from_the_text()
    {
        var doc = LrcParser.Parse("[00:01.00]<00:01.00>Hello <00:01.50>world");

        Assert.Equal("Hello world", doc.Lines[0].Text);
    }

    [Fact]
    public void Lines_in_a_file_that_is_out_of_order_are_sorted()
    {
        var doc = LrcParser.Parse("[00:20.00]second\n[00:05.00]first");

        Assert.Equal(["first", "second"], doc.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Active_group_follows_the_position_with_a_binary_search_at_every_boundary()
    {
        var doc = LrcParser.Parse("[00:05.00]a\n[00:10.00]b\n[00:15.00]c");

        Assert.Equal(-1, doc.FindActiveGroup(TimeSpan.FromSeconds(4.99)));
        Assert.Equal(0, doc.FindActiveGroup(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, doc.FindActiveGroup(TimeSpan.FromSeconds(9.99)));
        Assert.Equal(1, doc.FindActiveGroup(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, doc.FindActiveGroup(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Lines_that_share_a_timestamp_form_one_group_that_is_active_together()
    {
        var doc = LrcParser.Parse("[00:05.00]a\n[00:05.00]a translated\n[00:09.00]b");

        Assert.Equal(0, doc.Lines[0].Group);
        Assert.Equal(0, doc.Lines[1].Group);
        Assert.Equal(2, doc.Lines[2].Group);
        Assert.Equal(0, doc.FindActiveGroup(TimeSpan.FromSeconds(6)));
    }

    [Fact]
    public void A_blank_timed_line_is_kept_so_a_pause_between_verses_ends_the_previous_line()
    {
        var doc = LrcParser.Parse("[00:05.00]a\n[00:08.00]\n[00:12.00]b");

        Assert.Equal(3, doc.Lines.Count);
        Assert.Equal(string.Empty, doc.Lines[1].Text);
        Assert.Equal(1, doc.FindActiveGroup(TimeSpan.FromSeconds(9)));
    }

    [Fact]
    public void Text_without_timestamps_is_unsynced_and_never_reports_an_active_line()
    {
        var doc = LrcParser.Parse("just words\nmore words");

        Assert.False(doc.IsSynced);
        Assert.Equal(2, doc.Lines.Count);
        Assert.All(doc.Lines, l => Assert.Null(l.Time));
        Assert.Equal(-1, doc.FindActiveGroup(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Empty_or_metadata_only_files_are_empty()
    {
        Assert.True(LrcParser.Parse(string.Empty).IsEmpty);
        Assert.True(LrcParser.Parse("[ti:Only a title]").IsEmpty);
    }

    [Fact]
    public void Windows_line_endings_and_a_byte_order_mark_are_handled()
    {
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("[00:01.00]one\r\n[00:02.00]two\r\n")).ToArray();

        var doc = LrcParser.Parse(LrcParser.Decode(bytes));

        Assert.Equal(["one", "two"], doc.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Decoding_keeps_utf8_text_and_falls_back_to_latin1_for_legacy_files()
    {
        Assert.Equal("日本語 café", LrcParser.Decode(Encoding.UTF8.GetBytes("日本語 café")));
        Assert.Equal("café", LrcParser.Decode([0x63, 0x61, 0x66, 0xE9]));
    }

    [Fact]
    public void Utf16_with_a_byte_order_mark_is_decoded()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("[00:01.00]hi")).ToArray();

        Assert.Equal("[00:01.00]hi", LrcParser.Decode(bytes));
    }

    [Fact]
    public void The_lrc_with_the_same_base_name_beside_the_song_is_found_case_insensitively()
    {
        var song = Path.Combine(_root, "Song.flac");
        File.WriteAllText(Path.Combine(_root, "song.LRC"), "[00:01.00]x");

        Assert.Equal(Path.Combine(_root, "Song.lrc"), LyricsLocator.Find(song), ignoreCase: true);
    }

    [Fact]
    public void A_lrc_with_a_different_name_is_not_used()
    {
        var song = Path.Combine(_root, "Song.flac");
        File.WriteAllText(Path.Combine(_root, "Other name.lrc"), "[00:01.00]x");

        Assert.Null(LyricsLocator.Find(song));
    }
    [Fact]
    public void No_lyrics_file_gives_null()
    {
        Assert.Null(LyricsLocator.Find(Path.Combine(_root, "Nothing.flac")));
    }

    [Fact]
    public async Task Loading_reports_a_file_that_is_too_large_instead_of_reading_it()
    {
        var path = Path.Combine(_root, "big.lrc");
        await File.WriteAllBytesAsync(path, new byte[LrcParser.MaxFileBytes + 1]);

        var result = await LyricsLocator.LoadAsync(path, CancellationToken.None);

        Assert.False(result.Found);
        Assert.Contains("1 MB", result.Problem);
    }

    [Fact]
    public async Task Loading_a_valid_file_returns_synced_lyrics()
    {
        var path = Path.Combine(_root, "ok.lrc");
        await File.WriteAllTextAsync(path, "[00:01.00]one\n[00:02.00]two");

        var result = await LyricsLocator.LoadAsync(path, CancellationToken.None);

        Assert.True(result.Found);
        Assert.True(result.Document!.IsSynced);
    }

    [Fact]
    public void A_settings_file_from_the_removed_picker_build_still_loads_and_keeps_its_other_values()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """
            { "schemaVersion": 3, "libraryFolders": ["D:\\Music\\A"], "volume": 0.4,
              "lyricsFiles": { "d:\\music\\a\\song.flac": "d:\\lyrics\\other.lrc" } }
            """);

        using var store = new SettingsStore(path);

        Assert.Null(store.LoadIssue);
        Assert.Equal(["D:\\Music\\A"], store.Current.LibraryFolders);
        Assert.Equal(0.4, store.Current.Volume);
    }
}