using Noct.Core.Library;
using Noct.Core.Settings;

namespace Noct.Tests;

public sealed class LibraryTests : IDisposable
{
    // Escapes rather than literals, so the test is independent of the file encoding on any editor.
    private const string JapaneseWord = "\u30C6\u30B9\u30C8";   // test
    private const string ChineseWord = "\u6D4B\u8BD5";          // test

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "noct-tests-" + Guid.NewGuid().ToString("N"));

    public LibraryTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of temp files.
        }
    }

    private static async Task<ScanResult> ScanReference(CancellationToken token = default) =>
        await new LibraryScanner().ScanAsync([TestData.Directory], null, token);

    [Fact]
    public async Task Scan_indexes_real_flac_files_and_reports_the_unreadable_one_by_name()
    {
        var result = await ScanReference();

        Assert.False(result.Cancelled);
        Assert.Contains(result.Tracks, t => Path.GetFileName(t.Path) == "tone_16_44100_stereo.flac");

        var problem = Assert.Single(result.Problems);
        Assert.Equal("not_audio.flac", Path.GetFileName(problem.Path));
        Assert.False(string.IsNullOrWhiteSpace(problem.Reason));

        // One bad file must not stop the rest from being indexed.
        Assert.True(result.Tracks.Count >= 15);
    }

    [Fact]
    public async Task Scan_reads_stream_properties_from_the_file_not_from_the_name()
    {
        var result = await ScanReference();

        var hiRes = result.Tracks.Single(t => Path.GetFileName(t.Path) == "tone_24_96000_stereo.flac");
        Assert.Equal(96000, hiRes.SampleRate);
        Assert.Equal(24, hiRes.BitsPerSample);
        Assert.Equal(2, hiRes.Channels);
        Assert.Equal(48000, hiRes.TotalSamples);
        Assert.Equal(TimeSpan.FromSeconds(0.5), hiRes.Duration);

        var quad = result.Tracks.Single(t => Path.GetFileName(t.Path) == "tone_16_88200_quad.flac");
        Assert.Equal(4, quad.Channels);
    }

    [Fact]
    public async Task Japanese_and_chinese_tags_survive_scanning_unchanged()
    {
        var result = await ScanReference();

        var track = result.Tracks.Single(t => Path.GetFileName(t.Path) == "tone_16_44100_stereo.flac");

        Assert.EndsWith($"{JapaneseWord} {ChineseWord}", track.Title, StringComparison.Ordinal);
        Assert.False(track.TitleFromFileName);
    }

    [Fact]
    public async Task Embedded_artwork_is_detected()
    {
        var result = await ScanReference();

        Assert.True(result.Tracks.Single(t => Path.GetFileName(t.Path) == "withart_16_44100_stereo.flac").HasEmbeddedArtwork);
        Assert.False(result.Tracks.Single(t => Path.GetFileName(t.Path) == "tone_16_44100_stereo.flac").HasEmbeddedArtwork);
    }

    [Fact]
    public void Missing_title_tag_falls_back_to_the_file_name()
    {
        // The reference files all carry tags, so feed the rule an empty tag set for the same stream.
        var untagged = Path.Combine(_temp, "no title here.flac");
        File.Copy(TestData.Flac("silence_16_44100_stereo"), untagged);

        var fallback = Track.FromMetadata(
            untagged,
            new Noct.Core.Audio.FlacMetadata
            {
                StreamInfo = Noct.Core.Audio.FlacDecoder.ReadMetadata(untagged).StreamInfo,
                Tags = Noct.Core.Audio.FlacTags.Empty,
                SeekPoints = [],
                FirstFrameOffset = 0,
            },
            new FileInfo(untagged));

        Assert.Equal("no title here", fallback.Title);
        Assert.True(fallback.TitleFromFileName);
    }

    [Fact]
    public async Task Scanning_does_not_modify_the_music_files()
    {
        var file = TestData.Flac("tone_16_44100_stereo");
        var before = File.GetLastWriteTimeUtc(file);
        var lengthBefore = new FileInfo(file).Length;

        await ScanReference();

        Assert.Equal(before, File.GetLastWriteTimeUtc(file));
        Assert.Equal(lengthBefore, new FileInfo(file).Length);
    }

    [Fact]
    public async Task A_scan_cancelled_before_it_starts_reports_cancelled_instead_of_throwing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await ScanReference(cts.Token);

        Assert.True(result.Cancelled);
    }

    [Fact]
    public async Task A_scan_cancelled_mid_flight_stops_promptly()
    {
        // Enough copies that the scan cannot finish before the cancel lands.
        var source = TestData.Flac("silence_16_44100_stereo");
        for (var i = 0; i < 600; i++)
        {
            File.Copy(source, Path.Combine(_temp, $"copy-{i:D4}.flac"));
        }

        using var cts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(p =>
        {
            if (p.Found >= 5)
            {
                cts.Cancel();
            }
        });

        var result = await new LibraryScanner().ScanAsync([_temp], progress, cts.Token);

        Assert.True(result.Cancelled);
        Assert.True(result.Tracks.Count < 600);
    }

    [Fact]
    public async Task A_missing_folder_is_reported_as_a_problem()
    {
        var result = await new LibraryScanner().ScanAsync([Path.Combine(_temp, "does-not-exist")]);

        Assert.Empty(result.Tracks);
        Assert.Single(result.Problems);
    }

    [Fact]
    public async Task Search_matches_unicode_case_insensitively_and_requires_every_term()
    {
        var index = new LibraryIndex(Path.Combine(_temp, "library.json"));
        var scan = await ScanReference();
        index.Merge(scan.Tracks, scan.Problems, scan.Roots);

        Assert.NotEmpty(index.Search(JapaneseWord));
        Assert.NotEmpty(index.Search(ChineseWord));
        Assert.NotEmpty(index.Search("TONE 44100"));
        Assert.Empty(index.Search("tone zzz-not-present"));
        Assert.Equal(index.Count, index.Search("   ").Count);
    }

    [Fact]
    public async Task Index_round_trips_through_disk_including_unicode()
    {
        var path = Path.Combine(_temp, "library.json");
        var scan = await ScanReference();

        var written = new LibraryIndex(path);
        written.Merge(scan.Tracks, scan.Problems, scan.Roots);
        written.Save();

        var read = new LibraryIndex(path);
        read.Load();

        Assert.Equal(written.Count, read.Count);
        Assert.Null(read.LoadIssue);
        Assert.NotEmpty(read.Search(JapaneseWord));
        Assert.Contains(read.Problems, p => p.Path.EndsWith("not_audio.flac", StringComparison.Ordinal));
        Assert.False(File.Exists(path + ".tmp"), "Atomic save left its temporary file behind.");
    }

    [Fact]
    public async Task Rescanning_a_folder_replaces_its_old_entries_instead_of_duplicating_them()
    {
        var index = new LibraryIndex(Path.Combine(_temp, "library.json"));
        var scan = await ScanReference();

        index.Merge(scan.Tracks, scan.Problems, scan.Roots);
        var first = index.Count;
        index.Merge(scan.Tracks, scan.Problems, scan.Roots);

        Assert.Equal(first, index.Count);
        Assert.Single(index.Problems);
    }

    [Fact]
    public async Task Removing_a_folder_drops_only_its_tracks()
    {
        var index = new LibraryIndex(Path.Combine(_temp, "library.json"));
        var scan = await ScanReference();
        index.Merge(scan.Tracks, scan.Problems, scan.Roots);

        index.RemoveFolder(TestData.Directory);

        Assert.Equal(0, index.Count);
        Assert.Empty(index.Problems);
    }

    [Fact]
    public void Corrupt_index_file_falls_back_to_empty_with_an_explanation()
    {
        var path = Path.Combine(_temp, "library.json");
        File.WriteAllText(path, "{ this is not json");

        var index = new LibraryIndex(path);
        index.Load();

        Assert.Equal(0, index.Count);
        Assert.NotNull(index.LoadIssue);
    }

    [Fact]
    public void A_newer_schema_is_left_on_disk_untouched_and_defaults_are_used()
    {
        var path = Path.Combine(_temp, "settings.json");
        const string future = "{ \"schemaVersion\": 99, \"volume\": 0.1 }";
        File.WriteAllText(path, future);

        using var store = new SettingsStore(path);

        Assert.NotNull(store.LoadIssue);
        Assert.Equal(new AppSettings().Volume, store.Current.Volume);
        Assert.Equal(future, File.ReadAllText(path));
    }

    [Fact]
    public void Settings_changes_are_persisted_and_reloaded()
    {
        var path = Path.Combine(_temp, "settings.json");

        using (var store = new SettingsStore(path))
        {
            store.Update(s =>
            {
                s.Volume = 0.42;
                s.LibraryFolders = [@"D:\Music"];
                s.IslandEnabled = false;
            });
            store.Flush();
        }

        using var reloaded = new SettingsStore(path);

        Assert.Equal(0.42, reloaded.Current.Volume);
        Assert.Equal([@"D:\Music"], reloaded.Current.LibraryFolders);
        Assert.False(reloaded.Current.IslandEnabled);
        Assert.False(File.Exists(path + ".tmp"));
    }
}
