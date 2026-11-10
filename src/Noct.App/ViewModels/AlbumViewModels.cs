using System.Windows.Media;
using Noct.Core.Library;

namespace Noct.App.ViewModels;

/// <summary>
/// An album as shown on a card. Its cover is loaded only when a card is first drawn, and is read
/// back from the artwork service's bounded cache rather than held here, so scrolling through
/// thousands of albums cannot pile up bitmaps in memory.
/// </summary>
public sealed class AlbumCardViewModel : ViewModelBase
{
    private readonly ArtworkService _artwork;
    private readonly int _pixelWidth;
    private bool _loading;

    public AlbumCardViewModel(Album album, ArtworkService artwork, double displayDips)
    {
        Album = album;
        _artwork = artwork;
        _pixelWidth = artwork.PixelsFor(displayDips);
    }

    public Album Album { get; }

    public string Title => Album.Title;

    public string Subtitle
    {
        get
        {
            var count = Album.Tracks.Count == 1 ? "1 track" : $"{Album.Tracks.Count} tracks";
            return string.IsNullOrEmpty(Album.Artist) ? count : $"{Album.Artist} · {count}";
        }
    }

    /// <summary>The artist alone (it can be long and is trimmed); empty when the files are untagged.</summary>
    public string ArtistText => Album.Artist ?? string.Empty;

    public bool HasArtist => !string.IsNullOrEmpty(Album.Artist);

    /// <summary>The track count on its own line, so it is never the part that gets cut off.</summary>
    public string TrackCountText => Album.Tracks.Count == 1 ? "1 track" : $"{Album.Tracks.Count} tracks";

    public string AccessibleName => $"{Album.Title}, {Subtitle}";

    /// <summary>The cover when it is cached; otherwise null while it loads (the card shows a placeholder).</summary>
    public ImageSource? Cover
    {
        get
        {
            if (_artwork.TryGetCached(Album.Id, _pixelWidth, out var cached))
            {
                return cached;
            }

            if (!_loading)
            {
                _loading = true;
                _ = LoadAsync();
            }

            return null;
        }
    }

    private async Task LoadAsync()
    {
        var dropped = false;
        try
        {
            await _artwork.GetAlbumAsync(Album, _pixelWidth, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Pushed out of the artwork queue by newer requests while scrolling. Not retried here; the card asks
            // again the next time it is drawn.
            dropped = true;
        }
        finally
        {
            // Raised on the UI thread (the call started there); the getter now finds the cache entry.
            _loading = false;
            if (!dropped)
            {
                Raise(nameof(Cover));
            }
        }
    }
}

/// <summary>One row of the album grid: as many cards as fit the current width.</summary>
public sealed record AlbumRowViewModel(IReadOnlyList<AlbumCardViewModel> Cards);

/// <summary>The open album: cover, facts and its tracks in play order.</summary>
public sealed class AlbumDetailViewModel : ViewModelBase
{
    private readonly ArtworkService _artwork;
    private ImageSource? _cover;

    public AlbumDetailViewModel(Album album, IReadOnlyList<TrackRowViewModel> rows, ArtworkService artwork)
    {
        Album = album;
        Rows = rows;
        _artwork = artwork;
        _ = LoadCoverAsync();
    }

    public Album Album { get; }

    public IReadOnlyList<TrackRowViewModel> Rows { get; }

    public string Title => Album.Title;

    public string Facts
    {
        get
        {
            var count = Album.Tracks.Count == 1 ? "1 track" : $"{Album.Tracks.Count} tracks";
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Album.Artist))
            {
                parts.Add(Album.Artist);
            }

            parts.Add(count);
            if (Album.Duration > TimeSpan.Zero)
            {
                parts.Add(TextFormat.Duration(Album.Duration));
            }

            return string.Join(" · ", parts);
        }
    }

    public string FolderPath => Album.FolderPath;

    public ImageSource? Cover
    {
        get => _cover;
        private set
        {
            if (Set(ref _cover, value))
            {
                Raise(nameof(HasCover));
            }
        }
    }

    public bool HasCover => _cover is not null;

    private async Task LoadCoverAsync()
    {
        try
        {
            Cover = await _artwork.GetAlbumAsync(Album, _artwork.PixelsFor(168), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Pushed out of a long artwork queue; the page simply keeps its placeholder.
        }
    }
}
