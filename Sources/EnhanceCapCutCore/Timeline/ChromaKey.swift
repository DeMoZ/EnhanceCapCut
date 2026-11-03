import Foundation

/// "Chroma key" on a clip: the picked colour and CapCut's sliders (0…1). See FORMAT.md › Chroma key.
public struct ChromaKey: Hashable {
    /// The picked colour, sRGB 0…1.
    public var red: Double
    public var green: Double
    public var blue: Double
    public var intensity: Double
    public var shadow: Double
    public var edgeSmooth: Double
    public var spill: Double

    public init(red: Double, green: Double, blue: Double, intensity: Double, shadow: Double = 0,
                edgeSmooth: Double = 0, spill: Double = 0) {
        self.red = red
        self.green = green
        self.blue = blue
        self.intensity = intensity
        self.shadow = shadow
        self.edgeSmooth = edgeSmooth
        self.spill = spill
    }

    /// Nil when CapCut leaves the clip as it is: no colour picked or "Intensity" at 0.
    public init?(_ material: ChromaMaterial) {
        guard let (r, g, b) = Self.rgb(hex: material.color), material.intensity > 0 else { return nil }
        func unit(_ v: Double) -> Double { min(max(v, 0), 1) }
        self.init(red: r, green: g, blue: b, intensity: unit(material.intensity), shadow: unit(material.shadow),
                  edgeSmooth: unit(material.edgeSmooth), spill: unit(material.spill))
    }

    /// "#RRGGBB" or "#AARRGGBB" (the alpha is ignored).
    static func rgb(hex: String) -> (Double, Double, Double)? {
        var s = hex.trimmingCharacters(in: .whitespaces)
        if s.hasPrefix("#") { s.removeFirst() }
        guard s.count == 6 || s.count == 8, let v = UInt64(s, radix: 16) else { return nil }
        return (Double((v >> 16) & 0xFF) / 255, Double((v >> 8) & 0xFF) / 255, Double(v & 0xFF) / 255)
    }
}
