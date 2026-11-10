using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using Noct.Core.Audio;
using Noct.Core.Library;
using Noct.Core.Settings;

namespace Noct.App.ViewModels;

/// <summary>
/// Loads cover art as frozen bitmaps sized for where they are shown.
/// </summary>
/// <remarks>
/// Source order for a track or album: a cover/folder/front image in the album folder first, then the
/// picture embedded in a song, then nothing (the UI shows a neutral placeholder). Music files and
/// image files are only read. Decoding uses <c>DecodePixelWidth</c>, so a 3000 px scan is never held
/// in memory at full size. Two bounded caches apply: decoded bitmaps in memory (least recently used)
/// and re-encoded JPEG thumbnails on disk (pruned by file count and total size).
/// </remarks>
public sealed class ArtworkService
{
    private const int MaxMemoryEntries = 64;
    private const int MaxFolderCoverEntries = 4096;
    private const int MaxDiskFiles = 600;
    private const long MaxDiskBytes = 150L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<(Guid Key, int Width), LinkedListNode<CacheEntry>> _memory = [];
    private readonly LinkedList<CacheEntry> _order = new();
    private readonly ConcurrentDictionary<string, string?> _folderCovers = new(StringComparer.Ordinal);
    private readonly string _directory;
    private int _writesSincePrune;

    public ArtworkService(string? directory = null)
    {
        _directory = directory ?? AppPaths.ArtworkCache;
    }

    private readonly record struct CacheEntry((Guid Key, int Width) Id, BitmapSource? Image);

    /// <summary>Device pixels per DIP of the display the app is on; thumbnails are sized from it.</summary>
    public double DisplayScale { get; set; } = 1.0;

    /// <summary>Pixel width for something drawn <paramref name="dips"/> wide, clamped to a sane range.</summary>
    public int PixelsFor(double dips) => (int)Math.Clamp(Math.Ceiling(dips * Math.Max(1.0, DisplayScale)), 48, 640);

