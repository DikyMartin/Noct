using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Noct.App.Views;

/// <summary>
/// Smooth mouse-wheel scrolling for a ScrollViewer (lists, grids and pages).
/// </summary>
/// <remarks>
/// A notched wheel (120 units per click) glides to its target in about 140 ms. Further clicks move the
/// same target and replace the animation, so there is never a queue, and nothing keeps moving once the
/// wheel stops. A precision touchpad sends many small deltas already; those are applied directly,
/// with no animation to lag behind the fingers. Dragging the scroll bar, the keyboard and the
/// view's own auto-scroll all cancel the glide and take over from where the view actually is.
/// </remarks>
public static class SmoothWheel
{
    /// <summary>Distance for one wheel click, scaled by the user's Windows "lines per click" setting.</summary>
    private const double PixelsPerLine = 16;

    private const int GlideMilliseconds = 140;

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SmoothWheel), new PropertyMetadata(false, OnEnabledChanged));

    private static readonly DependencyProperty PendingTargetProperty = DependencyProperty.RegisterAttached(
        "PendingTarget", typeof(double?), typeof(SmoothWheel), new PropertyMetadata(null));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer)
        {
            return;
        }

        viewer.PreviewMouseWheel -= OnWheel;
        viewer.PreviewKeyDown -= OnOtherInput;
        viewer.PreviewMouseLeftButtonDown -= OnOtherInput;
        viewer.RemoveHandler(ScrollBar.ScrollEvent, new ScrollEventHandler(OnScrollBar));

        if ((bool)e.NewValue)
        {
            viewer.PreviewMouseWheel += OnWheel;
            viewer.PreviewKeyDown += OnOtherInput;
            viewer.PreviewMouseLeftButtonDown += OnOtherInput;
            viewer.AddHandler(ScrollBar.ScrollEvent, new ScrollEventHandler(OnScrollBar));
        }
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var viewer = (ScrollViewer)sender;
        if (e.Handled || viewer.ScrollableHeight <= 0 || Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        // A scrollable viewer nested under the pointer gets its own wheel input.
        if (e.OriginalSource is DependencyObject source && NearestViewer(source) is { } inner && !ReferenceEquals(inner, viewer) && inner.ScrollableHeight > 0)
        {
            return;
        }

        var lines = Math.Max(1, SystemParameters.WheelScrollLines);
        var distance = -e.Delta / 120.0 * lines * PixelsPerLine;

        if (Math.Abs(e.Delta) < 120)
        {
            // Precision touchpad: many small deltas, applied as they come.
            ClearPending(viewer);
            SmoothScroll.Cancel(viewer);
            viewer.ScrollToVerticalOffset(Math.Clamp(viewer.VerticalOffset + distance, 0, viewer.ScrollableHeight));
        }
        else
        {
            var basis = SmoothScroll.IsAnimating(viewer) && viewer.GetValue(PendingTargetProperty) is double pending
                ? pending
                : viewer.VerticalOffset;
            var target = Math.Clamp(basis + distance, 0, viewer.ScrollableHeight);
            viewer.SetValue(PendingTargetProperty, target);
            SmoothScroll.To(viewer, target, animate: true, GlideMilliseconds, () => ClearPending(viewer));
        }

        e.Handled = true;
    }

    private static ScrollViewer? NearestViewer(DependencyObject start)
    {
        for (var node = start; node is not null; node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
            ? System.Windows.Media.VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer found)
            {
                return found;
            }
        }

        return null;
    }

    private static void OnOtherInput(object sender, RoutedEventArgs e)
    {
        var viewer = (ScrollViewer)sender;
        if (e is KeyEventArgs { Key: not (Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End) })
        {
            return;
        }

        ClearPending(viewer);
        SmoothScroll.Cancel(viewer);
    }

    private static void OnScrollBar(object sender, ScrollEventArgs e)
    {
        var viewer = (ScrollViewer)sender;
        ClearPending(viewer);
        SmoothScroll.Cancel(viewer);
    }

    private static void ClearPending(ScrollViewer viewer) => viewer.ClearValue(PendingTargetProperty);
}
