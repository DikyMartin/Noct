using System.Diagnostics;
using Noct.Core.Audio;

namespace Noct.Diagnostics;

/// <summary>
/// Phase 2.1 / 3.4 evidence: checks the managed decoder three ways against reference material
/// produced by the Xiph encoder.
/// <list type="number">
/// <item>The MD5 of the decoded integers must equal the MD5 the encoder stored in STREAMINFO.</item>
/// <item>The decoded integers must equal the .pcm file this project fed to the encoder.</item>
/// <item>Seeking to an offset must yield the same samples as reading straight through to it.</item>
/// </list>
/// </summary>
internal static class DecoderCheck
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: decode <file-or-directory>");
            return 2;
        }

        var target = args[0];
        var files = Directory.Exists(target)
            ? Directory.GetFiles(target, "*.flac").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray()
            : [target];

        var failures = 0;
        var checkedFiles = 0;

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var expectCorrupt = name.StartsWith("corrupt_", StringComparison.OrdinalIgnoreCase) ||
                                name.StartsWith("not_audio", StringComparison.OrdinalIgnoreCase);

            try
            {
                var result = CheckOne(file);
                checkedFiles++;

                if (expectCorrupt)
                {
                    Console.WriteLine($"FAIL {name}: expected a decode error but the file decoded cleanly.");
                    failures++;
                    continue;
                }

                Console.WriteLine(result.Ok ? $"PASS {name}: {result.Detail}" : $"FAIL {name}: {result.Detail}");
                if (!result.Ok)
                {
                    failures++;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or NotSupportedException)
            {
                checkedFiles++;
                if (expectCorrupt)
                {
                    Console.WriteLine($"PASS {name}: rejected as expected ({ex.GetType().Name}: {ex.Message})");
                }
                else
                {
                    Console.WriteLine($"FAIL {name}: unexpected {ex.GetType().Name}: {ex.Message}");
                    failures++;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{checkedFiles} file(s) checked, {failures} failure(s).");
        return failures == 0 ? 0 : 1;
    }

    private static CheckResult CheckOne(string path)
    {
        var sw = Stopwatch.StartNew();
        var md5 = FlacDecoder.VerifyMd5(path);
        var md5Elapsed = sw.Elapsed;

        if (md5.Md5Present && !md5.Matches)
        {
            return new CheckResult(false, md5.Describe());
        }

        using var decoder = FlacDecoder.Open(path);
        var info = decoder.StreamInfo;
        var details = new List<string>
        {
            $"{info.BitsPerSample}-bit/{info.SampleRate} Hz/{info.Channels}ch",
            md5.Md5Present && md5.Matches ? "streaminfo-md5=match" : "streaminfo-md5=absent",
        };

        // 2. Byte-for-byte comparison with the PCM this project handed to the encoder.
        var pcmPath = Path.ChangeExtension(path, ".pcm");
        if (File.Exists(pcmPath))
        {
            var mismatch = CompareWithReferencePcm(path, pcmPath, info, out var comparedFrames);
            if (mismatch is not null)
            {
                return new CheckResult(false, $"reference PCM mismatch: {mismatch}");
            }

            details.Add($"reference-pcm=identical ({comparedFrames:N0} frames)");
        }
        else
        {
            details.Add("reference-pcm=unavailable");
        }

        // 3. Seek accuracy at several positions.
        var seekProblem = CheckSeeking(path, info);
        details.Add(seekProblem is null ? "seek=exact" : $"seek=FAILED ({seekProblem})");

        var throughput = md5Elapsed.TotalSeconds > 0 && info.SampleRate > 0
            ? md5.DecodedFrames / md5Elapsed.TotalSeconds / info.SampleRate
            : 0;
        details.Add($"{throughput:F0}x realtime");

        return new CheckResult(seekProblem is null, string.Join(", ", details));
    }

    private static string? CompareWithReferencePcm(string flacPath, string pcmPath, FlacStreamInfo info, out long comparedFrames)
    {
        comparedFrames = 0;
        var bytesPerSample = (info.BitsPerSample + 7) / 8;
        var channels = info.Channels;

        using var decoder = FlacDecoder.Open(flacPath);
        using var reference = File.OpenRead(pcmPath);

        const int framesPerPass = 2048;
        var decoded = new int[framesPerPass * channels];
        var referenceBytes = new byte[framesPerPass * channels * bytesPerSample];

        while (true)
        {
            var frames = decoder.ReadSamples(decoded, framesPerPass);
            if (frames == 0)
            {
                break;
            }

            var values = frames * channels;
            var byteCount = values * bytesPerSample;
            var read = reference.ReadAtLeast(referenceBytes.AsSpan(0, byteCount), byteCount, throwOnEndOfStream: false);
            if (read < byteCount)
            {
                return $"decoder produced more audio than the reference ({comparedFrames + frames:N0} frames)";
            }

            for (var i = 0; i < values; i++)
            {
                var expected = ReadLittleEndianSigned(referenceBytes, i * bytesPerSample, bytesPerSample);
                if (expected != decoded[i])
                {
                    var frame = comparedFrames + (i / channels);
                    return $"frame {frame:N0} channel {i % channels}: expected {expected}, decoded {decoded[i]}";
                }
            }

            comparedFrames += frames;
        }

        if (reference.Position != reference.Length)
        {
            return $"decoder stopped early at frame {comparedFrames:N0}; reference has more data";
        }

        if (info.TotalSamples > 0 && comparedFrames != info.TotalSamples)
        {
            return $"decoded {comparedFrames:N0} frames but STREAMINFO declares {info.TotalSamples:N0}";
        }

        return null;
    }

    private static string? CheckSeeking(string path, FlacStreamInfo info)
    {
        if (info.TotalSamples <= 0)
        {
            return null;
        }

        var channels = info.Channels;
        long[] targets =
        [
            0,
            1,
            info.TotalSamples / 4,
            info.TotalSamples / 3,
            info.TotalSamples / 2,
            info.TotalSamples - 1000,
            info.TotalSamples - 1,
        ];

        const int window = 512;

        using var linear = FlacDecoder.Open(path);
        using var seeking = FlacDecoder.Open(path);
        var expected = new int[window * channels];
        var actual = new int[window * channels];

        foreach (var target in targets.Where(t => t >= 0 && t < info.TotalSamples).Distinct().OrderBy(t => t))
        {
            // Reference: read linearly from the start of the file every time.
            using var fresh = FlacDecoder.Open(path);
            var skipped = SkipTo(fresh, target);
            if (skipped != target)
            {
                return $"linear read could not reach sample {target}";
            }

            var expectedFrames = fresh.ReadSamples(expected, window);

            if (!seeking.Seek(target))
            {
                return $"Seek({target}) returned false";
            }

            if (seeking.Position != target)
            {
                return $"Seek({target}) left position at {seeking.Position}";
            }

            var actualFrames = seeking.ReadSamples(actual, window);
            if (actualFrames != expectedFrames)
            {
                return $"after Seek({target}) read {actualFrames} frames, expected {expectedFrames}";
            }

            for (var i = 0; i < expectedFrames * channels; i++)
            {
                if (expected[i] != actual[i])
                {
                    return $"after Seek({target}) sample {i} was {actual[i]}, expected {expected[i]}";
                }
            }
        }

        _ = linear;
        return null;
    }

    private static long SkipTo(FlacDecoder decoder, long target)
    {
        var scratch = new int[4096 * decoder.Channels];
        while (decoder.Position < target)
        {
            var want = (int)Math.Min(4096, target - decoder.Position);
            var got = decoder.ReadSamples(scratch, want);
            if (got == 0)
            {
                break;
            }
        }

        return decoder.Position;
    }

    private static int ReadLittleEndianSigned(byte[] data, int offset, int byteCount)
    {
        var value = 0;
        for (var i = 0; i < byteCount; i++)
        {
            value |= data[offset + i] << (8 * i);
        }

        var shift = 32 - (byteCount * 8);
        return shift > 0 ? (value << shift) >> shift : value;
    }

    private sealed record CheckResult(bool Ok, string Detail);
}
