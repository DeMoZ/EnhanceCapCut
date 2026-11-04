using System.Text.Json;

namespace EnhanceCapCut.Draft;

/// <summary>
/// <c>materials.material_animations[]</c>: the intro / outro / loop animations of a text or sticker clip
/// (<c>type</c> "sticker_animation" for both), linked from the segment's <c>extra_material_refs</c>.
/// </summary>
public sealed class AnimationMaterial
{
    public string Id { get; set; } = "";
    public List<AnimationItem> Animations { get; set; } = [];

    public static AnimationMaterial? From(JsonElement c) => c.ValueKind != JsonValueKind.Object ? null : new()
    {
        Id = c.Str("id") ?? "",
        Animations = c.List("animations", AnimationItem.From),
    };
}

/// <summary>One animation of a <c>material_animations</c> entry.</summary>
/// <param name="Type">"in", "out" or "loop".</param>
/// <param name="Name">Display name in the language CapCut used when it was applied ("Fade In", "渐显", …).</param>
/// <param name="ResourceID">The preset's id in CapCut's library.</param>
/// <param name="Duration">µs. For a loop: the length of one cycle.</param>
public sealed record AnimationItem(string Type, string Name, string ResourceID, long Duration)
{
    public static AnimationItem? From(JsonElement c) => c.ValueKind != JsonValueKind.Object ? null : new(
        c.Str("type") ?? "", c.Str("name") ?? "",
        c.Str("resource_id") ?? c.Str("third_resource_id") ?? c.Str("id") ?? "",
        (long)(c.Num("duration") ?? 0));
}
