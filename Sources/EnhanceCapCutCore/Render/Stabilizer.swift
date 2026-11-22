import Foundation

// "Stabilize" (`stable.stable_level`): CapCut computes the camera path itself and keeps nothing usable in the
// project (`matrix_path` is empty in the author's drafts), so we estimate the shake ourselves: block matching
// between consecutive downscaled frames gives a motion per frame, the accumulated path is smoothed and
// every frame is moved onto the smooth path, zoomed in so the moved edges stay outside the picture.
//
// Pure code, ported to `windows/src/EnhanceCapCut.Render/Stabilizer.cs`; see FORMAT.md › Stabilization.

/// An 8-bit-range grey image for motion estimation (row-major, top row first).
struct GrayImage {
    let width: Int
    let height: Int
    var pixels: [Float]

    init(width: Int, height: Int, pixels: [Float]) {
        self.width = width
        self.height = height
        self.pixels = pixels
    }

    /// `source` (a luma plane) box-averaged by `factor` in both directions.
    init(downscaling source: UnsafePointer<UInt8>, width: Int, height: Int, rowBytes: Int, factor: Int) {
        let f = max(factor, 1)
        self.width = max(width / f, 1)
        self.height = max(height / f, 1)
        var pixels = [Float](repeating: 0, count: self.width * self.height)
        let scale = 1 / Float(f * f)
        for y in 0..<self.height {
            for x in 0..<self.width {
                var sum = 0
                for dy in 0..<f {
                    let row = source + (y * f + dy) * rowBytes + x * f
                    for dx in 0..<f { sum += Int(row[dx]) }
                }
                pixels[y * self.width + x] = Float(sum) * scale
            }
        }
        self.pixels = pixels
    }

    subscript(x: Int, y: Int) -> Float { pixels[y * width + x] }

    /// Half the size, 2×2 box filter.
    func halved() -> GrayImage {
        let w = max(width / 2, 1), h = max(height / 2, 1)
        var out = [Float](repeating: 0, count: w * h)
        for y in 0..<h {
            for x in 0..<w {
                let x0 = min(2 * x, width - 1), x1 = min(2 * x + 1, width - 1)
                let y0 = min(2 * y, height - 1), y1 = min(2 * y + 1, height - 1)
                out[y * w + x] = (self[x0, y0] + self[x1, y0] + self[x0, y1] + self[x1, y1]) / 4
            }
        }
        return GrayImage(width: w, height: h, pixels: out)
    }

    /// Turned clockwise by `quarterTurns` × 90°.
    func rotated(quarterTurns: Int) -> GrayImage {
        let q = ((quarterTurns % 4) + 4) % 4
        guard q != 0 else { return self }
        let w = q == 2 ? width : height, h = q == 2 ? height : width
        var out = [Float](repeating: 0, count: w * h)
        for y in 0..<h {
            for x in 0..<w {
                let sx: Int, sy: Int
                switch q {
                case 1: (sx, sy) = (y, height - 1 - x)
                case 2: (sx, sy) = (width - 1 - x, height - 1 - y)
                default: (sx, sy) = (width - 1 - y, x)
                }
                out[y * w + x] = self[sx, sy]
            }
        }
        return GrayImage(width: w, height: h, pixels: out)
    }
}

/// A small similarity motion about the image centre, in units of the image height (+x right, +y down, angle in
/// radians clockwise on screen, `logScale` = ln(scale)). Applied to a point p (centred): s·R(angle)·p + (dx, dy).
struct Motion: Equatable {
    var dx: Double = 0
    var dy: Double = 0
    var angle: Double = 0
    var logScale: Double = 0

    static let zero = Motion()

    static func + (a: Motion, b: Motion) -> Motion {
        Motion(dx: a.dx + b.dx, dy: a.dy + b.dy, angle: a.angle + b.angle, logScale: a.logScale + b.logScale)
    }
    static func - (a: Motion, b: Motion) -> Motion {
        Motion(dx: a.dx - b.dx, dy: a.dy - b.dy, angle: a.angle - b.angle, logScale: a.logScale - b.logScale)
    }
    static func * (a: Motion, k: Double) -> Motion {
        Motion(dx: a.dx * k, dy: a.dy * k, angle: a.angle * k, logScale: a.logScale * k)
    }
}

