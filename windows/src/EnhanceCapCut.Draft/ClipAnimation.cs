namespace EnhanceCapCut.Draft;

/// <summary>
/// A video or photo clip's In / Out animation, re-implemented as a curve on top of the clip transform.
/// The presets are recognised by their name as CapCut stores it (in the UI language it was applied in). The motion is our own
/// approximation of CapCut's preset, not a copy of its effect package; the constants below are where to calibrate it.
/// Port of Swift <c>ClipAnimation</c>.
/// </summary>
public sealed record ClipAnimation(ClipAnimation.AnimationKind Kind, ClipAnimation.AnimationPreset Preset, long Start, long End)
{
    public enum AnimationKind { In, Out }

    public enum AnimationPreset
    {
        Fade, ZoomIn, ZoomOut, SlideLeft, SlideRight, SlideUp, SlideDown, Rotate, RotateCounterclockwise, Shake,
    }

    /// <summary>Scale the clip starts from (In) or ends at (Out) for "Zoom In" / "Zoom Out".</summary>
    public const double ZoomInScale = 0.5, ZoomOutScale = 1.5;
    /// <summary>How far a slide travels, in half-canvas units (2 = one canvas width or height: from off-screen).</summary>
    public const double SlideDistance = 2.0;
    /// <summary>Full turns of "Rotate".</summary>
    public const double RotateDegrees = 360.0;
    /// <summary>"Shake": peak offset in half-canvas units, and the number of swings over the animation.</summary>
    const double ShakeAmplitude = 0.06, ShakeSwings = 4.0;

    /// <summary>The clip's animations, and the names of the ones the renderer doesn't reproduce (Combo and unknown
    /// presets).</summary>
    public static (List<ClipAnimation> Animations, List<string> Unsupported) Make(AnimationMaterial? material,
        long segmentDuration)
    {
        long din = 0, dout = 0;
        var found = new List<(AnimationKind Kind, AnimationPreset Preset)>();
        var unsupported = new List<string>();
        foreach (var item in material?.Animations ?? [])
        {
            AnimationKind? kind = item.Type switch { "in" => AnimationKind.In, "out" => AnimationKind.Out, _ => null };
            if (kind is not { } k || PresetNamed(item.Name) is not { } preset || item.Duration <= 0)
            {
                unsupported.Add(item.Name.Length == 0 ? item.Type : item.Name);
                continue;
            }
            if (k == AnimationKind.In) din = item.Duration; else dout = item.Duration;
            found.Add((k, preset));
        }
        // `start` is always 0 in the draft: an Out plays in the clip's last `duration`. An In and Out that don't fit
        // are squeezed into the clip, as for texts.
        var total = Math.Max(segmentDuration, 0);
        if (din + dout > total && din + dout > 0)
        {
            din = (long)((double)din * total / (din + dout));
            dout = total - din;
        }
        var animations = found.Select(f => f.Kind == AnimationKind.In
            ? new ClipAnimation(f.Kind, f.Preset, 0, din)
            : new ClipAnimation(f.Kind, f.Preset, total - dout, total)).Where(a => a.End > a.Start).ToList();
        return (animations, unsupported);
    }

    /// <summary>The preset for a CapCut animation name (English, Chinese or Russian UI), or null when it isn't
    /// reproduced.</summary>
    public static AnimationPreset? PresetNamed(string name)
    {
        var n = name.ToLowerInvariant().Replace('-', ' ');
        bool Has(params string[] words) => words.Any(w => n.Contains(w, StringComparison.Ordinal));
        if (Has("counterclockwise", "anticlockwise", "逆时针", "против час")) return AnimationPreset.RotateCounterclockwise;
        if (Has("rotat", "spin", "旋转", "вращ", "поворот")) return AnimationPreset.Rotate;
        if (Has("shake", "抖动", "тряс", "дрож")) return AnimationPreset.Shake;
        if (Has("slide", "滑动", "сдвиг", "скольж", "слайд"))
        {
            if (Has("left", "向左", "влево", "налево")) return AnimationPreset.SlideLeft;
            if (Has("right", "向右", "вправо", "направо")) return AnimationPreset.SlideRight;
            if (Has("up", "向上", "вверх")) return AnimationPreset.SlideUp;
            if (Has("down", "向下", "вниз")) return AnimationPreset.SlideDown;
            return null;
        }
        if (Has("zoom in", "放大", "увелич", "приближ")) return AnimationPreset.ZoomIn;
        if (Has("zoom out", "缩小", "уменьш", "отдал")) return AnimationPreset.ZoomOut;
        if (Has("fade", "渐显", "渐隐", "растворен", "проявлен", "затухан", "угасан", "появлен", "исчезн")) return AnimationPreset.Fade;
        return null;
    }

