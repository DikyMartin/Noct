namespace Noct.Core.Island;

public enum IslandState
{
    /// <summary>Main window visible; no island.</summary>
    MainVisible,

    /// <summary>Small island at the top centre of the monitor.</summary>
    Compact,

    /// <summary>Island content slid above the screen edge; only the hover trigger remains.</summary>
    Hidden,

    /// <summary>Full island with transport controls.</summary>
    Expanded,

    /// <summary>Shutting down; no further transitions.</summary>
    Exiting,
}

public enum IslandEvent
{
    Minimize,
    OpenApp,
    PointerEnter,
    PointerExit,
    TriggerDwell,
    Interaction,
    InteractionEnded,
    Exit,

    /// <summary>Pointer entered the hidden edge strip. Starts the dwell timer.</summary>
    TriggerEnter,

    /// <summary>Pointer left the hidden edge strip before the dwell elapsed.</summary>
    TriggerLeave,

    /// <summary>Raised by the controller's own idle timer; not sent by callers.</summary>
    InternalIdleHide,

    /// <summary>Raised after the pointer-exit grace period; not sent by callers.</summary>
    InternalCollapse,
}

/// <summary>Timing values from the design system, overridable so tests can run without real waits.</summary>
public sealed record IslandTiming
{
    public static IslandTiming Default { get; } = new();

