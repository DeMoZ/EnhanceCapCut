namespace EnhanceCapCut.Draft;

/// <summary>
/// An overlay clip's blend mode (CapCut's "Blend" panel; port of the Swift <c>BlendMode</c>). The draft keeps it as a
/// <c>mix_mode</c> material in <c>materials.effects</c>, referenced from the segment's <c>extra_material_refs</c> (see
/// FORMAT.md). The opacity slider of the same panel is the clip's ordinary opacity (<c>clip.alpha</c>).
/// The modes use the standard separable blend formulas on gamma-encoded sRGB values, composited source-over.
/// </summary>
public enum BlendMode
{
    Normal, Screen, Darken, Lighten, Overlay, HardLight, SoftLight, LinearBurn, ColorBurn, ColorDodge, Multiply,
}

public static class BlendModes
{
    /// <summary>The ids CapCut uses for each mode: its effect id, resource id, internal name and English UI name. The
    /// draft's <c>effect_id</c> / <c>resource_id</c> are matched first, then <c>name</c>.</summary>
    static readonly (BlendMode Mode, string[] Ids)[] CapCutIds =
    [
        (BlendMode.Normal, ["normal"]),
        (BlendMode.Screen, ["871339", "6758325170760323597", "color_filter", "screen"]),
        (BlendMode.Darken, ["871342", "6758324839670354445", "dark_en", "darken"]),
        (BlendMode.Lighten, ["871341", "6758324919789949453", "bright_en", "brighten", "lighten"]),
        (BlendMode.Overlay, ["871340", "6758324989931295240", "over_lay", "overlay"]),
        (BlendMode.HardLight, ["871338", "6758325264670790152", "glare_pc", "hard light"]),
        (BlendMode.SoftLight, ["871337", "6758325439212556814", "soft_light", "soft light"]),
        (BlendMode.LinearBurn, ["871336", "6758325619253056013", "linear_deepening", "linear burn"]),
        (BlendMode.ColorBurn, ["871335", "6758325724848853518", "darken_color", "color burn"]),
        (BlendMode.ColorDodge, ["871334", "6758325800031752712", "color_dodge", "color dodge"]),
        (BlendMode.Multiply, ["871333", "6758325895519277582", "multiply_blend_mode", "multiply"]),
    ];

    /// <summary>The mode of a <c>mix_mode</c> material, null when it isn't one of the known modes.</summary>
    public static BlendMode? From(GenericMaterial material)
    {
        foreach (var key in new[] { material.EffectId, material.ResourceID, material.Name.ToLowerInvariant() })
        {
            if (key.Length == 0) continue;
            foreach (var (mode, ids) in CapCutIds)
            {
                if (ids.Contains(key)) return mode;
            }
        }
        return null;
    }
}
