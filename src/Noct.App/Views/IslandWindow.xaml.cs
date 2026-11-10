using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Noct.App.Platform;
using Noct.App.Themes;
using Noct.App.ViewModels;
using Noct.Core.Island;

namespace Noct.App.Views;

/// <summary>
/// The top-centre island. It owns drawing and animation only; <see cref="IslandStateController"/>
/// decides which state it should be in and when.
/// </summary>
/// <remarks>
/// Shape: one <see cref="PathGeometry"/> built from <see cref="IslandOutline"/> (a concave fillet
/// into the screen edge, straight sides, rounded bottom corners). The same object fills the body and
/// clips the content, so no layer can show outside the outline. The window is per-pixel transparent:
/// no DWM backdrop, window region, native frame or shadow, which is what allows an anti-aliased edge.
/// Cost: the island does not use the system blur (see docs/BUILD_NOTES.md).
/// Motion: animation changes the geometry and a lift transform only; the native window is sized once
/// per transition. Focus: WS_EX_NOACTIVATE and MA_NOACTIVATE, so hovering or clicking never takes focus.
/// </remarks>
public partial class IslandWindow : Window
{
    private static readonly TimeSpan MotionDuration = TimeSpan.FromMilliseconds(220);

    private readonly SystemPreferences _preferences;
    private readonly PlaybackViewModel _playback;
    private readonly PathGeometry _geometry = new();
    private readonly PathFigure _figure = new() { IsClosed = true, IsFilled = true };
    private readonly PathSegment[] _segments = new PathSegment[IslandOutline.SegmentCount];
    private readonly OutlineSegment[] _outline = new OutlineSegment[IslandOutline.SegmentCount];

    private IntPtr _handle;
    private MonitorSnapshot _monitor;
    private IslandState _state = IslandState.MainVisible;
    private PixelRect _box;
    private bool _hasBox;
    private IslandBody _body;
    private bool _hasBody;
    private double _centerX;

    private Stopwatch? _clock;
    private IslandBody _animFrom;
    private IslandBody _animTo;
    private Action? _animCompleted;
    private long _animationId;
    private long _runningAnimationId;

    public IslandWindow(PlaybackViewModel playback, SystemPreferences preferences)
    {
        _playback = playback;
        _preferences = preferences;
        DataContext = playback;
        InitializeComponent();

        BuildFigure();
        Body.Data = _geometry;
        ContentLayer.Clip = _geometry;
        ApplyTint();

        // A layered window moved into place before its first frame is drawn can show an opaque
        // rectangle. It stays transparent until WPF has rendered once, then becomes visible.
        Opacity = 0;
        ContentRendered += OnFirstRender;

        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        playback.PropertyChanged += OnPlaybackChanged;
        preferences.Changed += OnPreferencesChanged;
        UpdatePlayGlyph();

        Seek.ScrubStarted += (_, _) =>
        {
            _playback.BeginScrub();
            ScrubStarted?.Invoke(this, EventArgs.Empty);
        };
        Seek.Scrubbing += (_, fraction) => _playback.Scrub(fraction);
        Seek.SeekCommitted += (_, fraction) =>
        {
            _playback.EndScrub(fraction);
            ScrubEnded?.Invoke(this, EventArgs.Empty);
        };
        Seek.KeySeek += (_, fraction) => _playback.SeekToFraction(fraction);
    }

    public event EventHandler? PointerEntered;

    public event EventHandler? PointerLeft;

    public event EventHandler? OpenAppRequested;

    public event EventHandler? ScrubStarted;

    public event EventHandler? ScrubEnded;

    public IslandState VisualState => _state;

    public PixelRect CurrentBox => _box;

    public IslandBody CurrentBody => _body;

    public IntPtr Handle => _handle;

    // ----- Setup -----

