import Foundation

/// A soft halo drawn around the glyphs (and their outline): our approximation of CapCut's "… Glow" text effects.
public struct TextGlow: Equatable {
    /// RGB in 0...1.
    public var color: [Double]
    /// Blur radius relative to the font size.
    public var radius: Double

    public init(color: [Double], radius: Double = TextEffectLook.glowRadius) {
        self.color = color
        self.radius = radius
    }
}

/// CapCut text effects ("word art", `materials.effects` of type `text_effect`) are packages of shaders and textures
/// downloaded into CapCut's cache; we don't run them. A text uses one only when its content style has an
/// `effectStyle`: a segment can still reference an effect material after the style was reset, and CapCut then draws
/// plain text (checked on the author's exports). For effects that are in use, the common looks are approximated
/// from the effect's name; the rest stay in the report by name.
public enum TextEffectLook {
    /// Measured on the "ART Artistic EN Yellow Glow" export: the bright rim fades out about an eighth of the font size
    /// beyond the outline.
    public static let glowRadius = 0.12

    /// Colour words found in effect names, in the order they're checked ("yellow" before "red" and so on doesn't
    /// matter: names hold one colour per glow).
    static let colorWords: [(String, [Double])] = [
        ("yellow", [1.0, 0.86, 0.1]), ("gold", [1.0, 0.78, 0.2]), ("orange", [1.0, 0.55, 0.1]),
        ("red", [1.0, 0.15, 0.15]), ("pink", [1.0, 0.4, 0.75]), ("purple", [0.65, 0.3, 1.0]),
        ("violet", [0.65, 0.3, 1.0]), ("blue", [0.2, 0.5, 1.0]), ("cyan", [0.1, 0.9, 1.0]),
        ("green", [0.2, 1.0, 0.35]), ("white", [1.0, 1.0, 1.0]),
    ]

    /// The glow for an effect named like "ART Artistic EN Yellow Glow"; nil when the name isn't a glow.
    /// Without a colour word the glow takes `fill`, the text's own colour.
    public static func glow(named name: String, fill: [Double]?) -> TextGlow? {
        let words = name.lowercased().split(whereSeparator: { !$0.isLetter && !$0.isNumber }).map(String.init)
        guard words.contains("glow") else { return nil }
        let color = colorWords.first(where: { words.contains($0.0) })?.1 ?? fill ?? [1, 1, 1]
        return TextGlow(color: color)
    }
}
