using Noct.Core.Island;

namespace Noct.Tests;

public sealed class IslandStateControllerTests
{
    /// <summary>Captures scheduled callbacks so tests decide when "time" passes.</summary>
    private sealed class ManualScheduler
    {
        public List<(Action Action, TimeSpan Delay)> Pending { get; } = [];

        public void Schedule(Action action, TimeSpan delay) => Pending.Add((action, delay));

        /// <summary>Fires the most recently scheduled callback.</summary>
        public void FireLast()
        {
            var last = Pending[^1];
            Pending.RemoveAt(Pending.Count - 1);
            last.Action();
        }

        public void FireAll()
        {
            var all = Pending.ToArray();
            Pending.Clear();
            foreach (var item in all)
            {
                item.Action();
            }
        }
    }

    private static (IslandStateController Controller, ManualScheduler Scheduler, List<IslandStateChange> Changes) Create()
    {
        var scheduler = new ManualScheduler();
        var controller = new IslandStateController(schedule: scheduler.Schedule);
        var changes = new List<IslandStateChange>();
        controller.StateChanged += (_, change) => changes.Add(change);
        return (controller, scheduler, changes);
    }

    [Fact]
    public void Starts_with_main_window_visible()
    {
        var (controller, _, _) = Create();
        Assert.Equal(IslandState.MainVisible, controller.State);
    }

    [Fact]
    public void Minimize_shows_compact_island_and_arms_the_three_second_idle_timer()
    {
        var (controller, scheduler, _) = Create();

        controller.Handle(IslandEvent.Minimize);

        Assert.Equal(IslandState.Compact, controller.State);
        Assert.Single(scheduler.Pending);
        Assert.Equal(TimeSpan.FromSeconds(3), scheduler.Pending[0].Delay);
    }

    [Fact]
    public void Compact_island_hides_after_idle()
    {
        var (controller, scheduler, _) = Create();
        controller.Handle(IslandEvent.Minimize);

        scheduler.FireLast();

        Assert.Equal(IslandState.Hidden, controller.State);
    }

    [Fact]
    public void Trigger_dwell_reveals_expanded_island_from_hidden()
    {
        var (controller, scheduler, _) = Create();
        controller.Handle(IslandEvent.Minimize);
        scheduler.FireLast();

        controller.Handle(IslandEvent.TriggerDwell);

        Assert.Equal(IslandState.Expanded, controller.State);
    }

    [Fact]
    public void Pointer_exit_waits_for_grace_then_collapses_and_rearms_hide_timer()
    {
        var (controller, scheduler, _) = Create();
        controller.Handle(IslandEvent.Minimize);
        controller.Handle(IslandEvent.PointerEnter);
        Assert.Equal(IslandState.Expanded, controller.State);

        controller.Handle(IslandEvent.PointerExit);

        // Still expanded until the 500 ms grace elapses.
        Assert.Equal(IslandState.Expanded, controller.State);
        Assert.Equal(TimeSpan.FromMilliseconds(500), scheduler.Pending[^1].Delay);

        scheduler.FireLast();
        Assert.Equal(IslandState.Compact, controller.State);

        // Collapsing starts the 2 second hide timer.
        Assert.Equal(TimeSpan.FromSeconds(2), scheduler.Pending[^1].Delay);
        scheduler.FireLast();
        Assert.Equal(IslandState.Hidden, controller.State);
    }

    [Fact]
    public void Pointer_re_entry_during_grace_cancels_the_collapse()
    {
        var (controller, scheduler, _) = Create();
        controller.Handle(IslandEvent.Minimize);
        controller.Handle(IslandEvent.PointerEnter);
        controller.Handle(IslandEvent.PointerExit);

        controller.Handle(IslandEvent.PointerEnter);
        scheduler.FireAll();

        Assert.Equal(IslandState.Expanded, controller.State);
    }

    [Fact]
    public void Open_app_restores_main_and_defeats_a_pending_hide()
    {
        var (controller, scheduler, _) = Create();
        controller.Handle(IslandEvent.Minimize);

        controller.Handle(IslandEvent.OpenApp);
        scheduler.FireAll();

        Assert.Equal(IslandState.MainVisible, controller.State);
    }

    [Fact]
    public void Stale_timer_from_an_earlier_minimize_cannot_hide_a_newer_island()
    {
        var (controller, scheduler, _) = Create();

        controller.Handle(IslandEvent.Minimize);
        controller.Handle(IslandEvent.OpenApp);
        controller.Handle(IslandEvent.Minimize);

        // Two idle timers exist; only the newest may act. Fire the older one first.
        var older = scheduler.Pending[0];
        older.Action();

        Assert.Equal(IslandState.Compact, controller.State);

        scheduler.Pending[1].Action();
        Assert.Equal(IslandState.Hidden, controller.State);
    }

