namespace EnhanceCapCut.Draft;

public enum TransitionKind
{
    /// <summary>CapCut's "Flashback": a zoom-and-spin blur that peaks at the cut, dissolving into the next clip.</summary>
    Flashback,
    /// <summary>Every other transition, approximated by a cross-fade.</summary>
    CrossFade,
}

/// <summary>
/// A transition from a clip into the next clip on its track (<c>materials.transitions</c>, referenced from the
/// outgoing clip's <c>extra_material_refs</c>). CapCut centres it on the cut: the outgoing clip keeps playing until
/// the cut and the incoming one starts there, each holding its edge frame for the other half (see FORMAT.md).
/// </summary>
public sealed record LayerTransition(string Name, TransitionKind Kind, VisualLayer Next, TimeRange Range)
{
    public bool Contains(long t) => t >= Range.Start && t < Range.End;

    /// <summary>0 at the start, 0.5 at the cut, 1 at the end.</summary>
    public double Progress(long t) =>
        Range.Duration <= 0 ? 1 : Math.Clamp((double)(t - Range.Start) / Range.Duration, 0, 1);

    /// <summary>"Flashback"'s effect id in CapCut's library.</summary>
    const string FlashbackID = "7252586720078598658";
    /// <summary>Clips closer than this (µs) count as adjacent.</summary>
    const long Adjacency = 40_000;

    public static TransitionKind KindOf(string name, string resourceID) =>
        name == "Flashback" || resourceID == FlashbackID ? TransitionKind.Flashback : TransitionKind.CrossFade;

    /// <summary>The transition from <paramref name="from"/> into <paramref name="next"/>, or null when they don't
    /// touch. CapCut keeps a transition within half of each clip; so do we.</summary>
    public static LayerTransition? Make(string name, string resourceID, long duration, VisualLayer from, VisualLayer next)
    {
        var cut = from.Target.End;
        if (Math.Abs(next.Target.Start - cut) > Adjacency) return null;
        var half = Math.Min(duration / 2, Math.Min(from.Target.Duration / 2, next.Target.Duration / 2));
        if (half <= 0) return null;
        return new(name, KindOf(name, resourceID), next, new TimeRange(cut - half, 2 * half));
    }
}

/// <summary>
/// Our own "Flashback" look, calibrated by eye against CapCut's exports (ATV): both clips zoom in and turn a little
/// while a zoom-and-spin blur builds up to the cut and fades out after it; the picture dissolves around the cut.
/// </summary>
public static class FlashbackLook
{
    /// <summary>Copies averaged for the blur.</summary>
    public const int BlurCopies = 16;
    /// <summary>Zoom at the peak (fraction of the frame) and the extra zoom the blur spreads over.</summary>
    public const double Zoom = 0.2;
    public const double BlurZoom = 0.25;
    /// <summary>Rotation at the peak and the extra rotation the blur spreads over, degrees clockwise.</summary>
    public const double Rotation = 6.0;
    public const double BlurRotation = 14.0;
    /// <summary>The dissolve into the next clip runs over this part of the transition, centred on the cut.</summary>
    public const double DissolveStart = 0.35;
    public const double DissolveEnd = 0.65;

    /// <summary>Strength of the effect: 0 at the edges, 1 at the cut.</summary>
    public static double Strength(double p) => Math.Sin(Math.PI * Math.Clamp(p, 0, 1));

    /// <summary>How much of the next clip shows.</summary>
    public static double Mix(double p)
    {
        var x = Math.Clamp((p - DissolveStart) / (DissolveEnd - DissolveStart), 0, 1);
        return x * x * (3 - 2 * x);
    }

    /// <summary>Scale and clockwise rotation (degrees) about the frame centre of blur copy <paramref name="i"/> at
    /// progress <paramref name="p"/>.</summary>
    public static (double Scale, double Rotation) Copy(int i, double p)
    {
        var s = Strength(p);
        var f = BlurCopies > 1 ? (double)i / (BlurCopies - 1) : 0;
        return (1 + (Zoom + BlurZoom * f) * s, (Rotation + BlurRotation * f) * s);
    }
}
