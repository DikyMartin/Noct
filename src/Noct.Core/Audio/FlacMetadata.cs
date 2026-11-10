using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Noct.Core.Audio;

/// <summary>Everything read from the FLAC header region, plus the offset of the first audio frame.</summary>
public sealed class FlacMetadata
{
    public required FlacStreamInfo StreamInfo { get; init; }

    public required FlacTags Tags { get; init; }

    public required IReadOnlyList<FlacSeekPoint> SeekPoints { get; init; }

    /// <summary>Absolute byte offset of the first audio frame.</summary>
    public required long FirstFrameOffset { get; init; }
}

/// <summary>
/// Reads the FLAC stream marker and metadata blocks. Vorbis comments are UTF-8 by
/// specification, so Unicode/CJK tags round-trip without any code-page guessing.
/// </summary>
public static class FlacMetadataReader
{
    private const int StreamInfoBlock = 0;
    private const int SeekTableBlock = 3;
    private const int VorbisCommentBlock = 4;
    private const int PictureBlock = 6;

    /// <summary>Largest PICTURE payload accepted, to keep a malformed file from exhausting memory.</summary>
    private const int MaxPictureBytes = 16 * 1024 * 1024;

    /// <param name="stream">A seekable FLAC stream.</param>
    /// <param name="includePicture">
    /// False skips the embedded image bytes and only records that one exists
    /// (<see cref="FlacTags.HasPicture"/>). Use it when indexing a library.
    /// </param>
    public static FlacMetadata Read(Stream stream, bool includePicture = true)
    {
        SkipId3v2(stream);

        Span<byte> marker = stackalloc byte[4];
        ReadExactly(stream, marker);
        if (marker[0] != (byte)'f' || marker[1] != (byte)'L' || marker[2] != (byte)'a' || marker[3] != (byte)'C')
        {
            throw new InvalidDataException("Not a FLAC stream: missing fLaC marker.");
        }

        FlacStreamInfo? streamInfo = null;
        var tags = FlacTags.Empty;
        var seekPoints = new List<FlacSeekPoint>();
        byte[]? picture = null;
        string? pictureMime = null;
        var sawPicture = false;

        var last = false;
        Span<byte> header = stackalloc byte[4];
        while (!last)
        {
            ReadExactly(stream, header);
            last = (header[0] & 0x80) != 0;
            var blockType = header[0] & 0x7F;
            var length = (header[1] << 16) | (header[2] << 8) | header[3];

            switch (blockType)
            {
                case StreamInfoBlock:
                {
                    var payload = ReadBlock(stream, length);
                    streamInfo = ParseStreamInfo(payload);
                    break;
                }

                case SeekTableBlock:
                {
                    var payload = ReadBlock(stream, length);
                    for (var offset = 0; offset + 18 <= payload.Length; offset += 18)
                    {
                        var span = payload.AsSpan(offset);
                        seekPoints.Add(new FlacSeekPoint(
                            BinaryPrimitives.ReadUInt64BigEndian(span),
                            BinaryPrimitives.ReadUInt64BigEndian(span[8..]),
                            BinaryPrimitives.ReadUInt16BigEndian(span[16..])));
                    }

                    break;
                }

                case VorbisCommentBlock:
                {
                    var payload = ReadBlock(stream, length);
                    tags = ParseVorbisComment(payload, tags);
                    break;
                }

                case PictureBlock when !includePicture:
                    // Scanning only needs to know a picture exists; reading megabytes of image per file
                    // across a whole library is the single most expensive part of indexing.
                    sawPicture = true;
                    Skip(stream, length);
                    break;

                case PictureBlock when picture is null && length <= MaxPictureBytes:
                {
                    var payload = ReadBlock(stream, length);
                    (picture, pictureMime) = ParsePicture(payload);
                    break;
                }

                default:
                    Skip(stream, length);
                    break;
            }
        }

        if (streamInfo is null)
        {
            throw new InvalidDataException("FLAC stream is missing its STREAMINFO block.");
        }

        if (picture is not null || sawPicture)
        {
            tags = CopyWithPicture(tags, picture, pictureMime, hasPicture: picture is not null || sawPicture);
        }

        return new FlacMetadata
        {
            StreamInfo = streamInfo,
            Tags = tags,
            SeekPoints = seekPoints,
            FirstFrameOffset = stream.Position,
        };
    }