/// Frame-to-frame motion by block matching on an image pyramid and a robust affine fit.
enum MotionEstimator {
    /// Analysis images are box-downscaled so their longer side is about this many pixels.
    static let analysisSize = 320
    /// Blocks on a `grid × grid` lattice over the frame.
    static let grid = 10
    /// Half the side of a matched block, in pixels of each pyramid level.
    static let blockRadius = 6
    /// Pyramid levels (level 2 = ¼ size); the search covers ±`coarseSearch`·4 pixels at full analysis size.
    static let levels = 3
    static let coarseSearch = 6
    static let fineSearch = 2
    /// Blocks flatter than this (standard deviation, 0…255) carry no motion.
    static let minContrast: Float = 3
    /// Fewer usable blocks than this: no motion is measured for the frame.
    static let minBlocks = 8

    /// The box-downscale factor for a frame of `width × height`.
    static func factor(width: Int, height: Int) -> Int { max(1, max(width, height) / analysisSize) }

    /// The motion that takes `previous` to `current` (both the same size), or nil when it can't be measured.
    static func estimate(from previous: GrayImage, to current: GrayImage) -> Motion? {
        guard previous.width == current.width, previous.height == current.height,
              previous.width >= 32, previous.height >= 32 else { return nil }
        var a = [previous], b = [current]
        for _ in 1..<levels {
            a.append(a[a.count - 1].halved())
            b.append(b[b.count - 1].halved())
        }
        let w = previous.width, h = previous.height
        var points: [(x: Double, y: Double, u: Double, v: Double)] = []
        let top = 1 << (levels - 1)
        // Blocks stay inside the coarsest level; shifts that would leave the frame are skipped by `cost`.
        let margin = Double((blockRadius + 2) * top)
        for gy in 0..<grid {
            for gx in 0..<grid {
                let cx = margin + (Double(w) - 2 * margin) * (Double(gx) + 0.5) / Double(grid)
                let cy = margin + (Double(h) - 2 * margin) * (Double(gy) + 0.5) / Double(grid)
                guard contrast(a[0], Int(cx), Int(cy)) >= minContrast else { continue }
                var u = 0, v = 0
                for level in stride(from: levels - 1, through: 0, by: -1) {
                    let k = 1 << level
                    let px = Int(cx) / k, py = Int(cy) / k
                    if level < levels - 1 { u *= 2; v *= 2 }
                    let r = level == levels - 1 ? coarseSearch : fineSearch
                    (u, v) = bestShift(a[level], b[level], px, py, around: (u, v), search: r)
                }
                let (su, sv) = subpixel(a[0], b[0], Int(cx), Int(cy), u, v)
                points.append((cx - Double(w) / 2, cy - Double(h) / 2, Double(u) + su, Double(v) + sv))
            }
        }
        guard var fit = affine(points) else { return nil }
        // Moving subjects (people, water) disagree with the camera motion: drop the worst matches and refit.
        for _ in 0..<3 {
            let residuals = points.map { residual($0, fit) }
            let median = residuals.sorted()[residuals.count / 2]
            let limit = max(median * 2.5, 0.5)
            let kept = points.filter { residual($0, fit) <= limit }
            guard kept.count >= minBlocks, let refit = affine(kept) else { break }
            fit = refit
            if kept.count == points.count { break }
        }
        // Rotation and zoom come from how the shifts change along x only: a rolling shutter makes the shifts of a
        // pan change down the frame (u and v with y), which a similarity fit would read as rotation and zoom.
        let n = Double(h)
        let cosine = 1 + fit.ux, sine = fit.vx
        return Motion(dx: fit.tx / n, dy: fit.ty / n, angle: atan2(sine, cosine), logScale: log(max(hypot(cosine, sine), 1e-6)))
    }

