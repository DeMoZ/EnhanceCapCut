namespace EnhanceCapCut.Draft;

/// <summary>The shapes of CapCut's clip masks (see FORMAT.md › Masks). Port of the macOS ClipMask.swift.</summary>
public enum MaskShape
{
    /// <summary>"Split": a straight edge; the part above the line (before rotation) is kept.</summary>
    Line,
    /// <summary>"Mirror": a band <c>Height</c> tall.</summary>
    Mirror,
    Circle,
    Rectangle,
    Heart,
    Star,
}

/// <summary>
/// A mask's placement, in the clip's own frame (before the clip transform): center in half-frame units (+y up), size
/// as fractions of the frame's width and height, rotation in degrees clockwise, feather 0…1, round corner 0…1 of half
/// the shorter side.
/// </summary>
public sealed record MaskGeometry(double CenterX = 0, double CenterY = 0, double Width = 0.5, double Height = 0.5,
    double Rotation = 0, double Feather = 0, double RoundCorner = 0);

/// <summary>Mask properties that can be keyframed: <c>KFTypeCommonMask&lt;name&gt;</c> (older drafts: <c>KFTypeMask&lt;name&gt;</c>).</summary>
public enum MaskProperty
{
    PositionX,
    PositionY,
    Width,
    Height,
    Rotation,
    Feather,
    RoundCorner,
    /// <summary>Read so it isn't reported as unknown; a non-zero expansion is reported instead.</summary>
    Expansion,
}

public static class MaskProperties
{
    static readonly Dictionary<string, MaskProperty> Names = new()
    {
        ["PositionX"] = MaskProperty.PositionX, ["PositionY"] = MaskProperty.PositionY,
        ["SizeWidth"] = MaskProperty.Width, ["SizeHeight"] = MaskProperty.Height,
        ["Rotation"] = MaskProperty.Rotation, ["Feather"] = MaskProperty.Feather,
        ["RoundCorner"] = MaskProperty.RoundCorner, ["Expansion"] = MaskProperty.Expansion,
    };

    /// <summary>The property of a keyframe group's <c>property_type</c>, null when it isn't a known mask property.</summary>
    public static MaskProperty? FromType(string propertyType)
    {
        foreach (var prefix in (string[])["KFTypeCommonMask", "KFTypeMask"])
        {
            if (propertyType.StartsWith(prefix, StringComparison.Ordinal))
                return Names.TryGetValue(propertyType[prefix.Length..], out var p) ? p : null;
        }
        return null;
    }

    public static bool IsMask(string propertyType) =>
        propertyType.StartsWith("KFTypeCommonMask", StringComparison.Ordinal) || propertyType.StartsWith("KFTypeMask", StringComparison.Ordinal);
}

/// <summary>
/// One of a clip's masks. A clip can have several; what any of them keeps is shown. <see cref="Shape"/> is null for a
/// brush, whose strokes are in <see cref="BrushFolder"/> (one raw 8-bit frame per file named by its source time in µs).
/// Keyframes are on the clip's source time axis (µs), like CapCut stores them.
/// </summary>
public sealed record ClipMask(MaskShape? Shape, string? BrushFolder, MaskGeometry Geometry, bool Inverted,
    IReadOnlyDictionary<MaskProperty, KeyframeTrack> Keyframes)
{
    /// <summary>The geometry at source time <paramref name="t"/> (µs), with keyframes applied.</summary>
    public MaskGeometry GeometryAt(long t)
    {
        double Kf(MaskProperty p, double value) => Keyframes.TryGetValue(p, out var track) ? track.Value(t) : value;
        var g = Geometry;
        return new MaskGeometry(Kf(MaskProperty.PositionX, g.CenterX), Kf(MaskProperty.PositionY, g.CenterY),
            Kf(MaskProperty.Width, g.Width), Kf(MaskProperty.Height, g.Height), Kf(MaskProperty.Rotation, g.Rotation),
            Kf(MaskProperty.Feather, g.Feather), Kf(MaskProperty.RoundCorner, g.RoundCorner));
    }

    /// <summary>The shape of a mask material's <c>resource_type</c> (and name, for the geometric shapes).</summary>
    public static MaskShape? ShapeOf(string resourceType, string name)
    {
        switch (resourceType)
        {
            case "line": return MaskShape.Line;
            case "mirror": return MaskShape.Mirror;
            case "circle": return MaskShape.Circle;
            case "rectangle": return MaskShape.Rectangle;
            case "heart": return MaskShape.Heart;
            case "star": return MaskShape.Star;
            case "geometric_shape" or "love" or "pentagram":
            {
                var n = name.ToLowerInvariant();
                if (n.Contains("heart") || resourceType == "love") return MaskShape.Heart;
                if (n.Contains("star") || resourceType == "pentagram") return MaskShape.Star;
                return null;
            }
            default: return null;
        }
    }

    /// <summary>Brush frame times in a strokes folder: its files named by a number, ascending.</summary>
    public static List<long> BrushTimes(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder)
            .Select(f => long.TryParse(Path.GetFileName(f), out var t) ? t : (long?)null)
            .OfType<long>().Order().ToList();
    }
}
