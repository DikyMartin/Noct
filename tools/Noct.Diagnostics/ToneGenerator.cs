namespace Noct.Diagnostics;

/// <summary>
/// Writes a quiet, deterministic sine WAV so a long playback test does not need a real music file
/// and does not blast the speakers. Encode it with flac.exe to get a FLAC.
/// </summary>
internal static class ToneGenerator
{
    public static int Run(string[] args)
    {
        if (args.Length < 2 || !double.TryParse(args[1], out var seconds))
        {
            Console.Error.WriteLine("usage: tone <out.wav> <seconds> [rate=44100] [amplitude=0.04]");
            return 2;
        }

        var rate = args.Length > 2 && int.TryParse(args[2], out var r) ? r : 44100;
        var amplitude = args.Length > 3 && double.TryParse(args[3], out var a) ? a : 0.04;
        var frames = (int)(seconds * rate);
        const int channels = 2;
        var dataBytes = frames * channels * 2;

        using var stream = File.Create(args[0]);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(rate);
        writer.Write(rate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);

        for (var n = 0; n < frames; n++)
        {
            var t = (double)n / rate;
            var left = (short)(Math.Sin(2 * Math.PI * 220 * t) * amplitude * short.MaxValue);
            var right = (short)(Math.Sin(2 * Math.PI * 330 * t) * amplitude * short.MaxValue);
            writer.Write(left);
            writer.Write(right);
        }

        Console.WriteLine($"wrote {args[0]} ({seconds:F0} s, {rate} Hz, amplitude {amplitude})");
        return 0;
    }
}
