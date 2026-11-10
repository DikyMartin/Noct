namespace Noct.Core.Audio;

/// <summary>
/// Managed FLAC decoder producing native-scale integer samples.
/// </summary>
/// <remarks>
/// <para>
/// Written in place of a native decoder wrapper for three reasons recorded in docs/BUILD_NOTES.md:
/// the portable folder stays free of native binaries, the decoder hands back exact integers at the
/// source bit depth instead of a float interface, and correctness is checkable against the MD5 of
/// the unencoded audio that every FLAC encoder stores in STREAMINFO.
/// </para>
/// <para>
/// Samples are returned right-aligned and sign-extended in <see cref="int"/>: a 16-bit source
/// yields values in [-32768, 32767], a 24-bit source in [-8388608, 8388607]. No scaling,
/// normalisation or dithering happens here.
/// </para>
/// <para>Not thread-safe; the playback engine serialises all access.</para>
/// </remarks>
public sealed class FlacDecoder : IDisposable
{
    private const int MaxSupportedBlockSize = 65535;

    private static readonly int[] SampleRateCodes =
        [0, 88200, 176400, 192000, 8000, 16000, 22050, 24000, 32000, 44100, 48000, 96000, 0, 0, 0, -1];

    private static readonly int[] BitsPerSampleCodes = [0, 8, 12, -1, 16, 20, 24, 32];

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly FlacBitReader _reader;
    private readonly int[][] _channels;
    private readonly int _maxBlockSize;

    private int _frameSamples;
    private int _framePosition;
    private long _nextSampleIndex;
    private bool _endOfStream;
    private bool _disposed;

    private FlacDecoder(Stream stream, bool ownsStream, FlacMetadata metadata)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        Metadata = metadata;

        var info = metadata.StreamInfo;
        if (info.BitsPerSample is < 4 or > 32)
        {
            throw new NotSupportedException($"Unsupported FLAC bit depth: {info.BitsPerSample}.");
        }

        if (info.Channels is < 1 or > 8)
        {
            throw new NotSupportedException($"Unsupported FLAC channel count: {info.Channels}.");
        }

        _maxBlockSize = info.MaxBlockSize is > 0 and <= MaxSupportedBlockSize ? info.MaxBlockSize : MaxSupportedBlockSize;
        _channels = new int[info.Channels][];
        for (var i = 0; i < _channels.Length; i++)
        {
            _channels[i] = new int[_maxBlockSize];
        }

