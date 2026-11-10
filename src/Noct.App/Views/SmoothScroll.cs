using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Noct.App.Views;

/// <summary>
/// Animates a ScrollViewer to a vertical offset. ScrollViewer offers no animatable offset of its
/// own, so an attached property carries the animation and forwards each value to the viewer.
/// </summary>
/// <remarks>
/// One animation per viewer at most: starting another replaces the running one from where it is, so
/// animations never stack up or keep running after the last request.
/// </remarks>
public static class SmoothScroll
{
    public static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(SmoothScroll), new PropertyMetadata(0.0, OnOffsetChanged));

    private static readonly DependencyProperty GenerationProperty = DependencyProperty.RegisterAttached(
        "Generation", typeof(int), typeof(SmoothScroll), new PropertyMetadata(0));

    public static double GetOffset(DependencyObject d) => (double)d.GetValue(OffsetProperty);

    public static void SetOffset(DependencyObject d, double value) => d.SetValue(OffsetProperty, value);

    private static void OnOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer viewer)
        {
            viewer.ScrollToVerticalOffset((double)e.NewValue);
        }
    }

    /// <summary>True while an animation owns the viewer's offset.</summary>
    public static bool IsAnimating(ScrollViewer viewer) => viewer.HasAnimatedProperties;

    /// <summary>
    /// Moves to <paramref name="target"/>. With <paramref name="animate"/> false it jumps. The optional
    /// <paramref name="completed"/> runs once the viewer has settled on the target (not if it is replaced).
    /// </summary>
    public static void To(ScrollViewer viewer, double target, bool animate, int milliseconds = 320, Action? completed = null)
    {
        target = Math.Clamp(target, 0, Math.Max(0, viewer.ScrollableHeight));
        var from = viewer.VerticalOffset;

        if (!animate || Math.Abs(target - from) < 1)
        {
            Cancel(viewer);
            viewer.ScrollToVerticalOffset(target);
            completed?.Invoke();
            return;
        }

        var generation = unchecked((int)viewer.GetValue(GenerationProperty) + 1);
        viewer.SetValue(GenerationProperty, generation);

        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        animation.Completed += (_, _) =>
        {
            // Only the animation still in charge settles the viewer; a replaced or cancelled one does nothing.
            if ((int)viewer.GetValue(GenerationProperty) != generation)
            {
                return;
            }

            viewer.BeginAnimation(OffsetProperty, null);
            SetOffset(viewer, target);
            completed?.Invoke();
        };

        SetOffset(viewer, from);
        viewer.BeginAnimation(OffsetProperty, animation);
    }
    /// <summary>Stops a running animation where it is, so the user's own scrolling takes over cleanly.</summary>
    public static void Cancel(ScrollViewer viewer)
    {
        viewer.SetValue(GenerationProperty, unchecked((int)viewer.GetValue(GenerationProperty) + 1));
        if (viewer.HasAnimatedProperties)
        {
            var current = viewer.VerticalOffset;
            viewer.BeginAnimation(OffsetProperty, null);
            SetOffset(viewer, current);
        }
    }
}
