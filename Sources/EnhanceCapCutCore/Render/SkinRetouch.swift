import CoreImage
import Foundation

/// CapCut's automatic skin retouch ("Sparkly Skin" and the smoothing that comes with it; `effects` "figure" with
/// sub-type "auto_beauty", see FORMAT.md › Retouch and makeup), approximated: skin-coloured pixels are blended with
/// a blurred copy of the frame, which softens wrinkles and pores and keeps everything else.
///
/// Our own approximation, not CapCut's (theirs runs face-parsing and skin models): skin is found by colour only,
/// so skin-coloured things that aren't skin (a ginger beard, wood) are softened too.
final class SkinRetouch {
    private lazy var matteCube: CIFilter? = {
        let filter = CIFilter(name: "CIColorCubeWithColorSpace")
        filter?.setValue(SkinRetouchMath.cubeSize, forKey: "inputCubeDimension")
        let data = SkinRetouchMath.matteCube(size: SkinRetouchMath.cubeSize)
        filter?.setValue(data.withUnsafeBufferPointer { Data(buffer: $0) }, forKey: "inputCubeData")
        filter?.setValue(CGColorSpace(name: CGColorSpace.sRGB), forKey: "inputColorSpace")
        return filter
    }()

    /// `image` with its skin smoothed.
    func apply(_ image: CIImage) -> CIImage {
        let extent = image.extent
        guard extent.width > 0, extent.height > 0, let matteCube else { return image }
        let sigma = SkinRetouchMath.blurRadius * min(extent.width, extent.height)
        let blurred = image.clampedToExtent().applyingGaussianBlur(sigma: sigma).cropped(to: extent)
        matteCube.setValue(image, forKey: kCIInputImageKey)
        guard let matte = matteCube.outputImage else { return image }
        let mask = matte.clampedToExtent().applyingGaussianBlur(sigma: sigma).cropped(to: extent)
        return blurred.applyingFilter("CIBlendWithAlphaMask", parameters: [
            kCIInputBackgroundImageKey: image,
            kCIInputMaskImageKey: mask,
        ]).cropped(to: extent)
    }
}

/// The retouch's colour function, shared with the Windows port (`SkinRetouchMath.cs`), on sRGB values 0…1.
///
/// Calibrated on TwoHoursGay (the only project using it, a 360×640 selfie video): CapCut's export keeps the
/// colours and lowers the face's fine detail (high-pass deviation) by about a quarter relative to the rest of the frame;
/// ours by ~17 %, as strong as it goes before the eyes blur.
enum SkinRetouchMath {
    /// Points per side of the colour cube.
    static let cubeSize = 32
    /// How much of the blurred copy replaces a skin pixel.
    static let strength = 0.5
    /// Blur sigma, as a fraction of the frame's shorter side.
    static let blurRadius = 0.0025
    /// Skin's Cr (BT.601 on sRGB, 0.713·(R − Y′)): soft lower and upper bands.
    static let crRange = (low: 0.02, lowFull: 0.05, highFull: 0.17, high: 0.22)
    /// Skin's −Cb (0.564·(Y′ − B)): above grey, below saturated oranges.
    static let blueDeficitRange = (low: 0.0, lowFull: 0.02, highFull: 0.16, high: 0.22)
    /// Y′: very dark and blown-out pixels are left alone.
    static let lumaRange = (low: 0.08, lowFull: 0.15, highFull: 0.92, high: 0.98)

    static func smoothstep(_ lo: Double, _ hi: Double, _ x: Double) -> Double {
        let t = min(max((x - lo) / max(hi - lo, 1e-9), 0), 1)
        return t * t * (3 - 2 * t)
    }

    static func band(_ x: Double, _ r: (low: Double, lowFull: Double, highFull: Double, high: Double)) -> Double {
        smoothstep(r.low, r.lowFull, x) * (1 - smoothstep(r.highFull, r.high, x))
    }

    /// How much a pixel is smoothed, 0…`strength`.
    static func weight(_ r: Double, _ g: Double, _ b: Double) -> Double {
        let y = 0.299 * r + 0.587 * g + 0.114 * b
        let cb = 0.564 * (b - y), cr = 0.713 * (r - y)
        return strength * band(cr, crRange) * band(-cb, blueDeficitRange) * band(y, lumaRange)
    }

    /// RGBA floats, red changing fastest (Core Image's order): premultiplied white with the pixel's weight.
    static func matteCube(size: Int) -> [Float] {
        var data = [Float](repeating: 0, count: size * size * size * 4)
        let step = 1 / Double(size - 1)
        var i = 0
        for b in 0..<size {
            for g in 0..<size {
                for r in 0..<size {
                    let w = Float(weight(Double(r) * step, Double(g) * step, Double(b) * step))
                    data[i] = w; data[i + 1] = w; data[i + 2] = w; data[i + 3] = w
                    i += 4
                }
            }
        }
        return data
    }
}
