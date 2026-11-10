using NAudio.CoreAudioApi;
using Noct.Core.Audio;

namespace Noct.Diagnostics;

/// <summary>
/// Phase 3.1 evidence: lists real output endpoints and records, per endpoint, which formats the
/// driver accepts in shared and exclusive mode. The output is pasted into docs/BUILD_NOTES.md so a
/// capability claim can always be traced back to a device answer.
/// </summary>
internal static class DeviceProbe
{
    private static readonly SourceFormat[] Sources =
    [
        new("FLAC", 44100, 2, 16),
        new("FLAC", 48000, 2, 24),
        new("FLAC", 88200, 2, 24),
        new("FLAC", 96000, 2, 24),
        new("FLAC", 192000, 2, 24),
    ];

    public static int Run(string[] args)
    {
        using var catalog = new OutputDeviceCatalog();
        var devices = catalog.Enumerate();

        Console.WriteLine($"Output endpoints: {devices.Count}");
        foreach (var group in devices.GroupBy(d => d.Backend))
        {
            Console.WriteLine($"\n[{group.Key}]");
            foreach (var device in group)
            {
                Console.WriteLine($"  {(device.IsDefault ? "*" : " ")} {device.Name}");
                Console.WriteLine($"      id: {device.Id}");
            }
        }

        var wasapi = devices.Where(d => d.Backend == OutputBackend.WasapiShared).ToList();
        if (args.Length > 0)
        {
            wasapi = wasapi.Where(d => d.Name.Contains(args[0], StringComparison.OrdinalIgnoreCase)).ToList();
        }

        foreach (var device in wasapi)
        {
            Console.WriteLine($"\n==== {device.Name} ====");
            var mix = catalog.GetMixFormat(device.Id);
            Console.WriteLine($"shared mix format: {(mix is null ? "unreadable" : mix.Describe())}");

            foreach (var source in Sources)
            {
                Console.WriteLine($"\n  source {source.Describe()}");

                var exclusive = OutputFormatPolicy.ExclusiveCandidates(source);
                var exclusiveProbes = catalog.ProbeFormats(device.Id, AudioClientShareMode.Exclusive, exclusive);
                foreach (var probe in exclusiveProbes)
                {
                    Console.WriteLine($"    exclusive {Describe(probe)}");
                }

                var shared = OutputFormatPolicy.SharedCandidates(source);
                var sharedProbes = catalog.ProbeFormats(device.Id, AudioClientShareMode.Shared, shared);
                foreach (var probe in sharedProbes)
                {
                    Console.WriteLine($"    shared    {Describe(probe)}");
                }
            }
        }

        Console.WriteLine("\n==== Negotiation results (what the UI would report) ====");
        foreach (var device in devices.Where(d => d.Backend != OutputBackend.Asio))
        {
            if (args.Length > 0 && !device.Name.Contains(args[0], StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var mode in new[] { OutputMode.Normal, OutputMode.Transparent })
            {
                foreach (var source in Sources)
                {
                    var negotiation = WasapiAudioOutput.Negotiate(catalog, device, mode, source, 200);
                    Console.WriteLine($"\n  {device.Backend} / {mode} / {source.Describe()}");
                    Console.WriteLine($"    status      : {negotiation.Status}");
                    if (negotiation.Status == NegotiationStatus.Succeeded)
                    {
                        Console.WriteLine($"    accepted    : {negotiation.AcceptedFormat!.Describe()}");
                        Console.WriteLine($"    app output  : {negotiation.AppOutputFormat!.Describe()}");
                        Console.WriteLine($"    packing     : {negotiation.Packing ?? "none (exact container match)"}");
                        Console.WriteLine($"    conversions : {(negotiation.Conversions.Count == 0 ? "none" : string.Join(" | ", negotiation.Conversions))}");
                        Console.WriteLine($"    transparency: {negotiation.Transparency}");
                    }
                    else
                    {
                        Console.WriteLine($"    failure     : {negotiation.FailureKind} - {negotiation.FailureMessage}");
                    }
                }
            }
        }

        var asio = devices.Where(d => d.Backend == OutputBackend.Asio).ToList();
        Console.WriteLine();
        if (asio.Count == 0)
        {
            Console.WriteLine("ASIO: no compatible driver is installed on this machine; the ASIO path is untested here.");
        }
        else
        {
            foreach (var driver in asio)
            {
                Console.WriteLine($"ASIO driver present: {driver.Name} (opening it is only attempted on user request)");
            }
        }

        return 0;
    }

    private static string Describe(FormatProbe probe)
    {
        var status = probe.Error is not null ? $"ERROR {probe.Error}" : probe.Supported ? "accepted" : "rejected";
        var closest = probe.ClosestMatch is null || probe.Supported ? "" : $"  closest={probe.ClosestMatch.Describe()}";
        return $"{probe.Requested.Describe(),-56} {status}{closest}";
    }
}