    private static func contrast(_ img: GrayImage, _ cx: Int, _ cy: Int) -> Float {
        var sum: Float = 0, sq: Float = 0, n: Float = 0
        for y in (cy - blockRadius)...(cy + blockRadius) {
            for x in (cx - blockRadius)...(cx + blockRadius) {
                let p = img[x, y]
                sum += p; sq += p * p; n += 1
            }
        }
        let mean = sum / n
        return (max(sq / n - mean * mean, 0)).squareRoot()
    }

    /// Mean-removed sum of squared differences between the block at (cx, cy) in `a` and the block shifted by
    /// (u, v) in `b`; nil when the shifted block leaves `b`.
    private static func cost(_ a: GrayImage, _ b: GrayImage, _ cx: Int, _ cy: Int, _ u: Int, _ v: Int) -> Float? {
        let r = blockRadius
        guard cx - r >= 0, cy - r >= 0, cx + r < a.width, cy + r < a.height,
              cx + u - r >= 0, cy + v - r >= 0, cx + u + r < b.width, cy + v + r < b.height else { return nil }
        var sa: Float = 0, sb: Float = 0
        for y in -r...r {
            for x in -r...r {
                sa += a[cx + x, cy + y]
                sb += b[cx + u + x, cy + v + y]
            }
        }
        let n = Float((2 * r + 1) * (2 * r + 1))
        let offset = (sb - sa) / n
        var total: Float = 0
        for y in -r...r {
            for x in -r...r {
                let d = b[cx + u + x, cy + v + y] - a[cx + x, cy + y] - offset
                total += d * d
            }
        }
        return total
    }

    private static func bestShift(_ a: GrayImage, _ b: GrayImage, _ cx: Int, _ cy: Int, around: (Int, Int),
                                  search: Int) -> (Int, Int) {
        var best = around
        var bestCost = Float.infinity
        for v in (around.1 - search)...(around.1 + search) {
            for u in (around.0 - search)...(around.0 + search) {
                // Ties keep the smaller shift, so flat areas don't drift.
                if let c = cost(a, b, cx, cy, u, v),
                   c < bestCost || (c == bestCost && abs(u) + abs(v) < abs(best.0) + abs(best.1)) {
                    bestCost = c
                    best = (u, v)
                }
            }
        }
        return best
    }

    /// Parabola through the costs around the best integer shift, per axis.
    private static func subpixel(_ a: GrayImage, _ b: GrayImage, _ cx: Int, _ cy: Int, _ u: Int, _ v: Int)
        -> (Double, Double) {
        func vertex(_ m: Float?, _ c: Float?, _ p: Float?) -> Double {
            guard let m, let c, let p else { return 0 }
            let d = m - 2 * c + p
            guard d > 0 else { return 0 }
            return Double(max(-0.5, min(0.5, (m - p) / (2 * d))))
        }
        let c = cost(a, b, cx, cy, u, v)
        return (vertex(cost(a, b, cx, cy, u - 1, v), c, cost(a, b, cx, cy, u + 1, v)),
                vertex(cost(a, b, cx, cy, u, v - 1), c, cost(a, b, cx, cy, u, v + 1)))
    }

    /// An affine fit of the shifts: u = ux·x + uy·y + tx, v = vx·x + vy·y + ty (centred block positions).
    struct Affine {
        var ux = 0.0, uy = 0.0, tx = 0.0, vx = 0.0, vy = 0.0, ty = 0.0
    }