    [Fact]
    public void Rapid_minimize_and_restore_ends_in_one_valid_state()
    {
        var (controller, scheduler, _) = Create();

        for (var i = 0; i < 50; i++)
        {
            controller.Handle(IslandEvent.Minimize);
            controller.Handle(IslandEvent.PointerEnter);
            controller.Handle(IslandEvent.OpenApp);
        }

        scheduler.FireAll();

        Assert.Equal(IslandState.MainVisible, controller.State);
    }

    [Fact]
    public void Each_transition_carries_a_strictly_increasing_version()
    {
        var (controller, _, changes) = Create();

        controller.Handle(IslandEvent.Minimize);
        controller.Handle(IslandEvent.PointerEnter);
        controller.Handle(IslandEvent.OpenApp);

        Assert.Equal(3, changes.Count);
        Assert.True(changes[0].Version < changes[1].Version);
        Assert.True(changes[1].Version < changes[2].Version);
        Assert.Equal(controller.Version, changes[^1].Version);
    }

    [Fact]
    public void Seeking_suspends_auto_hide_until_released()
    {
        var (controller, scheduler, _) = Create();
        controller.Handle(IslandEvent.Minimize);
        controller.Handle(IslandEvent.PointerEnter);

        var hold = controller.SuspendAutoHide();
        controller.Handle(IslandEvent.PointerExit);
        scheduler.FireAll();

        Assert.Equal(IslandState.Expanded, controller.State);
        Assert.True(controller.AutoHideSuspended);

        hold.Dispose();
        Assert.False(controller.AutoHideSuspended);

        // Releasing starts the normal grace-then-collapse path.
        scheduler.FireLast();
        Assert.Equal(IslandState.Compact, controller.State);
    }

    [Fact]
    public void Releasing_a_hold_twice_does_not_underflow_the_suspend_count()
    {
        var (controller, _, _) = Create();
        controller.Handle(IslandEvent.Minimize);

        var hold = controller.SuspendAutoHide();
        hold.Dispose();
        hold.Dispose();

        Assert.Equal(0, controller.SuspendCount);
    }

    [Fact]
    public void Idle_hide_is_deferred_while_suspended_and_resumes_afterwards()
    {
        var (controller, scheduler, _) = Create();
        controller.Handle(IslandEvent.Minimize);

        using (controller.SuspendAutoHide())
        {
            scheduler.FireAll();
            Assert.Equal(IslandState.Compact, controller.State);
        }

        // After the hold ends the island must still be able to hide, not stay open forever.
        Assert.NotEmpty(scheduler.Pending);
        scheduler.FireLast();
        Assert.Equal(IslandState.Hidden, controller.State);
    }

    [Fact]
    public void Hovering_the_hidden_strip_reveals_only_after_the_dwell()
    {
        var (controller, scheduler, _) = Create();
        controller.Handle(IslandEvent.Minimize);
        scheduler.FireLast();
        Assert.Equal(IslandState.Hidden, controller.State);

        controller.Handle(IslandEvent.TriggerEnter);

        Assert.Equal(IslandState.Hidden, controller.State);
        Assert.Equal(TimeSpan.FromMilliseconds(100), scheduler.Pending[^1].Delay);

        scheduler.FireLast();
        Assert.Equal(IslandState.Expanded, controller.State);
    }

    [Fact]
    public void Leaving_the_strip_before_the_dwell_does_not_reveal()
    {
        var (controller, scheduler, _) = Create();
        controller.Handle(IslandEvent.Minimize);
        scheduler.FireLast();

        controller.Handle(IslandEvent.TriggerEnter);
        controller.Handle(IslandEvent.TriggerLeave);
        scheduler.FireAll();

        Assert.Equal(IslandState.Hidden, controller.State);
    }

    [Fact]
    public void Opening_the_app_during_the_dwell_wins_over_the_reveal()
    {
        var (controller, scheduler, _) = Create();
        controller.Handle(IslandEvent.Minimize);
        scheduler.FireLast();

        controller.Handle(IslandEvent.TriggerEnter);
        controller.Handle(IslandEvent.OpenApp);
        scheduler.FireAll();

        Assert.Equal(IslandState.MainVisible, controller.State);
    }

    [Fact]
    public void Exit_is_terminal()
    {
        var (controller, scheduler, _) = Create();
        controller.Handle(IslandEvent.Minimize);

        controller.Handle(IslandEvent.Exit);
        controller.Handle(IslandEvent.OpenApp);
        controller.Handle(IslandEvent.TriggerDwell);
        scheduler.FireAll();

        Assert.Equal(IslandState.Exiting, controller.State);
    }

    [Fact]
    public void Pointer_events_are_ignored_while_the_main_window_is_visible()
    {
        var (controller, _, changes) = Create();

        controller.Handle(IslandEvent.PointerEnter);
        controller.Handle(IslandEvent.TriggerDwell);
        controller.Handle(IslandEvent.PointerExit);

        Assert.Equal(IslandState.MainVisible, controller.State);
        Assert.Empty(changes);
    }
}
