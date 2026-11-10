using Noct.Core.Audio;
using Noct.Core.Library;

namespace Noct.Core.Playback;

public enum PlaybackStatus
{
    Idle,
    Opening,
    Playing,
    Paused,
    Stopped,
    Failed,
}

public enum NoticeSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>A message for the user with, where possible, something they can actually do about it.</summary>
public sealed record PlaybackNotice(NoticeSeverity Severity, string Message, string? Action = null)
{
    public DateTimeOffset At { get; } = DateTimeOffset.Now;
}

/// <summary>
/// An immutable view of the engine for the UI to bind to. Both windows render from the same
/// snapshot, which is why minimising cannot change what is playing.
/// </summary>
public sealed record PlaybackSnapshot
{
    public static PlaybackSnapshot Empty { get; } = new();

    public PlaybackStatus Status { get; init; } = PlaybackStatus.Idle;

    public Track? Track { get; init; }

    public TimeSpan Position { get; init; }

    public TimeSpan Duration { get; init; }

    public int QueueIndex { get; init; } = -1;

    public int QueueCount { get; init; }

    /// <summary>Changes whenever queue contents change, so views know when to rebuild their list.</summary>
    public int QueueVersion { get; init; }

    public RepeatMode Repeat { get; init; } = RepeatMode.Off;

    /// <summary>The negotiation that produced the live stream, or the last failed attempt.</summary>
    public OutputNegotiation? Output { get; init; }

    /// <summary>Software volume in use. Always 1.0 while <see cref="VolumeIsSoftware"/> is false.</summary>
    public double Volume { get; init; } = 1.0;

    /// <summary>
    /// False in transparent mode: there is no software gain stage, so the level is set on the DAC.
    /// </summary>
    public bool VolumeIsSoftware { get; init; } = true;

    /// <summary>True when the next track is armed to join the current stream without a gap.</summary>
    public bool NextTrackJoinedGaplessly { get; init; }

    public PlaybackNotice? Notice { get; init; }

    public bool IsPlaying => Status == PlaybackStatus.Playing;

    public bool HasTrack => Track is not null;

    public double ProgressFraction => Duration > TimeSpan.Zero
        ? Math.Clamp(Position.TotalSeconds / Duration.TotalSeconds, 0, 1)
        : 0;

    public TimeSpan Remaining => Duration > Position ? Duration - Position : TimeSpan.Zero;
}