    /// <summary>A cached bitmap if it is in memory, without starting any work.</summary>
    public bool TryGetCached(Guid key, int width, out BitmapSource? image)
    {
        lock (_gate)
        {
            if (_memory.TryGetValue((key, width), out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }

        image = null;
        return false;
    }

    public Task<BitmapSource?> GetTrackAsync(Track track, int width, CancellationToken cancellationToken) =>
        GetAsync(track.Id, width, () => ResolveTrackSource(track), cancellationToken);

    public Task<BitmapSource?> GetAlbumAsync(Album album, int width, CancellationToken cancellationToken) =>
        GetAsync(album.Id, width, () => ResolveAlbumSource(album), cancellationToken);

    private sealed class Pending
    {
        public required (Guid Key, int Width) Id { get; init; }

        public required Func<ArtSource?> Resolve { get; init; }

        public required CancellationToken Token { get; init; }

        public TaskCompletionSource<BitmapSource?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Requests wait here, newest first: what is on screen after a fast scroll was asked for last. Two workers
    // decode at a time, so a flick through thousands of albums cannot flood every core with decoding, and the
    // oldest waiting requests are dropped once the queue is longer than any screen could need.
    private const int MaxWorkers = 2;
    private const int MaxPending = 48;
    private readonly LinkedList<Pending> _pending = new();
    private readonly Dictionary<(Guid Key, int Width), Pending> _inFlight = [];
    private int _workers;

    /// <summary>
    /// Returns the picture, decoding on a background worker if needed. A request that was dropped because newer ones
    /// pushed it out of a full queue ends as cancelled; asking again later works.
    /// </summary>
    private Task<BitmapSource?> GetAsync(Guid key, int width, Func<ArtSource?> resolve, CancellationToken cancellationToken)
    {
        if (TryGetCached(key, width, out var cached))
        {
            return Task.FromResult(cached);
        }

        var id = (key, width);
        Pending request;
        var startWorker = false;
        lock (_gate)
        {
            if (_inFlight.TryGetValue(id, out var existing))
            {
                // The same picture is already queued or being decoded: share it, and move it to the front.
                if (_pending.Remove(existing))
                {
                    _pending.AddFirst(existing);
                }

                return existing.Result.Task.WaitAsync(cancellationToken);
            }

            request = new Pending { Id = id, Resolve = resolve, Token = cancellationToken };
            _inFlight[id] = request;
            _pending.AddFirst(request);

            while (_pending.Count > MaxPending)
            {
                var dropped = _pending.Last!.Value;
                _pending.RemoveLast();
                _inFlight.Remove(dropped.Id);
                dropped.Result.TrySetCanceled();
            }

            if (_workers < MaxWorkers)
            {
                _workers++;
                startWorker = true;
            }
        }

        if (startWorker)
        {
            _ = Task.Run(Work);
        }

        return request.Result.Task;
    }

    private void Work()
    {
        while (true)
        {
            Pending request;
            lock (_gate)
            {
                if (_pending.First is null)
                {
                    _workers--;
                    return;
                }

                request = _pending.First.Value;
                _pending.RemoveFirst();
            }

            BitmapSource? image = null;
            var failed = false;
            try
            {
                request.Token.ThrowIfCancellationRequested();
                image = Load(request.Resolve(), request.Id.Width, request.Token);
            }
            catch (OperationCanceledException)
            {
                failed = true;
            }

            lock (_gate)
            {
                _inFlight.Remove(request.Id);
                if (!failed && !_memory.ContainsKey(request.Id))
                {
                    _memory[request.Id] = _order.AddFirst(new CacheEntry(request.Id, image));
                    while (_order.Count > MaxMemoryEntries)
                    {
                        _memory.Remove(_order.Last!.Value.Id);
                        _order.RemoveLast();
                    }
                }
            }

            if (failed)
            {
                request.Result.TrySetCanceled();
            }
            else
            {
                request.Result.TrySetResult(image);
            }
        }
    }
    // ----- source selection -----

    private readonly record struct ArtSource(string? File, byte[]? Bytes, string CacheKey);

    private ArtSource? ResolveTrackSource(Track track)
    {
        var folder = Path.GetDirectoryName(track.Path) ?? string.Empty;
        return FolderCoverSource(folder) ?? EmbeddedSource(track);
    }

    private ArtSource? ResolveAlbumSource(Album album)
    {
        if (FolderCoverSource(album.FolderPath) is { } cover)
        {
            return cover;
        }

        // The first track that carries a picture represents the album.
        foreach (var track in album.Tracks.Where(t => t.HasEmbeddedArtwork))
        {
            if (EmbeddedSource(track) is { } embedded)
            {
                return embedded;
            }
        }

        return null;
    }

    private ArtSource? FolderCoverSource(string folder)
    {
        if (_folderCovers.Count > MaxFolderCoverEntries)
        {
            _folderCovers.Clear();
        }

        var path = _folderCovers.GetOrAdd(Track.NormalizePath(folder), _ => AlbumCover.Find(folder));
        if (path is null)
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new ArtSource(path, null, Hash($"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static ArtSource? EmbeddedSource(Track track)
    {
        if (!track.HasEmbeddedArtwork)
        {
            return null;
        }

        try
        {
            var picture = FlacDecoder.ReadMetadata(track.Path).Tags.PictureData;
            return picture is { Length: > 0 }
                ? new ArtSource(null, picture, Convert.ToHexString(SHA1.HashData(picture)))
                : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)));

    // ----- decoding and the disk cache -----

    private BitmapSource? Load(ArtSource? source, int width, CancellationToken cancellationToken)
    {
        if (source is not { } art)
        {
            return null;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cached = Path.Combine(_directory, $"{art.CacheKey}-{width}.jpg");

            if (File.Exists(cached))
            {
                try
                {
                    var fromDisk = Decode(File.ReadAllBytes(cached), width);
                    if (DateTime.UtcNow - File.GetLastAccessTimeUtc(cached) > TimeSpan.FromHours(6))
                    {
                        File.SetLastAccessTimeUtc(cached, DateTime.UtcNow);
                    }
                    return fromDisk;
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException)
                {
                    // Fall through and rebuild from the original.
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var bytes = art.Bytes ?? File.ReadAllBytes(art.File!);
            var decoded = Decode(bytes, width);
            Save(decoded, cached);
            return decoded;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static BitmapSource Decode(byte[] data, int width)
    {
        using var stream = new MemoryStream(data);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = width;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void Save(BitmapSource image, string path)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var encoder = new JpegBitmapEncoder { QualityLevel = 86 };
            encoder.Frames.Add(BitmapFrame.Create(image));

            var temporary = path + ".tmp";
            using (var stream = File.Create(temporary))
            {
                encoder.Save(stream);
            }

            File.Move(temporary, path, overwrite: true);

            if (Interlocked.Increment(ref _writesSincePrune) >= 40)
            {
                Interlocked.Exchange(ref _writesSincePrune, 0);
                Prune();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Not being able to cache only means decoding again next time.
        }
    }

    /// <summary>Removes the least recently used thumbnails until the cache is inside its limits.</summary>
    public void Prune()
    {
        try
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            var files = new DirectoryInfo(_directory).EnumerateFiles("*.jpg").OrderBy(f => f.LastAccessTimeUtc).ToList();
            var total = files.Sum(f => f.Length);
            var index = 0;
            while (index < files.Count && (files.Count - index > MaxDiskFiles || total > MaxDiskBytes))
            {
                total -= files[index].Length;
                files[index].Delete();
                index++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be trimmed is a nuisance, not a failure.
        }
    }
}
