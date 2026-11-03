using System.Globalization;

namespace EnhanceCapCut.Draft;

/// <summary>"Chroma key" on a clip: the picked colour and CapCut's sliders (0…1). See FORMAT.md › Chroma key.</summary>
/// <param name="Red">The picked colour, sRGB 0…1.</param>
public sealed record ChromaKey(double Red, double Green, double Blue, double Intensity, double Shadow = 0,
    double EdgeSmooth = 0, double Spill = 0)
{
    /// <summary>Null when CapCut leaves the clip as it is: no colour picked or "Intensity" at 0.</summary>
    public static ChromaKey? From(ChromaMaterial material)
    {
        if (Rgb(material.Color) is not { } rgb || material.Intensity <= 0) return null;
        static double Unit(double v) => Math.Clamp(v, 0, 1);
        return new ChromaKey(rgb.R, rgb.G, rgb.B, Unit(material.Intensity), Unit(material.Shadow),
            Unit(material.EdgeSmooth), Unit(material.Spill));
    }

    /// <summary>"#RRGGBB" or "#AARRGGBB" (the alpha is ignored).</summary>
    public static (double R, double G, double B)? Rgb(string hex)
    {
        var s = hex.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length is not (6 or 8) || !ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return null;
        return (((v >> 16) & 0xFF) / 255.0, ((v >> 8) & 0xFF) / 255.0, (v & 0xFF) / 255.0);
    }
}