    /// Least squares through the block centres and their shifts.
    private static func affine(_ points: [(x: Double, y: Double, u: Double, v: Double)]) -> Affine? {
        guard points.count >= minBlocks else { return nil }
        let n = Double(points.count)
        let mx = points.reduce(0) { $0 + $1.x } / n, my = points.reduce(0) { $0 + $1.y } / n
        let mu = points.reduce(0) { $0 + $1.u } / n, mv = points.reduce(0) { $0 + $1.v } / n
        var sxx = 0.0, sxy = 0.0, syy = 0.0, sxu = 0.0, syu = 0.0, sxv = 0.0, syv = 0.0
        for p in points {
            let x = p.x - mx, y = p.y - my, u = p.u - mu, v = p.v - mv
            sxx += x * x; sxy += x * y; syy += y * y
            sxu += x * u; syu += y * u; sxv += x * v; syv += y * v
        }
        let det = sxx * syy - sxy * sxy
        guard det > 1e-9 * max(sxx * syy, 1) else { return nil }
        var f = Affine()
        f.ux = (sxu * syy - syu * sxy) / det
        f.uy = (syu * sxx - sxu * sxy) / det
        f.vx = (sxv * syy - syv * sxy) / det
        f.vy = (syv * sxx - sxv * sxy) / det
        f.tx = mu - f.ux * mx - f.uy * my
        f.ty = mv - f.vx * mx - f.vy * my
        return f
    }

    private static func residual(_ p: (x: Double, y: Double, u: Double, v: Double), _ f: Affine) -> Double {
        hypot(f.ux * p.x + f.uy * p.y + f.tx - p.u, f.vx * p.x + f.vy * p.y + f.ty - p.v)
    }
}

/// The per-frame corrections for one clip: frame times (source µs, ascending) and, for each frame, the motion that
/// moves it onto the smoothed camera path, zoom included (so the moved edges stay outside the picture).
struct StabilizationPlan {
    let times: [Int64]
    let corrections: [Motion]

    /// The correction of the frame nearest to source time `t`.
    func correction(at t: Int64) -> Motion {
        guard !times.isEmpty else { return .zero }
        var lo = 0, hi = times.count
        while lo < hi {
            let mid = (lo + hi) / 2
            if times[mid] < t { lo = mid + 1 } else { hi = mid }
        }
        if lo == times.count { return corrections[lo - 1] }
        if lo > 0, t - times[lo - 1] <= times[lo] - t { return corrections[lo - 1] }
        return corrections[lo]
    }
}

enum Stabilizer {
    /// CapCut's levels: 1 = "Least cropping", 2 = "Recommended", 3 = "Most stable" (only 3 seen in the author's
    /// drafts). `smoothing` = Gaussian sigma of the camera path in seconds; `maxZoom` = the most the frame is enlarged
    /// to keep the moved edges out of the picture. The zoom follows the shake, like CapCut's: on ShrimpFishing1
    /// (level 3) a steady pan got 1.035, a shaky close-up 1.11–1.19.
    static func parameters(level: Int) -> (smoothing: Double, maxZoom: Double) {
        switch level {
        case ...1: return (0.3, 1.08)
        case 2: return (0.6, 1.15)
        default: return (1.0, 1.2)
        }
    }

    /// The plan for frames at `times` (µs) with `motions[i]` = motion from frame i−1 to frame i (`motions[0]` is
    /// ignored; nil = not measured, taken as no motion). `aspect` = frame width / height.
    static func plan(times: [Int64], motions: [Motion?], level: Int, aspect: Double) -> StabilizationPlan {
        let (smoothing, maxZoom) = parameters(level: level)
        return plan(times: times, motions: motions, aspect: aspect, smoothing: smoothing, maxZoom: maxZoom)
    }

