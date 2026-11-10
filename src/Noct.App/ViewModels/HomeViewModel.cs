using System.Windows.Input;
using Noct.Core.Library;
using Noct.Core.Playback;

namespace Noct.App.ViewModels;

/// <summary>
/// Home: shuffle everything, a few albums and a mixed handful of songs picked at random.
/// </summary>
/// <remarks>
/// The picks are made once per session, as soon as the library has content, and again only when the
/// user presses Refresh. A library change (a scan finishing, an album removed) never reshuffles what
/// is on screen: removed items just disappear, so nothing moves under the pointer. Everything comes
/// from the index that is already in memory; opening Home never scans a folder.
/// </remarks>
public sealed class HomeViewModel : ViewModelBase
{
    public const int AlbumPickCount = 8;
    public const int SongPickCount = 12;

    private readonly LibraryViewModel _library;
    private readonly PlaybackEngine _engine;
    private readonly Random _random;
    private readonly List<Guid> _pickedAlbums = [];
    private readonly List<Guid> _pickedSongs = [];

    private bool _picked;
    private IReadOnlyList<AlbumCardViewModel> _albums = [];
    private IReadOnlyList<TrackRowViewModel> _songs = [];

    public HomeViewModel(LibraryViewModel library, PlaybackEngine engine, Random? random = null)
    {
        _library = library;
        _engine = engine;
        _random = random ?? Random.Shared;

        ShuffleAllCommand = new RelayCommand(_library.PlayAllShuffled, () => _library.TrackCount > 0);
        RefreshCommand = new RelayCommand(Refresh, () => _library.TrackCount > 0);
        OpenAlbumCommand = new RelayCommand(p =>
        {
            if (p is AlbumCardViewModel card)
            {
                OpenAlbumRequested?.Invoke(card.Album);
            }
        });
        PlaySongCommand = new RelayCommand(p =>
        {
            if (p is TrackRowViewModel row)
            {
                PlaySong(row);
            }
        });

        _library.LibraryChanged += OnLibraryChanged;
    }

    public ICommand ShuffleAllCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand OpenAlbumCommand { get; }

    public ICommand PlaySongCommand { get; }

    /// <summary>Set by the shell: show this album in the Library page.</summary>
    public Action<Album>? OpenAlbumRequested { get; set; }

    public IReadOnlyList<AlbumCardViewModel> Albums
    {
        get => _albums;
        private set
        {
            if (Set(ref _albums, value))
            {
                Raise(nameof(HasAlbums));
            }
        }
    }

    public IReadOnlyList<TrackRowViewModel> Songs
    {
        get => _songs;
        private set
        {
            if (Set(ref _songs, value))
            {
                Raise(nameof(HasSongs));
            }
        }
    }

    public bool HasAlbums => _albums.Count > 0;

    public bool HasSongs => _songs.Count > 0;

    public bool HasContent => _library.TrackCount > 0;

    public bool IsEmpty => !HasContent;

    /// <summary>Picks once per session (and on demand through <see cref="Refresh"/>).</summary>
    public void EnsurePicked()
    {
        if (!_picked && _library.TrackCount > 0)
        {
            Pick();
        }
    }

    public void Refresh()
    {
        if (_library.TrackCount > 0)
        {
            Pick();
        }
    }

    private void Pick()
    {
        _picked = true;
        var albums = _library.AllAlbums();

        _pickedAlbums.Clear();
        _pickedAlbums.AddRange(Sample(albums, AlbumPickCount).Select(a => a.Id));

        _pickedSongs.Clear();
        _pickedSongs.AddRange(PickSongs(albums, SongPickCount).Select(t => t.Id));

        Show();
    }

    /// <summary>
    /// Songs drawn from as many different albums as possible: one random track per album in a random
    /// album order, then a second pass if there are fewer albums than songs wanted.
    /// </summary>
    private List<Track> PickSongs(IReadOnlyList<Album> albums, int count)
    {
        var order = Sample(albums, albums.Count);
        var picked = new List<Track>(count);
        var used = new HashSet<Guid>();

        for (var pass = 0; pass < 4 && picked.Count < count; pass++)
        {
            foreach (var album in order)
            {
                if (picked.Count >= count)
                {
                    break;
                }

                var free = album.Tracks.Where(t => !used.Contains(t.Id)).ToList();
                if (free.Count == 0)
                {
                    continue;
                }

                var track = free[_random.Next(free.Count)];
                used.Add(track.Id);
                picked.Add(track);
            }
        }

        return picked;
    }

    /// <summary>A uniform random sample without replacement (partial Fisher-Yates).</summary>
    private List<T> Sample<T>(IReadOnlyList<T> source, int count)
    {
        var copy = source.ToArray();
        count = Math.Min(count, copy.Length);
        for (var i = 0; i < count; i++)
        {
            var j = i + _random.Next(copy.Length - i);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }

        return copy.Take(count).ToList();
    }

    /// <summary>
    /// Builds what is shown from the remembered picks, skipping anything no longer in the library.
    /// Called after a pick and after library changes; it never chooses new items.
    /// </summary>
    private void Show()
    {
        var albumsById = _library.AllAlbums().ToDictionary(a => a.Id);
        var tracksById = _library.AllTracks().ToDictionary(t => t.Id);

        Albums = _pickedAlbums
            .Where(albumsById.ContainsKey)
            .Select(id => _library.CardFor(albumsById[id]))
            .ToArray();

        var rows = _pickedSongs
            .Where(tracksById.ContainsKey)
            .Select((id, i) => _library.NewRow(tracksById[id], i + 1))
            .ToArray();

        _library.TrackRows("home", rows);
        Songs = rows;

        Raise(nameof(HasContent));
        Raise(nameof(IsEmpty));
        (ShuffleAllCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        if (!_picked)
        {
            EnsurePicked();
            Raise(nameof(HasContent));
            Raise(nameof(IsEmpty));
            (ShuffleAllCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
            return;
        }

        // Already picked: keep the same items, drop any that are gone, never reshuffle.
        Show();
    }

    private void PlaySong(TrackRowViewModel row)
    {
        var tracks = _songs.Select(r => r.Track).ToArray();
        var index = Array.FindIndex(tracks, t => t.Id == row.Track.Id);
        if (index >= 0)
        {
            _engine.PlayTracks(tracks, index);
        }
    }
}