    private static FlacTags CopyWithPicture(FlacTags tags, byte[]? picture, string? mime, bool hasPicture) => new()
    {
        Title = tags.Title,
        Artist = tags.Artist,
        AlbumArtist = tags.AlbumArtist,
        Album = tags.Album,
        Date = tags.Date,
        Genre = tags.Genre,
        TrackNumber = tags.TrackNumber,
        DiscNumber = tags.DiscNumber,
        Vendor = tags.Vendor,
        PictureData = picture,
        PictureMimeType = mime,
        HasPicture = hasPicture,
    };

    private static FlacStreamInfo ParseStreamInfo(byte[] payload)
    {
        if (payload.Length < 34)
        {
            throw new InvalidDataException("STREAMINFO block is too short.");
        }

        var minBlock = BinaryPrimitives.ReadUInt16BigEndian(payload);
        var maxBlock = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2));
        var minFrame = (payload[4] << 16) | (payload[5] << 8) | payload[6];
        var maxFrame = (payload[7] << 16) | (payload[8] << 8) | payload[9];

        // 20 bits sample rate, 3 bits (channels-1), 5 bits (bps-1), 36 bits total samples.
        var packed = BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(10));
        var sampleRate = (int)(packed >> 44);
        var channels = (int)((packed >> 41) & 0x7) + 1;
        var bitsPerSample = (int)((packed >> 36) & 0x1F) + 1;
        var totalSamples = (long)(packed & 0xF_FFFF_FFFFUL);

        var md5 = payload.AsSpan(18, 16).ToArray();

        if (sampleRate <= 0)
        {
            throw new InvalidDataException("STREAMINFO declares a zero sample rate.");
        }

        return new FlacStreamInfo(minBlock, maxBlock, minFrame, maxFrame, sampleRate, channels, bitsPerSample, totalSamples, md5);
    }

    private static FlacTags ParseVorbisComment(byte[] payload, FlacTags existing)
    {
        var offset = 0;
        var vendorLength = ReadUInt32Le(payload, ref offset);
        var vendor = ReadUtf8(payload, ref offset, vendorLength);
        var count = ReadUInt32Le(payload, ref offset);

        string? title = null, artist = null, albumArtist = null, album = null, date = null, genre = null;
        int? track = null, disc = null;

        for (var i = 0u; i < count; i++)
        {
            if (offset + 4 > payload.Length)
            {
                break;
            }

            var length = ReadUInt32Le(payload, ref offset);
            var entry = ReadUtf8(payload, ref offset, length);
            var separator = entry.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = entry[..separator].ToUpperInvariant();
            var value = entry[(separator + 1)..];
            if (value.Length == 0)
            {
                continue;
            }

            switch (key)
            {
                case "TITLE": title ??= value; break;
                case "ARTIST": artist ??= value; break;
                case "ALBUMARTIST":
                case "ALBUM ARTIST": albumArtist ??= value; break;
                case "ALBUM": album ??= value; break;
                case "DATE":
                case "YEAR": date ??= value; break;
                case "GENRE": genre ??= value; break;
                case "TRACKNUMBER": track ??= ParseLeadingInt(value); break;
                case "DISCNUMBER": disc ??= ParseLeadingInt(value); break;
            }
        }

        return new FlacTags
        {
            Title = title ?? existing.Title,
            Artist = artist ?? existing.Artist,
            AlbumArtist = albumArtist ?? existing.AlbumArtist,
            Album = album ?? existing.Album,
            Date = date ?? existing.Date,
            Genre = genre ?? existing.Genre,
            TrackNumber = track ?? existing.TrackNumber,
            DiscNumber = disc ?? existing.DiscNumber,
            Vendor = vendor,
            PictureData = existing.PictureData,
            PictureMimeType = existing.PictureMimeType,
        HasPicture = existing.HasPicture,
        };
    }

    /// <summary>Handles values such as "3" and "3/12".</summary>
    private static int? ParseLeadingInt(string value)
    {
        var end = 0;
        while (end < value.Length && char.IsAsciiDigit(value[end]))
        {
            end++;
        }

        return end > 0 && int.TryParse(value[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static (byte[]? Data, string? Mime) ParsePicture(byte[] payload)
    {
        var offset = 0;
        _ = ReadUInt32Be(payload, ref offset);                       // picture type
        var mimeLength = ReadUInt32Be(payload, ref offset);
        var mime = ReadAscii(payload, ref offset, mimeLength);
        var descriptionLength = ReadUInt32Be(payload, ref offset);
        _ = ReadUtf8(payload, ref offset, descriptionLength);
        _ = ReadUInt32Be(payload, ref offset);                       // width
        _ = ReadUInt32Be(payload, ref offset);                       // height
        _ = ReadUInt32Be(payload, ref offset);                       // colour depth
        _ = ReadUInt32Be(payload, ref offset);                       // indexed colours
        var dataLength = ReadUInt32Be(payload, ref offset);

        if (dataLength == 0 || offset + dataLength > (uint)payload.Length)
        {
            return (null, null);
        }

        return (payload.AsSpan(offset, (int)dataLength).ToArray(), mime);
    }

    private static uint ReadUInt32Le(byte[] data, ref int offset)
    {
        if (offset + 4 > data.Length)
        {
            offset = data.Length;
            return 0;
        }

        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        offset += 4;
        return value;
    }

    private static uint ReadUInt32Be(byte[] data, ref int offset)
    {
        if (offset + 4 > data.Length)
        {
            offset = data.Length;
            return 0;
        }

        var value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
        offset += 4;
        return value;
    }

    private static string ReadUtf8(byte[] data, ref int offset, uint length)
    {
        if (length == 0 || offset + length > (uint)data.Length)
        {
            offset = Math.Min(data.Length, offset + (int)Math.Min(length, int.MaxValue));
            return string.Empty;
        }

        var text = Encoding.UTF8.GetString(data, offset, (int)length);
        offset += (int)length;
        return text;
    }

    private static string ReadAscii(byte[] data, ref int offset, uint length)
    {
        if (length == 0 || offset + length > (uint)data.Length)
        {
            return string.Empty;
        }

        var text = Encoding.ASCII.GetString(data, offset, (int)length);
        offset += (int)length;
        return text;
    }

    private static byte[] ReadBlock(Stream stream, int length)
    {
        var payload = new byte[length];
        ReadExactly(stream, payload);
        return payload;
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var n = stream.Read(destination[read..]);
            if (n <= 0)
            {
                throw new EndOfStreamException("Truncated FLAC metadata.");
            }

            read += n;
        }
    }

    private static void Skip(Stream stream, int length)
    {
        if (stream.CanSeek)
        {
            stream.Position += length;
            return;
        }

        var remaining = length;
        var scratch = new byte[Math.Min(remaining, 8192)];
        while (remaining > 0)
        {
            var n = stream.Read(scratch, 0, Math.Min(remaining, scratch.Length));
            if (n <= 0)
            {
                throw new EndOfStreamException("Truncated FLAC metadata.");
            }

            remaining -= n;
        }
    }

    /// <summary>Some files carry an ID3v2 tag ahead of the fLaC marker; step over it if present.</summary>
    private static void SkipId3v2(Stream stream)
    {
        var start = stream.Position;
        Span<byte> header = stackalloc byte[10];
        var read = 0;
        while (read < 10)
        {
            var n = stream.Read(header[read..]);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }

        if (read == 10 && header[0] == (byte)'I' && header[1] == (byte)'D' && header[2] == (byte)'3')
        {
            var size = ((header[6] & 0x7F) << 21) | ((header[7] & 0x7F) << 14) | ((header[8] & 0x7F) << 7) | (header[9] & 0x7F);
            var footer = (header[5] & 0x10) != 0 ? 10 : 0;
            stream.Position = start + 10 + size + footer;
            return;
        }

        stream.Position = start;
    }
}
