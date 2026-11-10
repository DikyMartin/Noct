using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace Noct.Core.Audio;

/// <summary>
/// WASAPI render output, shared or exclusive.
/// </summary>
/// <remarks>
/// <para>
/// Negotiation never silently substitutes a different sample rate. In exclusive mode the device is
/// offered the source rate in a series of lossless container widths; if none is accepted the
/// negotiation fails with the device's own answer so the user can choose what to do.
/// </para>
/// <para>
/// Shared mode is the functional baseline. It cannot be transparent on any machine: the Windows
/// mixer always sits in the path, applies the endpoint volume and mixes other applications. When
/// the endpoint will not take the source rate directly, the Media Foundation resampler is inserted
/// and listed in <see cref="OutputNegotiation.Conversions"/> rather than hidden.
/// </para>
/// </remarks>
public sealed class WasapiAudioOutput : IAudioOutput
{
    private readonly MMDevice _device;
    private readonly WasapiPlayer _player;
    private readonly MediaFoundationResampler? _resampler;
    private readonly object _gate = new();
    private bool _disposed;

    private WasapiAudioOutput(MMDevice device, WasapiPlayer player, MediaFoundationResampler? resampler, OutputNegotiation negotiation)
    {
        _device = device;
        _player = player;
        _resampler = resampler;
        Negotiation = negotiation;
        _player.PlaybackStopped += OnPlayerStopped;
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

                return _player.PlaybackState switch
                {
                    PlaybackState.Playing => OutputState.Playing,
                    PlaybackState.Paused => OutputState.Paused,
                    _ => OutputState.Stopped,
                };
            }
        }
    }

    public long? PlayedFrames
    {
        get
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return null;
                }

                try
                {
                    var bytes = _player.GetPosition();
                    var frameBytes = Negotiation.AcceptedFormat?.BytesPerFrame ?? 0;
                    return frameBytes > 0 ? bytes / frameBytes : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }
    }

    public event EventHandler<OutputStoppedEventArgs>? Stopped;

    /// <summary>
    /// Asks the endpoint what it will accept without opening a stream. Safe to call from the
    /// settings UI; it does not disturb an active playback session on another device.
    /// </summary>
    public static OutputNegotiation Negotiate(
        OutputDeviceCatalog catalog,
        OutputDeviceRef device,
        OutputMode mode,
        SourceFormat source,
        int latencyMilliseconds)
    {
        var attempts = new List<string>();
        var exclusive = device.Backend == OutputBackend.WasapiExclusive;

        if (mode == OutputMode.Transparent && !exclusive)
        {
            return OutputNegotiation.Failure(
                source,
                device.Backend,
                mode,
                OutputFailureKind.TransparentPathUnavailable,
                "Transparent output requires WASAPI Exclusive or ASIO. Shared mode always routes audio through the Windows mixer, which applies the system volume and mixes other applications.",
                attempts,
                device.Name,
                device.Id);
        }

        MMDevice? mmDevice = null;
        AudioClient? client = null;

        try
        {
            mmDevice = catalog.GetEnumerator().GetDevice(device.Id);
            client = mmDevice.CreateAudioClient();
        }
        catch (Exception ex)
        {
            client?.Dispose();
            mmDevice?.Dispose();
            var (kind, message) = OutputDeviceCatalog.Classify(ex);
            return OutputNegotiation.Failure(source, device.Backend, mode, kind, message, attempts, device.Name, device.Id);
        }

        try
        {
            var shareMode = exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared;
            var candidates = exclusive
                ? OutputFormatPolicy.ExclusiveCandidates(source)
                : OutputFormatPolicy.SharedCandidates(source);

            WaveFormat? acceptedWaveFormat = null;
            WaveFormat? suggestion = null;

            foreach (var candidate in candidates)
            {
                var described = AudioFormat.FromWaveFormat(candidate).Describe();
                try
                {
                    if (client.IsFormatSupported(shareMode, candidate, out var closest))
                    {
                        attempts.Add($"{described}: accepted");
                        acceptedWaveFormat = candidate;
                        break;
                    }

                    suggestion ??= closest;
                    attempts.Add($"{described}: rejected{(closest is null ? "" : $" (device suggested {AudioFormat.FromWaveFormat(closest).Describe()})")}");
                }
                catch (Exception ex)
                {
                    attempts.Add($"{described}: {OutputDeviceCatalog.Classify(ex).Message}");
                }
            }

            if (acceptedWaveFormat is not null)
            {
                var accepted = AudioFormat.FromWaveFormat(acceptedWaveFormat);
                var lossless = OutputFormatPolicy.IsLossless(source, accepted);
                var conversions = exclusive
                    ? Array.Empty<string>()
                    : ["Windows shared-mode mixer (system volume and application mixing)"];

                var transparency = mode != OutputMode.Transparent
                    ? TransparencyStatus.NotApplicable
                    : lossless && exclusive
                        ? TransparencyStatus.ConfiguredNotHardwareVerified
                        : TransparencyStatus.Unavailable;

                return new OutputNegotiation
                {
                    Status = NegotiationStatus.Succeeded,
                    Source = source,
                    RequestedBackend = device.Backend,
                    RequestedMode = mode,
                    ActualBackend = device.Backend,
                    ActualMode = transparency == TransparencyStatus.ConfiguredNotHardwareVerified ? OutputMode.Transparent : OutputMode.Normal,
                    AcceptedFormat = accepted,
                    AppOutputFormat = accepted,
                    DeviceId = device.Id,
                    DeviceName = device.Name,
                    Conversions = conversions,
                    Packing = OutputFormatPolicy.DescribePacking(source, accepted),
                    Transparency = transparency,
                    Attempts = attempts,
                    LatencyMilliseconds = latencyMilliseconds,
                };
            }

            if (exclusive)
            {
                var detail = suggestion is null
                    ? $"The device did not accept {source.SampleRate / 1000.0:0.###} kHz in exclusive mode at any container width."
                    : $"The device did not accept {source.SampleRate / 1000.0:0.###} kHz in exclusive mode; it suggested {AudioFormat.FromWaveFormat(suggestion).Describe()}. Noct does not resample, so this track cannot play on this device in exclusive mode.";

                return OutputNegotiation.Failure(
                    source,
                    device.Backend,
                    mode,
                    OutputFailureKind.FormatNotSupported,
                    detail,
                    attempts,
                    device.Name,
                    device.Id);
            }

            // Shared mode fallback: convert to the endpoint mix format and declare every stage.
            var mixWaveFormat = client.MixFormat;
            var mix = AudioFormat.FromWaveFormat(mixWaveFormat);
            var stages = new List<string>();
            if (mix.SampleRate != source.SampleRate)
            {
                stages.Add($"Media Foundation resampler {source.SampleRate / 1000.0:0.###} kHz -> {mix.SampleRate / 1000.0:0.###} kHz");
            }

            if (mix.Encoding == SampleEncoding.IeeeFloat)
            {
                stages.Add($"{source.BitsPerSample}-bit integer -> 32-bit float");
            }
            else if (mix.ValidBits != source.BitsPerSample)
            {
                stages.Add($"{source.BitsPerSample}-bit -> {mix.ValidBits}-bit");
            }

            stages.Add("Windows shared-mode mixer (system volume and application mixing)");
            attempts.Add($"shared mix format {mix.Describe()}: used as the conversion target");

            return new OutputNegotiation
            {
                Status = NegotiationStatus.Succeeded,
                Source = source,
                RequestedBackend = device.Backend,
                RequestedMode = mode,
                ActualBackend = OutputBackend.WasapiShared,
                ActualMode = OutputMode.Normal,
                AcceptedFormat = mix,
                AppOutputFormat = new AudioFormat(source.SampleRate, source.Channels, ContainerBits(source), source.BitsPerSample),
                DeviceId = device.Id,
                DeviceName = device.Name,
                Conversions = stages,
                Packing = null,
                Transparency = TransparencyStatus.NotApplicable,
                Attempts = attempts,
                LatencyMilliseconds = latencyMilliseconds,
            };
        }
        finally
        {
            client.Dispose();
            mmDevice.Dispose();
        }
    }

    /// <summary>
    /// Negotiates and opens a stream. <paramref name="createSource"/> is called with the format
    /// Noct must produce, which is <see cref="OutputNegotiation.AppOutputFormat"/>.
    /// </summary>
    public static (WasapiAudioOutput? Output, OutputNegotiation Negotiation) TryOpen(
        OutputDeviceCatalog catalog,
        OutputDeviceRef device,
        OutputMode mode,
        SourceFormat source,
        int latencyMilliseconds,
        Func<AudioFormat, IWaveProvider> createSource)
    {
        var negotiation = Negotiate(catalog, device, mode, source, latencyMilliseconds);
        if (negotiation.Status != NegotiationStatus.Succeeded)
        {
            return (null, negotiation);
        }

        MMDevice? mmDevice = null;
        WasapiPlayer? player = null;
        MediaFoundationResampler? resampler = null;

        try
        {
            mmDevice = catalog.GetEnumerator().GetDevice(device.Id);

            var builder = new WasapiPlayerBuilder()
                .WithDevice(mmDevice)
                .WithEventSync()
                .WithLatency(latencyMilliseconds)
                .WithCategory(AudioStreamCategory.Media);

            builder = device.Backend == OutputBackend.WasapiExclusive
                ? builder.WithExclusiveMode()
                : builder.WithSharedMode();

            if (mode == OutputMode.Transparent)
            {
                // Raw mode asks Windows to skip endpoint signal processing on the stream.
                builder = builder.WithRawMode();
            }

            player = builder.Build();

            var appFormat = negotiation.AppOutputFormat ?? negotiation.AcceptedFormat!;
            IWaveProvider provider = createSource(appFormat);

            var accepted = negotiation.AcceptedFormat!;
            if (!FormatsMatch(appFormat, accepted))
            {
                MediaFoundationApi.Startup();
                resampler = new MediaFoundationResampler(provider, ToWaveFormat(accepted)) { ResamplerQuality = 60 };
                provider = resampler;
            }

            // The player derives its stream format from the provider, so the provider's WaveFormat
            // is already the negotiated format: either the stream provider's own, or the
            // resampler's declared output.
            player.Init(provider);

            // Software volume stays at unity in transparent mode; the DAC controls level.
            if (mode == OutputMode.Transparent)
            {
                try
                {
                    player.Volume = 1.0f;
                }
                catch (Exception)
                {
                    // Some exclusive streams expose no session volume at all, which is fine.
                }
            }

            return (new WasapiAudioOutput(mmDevice, player, resampler, negotiation), negotiation);
        }
        catch (Exception ex)
        {
            resampler?.Dispose();
            player?.Dispose();
            mmDevice?.Dispose();
            var (kind, message) = OutputDeviceCatalog.Classify(ex);
            return (null, OutputNegotiation.Failure(source, device.Backend, mode, kind, message, negotiation.Attempts, device.Name, device.Id));
        }
    }

    /// <summary>
    /// Sets the session's software gain. Refused on a transparent stream: there is no software
    /// gain stage there by design, and the level belongs to the DAC.
    /// </summary>
    public bool SetSoftwareVolume(float volume)
    {
        if (Negotiation.Transparency == TransparencyStatus.ConfiguredNotHardwareVerified)
        {
            return false;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            try
            {
                _player.Volume = Math.Clamp(volume, 0f, 1f);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public void Play()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _player.Play();
            }
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!_disposed && _player.PlaybackState == PlaybackState.Playing)
            {
                _player.Pause();
            }
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_disposed && _player.PlaybackState != PlaybackState.Playing)
            {
                _player.Play();
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
                    _player.Stop();
                }
                catch (Exception)
                {
                    // A device pulled mid-stop still has to leave the app usable.
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

        _player.PlaybackStopped -= OnPlayerStopped;

        try
        {
            _player.Stop();
        }
        catch (Exception)
        {
            // Ignore: the device may already be gone.
        }

        _player.Dispose();
        _resampler?.Dispose();
        _device.Dispose();
    }

    internal static WaveFormat ToWaveFormat(AudioFormat format)
    {
        if (format.Encoding == SampleEncoding.IeeeFloat)
        {
            return format.ContainerBits == 32 && format.ValidBits == 32
                ? WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels)
                : new WaveFormatExtensible(
                    format.SampleRate,
                    format.ContainerBits,
                    format.Channels,
                    AudioSubFormats.IeeeFloat,
                    format.ValidBits,
                    OutputFormatPolicy.ChannelMask(format.Channels));
        }

        return format.ContainerBits == format.ValidBits && format.Channels <= 2
            ? new WaveFormat(format.SampleRate, format.ContainerBits, format.Channels)
            : new WaveFormatExtensible(
                format.SampleRate,
                format.ContainerBits,
                format.Channels,
                AudioSubFormats.Pcm,
                format.ValidBits,
                OutputFormatPolicy.ChannelMask(format.Channels));
    }

    private static bool FormatsMatch(AudioFormat a, AudioFormat b) =>
        a.SampleRate == b.SampleRate &&
        a.Channels == b.Channels &&
        a.ContainerBits == b.ContainerBits &&
        a.ValidBits == b.ValidBits &&
        a.Encoding == b.Encoding;

    private static int ContainerBits(SourceFormat source) => source.BitsPerSample switch
    {
        <= 8 => 8,
        <= 16 => 16,
        <= 24 => 24,
        _ => 32,
    };

    private void OnPlayerStopped(object? sender, StoppedEventArgs e) =>
        Stopped?.Invoke(this, new OutputStoppedEventArgs(e.Exception));
}
