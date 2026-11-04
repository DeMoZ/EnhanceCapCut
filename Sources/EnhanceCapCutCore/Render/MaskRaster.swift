import Foundation

/// Draws CapCut's clip masks into one 8-bit coverage frame (0 = hidden, 255 = shown), top row first, in the clip's
/// own frame (before crop and the clip transform). Shapes are signed distance fields, so the feather is a linear
/// ramp across the edge. Plain code, ported line by line to C# (`EnhanceCapCut.Render/MaskRaster.cs`).
enum MaskRaster {
    /// Width of the soft edge at feather 1, as a fraction of the frame's shorter side, centred on the edge. Checked
    /// against the Pullups7 export (feather 0.1 masks: frame differences only in the last few pixels of the ramp).
    static let featherScale = 0.5
    /// The mask raster's longer side at most; masks are smooth, so they're drawn smaller than the frame and scaled.
    static let maxSide = 1024
    /// Inner/outer radius of the "Star" (a regular pentagram).
    static let starInnerRatio = 0.381966

    enum Layer {
        case shape(MaskShape, MaskGeometry, inverted: Bool)
        /// A brush frame: `pixels` is `width`×`height`, top row first.
        case bitmap([UInt8], width: Int, height: Int, inverted: Bool)
    }

    /// The raster size for a frame of `width`×`height` pixels.
    static func rasterSize(width: Double, height: Double) -> (width: Int, height: Int) {
        guard width > 0, height > 0 else { return (1, 1) }
        let k = min(1, Double(maxSide) / max(width, height))
        return (max(Int((width * k).rounded()), 1), max(Int((height * k).rounded()), 1))
    }

    /// The union of `layers` (a pixel is shown when any mask shows it).
    static func render(_ layers: [Layer], width: Int, height: Int) -> [UInt8] {
        var out = [UInt8](repeating: 0, count: width * height)
        for layer in layers {
            switch layer {
            case let .shape(shape, g, inverted):
                drawShape(shape, g, inverted: inverted, width: width, height: height, into: &out)
            case let .bitmap(pixels, bw, bh, inverted):
                drawBitmap(pixels, width: bw, height: bh, inverted: inverted, outWidth: width, outHeight: height, into: &out)
            }
        }
        return out
    }

    static func drawShape(_ shape: MaskShape, _ g: MaskGeometry, inverted: Bool, width: Int, height: Int,
                          into out: inout [UInt8]) {
        let w = Double(width), h = Double(height)
        let cx = w / 2 * (1 + g.centerX), cy = h / 2 * (1 - g.centerY)
        let halfW = abs(g.width) * w / 2, halfH = abs(g.height) * h / 2
        let angle = g.rotation * .pi / 180
        let cosA = cos(angle), sinA = sin(angle)
        let soft = max(max(g.feather, 0) * featherScale * min(w, h), 1)
        for y in 0..<height {
            let qy = Double(y) + 0.5 - cy
            for x in 0..<width {
                let qx = Double(x) + 0.5 - cx
                // Into the mask's frame (y down): undo the clockwise rotation.
                let lx = qx * cosA + qy * sinA
                let ly = -qx * sinA + qy * cosA
                let d = distance(shape, lx, ly, halfW: halfW, halfH: halfH, roundCorner: g.roundCorner)
                var a = min(max(0.5 - d / soft, 0), 1)
                if inverted { a = 1 - a }
                let v = UInt8((a * 255).rounded())
                let i = y * width + x
                if v > out[i] { out[i] = v }
            }
        }
    }

