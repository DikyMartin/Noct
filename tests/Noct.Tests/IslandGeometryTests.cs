using Noct.Core.Island;

namespace Noct.Tests;

public sealed class IslandGeometryTests
{
    private static readonly PixelRect Fhd = new(0, 0, 1920, 1080);

    private static IslandBody[] RestingBodies() =>
    [
        IslandGeometry.BodyFor(IslandState.Compact),
        IslandGeometry.BodyFor(IslandState.Expanded),
        IslandGeometry.BodyFor(IslandState.Hidden),
    ];

    // ----- outline shape -----

    /// <summary>Walks the outline into a polyline, subdividing each quarter circle.</summary>
    private static List<(double X, double Y)> Tessellate(IslandBody body, double centerX, int stepsPerArc = 48)
    {
        var segments = new OutlineSegment[IslandOutline.SegmentCount];
        IslandOutline.Build(body, centerX, segments, out var sx, out var sy);

        var points = new List<(double X, double Y)> { (sx, sy) };
        var current = (X: sx, Y: sy);

        foreach (var segment in segments)
        {
            if (segment.Kind == OutlineKind.Line || segment.Radius < 1e-9)
            {
                points.Add((segment.X, segment.Y));
            }
            else
            {
                // Every arc is a quarter circle; find the centre from the endpoints and direction.
                var dx = segment.X - current.X;
                var dy = segment.Y - current.Y;
                var candidateA = (X: current.X + dx, Y: current.Y);
                var candidateB = (X: current.X, Y: current.Y + dy);

                // Clockwise on screen: the centre is the corner such that the quarter turns clockwise.
                // Cross product sign of (start - centre) x (end - centre) tells the direction.
                (double X, double Y) centre = candidateA;
                foreach (var candidate in new[] { candidateA, candidateB })
                {
                    var ax = current.X - candidate.X;
                    var ay = current.Y - candidate.Y;
                    var bx = segment.X - candidate.X;
                    var by = segment.Y - candidate.Y;
                    var cross = (ax * by) - (ay * bx);
                    if ((cross > 0) == segment.Clockwise)
                    {
                        centre = candidate;
                    }
                }

                var startAngle = Math.Atan2(current.Y - centre.Y, current.X - centre.X);
                var endAngle = Math.Atan2(segment.Y - centre.Y, segment.X - centre.X);
                var sweep = endAngle - startAngle;
                while (segment.Clockwise && sweep < 0) { sweep += 2 * Math.PI; }
                while (!segment.Clockwise && sweep > 0) { sweep -= 2 * Math.PI; }

                for (var i = 1; i <= stepsPerArc; i++)
                {
                    var angle = startAngle + (sweep * i / stepsPerArc);
                    points.Add((centre.X + (segment.Radius * Math.Cos(angle)), centre.Y + (segment.Radius * Math.Sin(angle))));
                }
            }

            current = (segment.X, segment.Y);
        }

        return points;
    }

    [Theory]
    [InlineData(220, 36, 10, 18)]
    [InlineData(420, 128, 16, 24)]
    [InlineData(300, 80, 12, 20)]
    [InlineData(180, 2, 0, 1)]
    [InlineData(250, 20, 10, 18)]
    public void No_corner_sharper_than_a_few_degrees_anywhere_on_the_visible_outline(double width, double height, double flare, double radius)
    {
        var raw = Tessellate(new IslandBody(width, height, flare, radius, 0), centerX: 300);

        // A straight side can have zero length when the radii fill the height; a repeated point has
        // no direction, so it must not be measured as a turn.
        var points = new List<(double X, double Y)>();
        foreach (var point in raw)
        {
            if (points.Count == 0 || Math.Abs(point.X - points[^1].X) > 1e-9 || Math.Abs(point.Y - points[^1].Y) > 1e-9)
            {
                points.Add(point);
            }
        }

        // Only vertices strictly below the screen edge count. The outline continues a little above the
        // edge (overscan), which is never visible, and its joint with the fillet lies on the edge.
        var worst = 0.0;
        for (var i = 1; i < points.Count - 1; i++)
        {
            if (points[i].Y <= 0.001)
            {
                continue;
            }

            var (x0, y0) = points[i - 1];
            var (x1, y1) = points[i];
            var (x2, y2) = points[i + 1];
            var turn = Math.Abs(Math.Atan2(y2 - y1, x2 - x1) - Math.Atan2(y1 - y0, x1 - x0));
            if (turn > Math.PI)
            {
                turn = (2 * Math.PI) - turn;
            }

            worst = Math.Max(worst, turn);
        }
        Assert.True(worst < 5 * Math.PI / 180, $"sharpest turn on the visible outline was {worst * 180 / Math.PI:F1} degrees");
    }

