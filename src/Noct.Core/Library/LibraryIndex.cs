using System.Text.Json.Serialization;
using Noct.Core.Settings;

namespace Noct.Core.Library;

/// <summary>The persisted library index. Schema version 1.</summary>
public sealed class LibraryDocument : IVersionedDocument
{
    /// <summary>Same track records as version 1; albums are derived from folders, not stored.</summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonIgnore]
    public string? LoadIssue { get; set; }

    public DateTime? LastScanUtc { get; set; }

    public List<Track> Tracks { get; set; } = [];

    public List<TrackProblem> Problems { get; set; } = [];

    public static LibraryDocument CreateDefault() => new();
}

/// <summary>
/// In-memory library with search and sort. Loading and saving use the same atomic JSON writer as
/// settings, so an interrupted save cannot corrupt the index.
/// </summary>
public sealed class LibraryIndex
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Track> _byId = [];
    private List<Track> _ordered = [];
    private List<TrackProblem> _problems = [];
    private IReadOnlyList<Album>? _albums;
    private List<Track>? _albumsSource;

    public LibraryIndex(string? path = null)
    {
        _path = path ?? AppPaths.LibraryFile;
    }

    public event EventHandler? Changed;

    public string? LoadIssue { get; private set; }

    public DateTime? LastScanUtc { get; private set; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _ordered.Count;
            }
        }
    }

    public IReadOnlyList<TrackProblem> Problems
    {
        get
        {
            lock (_gate)
            {
                return _problems.ToArray();
            }
        }
    }

    public void Load()
    {
        var document = JsonStore.Load(_path, LibraryDocument.CurrentSchemaVersion, LibraryDocument.CreateDefault);
        lock (_gate)
        {
            LoadIssue = document.LoadIssue;
            LastScanUtc = document.LastScanUtc;
            _problems = document.Problems;
            _byId.Clear();
            foreach (var track in document.Tracks)
            {
                _byId[track.Id] = track;
            }

            _ordered = Sort(_byId.Values);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        LibraryDocument document;
        lock (_gate)
        {
            document = new LibraryDocument
            {
                LastScanUtc = LastScanUtc,
                Tracks = [.. _ordered],
                Problems = [.. _problems],
            };
        }

        JsonStore.Save(_path, document);
    }

    /// <summary>Merges a scan result in, replacing entries for the folders that were scanned.</summary>
    public void Merge(IReadOnlyList<Track> tracks, IReadOnlyList<TrackProblem> problems, IReadOnlyList<string> scannedRoots)
    {
        lock (_gate)
        {
            if (scannedRoots.Count > 0)
            {
                var stale = _byId.Values
                    .Where(t => scannedRoots.Any(root => IsUnder(t.Path, root)))
                    .Select(t => t.Id)
                    .ToList();

                foreach (var id in stale)
                {
                    _byId.Remove(id);
                }

                _problems.RemoveAll(p => scannedRoots.Any(root => IsUnder(p.Path, root)));
            }

            foreach (var track in tracks)
            {
                _byId[track.Id] = track;
            }

            _problems.AddRange(problems);
            _ordered = Sort(_byId.Values);
            LastScanUtc = DateTime.UtcNow;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Drops the tracks under a source folder the user removed, except those another remaining
    /// source folder still covers (overlapping sources must not lose each other's albums).
    /// </summary>
    public void RemoveFolder(string root, IReadOnlyCollection<string>? remainingRoots = null)
    {
        lock (_gate)
        {
            var remove = _byId.Values
                .Where(t => IsUnder(t.Path, root) && (remainingRoots is null || !remainingRoots.Any(other => IsUnder(t.Path, other))))
                .Select(t => t.Id)
                .ToList();
            foreach (var id in remove)
            {
                _byId.Remove(id);
            }

            _problems.RemoveAll(p => IsUnder(p.Path, root) && (remainingRoots is null || !remainingRoots.Any(other => IsUnder(p.Path, other))));
            _ordered = Sort(_byId.Values);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A snapshot of the index keyed by track id, for incremental rescans.</summary>
    public IReadOnlyDictionary<Guid, Track> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<Guid, Track>(_byId);
        }
    }

    /// <summary>
    /// Albums, one per folder that directly holds FLAC files. Cached until the track list changes:
    /// every mutation replaces the ordered list, so comparing its reference is enough to know the
    /// cache is stale, and opening Home or the album grid never regroups 10,000 tracks twice.
    /// </summary>
    public IReadOnlyList<Album> Albums()
    {
        lock (_gate)
        {
            if (_albums is null || !ReferenceEquals(_albumsSource, _ordered))
            {
                _albums = AlbumBuilder.Build(_ordered);
                _albumsSource = _ordered;
            }

            return _albums;
        }
    }

    public Album? FindAlbum(Guid id) => Albums().FirstOrDefault(a => a.Id == id);

    public IReadOnlyList<Album> SearchAlbums(string? query)
    {
        var albums = Albums();
        if (string.IsNullOrWhiteSpace(query))
        {
            return albums;
        }

        var terms = query.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return albums.Where(a => terms.All(term => a.SearchText.Contains(term, StringComparison.Ordinal))).ToArray();
    }

    /// <summary>
    /// Removes an album from the library. Only Noct's record is dropped; the folder and its files
    /// are not touched. Returns the number of tracks removed.
    /// </summary>
    public int RemoveAlbum(string folderPath)
    {
        var key = Track.NormalizePath(folderPath);
        int removed;
        lock (_gate)
        {
            var ids = _byId.Values
                .Where(t => Track.NormalizePath(Path.GetDirectoryName(t.Path) ?? string.Empty) == key)
                .Select(t => t.Id)
                .ToList();

            foreach (var id in ids)
            {
                _byId.Remove(id);
            }

            _problems.RemoveAll(p => Track.NormalizePath(Path.GetDirectoryName(p.Path) ?? string.Empty) == key);
            _ordered = Sort(_byId.Values);
            removed = ids.Count;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    public IReadOnlyList<Track> All()
    {
        lock (_gate)
        {
            return _ordered;
        }
    }

    public Track? Find(Guid id)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(id, out var track) ? track : null;
        }
    }

    public Track? FindByPath(string path) => Find(Track.StableId(path));

    /// <summary>
    /// Case- and accent-insensitive substring search over title, artist, album, genre and file name.
    /// </summary>
    public IReadOnlyList<Track> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return All();
        }

        var terms = query.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        lock (_gate)
        {
            return _ordered
                .Where(track => terms.All(term => track.SearchText.Contains(term, StringComparison.Ordinal)))
                .ToArray();
        }
    }

    private static List<Track> Sort(IEnumerable<Track> tracks) => tracks
        .OrderBy(t => t.AlbumArtist ?? t.Artist ?? "\uffff", StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(t => t.Album ?? "\uffff", StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(t => t.DiscNumber ?? 0)
        .ThenBy(t => t.TrackNumber ?? int.MaxValue)
        .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    private static bool IsUnder(string path, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
