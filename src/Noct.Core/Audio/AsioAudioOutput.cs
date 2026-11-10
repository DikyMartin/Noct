using NAudio.Wave;

namespace Noct.Core.Audio;

/// <summary>
/// Optional ASIO output through an installed third-party driver.
/// </summary>
/// <remarks>
/// <para>
/// Status on this project: implemented and selectable, <b>untested on hardware</b>. No compatible
/// ASIO driver was installed on the development machine, so no claim is made about its latency,
/// stability or sample accuracy. See docs/BUILD_NOTES.md and docs/VALIDATION.md.
/// </para>
/// <para>
/// Transparency is deliberately reported as unavailable on this path even when the driver accepts
/// the source rate. NAudio converts samples into the driver's native sample type through its own
/// converter, and that conversion has not been measured here. Reporting it as transparent would be
/// exactly the unverified claim this project forbids; WASAPI Exclusive remains the transparent path.
/// </para>
/// </remarks>
public sealed class AsioAudioOutput : IAudioOutput
{
    private readonly AsioOut _asio;
    private readonly object _gate = new();
    private bool _disposed;

    private AsioAudioOutput(AsioOut asio, OutputNegotiation negotiation)
    {
        _asio = asio;
        Negotiation = negotiation;
        _asio.PlaybackStopped += OnStopped;
    }

    public OutputNegotiation Negotiation { get; }

    public OutputState State
    {
        get
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return OutputState.Stopped;
                }

                return _asio.PlaybackState switch
                {
                    PlaybackState.Playing => OutputState.Playing,
                    PlaybackState.Paused => OutputState.Paused,
                    _ => OutputState.Stopped,
                };
            }
        }
    }

    public long? PlayedFrames => null;

    public event EventHandler<OutputStoppedEventArgs>? Stopped;

    public static bool IsAvailable()
    {
        try
        {
            return AsioOut.IsSupported() && AsioOut.GetDriverNames().Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static OutputNegotiation Negotiate(OutputDeviceRef device, OutputMode mode, SourceFormat source)
    {
        var attempts = new List<string>();

        if (!IsAvailable())
        {
            return OutputNegotiation.Failure(
                source,
                OutputBackend.Asio,
                mode,
                OutputFailureKind.DeviceUnavailable,
                "No compatible ASIO driver is installed. WASAPI output remains available.",
                attempts,
                device.Name,
                device.Id);
        }

        AsioOut? probe = null;
        try
        {
            probe = new AsioOut(device.Id);
            var supported = probe.IsSampleRateSupported(source.SampleRate);
            attempts.Add($"{source.SampleRate / 1000.0:0.###} kHz: {(supported ? "accepted" : "rejected")} by driver '{probe.DriverName}'");

            if (!supported)
            {
                return OutputNegotiation.Failure(
                    source,
                    OutputBackend.Asio,
                    mode,
                    OutputFailureKind.FormatNotSupported,
                    $"The ASIO driver '{device.Name}' does not support {source.SampleRate / 1000.0:0.###} kHz. Noct does not resample.",
                    attempts,
                    device.Name,
                    device.Id);
            }

            if (probe.DriverOutputChannelCount < source.Channels)
            {
                return OutputNegotiation.Failure(
                    source,
                    OutputBackend.Asio,
                    mode,
                    OutputFailureKind.FormatNotSupported,
                    $"The ASIO driver exposes {probe.DriverOutputChannelCount} output channel(s) but the track needs {source.Channels}.",
                    attempts,
                    device.Name,
                    device.Id);
            }

            if (mode == OutputMode.Transparent)
            {
                return OutputNegotiation.Failure(
                    source,
                    OutputBackend.Asio,
                    mode,
                    OutputFailureKind.TransparentPathUnavailable,
                    "Noct's ASIO path has not been verified for sample-exact output, so it is not offered as a transparent path. Use WASAPI Exclusive for transparent playback, or select Normal mode to use this ASIO driver.",
                    attempts,
                    device.Name,
                    device.Id);
            }

            var appFormat = new AudioFormat(source.SampleRate, source.Channels, ContainerBits(source), source.BitsPerSample);

            return new OutputNegotiation
            {
                Status = NegotiationStatus.Succeeded,
                Source = source,
                RequestedBackend = OutputBackend.Asio,
                RequestedMode = mode,
                ActualBackend = OutputBackend.Asio,
                ActualMode = OutputMode.Normal,
                AcceptedFormat = appFormat,
                AppOutputFormat = appFormat,
                DeviceId = device.Id,
                DeviceName = device.Name,
                Conversions = ["ASIO driver sample-type conversion (not measured on this build)"],
                Packing = OutputFormatPolicy.DescribePacking(source, appFormat),
                Transparency = TransparencyStatus.NotApplicable,
                Attempts = attempts,
            };
        }
        catch (Exception ex)
        {
            var (kind, message) = OutputDeviceCatalog.Classify(ex);
            return OutputNegotiation.Failure(source, OutputBackend.Asio, mode, kind, message, attempts, device.Name, device.Id);
        }
        finally
        {
            probe?.Dispose();
        }
    }

    public static (AsioAudioOutput? Output, OutputNegotiation Negotiation) TryOpen(
        OutputDeviceRef device,
        OutputMode mode,
        SourceFormat source,
        Func<AudioFormat, IWaveProvider> createSource)
    {
        var negotiation = Negotiate(device, mode, source);
        if (negotiation.Status != NegotiationStatus.Succeeded)
        {
            return (null, negotiation);
        }

        AsioOut? asio = null;
        try
        {
            asio = new AsioOut(device.Id);
            var provider = createSource(negotiation.AppOutputFormat!);
            asio.Init(provider);

            var accepted = asio.OutputWaveFormat is null
                ? negotiation.AcceptedFormat!
                : AudioFormat.FromWaveFormat(asio.OutputWaveFormat);

            var resolved = negotiation with
            {
                AcceptedFormat = accepted,
                LatencyMilliseconds = asio.PlaybackLatency > 0 && source.SampleRate > 0
                    ? (int)Math.Round(asio.PlaybackLatency * 1000.0 / source.SampleRate)
                    : 0,
            };

            return (new AsioAudioOutput(asio, resolved), resolved);
        }
        catch (Exception ex)
        {
            asio?.Dispose();
            var (kind, message) = OutputDeviceCatalog.Classify(ex);
            return (null, OutputNegotiation.Failure(source, OutputBackend.Asio, mode, kind, message, negotiation.Attempts, device.Name, device.Id));
        }
    }

    public void Play()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _asio.Play();
            }
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!_disposed && _asio.PlaybackState == PlaybackState.Playing)
            {
                _asio.Pause();
            }
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_disposed && _asio.PlaybackState != PlaybackState.Playing)
            {
                _asio.Play();
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                try
                {
                    _asio.Stop();
                }
                catch (Exception)
                {
                    // A driver that fails to stop must not stall shutdown.
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _asio.PlaybackStopped -= OnStopped;
        try
        {
            _asio.Stop();
        }
        catch (Exception)
        {
            // Ignore.
        }

        _asio.Dispose();
    }

    private static int ContainerBits(SourceFormat source) => source.BitsPerSample switch
    {
        <= 8 => 8,
        <= 16 => 16,
        <= 24 => 24,
        _ => 32,
    };

    private void OnStopped(object? sender, StoppedEventArgs e) =>
        Stopped?.Invoke(this, new OutputStoppedEventArgs(e.Exception));
}