        _stream.Position = metadata.FirstFrameOffset;
        _reader = new FlacBitReader(_stream);
    }

    public FlacMetadata Metadata { get; }

    public FlacStreamInfo StreamInfo => Metadata.StreamInfo;

    public FlacTags Tags => Metadata.Tags;

    public int SampleRate => StreamInfo.SampleRate;

    public int Channels => StreamInfo.Channels;

    public int BitsPerSample => StreamInfo.BitsPerSample;

    /// <summary>Total sample frames, or 0 when the encoder did not record a length.</summary>
    public long TotalSamples => StreamInfo.TotalSamples;

    /// <summary>Index of the next sample frame that <see cref="ReadSamples"/> will return.</summary>
    public long Position => _nextSampleIndex;

    public static FlacDecoder Open(string path)
    {
        var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.SequentialScan,
                BufferSize = 0,
            });

        try
        {
            return Open(stream, ownsStream: true);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static FlacDecoder Open(Stream stream, bool ownsStream)
    {
        if (!stream.CanSeek)
        {
            throw new ArgumentException("FLAC decoding requires a seekable stream.", nameof(stream));
        }

        var metadata = FlacMetadataReader.Read(stream);
        return new FlacDecoder(stream, ownsStream, metadata);
    }

    /// <summary>Reads metadata only, without constructing decode buffers.</summary>
    public static FlacMetadata ReadMetadata(string path, bool includePicture = true)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.SequentialScan);
        return FlacMetadataReader.Read(stream, includePicture);
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with interleaved sample frames.
    /// Returns the number of sample frames written, which is 0 at end of stream.
    /// </summary>
    public int ReadSamples(Span<int> destination, int frameCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var channelCount = _channels.Length;
        frameCount = Math.Min(frameCount, destination.Length / channelCount);
        var written = 0;

        while (written < frameCount)
        {
            if (_framePosition >= _frameSamples)
            {
                if (_endOfStream || !DecodeNextFrame())
                {
                    break;
                }
            }

            var take = Math.Min(frameCount - written, _frameSamples - _framePosition);
            for (var ch = 0; ch < channelCount; ch++)
            {
                var source = _channels[ch].AsSpan(_framePosition, take);
                var target = destination[(written * channelCount + ch)..];
                for (var i = 0; i < take; i++)
                {
                    target[i * channelCount] = source[i];
                }
            }

            _framePosition += take;
            _nextSampleIndex += take;
            written += take;
        }

        return written;
    }

    /// <summary>
    /// Positions the decoder so the next read starts at <paramref name="sampleIndex"/>.
    /// Uses the SEEKTABLE when present and a validated binary search over frame headers otherwise.
    /// </summary>
    public bool Seek(long sampleIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (sampleIndex < 0)
        {
            sampleIndex = 0;
        }

        if (TotalSamples > 0 && sampleIndex >= TotalSamples)
        {
            // Park at the end: subsequent reads report end of stream.
            _reader.SeekTo(_stream.Length);
            _frameSamples = 0;
            _framePosition = 0;
            _endOfStream = true;
            _nextSampleIndex = TotalSamples;
            return true;
        }

        var startOffset = FindSeekStart(sampleIndex, out var startSample);
        _reader.SeekTo(startOffset);
        _endOfStream = false;
        _frameSamples = 0;
        _framePosition = 0;
        _nextSampleIndex = startSample;

        // Decode forward to the requested sample.
        while (true)
        {
            if (_framePosition >= _frameSamples)
            {
                var frameStart = _reader.BytePosition;
                if (!DecodeNextFrame())
                {
                    _endOfStream = true;
                    return false;
                }

                _ = frameStart;
            }

            var frameFirstSample = _nextSampleIndex;
            var frameLastSample = frameFirstSample + (_frameSamples - _framePosition);
            if (sampleIndex < frameLastSample)
            {
                var skip = (int)(sampleIndex - frameFirstSample);
                _framePosition += skip;
                _nextSampleIndex += skip;
                return true;
            }

            _nextSampleIndex = frameLastSample;
            _framePosition = _frameSamples;
        }
    }

    /// <summary>
    /// Decodes the whole stream and compares the MD5 of the unencoded audio with the value the
    /// encoder stored in STREAMINFO. This is the decoder's own correctness evidence: a match means
    /// every decoded integer sample equals what the encoder saw.
    /// </summary>
    public static FlacMd5Result VerifyMd5(string path, CancellationToken cancellationToken = default)
    {
        using var decoder = Open(path);
        var info = decoder.StreamInfo;
        if (!info.HasMd5)
        {
            return new FlacMd5Result(false, false, 0, null, null);
        }

        var bytesPerSample = (info.BitsPerSample + 7) / 8;
        var channelCount = info.Channels;
        const int framesPerPass = 4096;
        var samples = new int[framesPerPass * channelCount];
        var raw = new byte[framesPerPass * channelCount * bytesPerSample];

        using var md5 = System.Security.Cryptography.MD5.Create();
        long totalFrames = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frames = decoder.ReadSamples(samples, framesPerPass);
            if (frames == 0)
            {
                break;
            }

            totalFrames += frames;
            var count = frames * channelCount;
            var byteCount = count * bytesPerSample;
            for (var i = 0; i < count; i++)
            {
                var value = samples[i];
                var offset = i * bytesPerSample;
                for (var b = 0; b < bytesPerSample; b++)
                {
                    raw[offset + b] = (byte)(value >> (8 * b));
                }
            }

            md5.TransformBlock(raw, 0, byteCount, null, 0);
        }

        md5.TransformFinalBlock([], 0, 0);
        var computed = md5.Hash ?? [];
        var matches = computed.AsSpan().SequenceEqual(info.Md5);
        return new FlacMd5Result(true, matches, totalFrames, info.Md5, computed);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }

    private long FindSeekStart(long sampleIndex, out long startSample)
    {
        // Prefer a real seek point.
        var best = -1L;
        var bestSample = 0L;
        foreach (var point in Metadata.SeekPoints)
        {
            if (point.FirstSample == ulong.MaxValue)
            {
                continue;
            }

            if ((long)point.FirstSample <= sampleIndex && (long)point.FirstSample >= bestSample)
            {
                bestSample = (long)point.FirstSample;
                best = Metadata.FirstFrameOffset + (long)point.ByteOffset;
            }
        }

        if (best >= 0 && best < _stream.Length)
        {
            startSample = bestSample;
            return best;
        }

        // No usable seek table: binary search the byte range for a validated frame header.
        var low = Metadata.FirstFrameOffset;
        var high = _stream.Length;
        var resultOffset = low;
        var resultSample = 0L;

        for (var iteration = 0; iteration < 48 && high - low > 1024; iteration++)
        {
            var probe = low + ((high - low) / 2);
            if (!TryProbeFrame(probe, high, out var frameOffset, out var frameSample))
            {
                high = probe;
                continue;
            }

            if (frameSample <= sampleIndex)
            {
                low = frameOffset;
                resultOffset = frameOffset;
                resultSample = frameSample;
                if (frameOffset <= probe)
                {
                    low = frameOffset + 1;
                }
            }
            else
            {
                high = probe;
            }
        }

        startSample = resultSample;
        return resultOffset;
    }

    /// <summary>Scans forward from <paramref name="from"/> for a frame header that passes CRC-8.</summary>
    private bool TryProbeFrame(long from, long limit, out long frameOffset, out long frameSample)
    {
        frameOffset = 0;
        frameSample = 0;

        var buffer = new byte[16 * 1024];
        var position = from;
        var probe = new FlacBitReader(_stream);

        while (position < limit)
        {
            _stream.Position = position;
            var read = _stream.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - position));
            if (read < 2)
            {
                return false;
            }

            for (var i = 0; i < read - 1; i++)
            {
                if (buffer[i] != 0xFF || (buffer[i + 1] & 0xFE) != 0xF8)
                {
                    continue;
                }

                var candidate = position + i;
                probe.SeekTo(candidate);
                if (TryReadFrameHeader(probe, out var header) && TryResolveFirstSample(header, out var first))
                {
                    frameOffset = candidate;
                    frameSample = first;
                    return true;
                }
            }

            // Overlap by one byte so a sync word split across reads is still found.
            position += read - 1;
        }

        return false;
    }

    private bool TryResolveFirstSample(FrameHeader header, out long firstSample)
    {
        if (header.SampleRate != StreamInfo.SampleRate ||
            header.Channels != StreamInfo.Channels ||
            header.BitsPerSample != StreamInfo.BitsPerSample)
        {
            firstSample = 0;
            return false;
        }

        if (header.VariableBlockSize)
        {
            firstSample = (long)header.Number;
            return firstSample >= 0 && (TotalSamples <= 0 || firstSample < TotalSamples);
        }

        // Fixed blocking strategy: the header carries a frame number, and the sample position is
        // that number times the stream's constant block size. The final frame's own block size is
        // smaller than the constant, so header.BlockSize must not be used as the multiplier.
        // A fixed-block stream is required to report MinBlockSize == MaxBlockSize; when it does
        // not, the position cannot be derived and the probe is rejected so the caller falls back
        // to decoding from an earlier known-good offset.
        var constantBlockSize = StreamInfo.MinBlockSize;
        if (constantBlockSize <= 0 || constantBlockSize != StreamInfo.MaxBlockSize)
        {
            firstSample = 0;
            return false;
        }

        firstSample = (long)header.Number * constantBlockSize;
        return firstSample >= 0 && (TotalSamples <= 0 || firstSample < TotalSamples);
    }

    private bool DecodeNextFrame()
    {
        _reader.AlignToByte();

        if (!TryReadFrameHeader(_reader, out var header))
        {
            _endOfStream = true;
            return false;
        }

        if (header.Channels != _channels.Length || header.BitsPerSample != StreamInfo.BitsPerSample)
        {
            throw new InvalidDataException("FLAC frame changes channel count or bit depth mid-stream.");
        }

        if (header.BlockSize > _maxBlockSize)
        {
            throw new InvalidDataException($"FLAC frame block size {header.BlockSize} exceeds the declared maximum {_maxBlockSize}.");
        }

        DecodeSubframes(header);

        _reader.AlignToByte();
        var expected = _reader.Crc16;
        var stored = (ushort)_reader.ReadBits(16);
        if (expected != stored)
        {
            throw new InvalidDataException($"FLAC frame CRC-16 mismatch (computed 0x{expected:X4}, stored 0x{stored:X4}).");
        }

        _frameSamples = header.BlockSize;
        _framePosition = 0;
        return true;
    }

    private bool TryReadFrameHeader(FlacBitReader reader, out FrameHeader header)
    {
        header = default;
        reader.ResetCrc();

        if (!reader.TryNeed(16))
        {
            return false;
        }

        var sync = reader.ReadBits(14);
        if (sync != 0x3FFE)
        {
            return false;
        }

        if (reader.ReadBits(1) != 0)
        {
            return false; // reserved bit must be zero
        }

        var variableBlockSize = reader.ReadBits(1) == 1;
        var blockSizeCode = (int)reader.ReadBits(4);
        var sampleRateCode = (int)reader.ReadBits(4);
        var channelAssignment = (int)reader.ReadBits(4);
        var sampleSizeCode = (int)reader.ReadBits(3);

        if (reader.ReadBits(1) != 0)
        {
            return false; // reserved bit must be zero
        }

        if (blockSizeCode == 0 || sampleRateCode == 15 || channelAssignment > 10 || BitsPerSampleCodes[sampleSizeCode] < 0)
        {
            return false;
        }

        ulong number;
        try
        {
            number = reader.ReadUtf8Number();
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (EndOfStreamException)
        {
            return false;
        }

        int blockSize;
        switch (blockSizeCode)
        {
            case 1: blockSize = 192; break;
            case >= 2 and <= 5: blockSize = 576 << (blockSizeCode - 2); break;
            case 6: blockSize = (int)reader.ReadBits(8) + 1; break;
            case 7: blockSize = (int)reader.ReadBits(16) + 1; break;
            default: blockSize = 256 << (blockSizeCode - 8); break;
        }

        var sampleRate = sampleRateCode switch
        {
            0 => StreamInfo.SampleRate,
            12 => (int)reader.ReadBits(8) * 1000,
            13 => (int)reader.ReadBits(16),
            14 => (int)reader.ReadBits(16) * 10,
            _ => SampleRateCodes[sampleRateCode],
        };

        var bitsPerSample = sampleSizeCode == 0 ? StreamInfo.BitsPerSample : BitsPerSampleCodes[sampleSizeCode];
        var channels = channelAssignment < 8 ? channelAssignment + 1 : 2;

        var computedCrc = reader.Crc8;
        var storedCrc = (byte)reader.ReadBits(8);
        if (computedCrc != storedCrc)
        {
            return false;
        }

        if (blockSize <= 0 || sampleRate <= 0)
        {
            return false;
        }

        header = new FrameHeader
        {
            BlockSize = blockSize,
            SampleRate = sampleRate,
            Channels = channels,
            BitsPerSample = bitsPerSample,
            ChannelAssignment = channelAssignment,
            VariableBlockSize = variableBlockSize,
            Number = number,
        };
        return true;
    }

    private void DecodeSubframes(FrameHeader header)
    {
        var blockSize = header.BlockSize;
        var assignment = header.ChannelAssignment;

        for (var ch = 0; ch < header.Channels; ch++)
        {
            // In stereo decorrelation the side channel carries one extra bit of range.
            var extraBit = assignment switch
            {
                8 => ch == 1 ? 1 : 0,   // left / side
                9 => ch == 0 ? 1 : 0,   // side / right
                10 => ch == 1 ? 1 : 0,  // mid / side
                _ => 0,
            };

            DecodeSubframe(_channels[ch], blockSize, header.BitsPerSample + extraBit);
        }

        switch (assignment)
        {
            case 8: // left / side -> left, right
            {
                var left = _channels[0];
                var side = _channels[1];
                for (var i = 0; i < blockSize; i++)
                {
                    side[i] = left[i] - side[i];
                }

                break;
            }

            case 9: // side / right -> left, right
            {
                var side = _channels[0];
                var right = _channels[1];
                for (var i = 0; i < blockSize; i++)
                {
                    side[i] += right[i];
                }

                break;
            }

            case 10: // mid / side -> left, right
            {
                var mid = _channels[0];
                var side = _channels[1];
                for (var i = 0; i < blockSize; i++)
                {
                    var s = side[i];
                    var m = (mid[i] << 1) | (s & 1);
                    mid[i] = (m + s) >> 1;
                    side[i] = (m - s) >> 1;
                }

                break;
            }
        }
    }

    private void DecodeSubframe(int[] output, int blockSize, int bitsPerSample)
    {
        if (_reader.ReadBits(1) != 0)
        {
            throw new InvalidDataException("FLAC subframe padding bit is not zero.");
        }

        var type = (int)_reader.ReadBits(6);
        var wastedBits = 0;
        if (_reader.ReadBits(1) == 1)
        {
            wastedBits = _reader.ReadUnary() + 1;
        }

        var effectiveBits = bitsPerSample - wastedBits;
        if (effectiveBits <= 0 || effectiveBits > 32)
        {
            throw new InvalidDataException($"FLAC subframe has an invalid effective bit depth ({effectiveBits}).");
        }

        switch (type)
        {
            case 0:
            {
                var constant = _reader.ReadSigned(effectiveBits);
                output.AsSpan(0, blockSize).Fill(constant);
                break;
            }

            case 1:
            {
                for (var i = 0; i < blockSize; i++)
                {
                    output[i] = _reader.ReadSigned(effectiveBits);
                }

                break;
            }

            case >= 8 and <= 12:
                DecodeFixed(output, blockSize, effectiveBits, type - 8);
                break;

            case >= 32:
                DecodeLpc(output, blockSize, effectiveBits, (type & 0x1F) + 1);
                break;

            default:
                throw new InvalidDataException($"Reserved FLAC subframe type {type}.");
        }

        if (wastedBits > 0)
        {
            for (var i = 0; i < blockSize; i++)
            {
                output[i] <<= wastedBits;
            }
        }
    }

    private void DecodeFixed(int[] output, int blockSize, int bitsPerSample, int order)
    {
        if (order > blockSize)
        {
            throw new InvalidDataException("FLAC fixed subframe order exceeds the block size.");
        }

        for (var i = 0; i < order; i++)
        {
            output[i] = _reader.ReadSigned(bitsPerSample);
        }

        DecodeResidual(output, blockSize, order);

        switch (order)
        {
            case 0:
                break;
            case 1:
                for (var i = 1; i < blockSize; i++)
                {
                    output[i] += output[i - 1];
                }

                break;
            case 2:
                for (var i = 2; i < blockSize; i++)
                {
                    output[i] += (2 * output[i - 1]) - output[i - 2];
                }

                break;
            case 3:
                for (var i = 3; i < blockSize; i++)
                {
                    output[i] += (3 * output[i - 1]) - (3 * output[i - 2]) + output[i - 3];
                }

                break;
            case 4:
                for (var i = 4; i < blockSize; i++)
                {
                    output[i] += (4 * output[i - 1]) - (6 * output[i - 2]) + (4 * output[i - 3]) - output[i - 4];
                }

                break;
        }
    }

    private void DecodeLpc(int[] output, int blockSize, int bitsPerSample, int order)
    {
        if (order > blockSize)
        {
            throw new InvalidDataException("FLAC LPC subframe order exceeds the block size.");
        }

        for (var i = 0; i < order; i++)
        {
            output[i] = _reader.ReadSigned(bitsPerSample);
        }

        var precision = (int)_reader.ReadBits(4) + 1;
        if (precision == 16)
        {
            throw new InvalidDataException("Invalid FLAC LPC coefficient precision.");
        }

        var shift = _reader.ReadSigned(5);
        if (shift < 0)
        {
            throw new InvalidDataException("Negative FLAC LPC shift is not permitted.");
        }

        var coefficients = new int[order];
        for (var i = 0; i < order; i++)
        {
            coefficients[i] = _reader.ReadSigned(precision);
        }

        DecodeResidual(output, blockSize, order);

        // 64-bit accumulation keeps 24/32-bit material with high coefficient precision exact.
        for (var i = order; i < blockSize; i++)
        {
            long sum = 0;
            for (var j = 0; j < order; j++)
            {
                sum += (long)coefficients[j] * output[i - 1 - j];
            }

            output[i] += (int)(sum >> shift);
        }
    }

    private void DecodeResidual(int[] output, int blockSize, int predictorOrder)
    {
        var method = (int)_reader.ReadBits(2);
        if (method > 1)
        {
            throw new InvalidDataException($"Reserved FLAC residual coding method {method}.");
        }

        var parameterBits = method == 0 ? 4 : 5;
        var escapeValue = method == 0 ? 0x0F : 0x1F;

        var partitionOrder = (int)_reader.ReadBits(4);
        var partitionCount = 1 << partitionOrder;

        if (blockSize % partitionCount != 0)
        {
            throw new InvalidDataException("FLAC block size is not divisible by the partition count.");
        }

        var partitionSamples = blockSize >> partitionOrder;
        if (partitionSamples < predictorOrder)
        {
            throw new InvalidDataException("FLAC residual partition is smaller than the predictor order.");
        }

        var index = predictorOrder;
        for (var partition = 0; partition < partitionCount; partition++)
        {
            var count = partition == 0 ? partitionSamples - predictorOrder : partitionSamples;
            var parameter = (int)_reader.ReadBits(parameterBits);

            if (parameter == escapeValue)
            {
                var rawBits = (int)_reader.ReadBits(5);
                if (rawBits == 0)
                {
                    output.AsSpan(index, count).Clear();
                }
                else
                {
                    for (var i = 0; i < count; i++)
                    {
                        output[index + i] = _reader.ReadSigned(rawBits);
                    }
                }
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    output[index + i] = _reader.ReadRiceSigned(parameter);
                }
            }

            index += count;
        }
    }

    private struct FrameHeader
    {
        public int BlockSize;
        public int SampleRate;
        public int Channels;
        public int BitsPerSample;
        public int ChannelAssignment;
        public bool VariableBlockSize;
        public ulong Number;
    }
}

/// <summary>Outcome of comparing decoded audio with the MD5 recorded in STREAMINFO.</summary>
/// <param name="Md5Present">False when the encoder left the MD5 field zeroed.</param>
/// <param name="Matches">True when every decoded sample reproduces the encoder's input.</param>
/// <param name="DecodedFrames">Sample frames decoded.</param>
public sealed record FlacMd5Result(bool Md5Present, bool Matches, long DecodedFrames, byte[]? Expected, byte[]? Actual)
{
    public string Describe() => !Md5Present
        ? "STREAMINFO contains no MD5; decoder self-check unavailable for this file."
        : Matches
            ? $"MD5 match over {DecodedFrames:N0} sample frames."
            : $"MD5 MISMATCH: expected {Convert.ToHexString(Expected ?? [])}, computed {Convert.ToHexString(Actual ?? [])}.";
}