    [Fact]
    public void Fillet_meets_the_screen_edge_tangentially()
    {
        var body = IslandGeometry.BodyFor(IslandState.Compact);
        var points = Tessellate(body, centerX: 300, stepsPerArc: 200);

        // The first arc starts on the edge; its first step must head almost purely along the edge.
        var start = points.First(p => Math.Abs(p.Y) < 1e-9 && Math.Abs(p.X - (300 - (body.Width / 2) - body.Flare)) < 1e-6);
        var next = points[points.IndexOf(start) + 1];
        var angle = Math.Atan2(next.Y - start.Y, next.X - start.X) * 180 / Math.PI;

        Assert.InRange(angle, -1, 1);
    }

    [Theory]
    [InlineData(220, 36, 10, 18)]
    [InlineData(420, 128, 16, 24)]
    public void Visible_outline_stays_inside_its_box_and_is_mirror_symmetric(double width, double height, double flare, double radius)
    {
        const double centre = 400;
        var points = Tessellate(new IslandBody(width, height, flare, radius, 0), centre);

        foreach (var (x, y) in points.Where(p => p.Y >= 0))
        {
            Assert.InRange(x, centre - (width / 2) - flare - 1e-6, centre + (width / 2) + flare + 1e-6);
            Assert.InRange(y, -1e-6, height + 1e-6);
        }

        // Each point has a mirror image about the vertical centre line.
        foreach (var (x, y) in points.Where(p => p.Y >= 0))
        {
            var mirrorX = (2 * centre) - x;
            Assert.Contains(points, p => Math.Abs(p.X - mirrorX) < 0.5 && Math.Abs(p.Y - y) < 0.5);
        }
    }

    [Fact]
    public void Radii_are_clamped_so_a_tiny_mid_animation_body_never_produces_a_broken_arc()
    {
        var segments = new OutlineSegment[IslandOutline.SegmentCount];
        IslandOutline.Build(new IslandBody(220, 5, 10, 18, 0), 100, segments, out _, out _);

        foreach (var segment in segments.Where(s => s.Kind == OutlineKind.Arc))
        {
            Assert.InRange(segment.Radius, 0, 5);
        }

        foreach (var segment in segments)
        {
            Assert.False(double.IsNaN(segment.X) || double.IsNaN(segment.Y));
        }
    }

    [Fact]
    public void The_outline_overscans_above_the_edge_so_row_zero_is_fully_covered()
    {
        var segments = new OutlineSegment[IslandOutline.SegmentCount];
        IslandOutline.Build(IslandGeometry.BodyFor(IslandState.Compact), 100, segments, out _, out var startY);

        Assert.True(startY < 0);
        Assert.True(segments[^1].Y < 0);
    }

    // ----- window placement -----

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Every_resting_window_is_centred_on_the_top_edge_and_inside_the_monitor(double scale)
    {
        foreach (var body in RestingBodies())
        {
            var box = IslandGeometry.RestBox(Fhd, scale, body);

            Assert.Equal(0, box.Y);
            Assert.True(box.IsInside(Fhd), $"{body} at {scale}x left the monitor: {box}");
            Assert.Equal((Fhd.Width - box.Width) / 2, box.X);
        }
    }

