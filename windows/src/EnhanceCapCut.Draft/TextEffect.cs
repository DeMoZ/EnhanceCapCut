namespace EnhanceCapCut.Draft;

/// <summary>A soft halo drawn around the glyphs (and their outline): our approximation of CapCut's "… Glow" text
/// effects. <paramref name="Color"/> is RGB in 0…1, <paramref name="Radius"/> the blur radius relative to the font
/// size.</summary>
public sealed record TextGlow(IReadOnlyList<double> Color, double Radius);

/// <summary>CapCut text effects ("word art", <c>materials.effects</c> of type <c>text_effect</c>) are packages of
/// shaders and textures downloaded into CapCut's cache; we don't run them. A text uses one only when its content
/// style has an <c>effectStyle</c>: a segment can still reference an effect material after the style was reset, and
/// CapCut then draws plain text (checked on the author's exports). For effects that are in use, the common looks are
/// approximated from the effect's name; the rest stay in the report by name. Twin of Swift's
/// <c>TextEffectLook</c>.</summary>
public static class TextEffectLook
{
    /// <summary>Measured on the "ART Artistic EN Yellow Glow" export: the bright rim fades out about an eighth of the
    /// font size beyond the outline.</summary>
    public const double GlowRadius = 0.12;

    /// <summary>Colour words found in effect names, in the order they're checked.</summary>
    static readonly (string Word, double[] Color)[] ColorWords =
    [
        ("yellow", [1.0, 0.86, 0.1]), ("gold", [1.0, 0.78, 0.2]), ("orange", [1.0, 0.55, 0.1]),
        ("red", [1.0, 0.15, 0.15]), ("pink", [1.0, 0.4, 0.75]), ("purple", [0.65, 0.3, 1.0]),
        ("violet", [0.65, 0.3, 1.0]), ("blue", [0.2, 0.5, 1.0]), ("cyan", [0.1, 0.9, 1.0]),
        ("green", [0.2, 1.0, 0.35]), ("white", [1.0, 1.0, 1.0]),
    ];

    /// <summary>The glow for an effect named like "ART Artistic EN Yellow Glow"; null when the name isn't a glow.
    /// Without a colour word the glow takes <paramref name="fill"/>, the text's own colour.</summary>
    public static TextGlow? Glow(string name, IReadOnlyList<double>? fill)
    {
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        foreach (var ch in name.ToLowerInvariant() + " ")
        {
            if (char.IsLetterOrDigit(ch)) { word.Append(ch); continue; }
            if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); }
        }
        if (!words.Contains("glow")) return null;
        IReadOnlyList<double> color = ColorWords.FirstOrDefault(c => words.Contains(c.Word)).Color ?? fill ?? [1.0, 1.0, 1.0];
        return new TextGlow(color, GlowRadius);
    }
}
