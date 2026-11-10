namespace Noct.Core.Audio;

/// <summary>Contents of the FLAC STREAMINFO metadata block.</summary>
public sealed record FlacStreamInfo(
    int MinBlockSize,
    int MaxBlockSize,
    int MinFrameSize,
    int MaxFrameSize,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    long TotalSamples,
    byte[] Md5)
{
    /// <summary>True when the encoder stored an MD5 of the unencoded audio (all zero means absent).</summary>
    public bool HasMd5 => Md5.Length == 16 && Md5.Any(b => b != 0);

    public TimeSpan? Duration => SampleRate > 0 && TotalSamples > 0
        ? TimeSpan.FromSeconds((double)TotalSamples / SampleRate)
        : null;
}

/// <summary>A single SEEKTABLE entry. Placeholder points carry <see cref="ulong.MaxValue"/>.</summary>
public readonly record struct FlacSeekPoint(ulong FirstSample, ulong ByteOffset, ushort FrameSamples);

/// <summary>Vorbis comment fields and the first embedded picture, if any.</summary>
public sealed class FlacTags
{
    public string? Title { get; init; }

    public string? Artist { get; init; }

    public string? AlbumArtist { get; init; }

    public string? Album { get; init; }

    public string? Date { get; init; }

    public string? Genre { get; init; }

    public int? TrackNumber { get; init; }

    public int? DiscNumber { get; init; }

    public string? Vendor { get; init; }

    /// <summary>Raw bytes of the embedded PICTURE block, if present.</summary>
    public byte[]? PictureData { get; init; }

    public string? PictureMimeType { get; init; }

    /// <summary>True when the file has an embedded picture, even if its bytes were not read.</summary>
    public bool HasPicture { get; init; }

    public static FlacTags Empty { get; } = new();
}