    /// <summary>Creates the nine outline segments once; frames only change their points.</summary>
    private void BuildFigure()
    {
        for (var i = 0; i < _segments.Length; i++)
        {
            _segments[i] = i % 2 == 0
                ? new LineSegment()
                : new ArcSegment { IsLargeArc = false, RotationAngle = 0 };
            _figure.Segments.Add(_segments[i]);
        }

        _geometry.Figures.Add(_figure);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;

        var style = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GWL_EXSTYLE).ToInt64();
        style |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        style &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GWL_EXSTYLE, new IntPtr(style));

        HwndSource.FromHwnd(_handle)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }

        return IntPtr.Zero;
    }

    private void OnFirstRender(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstRender;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => Opacity = 1);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // The island is created per minimize and destroyed on restore, so every subscription that
        // outlives the window is removed here; otherwise each cycle would leave a window alive.
        CancelAnimation();
        _playback.PropertyChanged -= OnPlaybackChanged;
        _preferences.Changed -= OnPreferencesChanged;
        SourceInitialized -= OnSourceInitialized;
        Closed -= OnClosed;
        HwndSource.FromHwnd(_handle)?.RemoveHook(WndProc);
        DataContext = null;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        // Windows resizes the window to its own suggestion when the DPI changes; put it back on the
        // box computed from the monitor's real bounds.
        Dispatcher.BeginInvoke(() =>
        {
            if (_hasBox && _state != IslandState.MainVisible)
            {
                ApplyState(_state, MonitorService.ForHandle(_monitor.Handle), animate: false);
            }
        });
    }

    // ----- State application -----

    /// <summary>
    /// Moves the island to <paramref name="state"/> on <paramref name="monitor"/>. A newer call
    /// cancels any animation still running, so rapid changes always end in the latest state.
    /// </summary>
    public void ApplyState(IslandState state, MonitorSnapshot monitor, bool animate)
    {
        if (state is IslandState.MainVisible or IslandState.Exiting)
        {
            HideIsland();
            return;
        }

        CancelAnimation();
        var previous = _state;
        _monitor = monitor;
        _state = state;
        animate &= !_preferences.ReducedMotion;

        var compact = IslandGeometry.BodyFor(IslandState.Compact);
        var expanded = IslandGeometry.BodyFor(IslandState.Expanded);
        var target = IslandGeometry.BodyFor(state);

        if (state == IslandState.Hidden)
        {
            // Slide the content above the edge, then shrink the window to the thin hover strip.
            var current = _hasBody ? _body : compact;
            var strip = IslandGeometry.BodyFor(IslandState.Hidden);
            MoveTo(current, IslandGeometry.Raised(current), animate, () =>
            {
                ShowPanel(IslandState.Hidden, fade: false);
                SetBox(IslandGeometry.RestBox(monitor.Bounds, monitor.Scale, strip));
                SetBody(strip);
            });
            return;
        }

        // One window box holds both sizes, so hovering never resizes the native window.
        SetBox(IslandGeometry.WindowBox(monitor.Bounds, monitor.Scale, compact, expanded));
        EnsureShown();

        var wasVisibleSize = previous is IslandState.Compact or IslandState.Expanded;
        ShowPanel(state, fade: animate && wasVisibleSize && previous != state);

        var arriving = !_hasBody || previous is IslandState.Hidden or IslandState.MainVisible;
        MoveTo(arriving ? IslandGeometry.Raised(target) : _body, target, animate, completed: null);
    }

    /// <summary>Re-reads monitor geometry after a display change and re-places the island at once.</summary>
    public void Reposition(MonitorSnapshot monitor)
    {
        if (_state is IslandState.MainVisible or IslandState.Exiting || !IsVisible)
        {
            _monitor = monitor;
            return;
        }

        ApplyState(_state, monitor, animate: false);
    }

    public void HideIsland()
    {
        CancelAnimation();
        _state = IslandState.MainVisible;
        _hasBody = false;
        _hasBox = false;
        if (IsVisible)
        {
            Hide();
        }
    }

    private void EnsureShown()
    {
        if (IsVisible)
        {
            return;
        }

        if (_handle == IntPtr.Zero)
        {
            _handle = new WindowInteropHelper(this).EnsureHandle();
        }

        // Park far off screen so the first frame can never flash at the wrong place.
        Left = -10000;
        Top = -10000;
        Show();
        _hasBox = false;
        SetBox(IslandGeometry.WindowBox(
            _monitor.Bounds,
            _monitor.Scale,
            IslandGeometry.BodyFor(IslandState.Compact),
            IslandGeometry.BodyFor(IslandState.Expanded)));
    }

    private void ShowPanel(IslandState state, bool fade)
    {
        var compact = state == IslandState.Compact;
        var expanded = state == IslandState.Expanded;
        CompactPanel.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        ExpandedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;

        UIElement? panel = compact ? CompactPanel : expanded ? ExpandedPanel : null;
        if (panel is null)
        {
            return;
        }

        panel.BeginAnimation(OpacityProperty, null);
        if (fade)
        {
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(180)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
        else
        {
            panel.Opacity = 1;
        }
    }

    // ----- Animation -----

    private void MoveTo(IslandBody from, IslandBody to, bool animate, Action? completed)
    {
        if (!animate || from == to)
        {
            SetBody(to);
            completed?.Invoke();
            return;
        }

        _animFrom = from;
        _animTo = to;
        _animCompleted = completed;
        _clock = Stopwatch.StartNew();
        _runningAnimationId = ++_animationId;

        SetBody(from);
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_clock is null)
        {
            CompositionTarget.Rendering -= OnFrame;
            return;
        }

        var t = _clock.Elapsed.TotalMilliseconds / MotionDuration.TotalMilliseconds;
        SetBody(IslandBody.Lerp(_animFrom, _animTo, IslandGeometry.EaseOut(t)));
        if (t < 1)
        {
            return;
        }

        var id = _runningAnimationId;
        var completed = _animCompleted;
        StopFrames();

        // A completion belongs to the animation that produced it; a newer one has replaced it.
        if (id == _animationId)
        {
            completed?.Invoke();
        }
    }

    private void CancelAnimation()
    {
        _animationId++;
        StopFrames();
    }

    private void StopFrames()
    {
        CompositionTarget.Rendering -= OnFrame;
        _clock = null;
        _animCompleted = null;
    }

    // ----- Drawing -----

    /// <summary>Moves the shared outline to <paramref name="body"/>, reusing every object.</summary>
    private void SetBody(IslandBody body)
    {
        _body = body;
        _hasBody = true;

        IslandOutline.Build(body, _centerX, _outline, out var startX, out var startY);
        _figure.StartPoint = new Point(startX, startY);

        for (var i = 0; i < _outline.Length; i++)
        {
            var s = _outline[i];
            if (_segments[i] is LineSegment line)
            {
                line.Point = new Point(s.X, s.Y);
            }
            else if (_segments[i] is ArcSegment arc)
            {
                arc.Point = new Point(s.X, s.Y);
                arc.Size = new Size(s.Radius, s.Radius);
                arc.SweepDirection = s.Clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
            }
        }

        Lift.Y = body.Lift;
    }

    private void SetBox(PixelRect box)
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        // The body is centred in DIPs on the box, so a changed box needs the centre re-derived.
        _centerX = box.Width / _monitor.Scale / 2;
        if (_hasBox && _box == box)
        {
            return;
        }

        _box = box;
        _hasBox = true;
        NativeMethods.SetWindowPos(
            _handle, IntPtr.Zero, box.X, box.Y, box.Width, box.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        if (_hasBody)
        {
            SetBody(_body);
        }
    }

    // ----- Events and bindings -----

    private void OnPreferencesChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ApplyTint);

    /// <summary>
    /// Strongly tinted near-black. With system transparency on it keeps a whisper of see-through;
    /// with transparency off, or in high contrast, it is fully opaque.
    /// </summary>
    private void ApplyTint() =>
        ThemeManager.SetIslandTint(Application.Current, translucent: _preferences.TransparencyEnabled && !_preferences.HighContrast);

    private void OnRootMouseEnter(object sender, System.Windows.Input.MouseEventArgs e) =>
        PointerEntered?.Invoke(this, EventArgs.Empty);

    private void OnRootMouseLeave(object sender, System.Windows.Input.MouseEventArgs e) =>
        PointerLeft?.Invoke(this, EventArgs.Empty);

    private void OnOpenAppClick(object sender, RoutedEventArgs e) =>
        OpenAppRequested?.Invoke(this, EventArgs.Empty);

    private void OnPlaybackChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackViewModel.IsPlaying))
        {
            UpdatePlayGlyph();
        }
    }

    private void UpdatePlayGlyph()
    {
        PlayGlyph.Data = (Geometry)FindResource(_playback.IsPlaying ? "IconPause" : "IconPlay");
        PlayGlyph.Filled = !_playback.IsPlaying;
        PlayGlyph.StrokeThickness = _playback.IsPlaying ? 2.25 : 1.5;
    }
}
