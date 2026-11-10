using System.Diagnostics;
using Noct.Core.Library;

namespace Noct.Diagnostics;

/// <summary>
/// Times the library scanner on a folder: once cold (nothing known), then incrementally (every file
/// already indexed and unchanged). The numbers go into docs/BUILD_NOTES.md with the machine and
/// file counts, so a claimed improvement can be traced to a measurement.
/// </summary>
internal static class ScanBenchmark
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: scan <dir> [runs=3]");
            return 2;
        }

        var runs = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 3;
        var scanner = new LibraryScanner();
        var root = args[0];

        for (var run = 1; run <= runs; run++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var sw = Stopwatch.StartNew();
            var cold = await scanner.ScanAsync([root]).ConfigureAwait(false);
            sw.Stop();
            var allocated = (GC.GetTotalAllocatedBytes(precise: true) - before) / (1024.0 * 1024.0);

            var known = cold.Tracks.ToDictionary(t => t.Id);
            var before2 = GC.GetTotalAllocatedBytes(precise: true);
            var sw2 = Stopwatch.StartNew();
            var warm = await scanner.ScanAsync([root], null, default, known).ConfigureAwait(false);
            sw2.Stop();
            var allocated2 = (GC.GetTotalAllocatedBytes(precise: true) - before2) / (1024.0 * 1024.0);

            Console.WriteLine(
                $"run {run}: cold {sw.Elapsed.TotalMilliseconds,7:F0} ms ({cold.Tracks.Count} tracks, {cold.Problems.Count} unreadable, {allocated:F0} MB allocated)  |  " +
                $"incremental {sw2.Elapsed.TotalMilliseconds,6:F0} ms ({warm.Tracks.Count} tracks, {allocated2:F0} MB allocated)");
        }

        return 0;
    }
}
