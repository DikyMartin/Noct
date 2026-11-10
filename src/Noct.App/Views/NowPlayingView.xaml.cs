using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Noct.App.ViewModels;

namespace Noct.App.Views;

public partial class NowPlayingView : UserControl
{
    /// <summary>One turn every 24 seconds.</summary>
    private static readonly Duration TurnDuration = new(TimeSpan.FromSeconds(24));

    /// <summary>Below this width the disc sits above the lyrics instead of beside them.</summary>
    private const double StackedBelowWidth = 640;

    private MainViewModel? _model;
    private bool _spinning;

    public NowPlayingView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += (_, _) => OnVisibilityChanged();
        SizeChanged += (_, _) => ApplyLayout();
        LyricsScroll.SizeChanged += (_, _) => OnLyricsViewportChanged();

        // Only the user's own input counts as "scrolling away". Programmatic scrolls raise none of these.
        // The wheel glide itself comes from SmoothWheel (which handles the event), so listen even when handled, and
        // do not cancel the glide here.
        LyricsScroll.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, _) => _model?.Lyrics.UserScrolled()), handledEventsToo: true);
        LyricsScroll.PreviewTouchMove += (_, _) => UserTookOver();
        LyricsScroll.AddHandler(ScrollBar.ScrollEvent, new ScrollEventHandler((_, _) => UserTookOver()));
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End && LyricsScroll.IsKeyboardFocusWithin)
            {
                UserTookOver();
            }
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_model is not null)
        {
            _model.Playback.PropertyChanged -= OnPlaybackChanged;
            _model.Lyrics.PropertyChanged -= OnLyricsChanged;
            _model.Lyrics.ActiveLineChanged -= OnActiveLineChanged;
        }

        _model = DataContext as MainViewModel;
        if (_model is null)
        {
            return;
        }

        _model.Playback.PropertyChanged += OnPlaybackChanged;
        _model.Lyrics.PropertyChanged += OnLyricsChanged;
        _model.Lyrics.ActiveLineChanged += OnActiveLineChanged;
        UpdateGlow();
        UpdateSpin();
    }

    // ----- Layout -----

    private void ApplyLayout()
    {
        var stacked = ActualWidth < StackedBelowWidth;
        if (stacked)
        {
            DiscColumn.Width = new GridLength(1, GridUnitType.Star);
            LyricsColumn.Width = new GridLength(0);
            DiscRow.Height = new GridLength(2, GridUnitType.Star);
            LyricsRow.Height = new GridLength(3, GridUnitType.Star);
            Grid.SetRow(LyricsPanel, 1);
            Grid.SetColumn(LyricsPanel, 0);
            LyricsPanel.Margin = new Thickness(0, 8, 0, 0);
        }
        else
        {
            DiscColumn.Width = new GridLength(9, GridUnitType.Star);
            LyricsColumn.Width = new GridLength(11, GridUnitType.Star);
            DiscRow.Height = new GridLength(1, GridUnitType.Star);
            LyricsRow.Height = new GridLength(0);
            Grid.SetRow(LyricsPanel, 0);
            Grid.SetColumn(LyricsPanel, 1);
            LyricsPanel.Margin = new Thickness(32, 0, 0, 0);
        }

        // Large type where there is room, smaller (still comfortable) where there is not.
        LyricsList.FontSize = ActualWidth >= 900 ? 28 : ActualWidth >= 700 ? 24 : 20;
    }

    private void OnLyricsViewportChanged()
    {
        // Padding of about half the viewport lets the first and last lines reach the middle.
        var height = LyricsScroll.ActualHeight;
        LyricsList.Margin = new Thickness(0, height * 0.42, 12, height * 0.5);
        LyricsList.UpdateLayout();
        if (_model?.Lyrics.IsFollowing == true)
        {
            ScrollToActive(animate: false);
        }
    }

    // ----- Disc -----

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackViewModel.IsPlaying))
        {
            UpdateSpin();
        }
        else if (e.PropertyName == nameof(PlaybackViewModel.AccentColor))
        {
            UpdateGlow();
        }
    }

    private void OnVisibilityChanged()
    {
        UpdateSpin();
        if (IsVisible)
        {
            Dispatcher.BeginInvoke(() => ScrollToActive(animate: false), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// The record turns only while a song plays, this page is on screen and the user has not turned
    /// animation off. Pausing keeps the angle it reached; playing carries on from there.
    /// </summary>
    private void UpdateSpin()
    {
        var shouldSpin = _model?.Playback.IsPlaying == true && IsVisible && SystemParameters.ClientAreaAnimation;
        if (shouldSpin == _spinning)
        {
            return;
        }

        _spinning = shouldSpin;
        if (shouldSpin)
        {
            var from = SpinTransform.Angle % 360;
            SpinTransform.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(from, from + 360, TurnDuration)
            {
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }
        else
        {
            // Read the animated value, drop the animation, and pin the angle where the disc stopped.
            var current = SpinTransform.Angle % 360;
            SpinTransform.BeginAnimation(RotateTransform.AngleProperty, null);
            SpinTransform.Angle = current;
        }

        // Exposed to UI Automation so the turning/stopped state and the held angle can be checked from outside.
        System.Windows.Automation.AutomationProperties.SetItemStatus(Disc,
            FormattableString.Invariant($"spinning={shouldSpin};angle={SpinTransform.Angle:F1}"));
    }

    private void UpdateGlow()
    {
        if (_model?.Playback.AccentColor is not { } accent)
        {
            Glow.Fill = null;
            return;
        }

        var brush = new RadialGradientBrush
        {
            Center = new Point(0.25, 0.5),
            GradientOrigin = new Point(0.25, 0.5),
            RadiusX = 0.55,
            RadiusY = 0.8,
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x2E, accent.R, accent.G, accent.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, accent.R, accent.G, accent.B), 1));
        brush.Freeze();
        Glow.Fill = brush;
    }

    // ----- Lyrics scrolling -----

    private void OnLyricsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsViewModel.Lines))
        {
            // New lyrics (or none): start at the top, then settle on the active line once laid out.
            SmoothScroll.To(LyricsScroll, 0, animate: false);
            Dispatcher.BeginInvoke(() => ScrollToActive(animate: false), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void OnActiveLineChanged(object? sender, EventArgs e) => ScrollToActive(animate: true);

    private void ScrollToActive(bool animate)
    {
        if (_model is null || !IsVisible || LyricsScroll.ActualHeight <= 0)
        {
            return;
        }

        var animated = animate && SystemParameters.ClientAreaAnimation;
        var index = _model.Lyrics.ActiveIndex;
        if (index < 0)
        {
            SmoothScroll.To(LyricsScroll, 0, animated && LyricsScroll.VerticalOffset <= LyricsScroll.ViewportHeight);
            return;
        }

        if (LyricsList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
        {
            return;
        }

        LyricsList.UpdateLayout();
        var top = container.TransformToAncestor(LyricsList).Transform(new Point(0, 0)).Y + LyricsList.Margin.Top;
        var target = top + container.ActualHeight / 2 - LyricsScroll.ViewportHeight / 2;

        // A seek or a track change can move the lyrics far. Glide to a nearby line, but jump to a distant one so the
        // right line is on screen at once instead of sweeping past half the song.
        animated &= Math.Abs(target - LyricsScroll.VerticalOffset) <= LyricsScroll.ViewportHeight;
        SmoothScroll.To(LyricsScroll, target, animated);
    }

    private void UserTookOver()
    {
        SmoothScroll.Cancel(LyricsScroll);
        _model?.Lyrics.UserScrolled();
    }
}
