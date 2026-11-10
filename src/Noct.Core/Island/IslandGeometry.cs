namespace Noct.Core.Island;

/// <summary>An integer rectangle in physical screen pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsInside(PixelRect outer) =>
        X >= outer.X && Y >= outer.Y && Right <= outer.Right && Bottom <= outer.Bottom;
}

/// <summary>
/// The island's visible silhouette in device-independent pixels.
/// </summary>
/// <param name="Width">Width of the straight-sided body.</param>
/// <param name="Height">Height from the screen edge to the bottom of the body.</param>
/// <param name="Flare">Radius of the concave fillets that blend the body into the screen edge.</param>
/// <param name="Radius">Radius of the convex bottom corners.</param>
/// <param name="Lift">Vertical offset; zero when attached, negative while sliding above the edge.</param>
public readonly record struct IslandBody(double Width, double Height, double Flare, double Radius, double Lift)
{
    /// <summary>Width including the flares on both sides.</summary>
    public double OuterWidth => Width + (2 * Flare);

    /// <summary>How far below the screen's top edge the body still reaches.</summary>
    public double VisibleBottom => Height + Lift;

    public static IslandBody Lerp(IslandBody from, IslandBody to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new IslandBody(
            from.Width + ((to.Width - from.Width) * t),
            from.Height + ((to.Height - from.Height) * t),
            from.Flare + ((to.Flare - from.Flare) * t),
            from.Radius + ((to.Radius - from.Radius) * t),
            from.Lift + ((to.Lift - from.Lift) * t));
    }
}

public enum OutlineKind
{
    Line,

    /// <summary>A quarter circle; every arc in the island outline is one.</summary>
    Arc,
}

/// <param name="Radius">Arc radius; unused for lines.</param>
/// <param name="Clockwise">Sweep direction in screen coordinates (y grows downward).</param>
public readonly record struct OutlineSegment(OutlineKind Kind, double X, double Y, double Radius, bool Clockwise);

/// <summary>
/// The island outline as plain segments, independent of any UI toolkit.
/// </summary>
/// <remarks>
/// <para>
/// The shape stays attached to the top edge of the screen. Seen from above the screen edge it is:
/// a concave fillet that widens into the edge, straight sides, and convex rounded bottom corners.
/// Every join is tangent-continuous, so there is no sharp corner anywhere on the visible outline;
/// the tangent where the fillet meets the edge is horizontal, so the silhouette fades into the edge
/// instead of ending in a right angle.
/// </para>
/// <para>
/// The outline is extended a couple of pixels <i>above</i> the edge (<see cref="EdgeOverscan"/>) so
/// that the first pixel row of the screen is covered by fill rather than by an anti-aliased sliver.
/// That overscan lies outside the window and is never visible.
/// </para>
/// </remarks>
public static class IslandOutline
{
    public const double EdgeOverscan = 2;

    /// <summary>Number of segments <see cref="Build"/> writes.</summary>
    public const int SegmentCount = 9;

    /// <summary>
    /// Writes the outline for <paramref name="body"/> centred on <paramref name="centerX"/>.
    /// Radii are clamped so the arcs always fit, whatever size the body is mid-animation.
    /// </summary>
    public static void Build(IslandBody body, double centerX, Span<OutlineSegment> segments, out double startX, out double startY)
    {
        if (segments.Length < SegmentCount)
        {
            throw new ArgumentException($"At least {SegmentCount} segments are required.", nameof(segments));
        }

        var width = Math.Max(0, body.Width);
        var height = Math.Max(0, body.Height);
        var flare = Math.Clamp(body.Flare, 0, height / 2);
        var radius = Math.Clamp(body.Radius, 0, Math.Min(width / 2, Math.Max(0, height - flare)));

        var left = centerX - (width / 2);
        var right = centerX + (width / 2);
        var outerLeft = left - flare;
        var outerRight = right + flare;

        startX = outerLeft;
        startY = -EdgeOverscan;

        segments[0] = new OutlineSegment(OutlineKind.Line, outerLeft, 0, 0, false);
        segments[1] = new OutlineSegment(OutlineKind.Arc, left, flare, flare, true);
        segments[2] = new OutlineSegment(OutlineKind.Line, left, height - radius, 0, false);
        segments[3] = new OutlineSegment(OutlineKind.Arc, left + radius, height, radius, false);
        segments[4] = new OutlineSegment(OutlineKind.Line, right - radius, height, 0, false);
        segments[5] = new OutlineSegment(OutlineKind.Arc, right, height - radius, radius, false);
        segments[6] = new OutlineSegment(OutlineKind.Line, right, flare, 0, false);
        segments[7] = new OutlineSegment(OutlineKind.Arc, outerRight, 0, flare, true);
        segments[8] = new OutlineSegment(OutlineKind.Line, outerRight, -EdgeOverscan, 0, false);
    }
}

