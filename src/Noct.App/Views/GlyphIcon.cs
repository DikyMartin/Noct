using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Noct.App.Views;

/// <summary>
/// Draws one outline icon from the shared icon set.
/// </summary>
/// <remarks>
/// Every glyph is authored on a 24 x 24 grid. This element scales that grid to <see cref="Size"/>
/// but keeps the stroke at a constant device-independent width, so icons of different sizes keep
/// the same line weight. The colour follows the inherited foreground, which means a button's
/// hover, pressed and disabled colours reach its icon with no extra styling.
/// </remarks>
public sealed class GlyphIcon : FrameworkElement
{
    private const double Grid = 24;

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(GlyphIcon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(bool), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    static GlyphIcon()
    {
        IsHitTestVisibleProperty.OverrideMetadata(typeof(GlyphIcon), new UIPropertyMetadata(false));

        // A FrameworkElement stretches by default, and this one draws from the top-left of
        // whatever slot it is given. That left icons riding high inside taller rows. Centering by
        // default keeps every icon on the middle of its slot.
        HorizontalAlignmentProperty.OverrideMetadata(typeof(GlyphIcon), new FrameworkPropertyMetadata(HorizontalAlignment.Center));
        VerticalAlignmentProperty.OverrideMetadata(typeof(GlyphIcon), new FrameworkPropertyMetadata(VerticalAlignment.Center));
    }

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Fills the shape as well as stroking it. Used for the play triangle.</summary>
    public bool Filled
    {
        get => (bool)GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    /// <summary>Stroke width in device-independent pixels, independent of <see cref="Size"/>.</summary>
    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Data is not { } geometry || Foreground is not { } brush)
        {
            return;
        }

        var scale = Size / Grid;
        var pen = new Pen(brush, StrokeThickness / scale)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();

        // If a caller forces Stretch, the slot is larger than the icon: draw in its middle.
        var offsetX = Math.Max(0, (ActualWidth - Size) / 2);
        var offsetY = Math.Max(0, (ActualHeight - Size) / 2);
        drawingContext.PushTransform(new TranslateTransform(offsetX, offsetY));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.DrawGeometry(Filled ? brush : null, pen, geometry);
        drawingContext.Pop();
        drawingContext.Pop();
    }
}
