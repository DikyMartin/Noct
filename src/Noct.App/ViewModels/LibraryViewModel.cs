using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Noct.Core.Library;
using Noct.Core.Playback;
using Noct.Core.Settings;

namespace Noct.App.ViewModels;

/// <summary>A library row. Shared between the full list and filtered views of it.</summary>
public sealed class TrackRowViewModel : ViewModelBase
{
    private bool _isCurrent;
    private int _number;

    public TrackRowViewModel(Track track)
    {
        Track = track;
    }

    public Track Track { get; }

    public string Title => Track.Title;

    public string Artist => Track.DisplayArtist;

    public string Album => Track.DisplayAlbum;

    /// <summary>Plain text for the row's tooltip (a string, so no tooltip visuals are built for rows that are never hovered).</summary>
    public string TooltipText => $"{Title}\n{Artist} · {Album}\n{Track.Path}";

    public string FormatText => TextFormat.CompactFormat(Track.BitsPerSample, Track.SampleRate);

    public string DurationText => Track.Duration > TimeSpan.Zero ? TextFormat.Duration(Track.Duration) : "–";

    public int Number
    {
        get => _number;
        set => Set(ref _number, value);
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }
}

public sealed record ProblemRow(string FileName, string Folder, string Reason);

/// <summary>
/// The library page: folders, scanning, search and the track list.
/// </summary>
/// <remarks>
/// Scanning runs on a worker with cancellation and reports progress; the list updates only when a
/// scan finishes, so a half-finished scan never leaves the list in a half-built state. A file that
/// cannot be read is listed by name with the reason instead of being dropped silently.
/// </remarks>
public sealed class LibraryViewModel : ViewModelBase, IDisposable
{
    private readonly LibraryIndex _index;
    private readonly SettingsStore _settings;
    private readonly PlaybackEngine _engine;
    private readonly PlaybackViewModel _playback;
    private readonly Dispatcher _dispatcher;
    private readonly LibraryScanner _scanner = new();
    private readonly DispatcherTimer _searchDebounce;
    private readonly ArtworkService _artwork;
    private readonly Dictionary<Guid, AlbumCardViewModel> _cards = [];
    private readonly Dictionary<string, IReadOnlyList<TrackRowViewModel>> _auxiliaryRows = [];
    private readonly List<string> _pendingScanRoots = [];

    private LibraryMode _mode = LibraryMode.Albums;
    private IReadOnlyList<AlbumCardViewModel> _visibleCards = [];
    private IReadOnlyList<AlbumRowViewModel> _albumRows = [];
    private int _albumColumns = 4;
    private AlbumDetailViewModel? _selectedAlbum;

    private Dictionary<Guid, TrackRowViewModel> _rows = [];
    private IReadOnlyList<TrackRowViewModel> _visible = [];
    private TrackRowViewModel? _currentRow;
    private CancellationTokenSource? _scanCancellation;

    private string _searchText = string.Empty;
    private bool _isScanning;
    private string _scanStatus = string.Empty;
    private IReadOnlyList<ProblemRow> _problems = [];
    private bool _showProblems;
    private string? _loadIssue;

    public LibraryViewModel(
        LibraryIndex index,
        SettingsStore settings,
        PlaybackEngine engine,
        PlaybackViewModel playback,
        ArtworkService artwork,
        Dispatcher dispatcher)
    {
        _artwork = artwork;
        _index = index;
        _settings = settings;
        _engine = engine;
        _playback = playback;
        _dispatcher = dispatcher;

        _searchDebounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(160) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            ApplyFilter();
        };