/// <summary>
/// Island sizes and placement as pure arithmetic, so DPI behaviour can be tested without a desktop.
/// Sizes are the design-system starting values; the island is always attached to the physical
/// top-centre of the chosen monitor.
/// </summary>
public static class IslandGeometry
{
    public const double CompactWidth = 220;
    public const double CompactHeight = 36;
    public const double ExpandedWidth = 420;
    public const double ExpandedHeight = 128;

    /// <summary>The hidden state keeps a narrow strip on screen so hover can still find it.</summary>
    public const double HiddenWidth = 180;
    public const double HiddenHeight = 2;

    public const double CompactRadius = 18;
    public const double ExpandedRadius = 24;
    public const double CompactFlare = 10;
    public const double ExpandedFlare = 16;

    /// <summary>The body for a resting state.</summary>
    public static IslandBody BodyFor(IslandState state) => state switch
    {
        IslandState.Expanded => new IslandBody(ExpandedWidth, ExpandedHeight, ExpandedFlare, ExpandedRadius, 0),
        IslandState.Hidden => new IslandBody(HiddenWidth, HiddenHeight, HiddenHeight / 2, HiddenHeight / 2, 0),
        _ => new IslandBody(CompactWidth, CompactHeight, CompactFlare, CompactRadius, 0),
    };

    /// <summary>
    /// The compact or expanded body pushed up so that only the hidden strip's height remains below
    /// the screen edge. Sliding to this and then swapping to the strip is the auto-hide animation.
    /// </summary>
    public static IslandBody Raised(IslandBody body) =>
        body with { Lift = -(body.Height - HiddenHeight) };

    /// <summary>
    /// The window rectangle in physical pixels that contains every body in <paramref name="bodies"/>
    /// for the whole of an animation. Centred on the monitor and attached to its top edge.
    /// </summary>
    public static PixelRect WindowBox(PixelRect monitor, double scale, params IslandBody[] bodies)
    {
        var width = 1.0;
        var height = HiddenHeight;
        foreach (var body in bodies)
        {
            width = Math.Max(width, body.OuterWidth);
            height = Math.Max(height, Math.Max(body.VisibleBottom, 0));
        }

        var widthPx = Math.Min((int)Math.Ceiling(width * scale), Math.Max(1, monitor.Width));
        var heightPx = Math.Min(Math.Max(2, (int)Math.Ceiling(height * scale)), monitor.Height);

        // Keep the centre exact so the body does not shift when the window is resized around it.
        if ((monitor.Width - widthPx) % 2 != 0)
        {
            widthPx = Math.Min(widthPx + 1, monitor.Width);
        }

        return new PixelRect(monitor.X + ((monitor.Width - widthPx) / 2), monitor.Y, widthPx, heightPx);
    }

    /// <summary>The window rectangle for a body at rest.</summary>
    public static PixelRect RestBox(PixelRect monitor, double scale, IslandBody body) => WindowBox(monitor, scale, body);

    /// <summary>Cubic ease-out: fast start, gentle settle, no overshoot.</summary>
    public static double EaseOut(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var inverse = 1 - t;
        return 1 - (inverse * inverse * inverse);
    }
}
