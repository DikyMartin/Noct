using System.Windows.Threading;
using Microsoft.Win32;
using Noct.App.ViewModels;
using Noct.App.Views;
using Noct.Core.Island;
using Noct.Core.Settings;

namespace Noct.App.Platform;

/// <summary>
/// Connects the main window, the island, the tray icon and the island state machine.
/// </summary>
/// <remarks>
/// All three surfaces read one <see cref="PlaybackViewModel"/>, so nothing here ever touches the
/// playback engine: hiding the main window, sliding the island or opening the tray menu cannot
/// restart, pause or duplicate audio. The state machine fires on timer threads; every change is
/// re-read on the UI thread and the island is moved to the <i>latest</i> state, so a stale callback
/// from a cancelled transition cannot hide a window the user just reopened.
/// </remarks>
internal sealed class ShellCoordinator : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly MainWindow _main;
    private readonly Func<IslandWindow> _createIsland;
    private readonly TrayIcon _tray;
    private readonly TrayMenu _trayMenu;
    private readonly IslandStateController _controller;
    private readonly PlaybackViewModel _playback;
    private readonly SettingsStore _settings;

    private IslandWindow? _island;
    private MonitorSnapshot _monitor;
    private long _appliedVersion = -1;
    private IDisposable? _scrubHold;
    private bool _exiting;
    private bool _disposed;

    public ShellCoordinator(
        Dispatcher dispatcher,
        MainWindow main,
        Func<IslandWindow> createIsland,
        TrayIcon tray,
        TrayMenu trayMenu,
        IslandStateController controller,
        PlaybackViewModel playback,
        SettingsStore settings)
    {
        _dispatcher = dispatcher;
        _main = main;
        _createIsland = createIsland;
        _tray = tray;
        _trayMenu = trayMenu;
        _controller = controller;
        _playback = playback;
        _settings = settings;
    }

    /// <summary>Raised on the UI thread when the user asked to leave (close button, tray Exit).</summary>
    public event EventHandler? ExitRequested;

    public IslandStateController Controller => _controller;

    public IslandWindow? Island => _island;

    public void Start()
    {
        _controller.StateChanged += OnControllerStateChanged;
        _playback.PropertyChanged += OnPlaybackChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _main.IsVisibleChanged += (_, _) => UpdatePositionConsumers();
        _main.InterceptMinimize = TryMinimizeToIsland;
        _main.CloseRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _tray.OpenRequested += (_, _) => _dispatcher.BeginInvoke(OpenMain);
        _tray.MenuRequested += (_, e) => _dispatcher.BeginInvoke(() => _trayMenu.Show(e.X, e.Y));
        _tray.Show();
        UpdateTrayTooltip();

        _main.Show();
    }

    /// <summary>Brings the one main window back and hides the island. Safe to call from any state.</summary>
    public void OpenMain()
    {
        if (_exiting)
        {
            return;
        }

        _controller.Handle(IslandEvent.OpenApp);
        _main.RestoreFromIsland();
    }

    /// <summary>Called for a minimize request. Returns true when the island took it over.</summary>
    private bool TryMinimizeToIsland()
    {
        if (_exiting || !_settings.Current.IslandEnabled)
        {
            return false;
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (_exiting || !_main.IsVisible)
            {
                return;
            }

            // Capture the monitor while the window is still on screen, then hide it. Hiding removes
            // its taskbar button; a pin the user made in the taskbar is a shortcut and is untouched.
            _monitor = MonitorService.ForWindow(_main);
            _main.Hide();
            _controller.Handle(IslandEvent.Minimize);
        });

        return true;
    }

    public void BeginExit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _controller.Handle(IslandEvent.Exit);
        _scrubHold?.Dispose();

        if (_island is not null)
        {
            _island.HideIsland();
            _island.Close();
            _island = null;
        }

        _tray.Dispose();
        _trayMenu.Dispose();
        _main.CloseForExit();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _controller.StateChanged -= OnControllerStateChanged;
        _playback.PropertyChanged -= OnPlaybackChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _controller.Dispose();
    }

    // ----- State machine -> island -----

    private void OnControllerStateChanged(object? sender, IslandStateChange change) =>
        _dispatcher.BeginInvoke(ApplyLatestState);

    private void ApplyLatestState()
    {
        if (_exiting)
        {
            return;
        }

        var state = _controller.State;
        var version = _controller.Version;
        if (version == _appliedVersion)
        {
            return;
        }

        _appliedVersion = version;
        UpdatePositionConsumers();

        if (state == IslandState.MainVisible)
        {
            // The island exists only while the main window is hidden. Closing it releases its
            // window, layered surface and subscriptions instead of keeping a hidden window around.
            ReleaseIsland();
            return;
        }

        if (!MonitorService.Exists(_monitor.Handle))
        {
            _monitor = MonitorService.Primary();
        }

        EnsureIsland().ApplyState(state, _monitor, animate: true);
    }

    /// <summary>Only surfaces that actually draw the elapsed time keep the position updates running.</summary>
    private void UpdatePositionConsumers()
    {
        var showsPosition = _main.IsVisible || (_island is not null && _controller.State == IslandState.Expanded);
        _playback.PositionUpdatesEnabled = showsPosition;
    }

    private void ReleaseIsland()
    {
        _scrubHold?.Dispose();
        _scrubHold = null;

        if (_island is null)
        {
            return;
        }

        _island.PointerEntered -= OnIslandPointerEntered;
        _island.PointerLeft -= OnIslandPointerLeft;
        _island.HideIsland();
        _island.Close();
        _island = null;
    }

    private IslandWindow EnsureIsland()
    {
        if (_island is not null)
        {
            return _island;
        }

        var island = _createIsland();
        island.PointerEntered += OnIslandPointerEntered;
        island.PointerLeft += OnIslandPointerLeft;
        island.OpenAppRequested += (_, _) => OpenMain();
        island.ScrubStarted += (_, _) => _scrubHold ??= _controller.SuspendAutoHide();
        island.ScrubEnded += (_, _) =>
        {
            _scrubHold?.Dispose();
            _scrubHold = null;
        };

        _island = island;
        return island;
    }

    private void OnIslandPointerEntered(object? sender, EventArgs e)
    {
        // Hovering the hidden strip must not pop an overlay over a full-screen app.
        if (_controller.State == IslandState.Hidden && FullScreenDetector.IsFullScreenAppActive())
        {
            return;
        }

        _controller.Handle(IslandEvent.PointerEnter);
        _controller.Handle(IslandEvent.TriggerEnter);
    }

    private void OnIslandPointerLeft(object? sender, EventArgs e)
    {
        _controller.Handle(IslandEvent.PointerExit);
        _controller.Handle(IslandEvent.TriggerLeave);
    }

    // ----- Monitor topology -----

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            // A monitor that disappeared falls back to the primary one; sizes and positions are
            // recomputed for the new resolution and DPI either way.
            _monitor = MonitorService.Exists(_monitor.Handle)
                ? MonitorService.ForHandle(_monitor.Handle)
                : MonitorService.Primary();

            _island?.Reposition(_monitor);
        });

    // ----- Tray tooltip -----

    private void OnPlaybackChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackViewModel.NowPlayingDescription))
        {
            UpdateTrayTooltip();
        }
    }

    private void UpdateTrayTooltip() => _tray.SetTooltip("Noct · " + _playback.NowPlayingDescription);
}
