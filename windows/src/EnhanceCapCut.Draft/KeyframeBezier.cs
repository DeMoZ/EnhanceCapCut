namespace EnhanceCapCut.Draft;

/// <summary>A keyframe's easing: CapCut's <c>curveType</c> and its bezier handles.</summary>
/// <param name="Linear"><c>curveType</c> "Line": a span between two linear keyframes is a straight line whatever the handles say.</param>
/// <param name="Left"><c>left_control</c> (incoming), relative to the keyframe: x in µs, y in the property's units.</param>
/// <param name="Right"><c>right_control</c> (outgoing), the same way.</param>
public sealed record KeyframeHandles(bool Linear, Point2 Left, Point2 Right)
{
    public static KeyframeHandles From(Keyframe k) =>
        new(k.CurveType is "" or "Line", k.LeftControl, k.RightControl);

    public KeyframeHandles Scaled(double factor) =>
        new(Linear, new Point2(Left.X, Left.Y * factor), new Point2(Right.X, Right.Y * factor));
}

/// <summary>
/// A cubic bezier in (time, value) between two keyframes, like a graph editor's curve: P0 = (0, v0),
/// P1 = P0 + the first keyframe's outgoing handle, P2 = P3 + the second one's incoming handle, P3 = (span, v1).
/// Zero handles give a straight line. Handle times are clamped into the span so that time only moves forward.
/// </summary>
public static class KeyframeBezier
{
    public static double Value(double u, double d, double v0, double v1, Point2 @out, Point2 incoming)
    {
        if (d <= 0) return v1;
        double x1 = Math.Clamp(@out.X, 0, d), x2 = Math.Clamp(d + incoming.X, 0, d);
        double y1 = v0 + @out.Y, y2 = v1 + incoming.Y;
        var s = Parameter(Math.Clamp(u, 0, d), x1, x2, d);
        var r = 1 - s;
        return r * r * r * v0 + 3 * r * r * s * y1 + 3 * r * s * s * y2 + s * s * s * v1;
    }

    /// <summary>The bezier parameter where x(s) = u: Newton steps from the linear guess, bisection when they stall.</summary>
    public static double Parameter(double u, double x1, double x2, double d)
    {
        double X(double s) { var r = 1 - s; return 3 * r * r * s * x1 + 3 * r * s * s * x2 + s * s * s * d; }
        double Dx(double s) { var r = 1 - s; return 3 * r * r * x1 + 6 * r * s * (x2 - x1) + 3 * s * s * (d - x2); }
        var tolerance = 1e-6 * d;
        var s = u / d;
        for (var i = 0; i < 8; i++)
        {
            var e = X(s) - u;
            if (Math.Abs(e) < tolerance) return s;
            var slope = Dx(s);
            if (Math.Abs(slope) < 1e-9 * d) break;
            s = Math.Clamp(s - e / slope, 0, 1);
        }
        double lo = 0, hi = 1;
        for (var i = 0; i < 60; i++)
        {
            s = (lo + hi) / 2;
            var e = X(s) - u;
            if (Math.Abs(e) < tolerance) break;
            if (e < 0) lo = s; else hi = s;
        }
        return s;
    }
}
