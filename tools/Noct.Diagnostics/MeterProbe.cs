using NAudio.CoreAudioApi;

namespace Noct.Diagnostics;

/// <summary>
/// Reads what Windows sees for one process's audio session on the default output device, so a volume
/// change can be checked against the audio itself and not only against what the UI says.
/// Usage: meter &lt;pid&gt; [seconds] [intervalMs]. Prints the session volume and the highest endpoint peak
/// (0..1) over the window.
/// </summary>
/// <remarks>
/// The session's own meter reads the stream before the session volume is applied, so it cannot show a volume
/// change. The endpoint meter reads the mix after it, so it can, but it also hears every other program that is
/// playing: use it on a quiet machine. Windows lists a process's sessions once per process, so a tool that
/// needs to see a session that appears later must be started again.
/// </remarks>
internal static class MeterProbe
{
    public static int Run(string[] args)
    {
        if (args.Length < 1 || !int.TryParse(args[0], out var pid))
        {
            Console.Error.WriteLine("usage: meter <pid> [seconds] [intervalMs]");
            return 2;
        }

        var seconds = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 1.0;
        var interval = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 20;

        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;

        AudioSessionControl? mine = null;
        var seen = new List<string>();
        for (var i = 0; i < sessions.Count; i++)
        {
            seen.Add(sessions[i].GetProcessID.ToString());
            if (sessions[i].GetProcessID == (uint)pid)
            {
                mine = sessions[i];
            }
        }

        if (mine is null)
        {
            Console.WriteLine($"none (no audio session for pid {pid} on the default device; sessions present for pids: {string.Join(",", seen)})");
            return 3;
        }

        var start = DateTime.UtcNow;
        var deadline = start.AddSeconds(seconds);
        float endpointMax = 0, endpointFirst = 0, volumeFirst = -1;
        while (DateTime.UtcNow < deadline)
        {
            var peak = device.AudioMeterInformation.MasterPeakValue;
            endpointMax = Math.Max(endpointMax, peak);
            if (volumeFirst < 0)
            {
                volumeFirst = mine.SimpleAudioVolume.Volume;
                endpointFirst = peak;
            }

            Thread.Sleep(interval);
        }

        Console.WriteLine(FormattableString.Invariant(
            $"endpointPeak={endpointMax:F4} sessionVolume={mine.SimpleAudioVolume.Volume:F2} muted={mine.SimpleAudioVolume.Mute} | first sample: endpointPeak={endpointFirst:F4} sessionVolumeFirst={volumeFirst:F2}"));
        return 0;
    }
}
