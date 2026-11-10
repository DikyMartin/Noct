namespace Noct.Diagnostics;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 0)
        {
            Console.WriteLine("Noct diagnostics harness");
            Console.WriteLine("  api [filter...]        dump the pinned NAudio public API surface");
            Console.WriteLine("  decode <file|dir>      verify the managed FLAC decoder against reference material");
            Console.WriteLine("  devices [name-filter]  list output endpoints and probe accepted formats");
            Console.WriteLine("  play <file|dir> [sec] [shared|exclusive] [normal|transparent]");
            Console.WriteLine("  tone <out.wav> <seconds> [rate] [amplitude]   write a quiet 16-bit stereo sine WAV");
            Console.WriteLine("  meter <pid> [sec] [ms]  peak level of that process's audio session on the default output");
            Console.WriteLine("  scan <dir> [runs]       time the library scanner (cold, then incremental)");
            return 1;
        }

        return args[0].ToLowerInvariant() switch
        {
            "api" => ApiDump.Run(args.Skip(1).ToArray()),
            "decode" => DecoderCheck.Run(args.Skip(1).ToArray()),
            "devices" => DeviceProbe.Run(args.Skip(1).ToArray()),
            "play" => await PlaybackCheck.RunAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
            "tone" => ToneGenerator.Run(args.Skip(1).ToArray()),
            "meter" => MeterProbe.Run(args.Skip(1).ToArray()),
            "scan" => await ScanBenchmark.RunAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
            _ => Unknown(args[0]),
        };
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"unknown command: {verb}");
        return 2;
    }
}
