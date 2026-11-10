using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Noct.App.Views;

/// <summary>
/// A 0..1 slider that reports a seek only when the user finishes, not on every pixel of a drag.
/// </summary>
/// <remarks>
/// While dragging, the owner is told the value so it can update time labels, but no seek is sent.
/// A click on the rail, a drag release or an arrow key commits one seek. This keeps the audio from
/// stuttering through dozens of intermediate seeks and gives the island a clear moment to hold
/// itself open (drag start) and release (drag end).
/// </remarks>
public sealed class SeekBar : Slider
{
    /// <summary>Fraction moved per arrow key press; roughly five seconds in a four minute track.</summary>
    private const double KeyStep = 0.02;

    public SeekBar()
    {
        Minimum = 0;
        Maximum = 1;
        Focusable = true;
        AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnDragStarted), handledEventsToo: true);
        AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted), handledEventsToo: true);
    }

    public bool IsScrubbing { get; private set; }

    public event EventHandler? ScrubStarted;

    /// <summary>Raised repeatedly during a drag with the value under the thumb.</summary>
    public event EventHandler<double>? Scrubbing;

    /// <summary>Raised once when the user finishes a drag or click.</summary>
    public event EventHandler<double>? SeekCommitted;

    /// <summary>Raised for an arrow-key or Home/End seek.</summary>
    public event EventHandler<double>? KeySeek;

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        if (IsScrubbing)
        {
            Scrubbing?.Invoke(this, newValue);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        double? target = e.Key switch
        {
            Key.Left or Key.Down => Math.Max(0, Value - KeyStep),
            Key.Right or Key.Up => Math.Min(1, Value + KeyStep),
            Key.Home => 0,
            Key.End => 1,
            _ => null,
        };

        if (target is { } fraction && IsEnabled)
        {
            e.Handled = true;
            KeySeek?.Invoke(this, fraction);
            return;
        }

        base.OnKeyDown(e);
    }

    // ----- clicking or dragging on the rail (anywhere except the thumb) -----
    //
    // Slider's own "move to point" jumps the value on a click but raises no drag events, so nothing would ever
    // commit a seek. The rail is handled here instead as a press-drag-release: it scrubs while the button is held
    // and commits one seek on release, exactly like dragging the thumb.

    private bool _railDrag;

    // Slider keeps its track private, and the name Track here would mean the Track type. The template names the
    // track PART_Track (see the RailSlider style), so it is picked up from there.
    private Track? _track;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _track = GetTemplateChild("PART_Track") as Track;
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (IsEnabled && _track is { Thumb: { } thumb } track && !thumb.IsMouseOver)
        {
            _railDrag = true;
            IsScrubbing = true;
            ScrubStarted?.Invoke(this, EventArgs.Empty);
            Focus();
            CaptureMouse();
            SetValueFrom(e.GetPosition(track));
            e.Handled = true;
            return;
        }

        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (_railDrag && _track is { } track)
        {
            SetValueFrom(e.GetPosition(track));
        }

        base.OnPreviewMouseMove(e);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_railDrag)
        {
            _railDrag = false;
            IsScrubbing = false;
            ReleaseMouseCapture();
            SeekCommitted?.Invoke(this, Value);
            e.Handled = true;
            return;
        }

        base.OnPreviewMouseLeftButtonUp(e);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        // Capture taken away mid-drag (another window, Alt+Tab): end the scrub without seeking.
        if (_railDrag)
        {
            _railDrag = false;
            IsScrubbing = false;
        }

        base.OnLostMouseCapture(e);
    }

    private void SetValueFrom(Point point)
    {
        if (_track is not { ActualWidth: > 0 } track)
        {
            return;
        }

        // Computed from the pointer alone. Track.ValueFromPoint is relative to where the thumb currently is, and
        // the thumb only moves on the next layout pass, so calling it again before then (a mouse move right after
        // the press) would apply the same offset twice.
        var travel = track.ActualWidth - (track.Thumb?.ActualWidth ?? 0);
        if (travel <= 0)
        {
            return;
        }

        var fraction = Math.Clamp((point.X - (track.Thumb?.ActualWidth ?? 0) / 2) / travel, 0, 1);

        // Not Value = ...: that would replace the one-way binding to the playback position for good.
        SetCurrentValue(ValueProperty, Minimum + fraction * (Maximum - Minimum));
    }
    private void OnDragStarted(object sender, DragStartedEventArgs e)
    {
        IsScrubbing = true;
        ScrubStarted?.Invoke(this, EventArgs.Empty);
    }

    private void OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!IsScrubbing)
        {
            return;
        }

        // A click on the rail also ends here, so the value under the pointer is always the target.
        IsScrubbing = false;
        SeekCommitted?.Invoke(this, Value);
    }
}