    static func plan(times: [Int64], motions: [Motion?], aspect: Double, smoothing: Double, maxZoom: Double)
        -> StabilizationPlan {
        let count = min(times.count, motions.count)
        guard count > 0 else { return StabilizationPlan(times: [], corrections: []) }
        var path = [Motion](repeating: .zero, count: count)
        for i in 1..<max(count, 1) { path[i] = path[i - 1] + (motions[i] ?? .zero) }
        let duration = Double(times[count - 1] - times[0]) / 1_000_000
        let fps = count > 1 && duration > 0 ? Double(count - 1) / duration : 30
        let sigma = smoothing * fps
        let smooth = gaussian(path, sigma: sigma)
        let raw = (0..<count).map { smooth[$0] - path[$0] }

        // The zoom: what each frame needs, held over the smoothing window and eased, at most `maxZoom`.
        let need = raw.map { requiredZoom($0, aspect: aspect, limit: maxZoom) }
        let window = max(Int(sigma.rounded()), 1)
        let held = need.indices.map { i in need[max(0, i - window)...min(count - 1, i + window)].max() ?? 1 }
        let zooms = gaussian(held, sigma: sigma / 2).map { min(max($0, 1), maxZoom) }

        // How much of each correction fits in its zoom; eased (lowest over the window first) so the corrections don't
        // jump where the limit bites. Scaling the whole correction keeps its frame-to-frame part, the shake removal.
        let fits = (0..<count).map { clampFactor(raw[$0], zoom: zooms[$0], aspect: aspect) }
        let lowest = fits.indices.map { i in fits[max(0, i - window)...min(count - 1, i + window)].min() ?? 1 }
        let factors = gaussian(lowest, sigma: Double(window) / 2)
        let corrections = (0..<count).map { i in
            var c = raw[i] * factors[i]
            c = c * clampFactor(c, zoom: zooms[i], aspect: aspect)
            c.logScale += log(zooms[i])
            return c
        }
        return StabilizationPlan(times: Array(times.prefix(count)), corrections: corrections)
    }

    /// The least zoom (1…`limit`) at which the frame moved by `m` still covers the picture.
    static func requiredZoom(_ m: Motion, aspect: Double, limit: Double) -> Double {
        if covers(m, zoom: 1, aspect: aspect) { return 1 }
        if !covers(m, zoom: limit, aspect: aspect) { return limit }
        var lo = 1.0, hi = limit
        for _ in 0..<20 {
            let mid = (lo + hi) / 2
            if covers(m, zoom: mid, aspect: aspect) { hi = mid } else { lo = mid }
        }
        return hi
    }

    static func gaussian(_ values: [Double], sigma: Double) -> [Double] {
        gaussian(values.map { Motion(dx: $0) }, sigma: sigma).map(\.dx)
    }

    /// Gaussian smoothing with the kernel renormalized at the ends.
    static func gaussian(_ values: [Motion], sigma: Double) -> [Motion] {
        guard sigma > 0.01, values.count > 1 else { return values }
        let radius = Int((3 * sigma).rounded(.up))
        let weights = (0...radius).map { exp(-Double($0 * $0) / (2 * sigma * sigma)) }
        return values.indices.map { i in
            var sum = Motion.zero, total = 0.0
            for j in max(0, i - radius)...min(values.count - 1, i + radius) {
                let w = weights[abs(i - j)]
                sum = sum + values[j] * w
                total += w
            }
            return sum * (1 / total)
        }
    }

    /// How far (0…1) `correction` can be scaled before the corrected, zoomed frame stops covering the picture.
    static func clampFactor(_ correction: Motion, zoom: Double, aspect: Double) -> Double {
        if covers(correction, zoom: zoom, aspect: aspect) { return 1 }
        var lo = 0.0, hi = 1.0
        for _ in 0..<20 {
            let mid = (lo + hi) / 2
            if covers(correction * mid, zoom: zoom, aspect: aspect) { lo = mid } else { hi = mid }
        }
        return lo
    }

    /// Whether the frame (centred, `aspect` × 1) moved by `m` and enlarged by `zoom` covers its own rectangle.
    static func covers(_ m: Motion, zoom: Double, aspect: Double) -> Bool {
        let s = exp(m.logScale) * zoom
        let c = cos(m.angle), sn = sin(m.angle)
        for (x, y) in [(-aspect / 2, -0.5), (aspect / 2, -0.5), (aspect / 2, 0.5), (-aspect / 2, 0.5)] {
            // The picture corner in frame coordinates: invert p' = s·R·p + d.
            let px = x - m.dx, py = y - m.dy
            let fx = (c * px + sn * py) / s, fy = (-sn * px + c * py) / s
            if abs(fx) > aspect / 2 + 1e-9 || abs(fy) > 0.5 + 1e-9 { return false }
        }
        return true
    }
}