    /// <summary>Compact island hides after this much idle time.</summary>
    public TimeSpan CompactIdleHide { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Pointer must rest on the edge trigger this long before the island reveals.</summary>
    public TimeSpan TriggerDwell { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Grace period after the pointer leaves the expanded island before it collapses.</summary>
    public TimeSpan ExitGrace { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Idle time after collapsing before the island hides again.</summary>
    public TimeSpan CollapsedIdleHide { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// The island's state machine and its timers, with no dependency on windows or rendering.
/// </summary>
/// <remarks>
/// <para>
/// Rendering is kept out on purpose: this class decides <i>what</i> state the island should be in
/// and the window layer decides how to animate into it. That split is what makes the timing rules
/// testable without a desktop, and it is why a late animation callback cannot hide a window the
/// user just reopened: every requested state carries a monotonically increasing
/// <see cref="Version"/>, and the renderer discards a completion whose version is stale.
/// </para>
/// <para>
/// Auto-hide is suspended while <see cref="SuspendCount"/> is non-zero, which the window layer
/// raises during a seek drag, an open menu and whenever keyboard focus is inside the island.
/// </para>
/// </remarks>
public sealed class IslandStateController : IDisposable
{
    private readonly object _gate = new();
    private readonly IslandTiming _timing;
    private readonly ITimeProvider _clock;
    private readonly Action<Action, TimeSpan> _schedule;

    private IslandState _state = IslandState.MainVisible;
    private long _version;
    private int _suspendCount;
    private long _pendingTimerToken;
    private bool _disposed;

    public IslandStateController(IslandTiming? timing = null, ITimeProvider? clock = null, Action<Action, TimeSpan>? schedule = null)
    {
        _timing = timing ?? IslandTiming.Default;
        _clock = clock ?? SystemTimeProvider.Instance;
        _schedule = schedule ?? ScheduleWithTimer;
    }

    /// <summary>Raised whenever the desired state changes. Carries the version to render.</summary>
    public event EventHandler<IslandStateChange>? StateChanged;

    public IslandState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    public int SuspendCount
    {
        get
        {
            lock (_gate)
            {
                return _suspendCount;
            }
        }
    }

    public bool AutoHideSuspended => SuspendCount > 0;

    /// <summary>
    /// Holds the island open. Dispose the returned token to release. Used for seek drags, open
    /// menus and keyboard focus inside the island.
    /// </summary>
    public IDisposable SuspendAutoHide()
    {
        lock (_gate)
        {
            _suspendCount++;
            CancelTimerLocked();
        }

        return new SuspendToken(this);
    }

    public void Handle(IslandEvent islandEvent)
    {
        IslandStateChange? change = null;

        lock (_gate)
        {
            if (_disposed || _state == IslandState.Exiting)
            {
                return;
            }

            switch (islandEvent)
            {
                case IslandEvent.Exit:
                    change = TransitionLocked(IslandState.Exiting);
                    CancelTimerLocked();
                    break;

                case IslandEvent.Minimize:
                    change = TransitionLocked(IslandState.Compact);
                    ScheduleLocked(_timing.CompactIdleHide, IslandState.Compact, () => Handle(IslandEvent.InternalIdleHide));
                    break;

                case IslandEvent.OpenApp:
                    CancelTimerLocked();
                    change = TransitionLocked(IslandState.MainVisible);
                    break;

                case IslandEvent.PointerEnter:
                    if (_state is IslandState.Compact or IslandState.Expanded)
                    {
                        CancelTimerLocked();
                        change = TransitionLocked(IslandState.Expanded);
                    }

                    break;

                case IslandEvent.TriggerEnter:
                    if (_state == IslandState.Hidden)
                    {
                        ScheduleLocked(_timing.TriggerDwell, IslandState.Hidden, () => Handle(IslandEvent.TriggerDwell));
                    }

                    break;

                case IslandEvent.TriggerLeave:
                    if (_state == IslandState.Hidden)
                    {
                        CancelTimerLocked();
                    }

                    break;

                case IslandEvent.TriggerDwell:
                    if (_state is IslandState.Hidden or IslandState.Compact)
                    {
                        CancelTimerLocked();
                        change = TransitionLocked(IslandState.Expanded);
                    }

                    break;

                case IslandEvent.PointerExit:
                    if (_state == IslandState.Expanded && _suspendCount == 0)
                    {
                        ScheduleLocked(_timing.ExitGrace, IslandState.Expanded, CollapseAfterGrace);
                    }

                    break;

                case IslandEvent.Interaction:
                    CancelTimerLocked();
                    if (_state is IslandState.Hidden or IslandState.Compact)
                    {
                        change = TransitionLocked(IslandState.Expanded);
                    }

                    break;

                case IslandEvent.InteractionEnded:
                    if (_state == IslandState.Expanded && _suspendCount == 0)
                    {
                        ScheduleLocked(_timing.ExitGrace, IslandState.Expanded, CollapseAfterGrace);
                    }

                    break;

                case IslandEvent.InternalIdleHide:
                    if (_state is IslandState.Compact && _suspendCount == 0)
                    {
                        change = TransitionLocked(IslandState.Hidden);
                    }

                    break;

                case IslandEvent.InternalCollapse:
                    if (_state == IslandState.Expanded && _suspendCount == 0)
                    {
                        change = TransitionLocked(IslandState.Compact);
                        ScheduleLocked(_timing.CollapsedIdleHide, IslandState.Compact, () => Handle(IslandEvent.InternalIdleHide));
                    }

                    break;
            }
        }

        if (change is not null)
        {
            StateChanged?.Invoke(this, change);
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
            CancelTimerLocked();
        }
    }

    private void CollapseAfterGrace() => Handle(IslandEvent.InternalCollapse);

    private IslandStateChange? TransitionLocked(IslandState next)
    {
        if (_state == next)
        {
            return null;
        }

        var previous = _state;
        _state = next;
        _version++;
        return new IslandStateChange(previous, next, _version);
    }

    /// <summary>
    /// Arms a timer that only fires if the state has not moved on. The captured state and token
    /// together defeat a stale callback from a cancelled transition.
    /// </summary>
    private void ScheduleLocked(TimeSpan delay, IslandState expectedState, Action action)
    {
        var token = ++_pendingTimerToken;
        var scheduledAt = _clock.UtcNow;
        _ = scheduledAt;

        _schedule(
            () =>
            {
                lock (_gate)
                {
                    if (_disposed || token != _pendingTimerToken || _state != expectedState || _suspendCount > 0)
                    {
                        return;
                    }
                }

                action();
            },
            delay);
    }

    private void CancelTimerLocked() => _pendingTimerToken++;

    private static void ScheduleWithTimer(Action action, TimeSpan delay)
    {
        Timer? timer = null;
        timer = new Timer(
            _ =>
            {
                try
                {
                    action();
                }
                finally
                {
                    timer?.Dispose();
                }
            },
            null,
            delay,
            Timeout.InfiniteTimeSpan);
    }

    private void Release()
    {
        var shouldCollapse = false;
        lock (_gate)
        {
            if (_suspendCount > 0)
            {
                _suspendCount--;
            }

            if (_suspendCount == 0)
            {
                if (_state == IslandState.Expanded)
                {
                    shouldCollapse = true;
                }
                else if (_state == IslandState.Compact)
                {
                    // An idle-hide that fired during the hold was discarded; re-arm it so the
                    // island cannot stay open forever after the hold ends.
                    ScheduleLocked(_timing.CollapsedIdleHide, IslandState.Compact, () => Handle(IslandEvent.InternalIdleHide));
                }
            }
        }

        if (shouldCollapse)
        {
            Handle(IslandEvent.InteractionEnded);
        }
    }

    private sealed class SuspendToken(IslandStateController owner) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            owner.Release();
        }
    }
}

public sealed record IslandStateChange(IslandState From, IslandState To, long Version);

public interface ITimeProvider
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemTimeProvider : ITimeProvider
{
    public static SystemTimeProvider Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
