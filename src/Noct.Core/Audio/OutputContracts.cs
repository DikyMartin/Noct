namespace Noct.Core.Audio;

/// <summary>The device path used to reach the hardware.</summary>
public enum OutputBackend
{
    /// <summary>WASAPI shared mode. Goes through the Windows mixer; other apps keep working.</summary>
    WasapiShared,

    /// <summary>WASAPI exclusive mode. The app owns the endpoint and the mixer is bypassed.</summary>
    WasapiExclusive,

    /// <summary>ASIO through an installed third-party driver.</summary>
    Asio,
}

/// <summary>The audio policy the user chose, independent of the backend.</summary>
public enum OutputMode
{
    /// <summary>Software volume allowed; the system may convert.</summary>
    Normal,

    /// <summary>Unity software gain, no sample-changing stage, source rate required.</summary>
    Transparent,
}

/// <summary>
/// How much is actually known about transparency. These levels are never promoted into each other:
/// a configured path is not a measured path, and a measured software path is not hardware proof.
/// </summary>
public enum TransparencyStatus
{
    /// <summary>Normal mode: the question does not apply.</summary>
    NotApplicable,

    /// <summary>The user asked for transparent output but this path cannot provide it.</summary>
    Unavailable,

    /// <summary>
    /// Configured: unity gain, no DSP stage, source rate accepted, lossless packing only.
    /// Not hardware-verified bit-perfect output.
    /// </summary>
    ConfiguredNotHardwareVerified,
}

public enum NegotiationStatus
{
    Succeeded,
    Failed,
}

/// <summary>Why a negotiation failed, so the UI can offer the right recovery action.</summary>
public enum OutputFailureKind
{
    None,
    DeviceUnavailable,
    DeviceInUse,
    ExclusiveModeNotAllowed,
    FormatNotSupported,
    TransparentPathUnavailable,
    DriverError,
}

/// <summary>An output endpoint the user can select.</summary>
public sealed record OutputDeviceRef(
    string Id,
    string Name,
    OutputBackend Backend,
    bool IsDefault,
    bool IsAvailable = true)
{
    public string Key => $"{Backend}|{Id}";
}

/// <summary>
/// The full result of asking a device to accept the source. Every field the quality panel shows
/// comes from here, so the UI cannot invent a value that the device never confirmed.
/// </summary>
public sealed record OutputNegotiation
{
    public required NegotiationStatus Status { get; init; }

    public required SourceFormat Source { get; init; }

    public required OutputBackend RequestedBackend { get; init; }

    public required OutputMode RequestedMode { get; init; }

    public OutputBackend? ActualBackend { get; init; }

    public OutputMode? ActualMode { get; init; }

    /// <summary>The format the device actually accepted. Null when negotiation failed.</summary>
    public AudioFormat? AcceptedFormat { get; init; }

    /// <summary>
    /// The format Noct packs decoded samples into. Equal to <see cref="AcceptedFormat"/> unless a
    /// conversion stage sits in between, which only happens in shared mode.
    /// </summary>
    public AudioFormat? AppOutputFormat { get; init; }

    public string? DeviceId { get; init; }

    public string? DeviceName { get; init; }

    /// <summary>Sample-changing stages present in the path. Empty means none were reported.</summary>
    public IReadOnlyList<string> Conversions { get; init; } = [];

    /// <summary>Lossless container repacking applied, if any. Not a conversion of sample values.</summary>
    public string? Packing { get; init; }

    public TransparencyStatus Transparency { get; init; } = TransparencyStatus.NotApplicable;

    public OutputFailureKind FailureKind { get; init; } = OutputFailureKind.None;

    public string? FailureMessage { get; init; }

    /// <summary>What the negotiation tried, kept for build notes and the settings diagnostics view.</summary>
    public IReadOnlyList<string> Attempts { get; init; } = [];

    public int LatencyMilliseconds { get; init; }

    public static OutputNegotiation Failure(
        SourceFormat source,
        OutputBackend backend,
        OutputMode mode,
        OutputFailureKind kind,
        string message,
        IReadOnlyList<string>? attempts = null,
        string? deviceName = null,
        string? deviceId = null) => new()
        {
            Status = NegotiationStatus.Failed,
            Source = source,
            RequestedBackend = backend,
            RequestedMode = mode,
            FailureKind = kind,
            FailureMessage = message,
            Attempts = attempts ?? [],
            DeviceName = deviceName,
            DeviceId = deviceId,
            Transparency = mode == OutputMode.Transparent ? TransparencyStatus.Unavailable : TransparencyStatus.NotApplicable,
        };
}

public enum OutputState
{
    Idle,
    Playing,
    Paused,
    Stopped,
}

public sealed class OutputStoppedEventArgs(Exception? exception) : EventArgs
{
    public Exception? Exception { get; } = exception;
}

/// <summary>
/// An opened output endpoint. Created already bound to a negotiated format and a sample source;
/// the engine never changes format on a live instance, it disposes and opens a new one.
/// </summary>
public interface IAudioOutput : IDisposable
{
    OutputNegotiation Negotiation { get; }

    OutputState State { get; }

    /// <summary>Frames the device reports as played since the stream started, or null if unknown.</summary>
    long? PlayedFrames { get; }

    event EventHandler<OutputStoppedEventArgs>? Stopped;

    void Play();

    void Pause();

    void Resume();

    void Stop();
}
