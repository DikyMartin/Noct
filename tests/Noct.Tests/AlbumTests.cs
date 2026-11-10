using Noct.Core.Library;
using Noct.Core.Settings;

namespace Noct.Tests;

public sealed class AlbumTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noct-album-" + Guid.NewGuid().ToString("N"));

    public AlbumTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }

    /// <summary>Copies a small reference FLAC into <paramref name="relativeFolder"/> and returns its path.</summary>
    private string AddFile(string relativeFolder, string fileName)
    {
        var folder = Path.Combine(_root, relativeFolder);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        File.Copy(TestData.Flac("silence_16_44100_stereo"), path, overwrite: true);
        return path;
    }

    private static Track Synthetic(string path, string? album = null, int? number = null, int? disc = null, string? artist = null) => new()
    {
        Id = Track.StableId(path),
        Path = path,
        Title = Path.GetFileNameWithoutExtension(path),
        Album = album,
        TrackNumber = number,
        DiscNumber = disc,
        Artist = artist,
        SampleRate = 44100,
        Channels = 2,
        BitsPerSample = 16,
        TotalSamples = 44100,
    };

    private async Task<ScanResult> Scan(string[] roots, IReadOnlyDictionary<Guid, Track>? existing = null, string[]? excluded = null) =>
        await new LibraryScanner().ScanAsync(roots, null, default, existing, excluded);

    // ----- one folder, one album -----

    [Fact]
    public void Different_album_tags_inside_one_folder_stay_one_album_named_after_the_folder()
    {
        var tracks = new[]
        {
            Synthetic(@"D:\Music\Night Drive\1.flac", album: "First Tag"),
            Synthetic(@"D:\Music\Night Drive\2.flac", album: "Second Tag"),
            Synthetic(@"D:\Music\Night Drive\3.flac", album: null),
        };

        var album = Assert.Single(AlbumBuilder.Build(tracks));

        Assert.Equal("Night Drive", album.Title);
        Assert.Equal(3, album.Tracks.Count);
    }

    [Fact]
    public async Task A_parent_folder_makes_every_subfolder_with_flac_its_own_album()
    {
        AddFile("Collection\\Album One", "a.flac");
        AddFile("Collection\\Album One", "b.flac");
        AddFile("Collection\\Album Two", "a.flac");
        AddFile("Collection", "loose.flac");
        Directory.CreateDirectory(Path.Combine(_root, "Collection", "Empty folder"));

        var scan = await Scan([Path.Combine(_root, "Collection")]);
        var albums = AlbumBuilder.Build(scan.Tracks);

        Assert.Equal(["Album One", "Album Two", "Collection"], albums.Select(a => a.Title));
        Assert.Equal(2, albums.Single(a => a.Title == "Album One").Tracks.Count);
        Assert.DoesNotContain(albums, a => a.Title == "Empty folder");
    }

    [Fact]
    public async Task Folders_with_the_same_name_in_different_places_are_different_albums()
    {
        AddFile("Lossless\\Greatest Hits", "a.flac");
        AddFile("Archive\\Greatest Hits", "a.flac");

        var scan = await Scan([_root]);
        var albums = AlbumBuilder.Build(scan.Tracks).Where(a => a.Title == "Greatest Hits").ToList();

        Assert.Equal(2, albums.Count);
        Assert.NotEqual(albums[0].Id, albums[1].Id);
        Assert.NotEqual(albums[0].FolderPath, albums[1].FolderPath);
    }

    [Fact]
    public void Album_identity_ignores_case_and_slash_style_of_the_same_location()
    {
        var a = AlbumBuilder.Build([Synthetic(@"D:\Music\Jazz\1.flac")]).Single();
        var b = AlbumBuilder.Build([Synthetic("d:/music/JAZZ/2.flac")]).Single();

        Assert.Equal(a.Id, b.Id);
    }

    [Fact]
    public void Album_artist_is_the_shared_artist_or_various_artists()
    {
        var same = AlbumBuilder.Build([Synthetic(@"D:\A\1.flac", artist: "Kiana"), Synthetic(@"D:\A\2.flac", artist: "kiana")]).Single();
        var mixed = AlbumBuilder.Build([Synthetic(@"D:\B\1.flac", artist: "Kiana"), Synthetic(@"D:\B\2.flac", artist: "Orla")]).Single();
        var untagged = AlbumBuilder.Build([Synthetic(@"D:\C\1.flac")]).Single();

        Assert.Equal("Kiana", same.Artist);
        Assert.Equal(Album.VariousArtists, mixed.Artist);
        Assert.Null(untagged.Artist);
    }

    // ----- overlap, re-import, exclusion -----

    [Fact]
    public async Task Overlapping_source_folders_never_duplicate_tracks_or_albums()
    {
        AddFile("Library\\Jazz", "a.flac");
        AddFile("Library\\Jazz", "b.flac");
        AddFile("Library\\Rock", "a.flac");

        var parent = Path.Combine(_root, "Library");
        var child = Path.Combine(parent, "Jazz");
        var scan = await Scan([parent, child, parent]);

        Assert.Equal(3, scan.Tracks.Count);
        Assert.Equal(3, scan.Tracks.Select(t => t.Id).Distinct().Count());
        Assert.Equal(2, AlbumBuilder.Build(scan.Tracks).Count);
    }

    [Fact]
    public async Task Importing_the_same_folder_again_changes_nothing()
    {
        AddFile("Library\\Jazz", "a.flac");
        AddFile("Library\\Rock", "a.flac");
        var index = new LibraryIndex(Path.Combine(_root, "library.json"));
        var roots = new[] { Path.Combine(_root, "Library") };

        var first = await Scan(roots);
        index.Merge(first.Tracks, first.Problems, first.Roots);
        var tracksBefore = index.Count;
        var albumsBefore = index.Albums().Count;

        var second = await Scan(roots, index.Snapshot());
        index.Merge(second.Tracks, second.Problems, second.Roots);

        Assert.Equal(tracksBefore, index.Count);
        Assert.Equal(albumsBefore, index.Albums().Count);
    }

    [Fact]
    public async Task A_rescan_reuses_the_stored_entries_instead_of_reading_files_again()
    {
        AddFile("Library\\Jazz", "a.flac");
        var roots = new[] { Path.Combine(_root, "Library") };

        var first = await Scan(roots);
        var second = await Scan(roots, first.Tracks.ToDictionary(t => t.Id));

        Assert.Same(first.Tracks[0], second.Tracks[0]);
    }

    [Fact]
    public async Task A_removed_album_stays_out_of_later_scans_and_its_files_stay_on_disk()
    {
        var kept = AddFile("Library\\Kept", "a.flac");
        var removed = AddFile("Library\\Removed", "a.flac");
        var roots = new[] { Path.Combine(_root, "Library") };
        var index = new LibraryIndex(Path.Combine(_root, "library.json"));
        var scan = await Scan(roots);
        index.Merge(scan.Tracks, scan.Problems, scan.Roots);

        var removedCount = index.RemoveAlbum(Path.GetDirectoryName(removed)!);
        var rescan = await Scan(roots, index.Snapshot(), excluded: [Path.GetDirectoryName(removed)!]);
        index.Merge(rescan.Tracks, rescan.Problems, rescan.Roots);

        Assert.Equal(1, removedCount);
        Assert.Equal(["Kept"], index.Albums().Select(a => a.Title));
        Assert.True(File.Exists(removed), "Removing an album must never delete the music file.");
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public async Task Removing_one_source_keeps_albums_that_another_source_still_covers()
    {
        AddFile("Library\\Jazz", "a.flac");
        AddFile("Elsewhere\\Folk", "a.flac");
        var library = Path.Combine(_root, "Library");
        var jazz = Path.Combine(library, "Jazz");
        var folk = Path.Combine(_root, "Elsewhere");
        var index = new LibraryIndex(Path.Combine(_root, "library.json"));
        var scan = await Scan([library, jazz, folk]);
        index.Merge(scan.Tracks, scan.Problems, scan.Roots);

        index.RemoveFolder(library, [jazz, folk]);

        Assert.Equal(["Folk", "Jazz"], index.Albums().Select(a => a.Title).Order());

        index.RemoveFolder(jazz, [folk]);

        Assert.Equal(["Folk"], index.Albums().Select(a => a.Title));
    }

    [Fact]
    public async Task The_album_list_is_rebuilt_only_when_the_tracks_change()
    {
        AddFile("Library\\Jazz", "a.flac");
        var index = new LibraryIndex(Path.Combine(_root, "library.json"));
        var scan = await Scan([_root]);
        index.Merge(scan.Tracks, scan.Problems, scan.Roots);

        var first = index.Albums();
        var second = index.Albums();
        Assert.Same(first, second);

        AddFile("Library\\Rock", "a.flac");
        var again = await Scan([_root]);
        index.Merge(again.Tracks, again.Problems, again.Roots);

        Assert.NotSame(first, index.Albums());
        Assert.Equal(2, index.Albums().Count);
    }

    [Fact]
    public async Task Albums_and_folders_survive_saving_and_loading_the_index()
    {
        AddFile("Library\\Jazz", "a.flac");
        AddFile("Library\\Rock", "a.flac");
        var path = Path.Combine(_root, "library.json");
        var written = new LibraryIndex(path);
        var scan = await Scan([Path.Combine(_root, "Library")]);
        written.Merge(scan.Tracks, scan.Problems, scan.Roots);
        written.Save();

        var read = new LibraryIndex(path);
        read.Load();

        Assert.Equal(written.Albums().Select(a => a.Id), read.Albums().Select(a => a.Id));
    }

    // ----- track order -----

    [Fact]
    public void Tracks_play_by_number_then_by_natural_file_name()
    {
        var tracks = new List<Track>
        {
            Synthetic(@"D:\A\10 Outro.flac"),
            Synthetic(@"D:\A\2 Middle.flac"),
            Synthetic(@"D:\A\zzz numbered last.flac", number: 1),
            Synthetic(@"D:\A\1 Intro.flac"),
            Synthetic(@"D:\A\aaa numbered first.flac", number: 1),
        };

        var ordered = AlbumBuilder.Build(tracks).Single().Tracks.Select(t => Path.GetFileName(t.Path));

        Assert.Equal(["aaa numbered first.flac", "zzz numbered last.flac", "1 Intro.flac", "2 Middle.flac", "10 Outro.flac"], ordered);
    }

    [Fact]
    public void Disc_number_groups_tracks_before_track_number()
    {
        var tracks = new List<Track>
        {
            Synthetic(@"D:\A\d2t1.flac", number: 1, disc: 2),
            Synthetic(@"D:\A\d1t2.flac", number: 2, disc: 1),
            Synthetic(@"D:\A\d1t1.flac", number: 1, disc: 1),
        };

        var ordered = AlbumBuilder.Build(tracks).Single().Tracks.Select(t => Path.GetFileNameWithoutExtension(t.Path));

        Assert.Equal(["d1t1", "d1t2", "d2t1"], ordered);
    }

    [Theory]
    [InlineData("2", "10", -1)]
    [InlineData("10", "2", 1)]
    [InlineData("track 02", "track 2", 0)]
    [InlineData("Alpha", "alpha", 0)]
    [InlineData("a1b", "a1c", -1)]
    public void Natural_comparison_reads_digit_runs_as_numbers(string left, string right, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(NaturalStringComparer.Instance.Compare(left, right)));
    }

    // ----- cover files -----

    [Fact]
    public void Cover_file_priority_prefers_cover_over_folder_over_front_and_jpg_over_png()
    {
        var folder = Path.Combine(_root, "Covers");
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "front.jpg", "folder.png", "cover.png", "random.jpg", "notes.txt" })
        {
            File.WriteAllBytes(Path.Combine(folder, name), [1]);
        }

        Assert.Equal("cover.png", Path.GetFileName(AlbumCover.Find(folder)));

        File.WriteAllBytes(Path.Combine(folder, "cover.jpg"), [1]);
        Assert.Equal("cover.jpg", Path.GetFileName(AlbumCover.Find(folder)));
    }

    [Fact]
    public void A_folder_without_a_recognised_cover_name_has_no_cover_file()
    {
        var folder = Path.Combine(_root, "NoCover");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "holiday.jpg"), [1]);

        Assert.Null(AlbumCover.Find(folder));
        Assert.Null(AlbumCover.Find(Path.Combine(_root, "does-not-exist")));
    }

    // ----- storage and migration -----

    [Fact]
    public void Many_folders_persist_in_order_and_load_again_after_a_restart()
    {
        var path = Path.Combine(_root, "settings.json");
        string[] folders = [@"D:\Music\One", @"E:\Lossless\Two", @"D:\Music\Three"];

        using (var store = new SettingsStore(path))
        {
            store.Update(s => s.LibraryFolders.AddRange(folders));
            store.Update(s => s.RemovedAlbumFolders.Add(@"D:\Music\One\Skipped"));
        }

        using var reopened = new SettingsStore(path);

        Assert.Equal(folders, reopened.Current.LibraryFolders);
        Assert.Equal([@"D:\Music\One\Skipped"], reopened.Current.RemovedAlbumFolders);
    }

    [Fact]
    public void A_version_1_settings_file_keeps_every_folder_and_is_upgraded_on_save()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """
            { "schemaVersion": 1, "libraryFolders": ["D:\\Music\\Old A", "D:\\Music\\Old B"], "volume": 0.3 }
            """);

        using (var store = new SettingsStore(path))
        {
            Assert.Null(store.LoadIssue);
            Assert.Equal([@"D:\Music\Old A", @"D:\Music\Old B"], store.Current.LibraryFolders);
            Assert.Equal(0.3, store.Current.Volume);
            Assert.Empty(store.Current.RemovedAlbumFolders);
        }

        var upgraded = File.ReadAllText(path);
        Assert.Contains("\"schemaVersion\": 3", upgraded);
        Assert.Contains("Old A", upgraded);
        Assert.Contains("Old B", upgraded);
    }

    [Fact]
    public async Task A_version_1_library_index_still_loads_with_all_its_tracks()
    {
        AddFile("Library\\Jazz", "a.flac");
        var scan = await Scan([_root]);
        var path = Path.Combine(_root, "library.json");
        JsonStore.Save(path, new LibraryDocument { SchemaVersion = 1, Tracks = [.. scan.Tracks] });

        var index = new LibraryIndex(path);
        index.Load();

        Assert.Null(index.LoadIssue);
        Assert.Equal(scan.Tracks.Count, index.Count);
        Assert.Contains(index.Albums(), a => a.Title == "Jazz");
    }
}
