using Noct.Core.Audio;
using Noct.Core.Library;
using Noct.Core.Playback;
using Noct.Core.Settings;

namespace Noct.Diagnostics;

/// <summary>
/// Phase 2 evidence: drives the real engine against real files on the real default device, with no
/// stub anywhere in the path. Prints the snapshot the UI would show so position, format reporting
/// and queue advance can be checked without the window.
/// </summary>
internal static class PlaybackCheck
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: play <file-or-directory> [seconds] [shared|exclusive] [normal|transparent]");
            return 2;
        }

        var target = args[0];
        var seconds = args.Length > 1 && double.TryParse(args[1], out var s) ? s : 6.0;
        var backend = args.Length > 2 && args[2].Equals("exclusive", StringComparison.OrdinalIgnoreCase)
            ? OutputBackend.WasapiExclusive
            : OutputBackend.WasapiShared;
        var mode = args.Length > 3 && args[3].Equals("transparent", StringComparison.OrdinalIgnoreCase)
            ? OutputMode.Transparent
            : OutputMode.Normal;

        var files = Directory.Exists(target)
            ? Directory.GetFiles(target, "*.flac").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray()
            : [target];

        var tracks = new List<Track>();
        foreach (var file in files)
        {
            try
            {
                var metadata = FlacDecoder.ReadMetadata(file);
                tracks.Add(Track.FromMetadata(file, metadata, new FileInfo(file)));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"skipping {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        if (tracks.Count == 0)
        {
            Console.Error.WriteLine("No playable FLAC files found.");
            return 1;
        }

        // A throwaway settings file so the check never disturbs real user settings.
        var settingsPath = Path.Combine(Path.GetTempPath(), $"noct-diag-{Guid.NewGuid():N}.json");
        using var settings = new SettingsStore(settingsPath);
        settings.Update(s =>
        {
            s.OutputBackend = backend;
            s.OutputMode = mode;
            s.OutputDeviceKey = null;
            s.Volume = 0.35;
            s.GaplessEnabled = true;
        });

        await using var engine = new PlaybackEngine(settings);

        var lastLine = string.Empty;
        var reportedOutput = false;
        engine.SnapshotChanged += (_, snapshot) =>
        {
            if (!reportedOutput && snapshot.Output is { Status: NegotiationStatus.Succeeded } negotiation)
            {
                reportedOutput = true;
                Console.WriteLine();
                Console.WriteLine($"  device      : {negotiation.DeviceName}");
                Console.WriteLine($"  backend     : {negotiation.ActualBackend} ({negotiation.ActualMode})");
                Console.WriteLine($"  source      : {negotiation.Source.Describe()}");
                Console.WriteLine($"  accepted    : {negotiation.AcceptedFormat?.Describe()}");
                Console.WriteLine($"  packing     : {negotiation.Packing ?? "none (exact container match)"}");
                Console.WriteLine($"  conversions : {(negotiation.Conversions.Count == 0 ? "none" : string.Join(" | ", negotiation.Conversions))}");
                Console.WriteLine($"  transparency: {negotiation.Transparency}");
                Console.WriteLine($"  latency     : {negotiation.LatencyMilliseconds} ms");
                Console.WriteLine();
            }

            var line =
                $"  [{snapshot.Status,-8}] {snapshot.Position:mm\\:ss\\.ff}/{snapshot.Duration:mm\\:ss} " +
                $"q={snapshot.QueueIndex + 1}/{snapshot.QueueCount} gapless-armed={snapshot.NextTrackJoinedGaplessly,-5} " +
                $"vol={(snapshot.VolumeIsSoftware ? $"{snapshot.Volume:P0} software" : "unity (DAC)")} :: {snapshot.Track?.Title}";

            if (line != lastLine)
            {
                lastLine = line;
                Console.WriteLine(line);
            }
        };

        engine.NoticeRaised += (_, notice) =>
            Console.WriteLine($"  >> {notice.Severity}: {notice.Message} {notice.Action}");

        Console.WriteLine($"Playing {tracks.Count} track(s) for {seconds:F1} s on {backend}/{mode}.");
        engine.PlayTracks(tracks, 0);

        await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);

        Console.WriteLine("\n-- pause --");
        engine.Pause();
        await Task.Delay(1200).ConfigureAwait(false);

        Console.WriteLine("-- resume --");
        engine.Resume();
        await Task.Delay(1500).ConfigureAwait(false);

        Console.WriteLine("-- seek to 00:00.30 --");
        engine.Seek(TimeSpan.FromSeconds(0.30));
        await Task.Delay(1500).ConfigureAwait(false);

        if (tracks.Count > 1)
        {
            Console.WriteLine("-- next --");
            engine.Next();
            await Task.Delay(2500).ConfigureAwait(false);

            Console.WriteLine("-- previous --");
            engine.Previous();
            await Task.Delay(1500).ConfigureAwait(false);
        }

        Console.WriteLine("-- stop --");
        engine.Stop();
        await Task.Delay(500).ConfigureAwait(false);

        var final = engine.Snapshot;
        Console.WriteLine($"\nfinal status: {final.Status}");

        try
        {
            File.Delete(settingsPath);
        }
        catch (IOException)
        {
            // Temp file cleanup is best effort.
        }

        return final.Status == PlaybackStatus.Failed ? 1 : 0;
    }
}
