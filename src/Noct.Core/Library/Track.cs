using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Noct.Core.Audio;

namespace Noct.Core.Library;

/// <summary>
/// One indexed FLAC file. Everything here comes from the file itself; nothing is guessed.
/// </summary>
public sealed record Track
{
    public required Guid Id { get; init; }

    public required string Path { get; init; }

    /// <summary>Tag title, or the file name when the file carries no title tag.</summary>
    public required string Title { get; init; }

    /// <summary>True when <see cref="Title"/> came from the file name rather than a tag.</summary>
    public bool TitleFromFileName { get; init; }

    public string? Artist { get; init; }

    public string? AlbumArtist { get; init; }

    public string? Album { get; init; }

    public string? Date { get; init; }

    public string? Genre { get; init; }

    public int? TrackNumber { get; init; }

    public int? DiscNumber { get; init; }

    public int SampleRate { get; init; }

    public int Channels { get; init; }

    public int BitsPerSample { get; init; }

    public long TotalSamples { get; init; }

    public long FileLength { get; init; }

    public DateTime LastWriteUtc { get; init; }

    public bool HasEmbeddedArtwork { get; init; }

    [JsonIgnore]
    public TimeSpan Duration => SampleRate > 0 && TotalSamples > 0
        ? TimeSpan.FromSeconds((double)TotalSamples / SampleRate)
        : TimeSpan.Zero;

    [JsonIgnore]
    public SourceFormat SourceFormat => new("FLAC", SampleRate, Channels, BitsPerSample);

    [JsonIgnore]
    public string DisplayArtist => Artist ?? AlbumArtist ?? "Unknown artist";

    [JsonIgnore]
    public string DisplayAlbum => Album ?? "Unknown album";

    /// <summary>Text the search box matches against.</summary>
    public string SearchText { get; init; } = string.Empty;

    public static Track FromMetadata(string path, FlacMetadata metadata, FileInfo file)
    {
        var info = metadata.StreamInfo;
        var tags = metadata.Tags;
        var titleFromTag = !string.IsNullOrWhiteSpace(tags.Title);
        var title = titleFromTag ? tags.Title!.Trim() : System.IO.Path.GetFileNameWithoutExtension(path);

        var search = string.Join(
            ' ',
            new[] { title, tags.Artist, tags.AlbumArtist, tags.Album, tags.Genre, tags.Date, System.IO.Path.GetFileName(path) }
                .Where(s => !string.IsNullOrWhiteSpace(s)))
            .ToLowerInvariant();

        return new Track
        {
            Id = StableId(path),
            Path = path,
            Title = title,
            TitleFromFileName = !titleFromTag,
            Artist = Trim(tags.Artist),
            AlbumArtist = Trim(tags.AlbumArtist),
            Album = Trim(tags.Album),
            Date = Trim(tags.Date),
            Genre = Trim(tags.Genre),
            TrackNumber = tags.TrackNumber,
            DiscNumber = tags.DiscNumber,
            SampleRate = info.SampleRate,
            Channels = info.Channels,
            BitsPerSample = info.BitsPerSample,
            TotalSamples = info.TotalSamples,
            FileLength = file.Length,
            LastWriteUtc = file.LastWriteTimeUtc,
            HasEmbeddedArtwork = tags.HasPicture || tags.PictureData is { Length: > 0 },
            SearchText = search,
        };
    }

    /// <summary>
    /// A path-derived identifier. Being deterministic lets a saved queue and the output stream's
    /// track spans line up again after a restart without storing extra keys.
    /// </summary>
    public static Guid StableId(string path)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(NormalizePath(path)));
        return new Guid(hash);
    }

    /// <summary>
    /// The comparison form of a path: trimmed, backslashes, no trailing separator, lower case.
    /// Two spellings of the same location (case, slash style, trailing slash) normalise equal.
    /// </summary>
    public static string NormalizePath(string path)
    {
        var normalized = path.Trim().Replace('/', '\\').ToLowerInvariant();
        return normalized.Length > 3 ? normalized.TrimEnd('\\') : normalized;
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>A file that could not be indexed, kept so the user can see which one and why.</summary>
public sealed record TrackProblem(string Path, string Reason);