    static func drawBitmap(_ pixels: [UInt8], width bw: Int, height bh: Int, inverted: Bool, outWidth: Int, outHeight: Int,
                           into out: inout [UInt8]) {
        guard bw > 0, bh > 0, pixels.count >= bw * bh else { return }
        let sx = Double(bw) / Double(outWidth), sy = Double(bh) / Double(outHeight)
        for y in 0..<outHeight {
            let fy = min(max((Double(y) + 0.5) * sy - 0.5, 0), Double(bh - 1))
            let y0 = Int(fy), y1 = min(y0 + 1, bh - 1), ty = fy - Double(y0)
            for x in 0..<outWidth {
                let fx = min(max((Double(x) + 0.5) * sx - 0.5, 0), Double(bw - 1))
                let x0 = Int(fx), x1 = min(x0 + 1, bw - 1), tx = fx - Double(x0)
                let top = Double(pixels[y0 * bw + x0]) * (1 - tx) + Double(pixels[y0 * bw + x1]) * tx
                let bottom = Double(pixels[y1 * bw + x0]) * (1 - tx) + Double(pixels[y1 * bw + x1]) * tx
                var v = top * (1 - ty) + bottom * ty
                if inverted { v = 255 - v }
                let b = UInt8(min(max(v.rounded(), 0), 255))
                let i = y * outWidth + x
                if b > out[i] { out[i] = b }
            }
        }
    }

    /// Signed distance (raster pixels, negative inside) from a point in the mask's frame (origin at its center,
    /// y down) to the edge of `shape` filling a box of half-size `halfW`×`halfH`.
    static func distance(_ shape: MaskShape, _ x: Double, _ y: Double, halfW: Double, halfH: Double,
                         roundCorner: Double) -> Double {
        switch shape {
        case .line:
            // Keeps what's above the line.
            return y
        case .mirror:
            return abs(y) - halfH
        case .rectangle:
            let r = min(max(roundCorner, 0), 1) * min(halfW, halfH)
            let qx = abs(x) - halfW + r, qy = abs(y) - halfH + r
            return hypot(max(qx, 0), max(qy, 0)) + min(max(qx, qy), 0) - r
        case .circle:
            guard halfW > 0, halfH > 0 else { return 1e9 }
            let k0 = hypot(x / halfW, y / halfH)
            let k1 = hypot(x / (halfW * halfW), y / (halfH * halfH))
            return k1 > 0 ? k0 * (k0 - 1) / k1 : -min(halfW, halfH)
        case .heart:
            // Unit heart: tip at (0, 0), lobes up to y ≈ 1.104, x within ±0.604.
            let sx = 2 * halfW / 1.2071, sy = 2 * halfH / 1.1036
            guard sx > 0, sy > 0 else { return 1e9 }
            return heart(x / sx, -y / sy + 0.5518) * min(sx, sy)
        case .star:
            // Unit star (outer radius 1, a point up): width 1.902, height 1.809, box center 0.0955 above the center.
            let sx = 2 * halfW / 1.9021, sy = 2 * halfH / 1.8090
            guard sx > 0, sy > 0 else { return 1e9 }
            return star(x / sx, -y / sy + 0.0955, inner: starInnerRatio) * min(sx, sy)
        }
    }

    /// Distance to a heart shape (y up, tip at the origin).
    static func heart(_ px: Double, _ py: Double) -> Double {
        let x = abs(px)
        if py + x > 1 {
            return hypot(x - 0.25, py - 0.75) - 2.0.squareRoot() / 4
        }
        let a = x * x + (py - 1) * (py - 1)
        let m = 0.5 * max(x + py, 0)
        let b = (x - m) * (x - m) + (py - m) * (py - m)
        return min(a, b).squareRoot() * (x - py < 0 ? -1 : 1)
    }

    /// Distance to a five-pointed star of outer radius 1 (y up, a point up); `inner` is the inner radius.
    static func star(_ px: Double, _ py: Double, inner: Double) -> Double {
        let k1x = 0.809016994375, k1y = -0.587785252292
        let k2x = -k1x, k2y = k1y
        var x = abs(px), y = py
        var d = max(k1x * x + k1y * y, 0)
        x -= 2 * d * k1x; y -= 2 * d * k1y
        d = max(k2x * x + k2y * y, 0)
        x -= 2 * d * k2x; y -= 2 * d * k2y
        x = abs(x)
        y -= 1
        // Edge from the tip (0, 1) to the inner corner at radius `inner`.
        let bax = inner * -k1y, bay = inner * k1x - 1
        let h = min(max((x * bax + y * bay) / (bax * bax + bay * bay), 0), 1)
        let dist = hypot(x - bax * h, y - bay * h)
        return (y * bax - x * bay) < 0 ? -dist : dist
    }
}

