import CoreImage
import Foundation

/// "Chroma key" for one clip (see FORMAT.md › Chroma key): a colour-distance matte, spill removal and edge feather.
///
/// Our own keyer, not CapCut's: pixels are compared with the picked colour in Y′CbCr (BT.601, on sRGB values).
/// Both steps are per-pixel colour functions, so they are baked into colour cubes once per clip; the feather is a
/// blur of the matte. The constants are not calibrated against a CapCut export (none of the author's projects use
/// the feature yet).
final class ChromaKeyFilter {
    let key: ChromaKey
    private lazy var matteCube = Self.cubeFilter(ChromaKeyMath.matteCube(key, size: ChromaKeyMath.cubeSize))
    private lazy var colorCube = key.spill > 0
        ? Self.cubeFilter(ChromaKeyMath.colorCube(key, size: ChromaKeyMath.cubeSize)) : nil

    init(_ key: ChromaKey) { self.key = key }

    /// `image` with the key colour removed (transparent) and its spill taken out of what's kept.
    func apply(_ image: CIImage) -> CIImage {
        let extent = image.extent
        guard extent.width > 0, extent.height > 0, let matteCube else { return image }
        matteCube.setValue(image, forKey: kCIInputImageKey)
        guard var mask = matteCube.outputImage else { return image }
        if key.edgeSmooth > 0 {
            let sigma = ChromaKeyMath.edgeSmoothRadius * key.edgeSmooth * min(extent.width, extent.height)
            mask = mask.clampedToExtent().applyingGaussianBlur(sigma: sigma).cropped(to: extent)
        }
        var kept = image
        if let colorCube {
            colorCube.setValue(image, forKey: kCIInputImageKey)
            kept = colorCube.outputImage ?? image
        }
        return kept.applyingFilter("CIBlendWithAlphaMask", parameters: [
            kCIInputBackgroundImageKey: CIImage.empty(),
            kCIInputMaskImageKey: mask,
        ]).cropped(to: extent)
    }

    private static func cubeFilter(_ data: [Float]) -> CIFilter? {
        let filter = CIFilter(name: "CIColorCubeWithColorSpace")
        filter?.setValue(ChromaKeyMath.cubeSize, forKey: "inputCubeDimension")
        filter?.setValue(data.withUnsafeBufferPointer { Data(buffer: $0) }, forKey: "inputCubeData")
        filter?.setValue(CGColorSpace(name: CGColorSpace.sRGB), forKey: "inputColorSpace")
        return filter
    }
}

/// The keyer's colour functions, shared with the Windows port (`ChromaKeyMath.cs`), on sRGB values 0…1.
enum ChromaKeyMath {
    /// Points per side of the colour cubes.
    static let cubeSize = 64
    /// Distance from the key colour (in CbCr, plus weighted Y′) that is fully removed at "Intensity" 1.
    static let keyRange = 0.30
    /// Width of the soft band between removed and kept, in the same distance.
    static let keySoftness = 0.06
    /// Y′ below which pixels stay opaque at "Shadow" 1 (shadows cast on the screen are kept).
    static let shadowRange = 0.35
    /// Width of the soft band above that Y′.
    static let shadowSoftness = 0.08
    /// Matte blur sigma at "Edge feather" 1, as a fraction of the frame's shorter side.
    static let edgeSmoothRadius = 0.006
    /// Key colours with less CbCr than this are keyed mostly by brightness (white, grey and black screens).
    static let neutralChroma = 0.02
    static let saturatedChroma = 0.12
    /// How much a Y′ difference counts for a saturated key: light and shadow on a green screen are still screen.
    static let saturatedLumaWeight = 0.2

    static func ycc(_ r: Double, _ g: Double, _ b: Double) -> (y: Double, cb: Double, cr: Double) {
        let y = 0.299 * r + 0.587 * g + 0.114 * b
        return (y, 0.564 * (b - y), 0.713 * (r - y))
    }

    static func smoothstep(_ lo: Double, _ hi: Double, _ x: Double) -> Double {
        let t = min(max((x - lo) / max(hi - lo, 1e-9), 0), 1)
        return t * t * (3 - 2 * t)
    }

    /// Opacity of a pixel: 0 = the key colour (removed), 1 = kept.
    static func alpha(_ key: ChromaKey, _ r: Double, _ g: Double, _ b: Double) -> Double {
        guard key.intensity > 0 else { return 1 }
        let k = ycc(key.red, key.green, key.blue), p = ycc(r, g, b)
        let keyChroma = (k.cb * k.cb + k.cr * k.cr).squareRoot()
        let lumaWeight = 1 - (1 - saturatedLumaWeight) * smoothstep(neutralChroma, saturatedChroma, keyChroma)
        let dcb = p.cb - k.cb, dcr = p.cr - k.cr, dy = lumaWeight * (p.y - k.y)
        let distance = (dcb * dcb + dcr * dcr + dy * dy).squareRoot()
        let removed = keyRange * key.intensity
        var a = smoothstep(removed, removed + keySoftness, distance)
        if key.shadow > 0 {
            let dark = shadowRange * key.shadow
            a = max(a, 1 - smoothstep(dark, dark + shadowSoftness, p.y))
        }
        return a
    }

    /// The pixel with the key colour's cast taken out ("Spill"): its CbCr along the key's hue is reduced.
    static func despill(_ key: ChromaKey, _ r: Double, _ g: Double, _ b: Double) -> (Double, Double, Double) {
        let k = ycc(key.red, key.green, key.blue)
        let keyChroma = (k.cb * k.cb + k.cr * k.cr).squareRoot()
        guard key.spill > 0, keyChroma >= neutralChroma else { return (r, g, b) }
        var p = ycc(r, g, b)
        let ux = k.cb / keyChroma, uy = k.cr / keyChroma
        let along = p.cb * ux + p.cr * uy
        guard along > 0 else { return (r, g, b) }
        p.cb -= ux * along * key.spill
        p.cr -= uy * along * key.spill
        let r2 = p.y + p.cr / 0.713, b2 = p.y + p.cb / 0.564
        let g2 = (p.y - 0.299 * r2 - 0.114 * b2) / 0.587
        return (min(max(r2, 0), 1), min(max(g2, 0), 1), min(max(b2, 0), 1))
    }

    /// RGBA floats, red changing fastest (Core Image's order): premultiplied white with the pixel's opacity.
    static func matteCube(_ key: ChromaKey, size: Int) -> [Float] {
        cube(size) { r, g, b in
            let a = Float(alpha(key, r, g, b))
            return (a, a, a, a)
        }
    }

    /// RGBA floats, red changing fastest: the despilled colour, opaque.
    static func colorCube(_ key: ChromaKey, size: Int) -> [Float] {
        cube(size) { r, g, b in
            let c = despill(key, r, g, b)
            return (Float(c.0), Float(c.1), Float(c.2), 1)
        }
    }

    private static func cube(_ size: Int, _ f: (Double, Double, Double) -> (Float, Float, Float, Float)) -> [Float] {
        var data = [Float](repeating: 0, count: size * size * size * 4)
        let step = 1 / Double(size - 1)
        var i = 0
        for b in 0..<size {
            for g in 0..<size {
                for r in 0..<size {
                    let v = f(Double(r) * step, Double(g) * step, Double(b) * step)
                    data[i] = v.0; data[i + 1] = v.1; data[i + 2] = v.2; data[i + 3] = v.3
                    i += 4
                }
            }
        }
        return data
    }
}
