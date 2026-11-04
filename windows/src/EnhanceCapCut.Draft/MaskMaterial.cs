using System.Text.Json;

namespace EnhanceCapCut.Draft;

/// <summary>A clip mask (<c>materials.common_mask[]</c>), see FORMAT.md › Masks. Port of the macOS MaskMaterial.</summary>
public sealed class MaskMaterial
{
    public string Id { get; set; } = "";
    /// <summary>Mask keyframes point at this id (<c>common_keyframes[].material_id</c>).</summary>
    public string ConstantId { get; set; } = "";
    /// <summary>"Split", "Mirror", "Circle", "Rectangle", "Heart", "Star", "Brush".</summary>
    public string Name { get; set; } = "";
    /// <summary><c>line</c>, <c>mirror</c>, <c>circle</c>, <c>rectangle</c>, <c>geometric_shape</c>, <c>custom</c> (brush).</summary>
    public string ResourceType { get; set; } = "";
    /// <summary>The brush strokes' folder, usually <c>##_draftpath_placeholder_…_##/adjust_mask/&lt;id&gt;</c>.</summary>
    public string LoaderWorkSpace { get; set; } = "";
    /// <summary>Center in half-material units (+y up), size as fractions of the material's width and height.</summary>
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double Width { get; set; } = 0.5;
    public double Height { get; set; } = 0.5;
    /// <summary>Degrees, clockwise.</summary>
    public double Rotation { get; set; }
    /// <summary>0…1.</summary>
    public double Feather { get; set; }
    public double Expansion { get; set; }
    /// <summary>0…1 of the shorter side's half.</summary>
    public double RoundCorner { get; set; }
    public bool Invert { get; set; }

    public static MaskMaterial? From(JsonElement c)
    {
        if (c.ValueKind != JsonValueKind.Object || c.Str("id") is not { } id) return null;
        var config = c.Obj("config");
        double Num(string key, double fallback) => config is { } o ? o.Num(key) ?? fallback : fallback;
        return new()
        {
            Id = id,
            ConstantId = c.Str("constant_material_id") ?? "",
            Name = c.Str("name") ?? "",
            ResourceType = c.Str("resource_type") ?? "",
            LoaderWorkSpace = c.Str("loader_work_space") ?? "",
            CenterX = Num("centerX", 0),
            CenterY = Num("centerY", 0),
            Width = Num("width", 0.5),
            Height = Num("height", 0.5),
            Rotation = Num("rotation", 0),
            Feather = Num("feather", 0),
            Expansion = Num("expansion", 0),
            RoundCorner = Num("roundCorner", 0),
            Invert = config?.Bool("invert") ?? false,
        };
    }
}
