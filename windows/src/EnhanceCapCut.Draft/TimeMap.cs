namespace EnhanceCapCut.Draft;

/// <summary>
/// Maps a time offset inside a segment's timeline range to a time in its source media.
/// Constant speed is a straight line; a CapCut speed curve is integrated numerically.
/// </summary>
public sealed record TimeMap
{
    public long SourceStart { get; init; }
    public long SourceDuration { get; init; }
    public long TargetDuration { get; init; }
    /// <summary>Normalized cumulative source position S(u) sampled at <c>u = i / (count - 1)</c>; null for constant speed.</summary>
    public double[]? CurveTable { get; init; }

    internal const int TableSize = 1024;

    public TimeMap(long sourceStart, long sourceDuration, long targetDuration, IReadOnlyList<Point2>? curvePoints = null)
    {
        SourceStart = sourceStart;
        SourceDuration = sourceDuration;
        TargetDuration = targetDuration;
        CurveTable = curvePoints is { Count: >= 2 } ? Integrate(curvePoints) : null;
    }

    /// <summary>
    /// A map sampled from a monotonic function giving the source time (µs) at a normalized target
    /// position u in 0...1. Used to slice and compose maps; a straight line collapses to constant speed.
    /// </summary>
    public TimeMap(long targetDuration, Func<double, double> source)
    {
        const int n = TableSize;
        var values = new double[n + 1];
        for (var i = 0; i <= n; i++) values[i] = source((double)i / n);
        for (var i = 1; i <= n; i++) values[i] = Math.Max(values[i], values[i - 1]);
        double start = values[0], span = values[n] - values[0];
        SourceStart = Lenient.Round(start);
        SourceDuration = Lenient.Round(span);
        TargetDuration = targetDuration;
        if (span <= 0) return;
        var table = values.Select(v => (v - start) / span).ToArray();
        var isLine = Enumerable.Range(0, table.Length).All(i => Math.Abs(table[i] - (double)i / n) < 1e-4);
        CurveTable = isLine ? null : table;
    }

    public bool IsConstant => CurveTable is null;
    public double AverageSpeed => TargetDuration > 0 ? (double)SourceDuration / TargetDuration : 1;

    /// <summary>Source time (µs) shown at <paramref name="offset"/> µs after the segment's timeline start.</summary>
    public long SourceTime(long offset) => Lenient.Round(PreciseSourceTime(offset));

    public double PreciseSourceTime(double offset)
    {
        if (TargetDuration <= 0) return SourceStart;
        var u = Math.Clamp(offset / TargetDuration, 0, 1);
        return SourceStart + NormalizedSource(u) * SourceDuration;
    }

    /// <summary>Inverse of <see cref="SourceTime"/>: the offset at which source time <paramref name="time"/> is shown (clamped to the segment).</summary>
    public long Offset(long time)
    {
        if (SourceDuration <= 0 || TargetDuration <= 0) return 0;
        var s = Math.Clamp((double)(time - SourceStart) / SourceDuration, 0, 1);
        var u = s;
        if (!IsConstant)
        {
            double lo = 0, hi = 1;
            for (var i = 0; i < 40; i++)
            {
                var mid = (lo + hi) / 2;
                if (NormalizedSource(mid) < s) lo = mid; else hi = mid;
            }
            u = hi;
        }
        return Lenient.Round(u * TargetDuration);
    }

    /// <summary>The part of this map between two offsets, as a map of its own (offset 0 = <paramref name="a"/>).</summary>
    public TimeMap Slice(long a, long b)
    {
        if (IsConstant)
        {
            var s0 = SourceTime(a);
            return new TimeMap(s0, SourceTime(b) - s0, b - a);
        }
        return new TimeMap(b - a, u => PreciseSourceTime(a + u * (b - a)));
    }