        AddFolderCommand = new RelayCommand(AddFolder);
        RescanCommand = new RelayCommand(() => _ = ScanAsync(), () => !IsScanning && _settings.Current.LibraryFolders.Count > 0);
        CancelScanCommand = new RelayCommand(() => _scanCancellation?.Cancel(), () => IsScanning);
        ToggleProblemsCommand = new RelayCommand(() => ShowProblems = !ShowProblems);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);

        OpenAlbumCommand = new RelayCommand(p =>
        {
            if (p is AlbumCardViewModel card)
            {
                OpenAlbum(card.Album);
            }
        });
        CloseAlbumCommand = new RelayCommand(CloseAlbum);
        PlayAlbumCommand = new RelayCommand(p => PlayAlbum(AlbumOf(p), shuffle: false));
        ShuffleAlbumCommand = new RelayCommand(p => PlayAlbum(AlbumOf(p), shuffle: true));
        RemoveAlbumCommand = new RelayCommand(p => RemoveAlbum(AlbumOf(p)));

        _playback.CurrentTrackChanged += OnCurrentTrackChanged;
        _playback.CanStartFromLibrary = () => _visible.Count > 0;
        _playback.StartFromLibrary = PlayAllVisible;
    }

    public ICommand AddFolderCommand { get; }

    public ICommand RescanCommand { get; }

    public ICommand CancelScanCommand { get; }

    public ICommand ToggleProblemsCommand { get; }

    public ICommand ClearSearchCommand { get; }

    public ICommand OpenAlbumCommand { get; }

    public ICommand CloseAlbumCommand { get; }

    public ICommand PlayAlbumCommand { get; }

    public ICommand ShuffleAlbumCommand { get; }

    public ICommand RemoveAlbumCommand { get; }

    public IReadOnlyList<TrackRowViewModel> VisibleRows
    {
        get => _visible;
        private set
        {
            if (Set(ref _visible, value))
            {
                Raise(nameof(HasVisibleRows));
                Raise(nameof(NoResults));
                Raise(nameof(IsEmpty));
                Raise(nameof(CountText));
            }
        }
    }

    public bool HasVisibleRows => _visible.Count > 0;

    public bool HasSearchText => !string.IsNullOrEmpty(_searchText);

    /// <summary>True only when the library has no tracks at all and nothing is being scanned.</summary>
    public bool IsEmpty => _rows.Count == 0 && !IsScanning;

    public bool HasTracks => _rows.Count > 0;

    /// <summary>A search that matched nothing, as opposed to an empty library.</summary>
    public bool NoResults =>
        _rows.Count > 0 && _selectedAlbum is null &&
        (_mode == LibraryMode.Songs ? _visible.Count == 0 : _visibleCards.Count == 0);

    /// <summary>True when the Albums / All songs switch applies: there is a library and no album is open.</summary>
    public bool ShowModeSwitch => HasTracks && _selectedAlbum is null;

    public string CountText
    {
        get
        {
            if (_rows.Count == 0)
            {
                return string.Empty;
            }

            if (_mode == LibraryMode.Albums)
            {
                var total = _index.Albums().Count;
                return _visibleCards.Count == total
                    ? (total == 1 ? "1 album" : $"{total:N0} albums")
                    : $"{_visibleCards.Count:N0} of {total:N0}";
            }

            return _visible.Count == _rows.Count
                ? (_rows.Count == 1 ? "1 track" : $"{_rows.Count:N0} tracks")
                : $"{_visible.Count:N0} of {_rows.Count:N0}";
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
            {
                // A search is for the listing, not for the inside of one album.
                if (_selectedAlbum is not null && !string.IsNullOrEmpty(value))
                {
                    CloseAlbum();
                }

                Raise(nameof(HasSearchText));
                _searchDebounce.Stop();
                _searchDebounce.Start();
            }
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (Set(ref _isScanning, value))
            {
                Raise(nameof(IsEmpty));
                (RescanCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (CancelScanCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string ScanStatus
    {
        get => _scanStatus;
        private set => Set(ref _scanStatus, value);
    }

    public IReadOnlyList<ProblemRow> Problems
    {
        get => _problems;
        private set
        {
            if (Set(ref _problems, value))
            {
                Raise(nameof(HasProblems));
                Raise(nameof(ProblemsSummary));
            }
        }
    }

    public bool HasProblems => _problems.Count > 0;

    public string ProblemsSummary => _problems.Count == 1
        ? "1 file could not be read"
        : $"{_problems.Count} files could not be read";

    public bool ShowProblems
    {
        get => _showProblems;
        set => Set(ref _showProblems, value);
    }

    /// <summary>A message when the saved index could not be read and a fresh one is in use.</summary>
    public string? LoadIssue
    {
        get => _loadIssue;
        private set
        {
            if (Set(ref _loadIssue, value))
            {
                Raise(nameof(HasLoadIssue));
            }
        }
    }

    public bool HasLoadIssue => !string.IsNullOrEmpty(_loadIssue);

    public IReadOnlyList<string> Folders => _settings.Current.LibraryFolders;

    public event EventHandler? FoldersChanged;

    /// <summary>Raised on the UI thread after the track list or albums were rebuilt.</summary>
    public event EventHandler? LibraryChanged;

    /// <summary>
    /// Loads the saved index off the UI thread, shows it, then refreshes it from disk in the
    /// background. Unchanged files are reused from the index, so this is a stat per file, not a read.
    /// </summary>
    public async Task InitializeAsync()
    {
        await Task.Run(_index.Load);
        LoadIssue = _index.LoadIssue;
        RebuildRows();
        UpdateProblems();

        if (_settings.Current.LibraryFolders.Count > 0)
        {
            _ = ScanAsync();
        }
    }

    /// <summary>Resolves saved queue paths back to indexed tracks, skipping files that are gone.</summary>
    public IReadOnlyList<Track> ResolvePaths(IEnumerable<string> paths)
    {
        var result = new List<Track>();
        foreach (var path in paths)
        {
            if (_index.FindByPath(path) is { } track)
            {
                result.Add(track);
            }
        }

        return result;
    }

    public void AddFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Add a music folder",
            Multiselect = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        AddFolders(dialog.FolderNames);
    }

    public void AddFolders(IEnumerable<string> folders)
    {
        // Every Add folder is a new source, kept in the order it was added. Adding a folder that is
        // already a source changes nothing; an overlapping parent or child is allowed because the
        // scanner indexes each file once.
        var added = new List<string>();
        var requested = folders.Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f))).ToList();

        _settings.Update(s =>
        {
            foreach (var full in requested)
            {
                // Adding a folder on purpose brings back albums inside it that were removed earlier.
                var key = Track.NormalizePath(full);
                s.RemovedAlbumFolders.RemoveAll(removed =>
                {
                    var removedKey = Track.NormalizePath(removed);
                    return removedKey == key || removedKey.StartsWith(key + "\\", StringComparison.Ordinal);
                });

                if (!s.LibraryFolders.Any(existing => Track.NormalizePath(existing) == key))
                {
                    s.LibraryFolders.Add(full);
                    added.Add(full);
                }
            }
        });

        if (requested.Count > 0)
        {
            FoldersChanged?.Invoke(this, EventArgs.Empty);
            (RescanCommand as RelayCommand)?.RaiseCanExecuteChanged();

            // Only the folders just added are read; the rest of the library is already indexed.
            _ = ScanAsync(requested);
        }
    }

    public void RemoveFolder(string folder)
    {
        // Removing a source drops only its record in Noct. Music files are never touched, and albums
        // that another remaining source folder still covers are kept.
        _settings.Update(s => s.LibraryFolders.RemoveAll(f => Track.NormalizePath(f) == Track.NormalizePath(folder)));
        _index.RemoveFolder(folder, _settings.Current.LibraryFolders.ToArray());
        _index.Save();
        RebuildRows();
        UpdateProblems();
        FoldersChanged?.Invoke(this, EventArgs.Empty);
        (RescanCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <param name="only">Folders to scan; null scans every source folder.</param>
    public async Task ScanAsync(IReadOnlyList<string>? only = null)
    {
        var roots = (only ?? _settings.Current.LibraryFolders).ToArray();
        if (roots.Length == 0)
        {
            return;
        }

        if (IsScanning)
        {
            // Never drop a folder because a scan was already running: scan it right after.
            _pendingScanRoots.AddRange(roots);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        IsScanning = true;
        ScanStatus = "Looking for FLAC files…";

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                ScanStatus = p.Enumerating
                    ? $"Looking for FLAC files… {p.Examined:N0} found"
                    : $"Reading {p.Examined:N0} files · {p.Failed:N0} unreadable";
            });

            var result = await _scanner.ScanAsync(
                roots,
                progress,
                cancellation.Token,
                _index.Snapshot(),
                _settings.Current.RemovedAlbumFolders.ToArray());

            if (result.Cancelled)
            {
                ScanStatus = "Scan cancelled. The previous library was kept.";
                return;
            }

            _index.Merge(result.Tracks, result.Problems, result.Roots);
            await Task.Run(_index.Save);
            RebuildRows();
            UpdateProblems();
            ScanStatus = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ScanStatus = $"Scan stopped: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            _scanCancellation = null;
            cancellation.Dispose();
        }

        if (_pendingScanRoots.Count > 0)
        {
            var next = _pendingScanRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _pendingScanRoots.Clear();
            await ScanAsync(next);
        }
    }

    // ----- Albums -----

    public LibraryMode Mode
    {
        get => _mode;
        set
        {
            if (Set(ref _mode, value))
            {
                CloseAlbum();
                Raise(nameof(CountText));
                Raise(nameof(ShowAlbumGrid));
                Raise(nameof(ShowSongList));
                Raise(nameof(NoResults));
            }
        }
    }

    public IReadOnlyList<AlbumRowViewModel> AlbumRows
    {
        get => _albumRows;
        private set => Set(ref _albumRows, value);
    }

    public bool HasAlbums => _visibleCards.Count > 0;

    public int AlbumCount => _index.Albums().Count;

    public AlbumDetailViewModel? SelectedAlbum
    {
        get => _selectedAlbum;
        private set
        {
            if (Set(ref _selectedAlbum, value))
            {
                Raise(nameof(HasSelectedAlbum));
                Raise(nameof(ShowModeSwitch));
                Raise(nameof(ShowAlbumGrid));
                Raise(nameof(ShowSongList));
                Raise(nameof(NoResults));
            }
        }
    }

    public bool HasSelectedAlbum => _selectedAlbum is not null;

    public bool ShowAlbumGrid => _mode == LibraryMode.Albums && _selectedAlbum is null && HasAlbums;

    public bool ShowSongList => _mode == LibraryMode.Songs && _selectedAlbum is null && HasVisibleRows;

    /// <summary>Set by the view from its width; the grid is rebuilt only when the column count changes.</summary>
    public void SetAlbumColumns(int columns)
    {
        columns = Math.Clamp(columns, 1, 12);
        if (columns != _albumColumns)
        {
            _albumColumns = columns;
            LayoutAlbumRows();
        }
    }

    public AlbumCardViewModel CardFor(Album album) => GetCard(album);

    public void OpenAlbum(Album album)
    {
        // A search belongs to the list it was typed into; opening an album shows that whole album.
        var rows = album.Tracks.Select((track, i) => new TrackRowViewModel(track) { Number = i + 1 }).ToArray();
        _auxiliaryRows["album"] = rows;
        SelectedAlbum = new AlbumDetailViewModel(album, rows, _artwork);
        SyncCurrentRow(_playback.CurrentTrackId);
    }

    public void OpenAlbumById(Guid albumId)
    {
        if (_index.FindAlbum(albumId) is { } album)
        {
            _mode = LibraryMode.Albums;
            Raise(nameof(Mode));
            OpenAlbum(album);
        }
    }

    public void CloseAlbum()
    {
        _auxiliaryRows.Remove("album");
        SelectedAlbum = null;
    }

    public void PlayAlbum(Album? album, bool shuffle)
    {
        if (album is null || album.Tracks.Count == 0)
        {
            return;
        }

        _engine.PlayTracks(shuffle ? Shuffler.Shuffled(album.Tracks) : album.Tracks, 0);
    }

    /// <summary>Plays the open album starting at <paramref name="row"/>, in album order.</summary>
    public void PlayFromAlbum(TrackRowViewModel row)
    {
        if (_selectedAlbum is not { } detail)
        {
            return;
        }

        var index = detail.Album.Tracks.ToList().FindIndex(t => t.Id == row.Track.Id);
        if (index >= 0)
        {
            _engine.PlayTracks(detail.Album.Tracks, index);
        }
    }

    /// <summary>
    /// Removes an album from Noct's library. The folder and its music files are not touched, and the
    /// folder stays out of future rescans until the user adds it again.
    /// </summary>
    public void RemoveAlbum(Album? album)
    {
        if (album is null)
        {
            return;
        }

        _settings.Update(s =>
        {
            if (!s.RemovedAlbumFolders.Any(f => Track.NormalizePath(f) == Track.NormalizePath(album.FolderPath)))
            {
                s.RemovedAlbumFolders.Add(album.FolderPath);
            }
        });

        CloseAlbum();
        _index.RemoveAlbum(album.FolderPath);
        _index.Save();
        RebuildRows();
        UpdateProblems();
    }

    /// <summary>Shuffles the whole collection into a new queue.</summary>
    public void PlayAllShuffled()
    {
        var all = _index.All();
        if (all.Count > 0)
        {
            _engine.PlayTracks(Shuffler.Shuffled(all), 0);
        }
    }

    /// <summary>Keeps a set of rows owned by another view in step with the playing track.</summary>
    public void TrackRows(string owner, IReadOnlyList<TrackRowViewModel> rows)
    {
        _auxiliaryRows[owner] = rows;
        SyncCurrentRow(_playback.CurrentTrackId);
    }

    public TrackRowViewModel NewRow(Track track, int number)
    {
        var row = new TrackRowViewModel(track) { Number = number };
        row.IsCurrent = track.Id == _playback.CurrentTrackId;
        return row;
    }

    public IReadOnlyList<Album> AllAlbums() => _index.Albums();

    public IReadOnlyList<Track> AllTracks() => _index.All();

    public int TrackCount => _index.Count;

    private static Album? AlbumOf(object? parameter) => parameter switch
    {
        AlbumCardViewModel card => card.Album,
        AlbumDetailViewModel detail => detail.Album,
        Album album => album,
        _ => null,
    };

    private AlbumCardViewModel GetCard(Album album)
    {
        if (!_cards.TryGetValue(album.Id, out var card) || !ReferenceEquals(card.Album, album))
        {
            card = new AlbumCardViewModel(album, _artwork, 152);
            _cards[album.Id] = card;
        }

        return card;
    }

    private void RebuildAlbums()
    {
        var albums = _index.SearchAlbums(_searchText);
        _visibleCards = albums.Select(GetCard).ToArray();

        // Drop cards for albums that no longer exist so the cache cannot grow without bound.
        if (_cards.Count > _index.Albums().Count + 64)
        {
            var live = _index.Albums().Select(a => a.Id).ToHashSet();
            foreach (var stale in _cards.Keys.Where(id => !live.Contains(id)).ToList())
            {
                _cards.Remove(stale);
            }
        }

        LayoutAlbumRows();
        Raise(nameof(HasAlbums));
        Raise(nameof(AlbumCount));
        Raise(nameof(ShowAlbumGrid));
        Raise(nameof(NoResults));
        Raise(nameof(CountText));
        Raise(nameof(ShowModeSwitch));
    }

    private void LayoutAlbumRows()
    {
        var rows = new List<AlbumRowViewModel>((_visibleCards.Count / _albumColumns) + 1);
        for (var i = 0; i < _visibleCards.Count; i += _albumColumns)
        {
            rows.Add(new AlbumRowViewModel(_visibleCards.Skip(i).Take(_albumColumns).ToArray()));
        }

        AlbumRows = rows;
    }

    // ----- Playing from the list -----

    public void Play(TrackRowViewModel row)
    {
        var tracks = _visible.Select(r => r.Track).ToArray();
        var index = Array.FindIndex(tracks, t => t.Id == row.Track.Id);
        if (index >= 0)
        {
            _engine.PlayTracks(tracks, index);
        }
    }

    public void PlayAllVisible()
    {
        if (_visible.Count > 0)
        {
            _engine.PlayTracks(_visible.Select(r => r.Track).ToArray(), 0);
        }
    }

    public void PlayNext(IEnumerable<TrackRowViewModel> rows) =>
        _engine.EnqueueNext(rows.Select(r => r.Track).ToArray());

    public void AddToQueue(IEnumerable<TrackRowViewModel> rows) =>
        _engine.AppendToQueue(rows.Select(r => r.Track).ToArray());

    public void Dispose()
    {
        _playback.CurrentTrackChanged -= OnCurrentTrackChanged;
        _searchDebounce.Stop();
        _scanCancellation?.Cancel();
    }

    // ----- Internals -----

    private void RebuildRows()
    {
        var all = _index.All();
        var rows = new Dictionary<Guid, TrackRowViewModel>(all.Count);
        foreach (var track in all)
        {
            rows[track.Id] = _rows.TryGetValue(track.Id, out var existing) && ReferenceEquals(existing.Track, track)
                ? existing
                : new TrackRowViewModel(track);
        }

        _rows = rows;
        _currentRow = null;
        ApplyFilter();
        SyncCurrentRow(_playback.CurrentTrackId);

        // An open album that was removed or rescanned must not stay on screen as a stale copy.
        if (_selectedAlbum is { } open)
        {
            var current = _index.FindAlbum(open.Album.Id);
            if (current is null)
            {
                CloseAlbum();
            }
            else if (!ReferenceEquals(current, open.Album))
            {
                OpenAlbum(current);
            }
        }

        LibraryChanged?.Invoke(this, EventArgs.Empty);

        Raise(nameof(HasTracks));
        Raise(nameof(IsEmpty));
        Raise(nameof(CountText));
        (RescanCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void ApplyFilter()
    {
        var matches = _index.Search(_searchText);
        var rows = new List<TrackRowViewModel>(matches.Count);
        var number = 1;
        foreach (var track in matches)
        {
            if (_rows.TryGetValue(track.Id, out var row))
            {
                row.Number = number++;
                rows.Add(row);
            }
        }

        VisibleRows = rows;
        Raise(nameof(ShowSongList));
        RebuildAlbums();
        (_playback.PlayPauseCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void UpdateProblems()
    {
        Problems = _index.Problems
            .Select(p => new ProblemRow(Path.GetFileName(p.Path), Path.GetDirectoryName(p.Path) ?? string.Empty, p.Reason))
            .ToArray();

        if (_problems.Count == 0)
        {
            ShowProblems = false;
        }
    }

    private void OnCurrentTrackChanged(object? sender, Guid trackId) => SyncCurrentRow(trackId);

    private void SyncCurrentRow(Guid trackId)
    {
        if (_currentRow is not null)
        {
            _currentRow.IsCurrent = false;
            _currentRow = null;
        }

        if (trackId != Guid.Empty && _rows.TryGetValue(trackId, out var row))
        {
            row.IsCurrent = true;
            _currentRow = row;
        }

        // Rows owned by the album page and Home are separate objects (their numbering differs), so
        // their "playing" marker is kept in step here too.
        foreach (var set in _auxiliaryRows.Values)
        {
            foreach (var auxiliary in set)
            {
                auxiliary.IsCurrent = auxiliary.Track.Id == trackId;
            }
        }
    }
}

/// <summary>Which listing the Library page shows.</summary>
public enum LibraryMode
{
    Albums,
    Songs,
}

/// <summary>Random ordering for "shuffle" actions. A copy is shuffled; the source is never changed.</summary>
public static class Shuffler
{
    public static IReadOnlyList<T> Shuffled<T>(IEnumerable<T> source, Random? random = null)
    {
        var items = source.ToArray();
        random ??= Random.Shared;
        for (var i = items.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }

        return items;
    }
}
