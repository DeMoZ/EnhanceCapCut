import CoreGraphics
import CoreImage
import Foundation
import ImageIO

/// A filter's lookup table as a Core Image colour cube, with the filter's intensity baked in (the cube is blended
/// with the identity, which equals blending the filtered frame with the original).
final class ColorLUT {
    let size: Int
    /// RGBA float entries, red fastest, then green, then blue (`CIColorCube` order).
    let data: Data

    init(size: Int, data: Data) {
        self.size = size
        self.data = data
    }

    /// Reads `filter.lut` (a `.cube` file or a PNG lookup image); nil when it can't be read.
    convenience init?(_ filter: ColorFilter) {
        guard let table = filter.lut.pathExtension.lowercased() == "cube"
            ? ColorLUT.readCube(filter.lut) : ColorLUT.readImage(filter.lut) else { return nil }
        self.init(size: table.size, data: ColorLUT.cube(table.size, table.rgb, intensity: filter.intensity))
    }

    /// Entries as RGB triples in 0...1 (red fastest) → `CIColorCube` data at `intensity`.
    static func cube(_ n: Int, _ rgb: [Float], intensity: Double) -> Data {
        let a = Float(min(max(intensity, 0), 1))
        var out = [Float](repeating: 1, count: n * n * n * 4)
        for b in 0..<n {
            for g in 0..<n {
                for r in 0..<n {
                    let i = (b * n + g) * n + r
                    let id = [Float(r), Float(g), Float(b)].map { $0 / Float(n - 1) }
                    for c in 0..<3 { out[i * 4 + c] = id[c] + (rgb[i * 3 + c] - id[c]) * a }
                }
            }
        }
        return out.withUnsafeBufferPointer { Data(buffer: $0) }
    }

    static func readCube(_ url: URL) -> (size: Int, rgb: [Float])? {
        guard let text = try? String(contentsOf: url, encoding: .utf8) else { return nil }
        return parseCube(text)
    }

    /// An Adobe/Resolve `.cube` 3D table (`LUT_3D_SIZE N`, then N³ lines of `r g b`, red fastest).
    static func parseCube(_ text: String) -> (size: Int, rgb: [Float])? {
        var n = 0
        var rgb: [Float] = []
        for line in text.split(whereSeparator: \.isNewline) {
            let parts = line.split(whereSeparator: \.isWhitespace)
            guard let first = parts.first, !first.hasPrefix("#") else { continue }
            if first == "LUT_3D_SIZE", parts.count == 2 { n = Int(parts[1]) ?? 0; continue }
            if parts.count == 3, let r = Float(parts[0]), let g = Float(parts[1]), let b = Float(parts[2]) {
                rgb += [r, g, b]
            }
        }
        guard n >= 2, n <= ColorFilter.maxSize, rgb.count == n * n * n * 3 else { return nil }
        return (n, rgb)
    }

    static func readImage(_ url: URL) -> (size: Int, rgb: [Float])? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil),
              let layout = ColorFilter.layout(width: image.width, height: image.height) else { return nil }
        let w = image.width, h = image.height
        var pixels = [UInt8](repeating: 0, count: w * h * 4)
        // Draw in the image's own space (lookup images are untagged sRGB) so the entries aren't colour-converted.
        let space = image.colorSpace.flatMap { $0.model == .rgb ? $0 : nil } ?? CGColorSpace(name: CGColorSpace.sRGB)!
        let drawn = pixels.withUnsafeMutableBytes { buffer -> Bool in
            guard let context = CGContext(data: buffer.baseAddress, width: w, height: h, bitsPerComponent: 8,
                                          bytesPerRow: w * 4, space: space,
                                          bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { return false }
            context.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
            return true
        }
        guard drawn else { return nil }
        return (layout.size, table(pixels, width: w, layout: layout))
    }

    /// The table in RGBA8 pixels (top row first) laid out as `layout`.
    static func table(_ pixels: [UInt8], width: Int, layout: ColorFilter.Layout) -> [Float] {
        let n = layout.size
        var rgb = [Float](repeating: 0, count: n * n * n * 3)
        for b in 0..<n {
            for g in 0..<n {
                for r in 0..<n {
                    let p = layout.pixel(r: r, g: g, b: b)
                    let o = (p.y * width + p.x) * 4
                    let i = ((b * n + g) * n + r) * 3
                    for c in 0..<3 { rgb[i + c] = Float(pixels[o + c]) / 255 }
                }
            }
        }
        return rgb
    }

    /// `image` through the table. The table is made for sRGB-encoded values, so it's applied in sRGB.
    func apply(to image: CIImage) -> CIImage {
        image.applyingFilter("CIColorCubeWithColorSpace", parameters: [
            "inputCubeDimension": size,
            "inputCubeData": data,
            "inputColorSpace": CGColorSpace(name: CGColorSpace.sRGB)!,
        ])
    }
}