    /// <summary>S(u) in 0...1.</summary>
    public double NormalizedSource(double u)
    {
        if (CurveTable is not { } table) return u;
        var pos = u * (table.Length - 1);
        var i = Math.Min((int)pos, table.Length - 2);
        var f = pos - i;
        return table[i] + (table[i + 1] - table[i]) * f;
    }

    /// <summary>Instantaneous speed multiplier at normalized position u (relative to the average).</summary>
    public double Speed(double u)
    {
        if (CurveTable is not { } table) return AverageSpeed;
        double n = table.Length - 1;
        var i = Math.Min((int)(u * n), table.Length - 2);
        return (table[i + 1] - table[i]) * n * AverageSpeed;
    }

    /// <summary>Integrates the piecewise-linear speed curve so that S(0) = 0 and S(1) = 1.</summary>
    internal static double[] Integrate(IReadOnlyList<Point2> rawPoints)
    {
        var points = rawPoints.OrderBy(p => p.X).ToArray();
        double V(double x)
        {
            if (x <= points[0].X) return Math.Max(points[0].Y, 0.01);
            for (var k = 1; k < points.Length; k++)
            {
                if (x > points[k].X) continue;
                Point2 a = points[k - 1], b = points[k];
                var t = b.X > a.X ? (x - a.X) / (b.X - a.X) : 1;
                return Math.Max(a.Y + (b.Y - a.Y) * t, 0.01);
            }
            return Math.Max(points[^1].Y, 0.01);
        }
        var table = new double[TableSize + 1];
        double acc = 0, prev = V(0);
        for (var i = 1; i <= TableSize; i++)
        {
            var cur = V((double)i / TableSize);
            acc += (prev + cur) * 0.5 / TableSize;
            table[i] = acc;
            prev = cur;
        }
        if (acc > 0) for (var i = 0; i < table.Length; i++) table[i] /= acc;
        return table;
    }
}

/// <summary>Linear interpolation over a segment's keyframes for one property.</summary>
public sealed class KeyframeTrack(IReadOnlyList<long> times, IReadOnlyList<double> values)
{
    public IReadOnlyList<long> Times { get; } = times;
    public IReadOnlyList<double> Values { get; } = values;

    public double Value(long offset)
    {
        if (Times.Count == 0) return 0;
        if (offset <= Times[0]) return Values[0];
        if (offset >= Times[^1]) return Values[^1];
        var i = 1;
        while (i < Times.Count && Times[i] < offset) i++;
        long t0 = Times[i - 1], t1 = Times[i];
        var f = t1 > t0 ? (double)(offset - t0) / (t1 - t0) : 1;
        return Values[i - 1] + (Values[i] - Values[i - 1]) * f;
    }

    /// <summary>The same curve with every value multiplied by <paramref name="factor"/>.</summary>
    public KeyframeTrack Scaled(double factor) => new(Times, Values.Select(v => v * factor).ToList());

    /// <summary>The curve as seen from a clip that starts <paramref name="offset"/> later (or earlier, when negative).</summary>
    public KeyframeTrack Shifted(long offset) => new(Times.Select(t => t - offset).ToList(), Values);

    /// <summary><paramref name="curve"/> sampled every <paramref name="step"/> over 0…<paramref name="length"/>, plus the length itself.</summary>
    public static KeyframeTrack Sampled(long length, long step, Func<long, double> curve)
    {
        var times = new List<long>();
        for (long t = 0; t < length; t += Math.Max(step, 1)) times.Add(t);
        times.Add(length);
        return new KeyframeTrack(times, times.Select(curve).ToList());
    }

    public static KeyframeTrack? From(KeyframeGroup group)
    {
        var frames = group.Keyframes.Where(k => k.Values.Count > 0).OrderBy(k => k.TimeOffset).ToList();
        return frames.Count == 0 ? null
            : new KeyframeTrack(frames.Select(k => k.TimeOffset).ToList(), frames.Select(k => k.Values[0]).ToList());
    }
}