/// CapCut's brush strokes for one mask: `<µs>` files of raw 8-bit frames named by source time, with their size in
/// `data.json` (`data[].width/height`).
enum BrushFrames {
    static func times(in folder: URL) -> [Int64] {
        let names = (try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? []
        return names.compactMap { Int64($0) }.sorted()
    }

    static func size(in folder: URL) -> (width: Int, height: Int)? {
        guard let data = try? Data(contentsOf: folder.appendingPathComponent("data.json")),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let frames = json["data"] as? [[String: Any]], let first = frames.first,
              let w = (first["width"] as? NSNumber)?.intValue, let h = (first["height"] as? NSNumber)?.intValue,
              w > 0, h > 0 else { return nil }
        return (w, h)
    }
}

/// A clip's masks at a given time, as one coverage frame; redrawn only when something changed.
final class ClipMaskFrames {
    let masks: [ClipMask]
    private var brushes: [Int: (times: [Int64], size: (width: Int, height: Int)?, frame: (time: Int64, pixels: [UInt8])?)] = [:]
    private var lastKey: String?
    private(set) var last: (pixels: [UInt8], width: Int, height: Int)?

    init(_ masks: [ClipMask]) {
        self.masks = masks
        for (i, mask) in masks.enumerated() {
            if case let .brush(folder) = mask.kind {
                brushes[i] = (BrushFrames.times(in: folder), BrushFrames.size(in: folder), nil)
            }
        }
    }

    /// The coverage frame at source time `t` for a clip frame of `width`×`height` pixels; `changed` is false when it
    /// is the same as the previous call's.
    func frame(at t: Int64, width: Double, height: Double) -> (pixels: [UInt8], width: Int, height: Int, changed: Bool)? {
        let (w, h) = MaskRaster.rasterSize(width: width, height: height)
        var layers: [MaskRaster.Layer] = []
        var key = "\(w)x\(h)"
        for (i, mask) in masks.enumerated() {
            switch mask.kind {
            case let .shape(shape):
                let g = mask.geometry(at: t)
                layers.append(.shape(shape, g, inverted: mask.inverted))
                key += "|\(shape.rawValue) \(g.centerX) \(g.centerY) \(g.width) \(g.height) \(g.rotation) \(g.feather) \(g.roundCorner) \(mask.inverted)"
            case let .brush(folder):
                guard var brush = brushes[i], !brush.times.isEmpty else { continue }
                let time = CutoutMask.nearest(brush.times, to: t)
                if brush.frame?.time != time {
                    guard let data = try? Data(contentsOf: folder.appendingPathComponent(String(time))) else { continue }
                    if brush.size.map({ $0.width * $0.height }) != data.count {
                        brush.size = CutoutMask.inferSize(bytes: data.count, aspect: CGFloat(width / max(height, 1)))
                    }
                    brush.frame = (time, [UInt8](data))
                    brushes[i] = brush
                }
                guard let size = brush.size, let pixels = brush.frame?.pixels else { continue }
                layers.append(.bitmap(pixels, width: size.width, height: size.height, inverted: mask.inverted))
                key += "|brush \(i) \(time) \(mask.inverted)"
            }
        }
        guard !layers.isEmpty else { return nil }
        if key == lastKey, let last { return (last.pixels, last.width, last.height, false) }
        let pixels = MaskRaster.render(layers, width: w, height: h)
        last = (pixels, w, h)
        lastKey = key
        return (pixels, w, h, true)
    }
}