    /// <summary>Applies the animations that are running at <paramref name="offset"/> (µs from the segment start).</summary>
    public static LayerTransform Apply(IReadOnlyList<ClipAnimation> animations, LayerTransform transform, long offset) =>
        animations.Aggregate(transform, (t, a) => a.Apply(t, offset));

    LayerTransform Apply(LayerTransform transform, long offset)
    {
        // Before an In the clip is in its far state; after an Out it stays there.
        if (offset < Start) return Kind == AnimationKind.In ? Apply(transform, 0.0) : transform;
        if (offset >= End) return Kind == AnimationKind.Out ? Apply(transform, 1.0) : transform;
        return Apply(transform, (double)(offset - Start) / (End - Start));
    }

    /// <summary><c>amount</c> = 1 at the animation's far state (start of In, end of Out), 0 at the clip's own
    /// transform.</summary>
    internal LayerTransform Apply(LayerTransform t, double p)
    {
        var isIn = Kind == AnimationKind.In;
        // Ease out into the clip (In), ease in away from it (Out).
        var amount = isIn ? Math.Pow(1 - p, 3) : Math.Pow(p, 3);
        switch (Preset)
        {
            case AnimationPreset.Fade:
                return t with { Alpha = t.Alpha * (1 - amount) };
            case AnimationPreset.ZoomIn or AnimationPreset.ZoomOut:
            {
                // "Zoom In" grows: In starts small, Out ends large. "Zoom Out" shrinks: the other way round.
                var far = isIn == (Preset == AnimationPreset.ZoomIn) ? ZoomInScale : ZoomOutScale;
                var s = 1 + (far - 1) * amount;
                return t with { ScaleX = t.ScaleX * s, ScaleY = t.ScaleY * s, Alpha = t.Alpha * (1 - amount) };
            }
            case AnimationPreset.SlideLeft or AnimationPreset.SlideRight or AnimationPreset.SlideUp or AnimationPreset.SlideDown:
            {
                // The direction is the motion: an In "Slide Right" comes from the left, an Out one leaves to the right.
                var (dx, dy) = Preset switch
                {
                    AnimationPreset.SlideLeft => (-1.0, 0.0),
                    AnimationPreset.SlideRight => (1.0, 0.0),
                    AnimationPreset.SlideUp => (0.0, 1.0),
                    _ => (0.0, -1.0),
                };
                var d = SlideDistance * amount * (isIn ? -1 : 1);
                return t with { PositionX = t.PositionX + dx * d, PositionY = t.PositionY + dy * d };
            }
            case AnimationPreset.Rotate or AnimationPreset.RotateCounterclockwise:
            {
                var sign = Preset == AnimationPreset.Rotate ? 1.0 : -1.0;
                return t with
                {
                    Rotation = t.Rotation + sign * RotateDegrees * amount * (isIn ? -1 : 1),
                    ScaleX = t.ScaleX * (1 - amount), ScaleY = t.ScaleY * (1 - amount),
                };
            }
            default:
            {
                // "Shake": a decaying (In) or growing (Out) side-to-side swing; linear envelope so it's visible throughout.
                var envelope = isIn ? 1 - p : p;
                return t with { PositionX = t.PositionX + ShakeAmplitude * envelope * Math.Sin(2 * Math.PI * ShakeSwings * p) };
            }
        }
    }
}