    [Theory]
    [InlineData(1.0, 240, 36)]
    [InlineData(1.5, 360, 54)]
    [InlineData(2.0, 480, 72)]
    public void Compact_window_covers_the_body_and_its_flares_at_each_dpi(double scale, int width, int height)
    {
        var box = IslandGeometry.RestBox(Fhd, scale, IslandGeometry.BodyFor(IslandState.Compact));

        Assert.Equal(width, box.Width);
        Assert.Equal(height, box.Height);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void The_hidden_strip_window_is_thin_but_never_under_two_pixels(double scale)
    {
        var box = IslandGeometry.RestBox(Fhd, scale, IslandGeometry.BodyFor(IslandState.Hidden));

        Assert.InRange(box.Height, 2, 8);

        // 180 DIP body plus a 1 DIP fillet on each side, so even the strip's ends taper into the edge.
        // One extra pixel is allowed so the window stays exactly centred on the monitor.
        Assert.InRange(box.Width, (int)Math.Ceiling(182 * scale), (int)Math.Ceiling(182 * scale) + 1);
        Assert.Equal(0, (Fhd.Width - box.Width) % 2);
    }

    [Fact]
    public void An_animation_window_contains_both_ends_so_it_never_needs_resizing_mid_flight()
    {
        var compact = IslandGeometry.BodyFor(IslandState.Compact);
        var expanded = IslandGeometry.BodyFor(IslandState.Expanded);

        var box = IslandGeometry.WindowBox(Fhd, 1.5, compact, expanded);
        var small = IslandGeometry.RestBox(Fhd, 1.5, compact);
        var large = IslandGeometry.RestBox(Fhd, 1.5, expanded);

        Assert.True(box.Width >= small.Width && box.Width >= large.Width);
        Assert.True(box.Height >= small.Height && box.Height >= large.Height);
        Assert.Equal((Fhd.Width - box.Width) / 2, box.X);
    }

    [Fact]
    public void Sliding_up_to_hide_keeps_only_the_strip_height_below_the_edge()
    {
        var raised = IslandGeometry.Raised(IslandGeometry.BodyFor(IslandState.Compact));

        Assert.Equal(IslandGeometry.HiddenHeight, raised.VisibleBottom, precision: 6);
        Assert.True(raised.Lift < 0);
    }

    [Fact]
    public void A_monitor_with_a_non_zero_origin_places_the_island_on_that_monitor()
    {
        var secondary = new PixelRect(-2560, -300, 2560, 1440);

        var box = IslandGeometry.RestBox(secondary, 1.0, IslandGeometry.BodyFor(IslandState.Compact));

        Assert.Equal(secondary.Y, box.Y);
        Assert.Equal(secondary.X + ((secondary.Width - box.Width) / 2), box.X);
        Assert.True(box.IsInside(secondary));
    }

    [Fact]
    public void A_very_narrow_monitor_clamps_the_window_instead_of_overflowing()
    {
        var narrow = new PixelRect(0, 0, 300, 600);

        var box = IslandGeometry.RestBox(narrow, 1.0, IslandGeometry.BodyFor(IslandState.Expanded));

        Assert.True(box.IsInside(narrow));
    }

    // ----- interpolation and easing -----

    [Fact]
    public void Interpolation_hits_both_endpoints_and_never_overshoots()
    {
        var from = IslandGeometry.BodyFor(IslandState.Compact);
        var to = IslandGeometry.BodyFor(IslandState.Expanded);

        Assert.Equal(from, IslandBody.Lerp(from, to, 0));
        Assert.Equal(to, IslandBody.Lerp(from, to, 1));

        for (var step = 0; step <= 40; step++)
        {
            var body = IslandBody.Lerp(from, to, IslandGeometry.EaseOut(step / 40.0));
            Assert.InRange(body.Width, from.Width, to.Width);
            Assert.InRange(body.Height, from.Height, to.Height);
            Assert.InRange(body.Flare, from.Flare, to.Flare);
        }
    }

    [Fact]
    public void Easing_is_monotonic_and_never_overshoots()
    {
        var previous = -1.0;
        for (var step = 0; step <= 100; step++)
        {
            var value = IslandGeometry.EaseOut(step / 100.0);
            Assert.InRange(value, 0, 1);
            Assert.True(value >= previous);
            previous = value;
        }

        Assert.Equal(0, IslandGeometry.EaseOut(0));
        Assert.Equal(1, IslandGeometry.EaseOut(1));
    }
}
