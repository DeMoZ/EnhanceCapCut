import CoreImage
import XCTest
@testable import EnhanceCapCutCore

final class StabilizationTests: XCTestCase {
    /// A deterministic textured frame.
    static func texture(width: Int, height: Int) -> GrayImage {
        var pixels = [Float](repeating: 0, count: width * height)
        var seed: UInt32 = 12345
        var blobs: [(Double, Double, Double, Float)] = []
        for _ in 0..<160 {
            func next() -> Double {
                seed = seed &* 1_664_525 &+ 1_013_904_223
                return Double(seed >> 8) / Double(1 << 24)
            }
            blobs.append((next() * Double(width), next() * Double(height), 2 + next() * 6, Float(next() * 200 - 100)))
        }
        for y in 0..<height {
            for x in 0..<width {
                var v: Float = 128
                for (bx, by, r, a) in blobs {
                    let d2 = ((Double(x) - bx) * (Double(x) - bx) + (Double(y) - by) * (Double(y) - by)) / (r * r)
                    if d2 < 9 { v += a * Float(exp(-d2)) }
                }
                pixels[y * width + x] = min(max(v, 0), 255)
            }
        }
        return GrayImage(width: width, height: height, pixels: pixels)
    }

    static func ciImage(_ g: GrayImage) -> CIImage {
        let bytes = g.pixels.map { UInt8(min(max($0.rounded(), 0), 255)) }
        return CIImage(bitmapData: Data(bytes), bytesPerRow: g.width, size: CGSize(width: g.width, height: g.height),
                       format: .L8, colorSpace: nil)
    }

    static func gray(_ image: CIImage) -> GrayImage {
        let w = Int(image.extent.width), h = Int(image.extent.height)
        var bytes = [UInt8](repeating: 0, count: w * h)
        CIContext(options: [.workingColorSpace: NSNull()]).render(
            image, toBitmap: &bytes, rowBytes: w, bounds: image.extent, format: .L8, colorSpace: nil)
        return GrayImage(width: w, height: h, pixels: bytes.map(Float.init))
    }

    /// The estimator and the renderer agree on what a motion means: moving a frame by m reads back as m.
    func testEstimatedMotionMatchesTheAppliedOne() {
        let a = Self.texture(width: 320, height: 180)
        for m in [Motion(dx: 0.03, dy: -0.02), Motion(dx: -0.01, dy: 0.015, angle: 0.02),
                  Motion(dx: 0, dy: 0, angle: -0.015, logScale: 0.02)] {
            let b = Self.gray(VideoStabilization.apply(m, to: Self.ciImage(a)))
            guard let e = MotionEstimator.estimate(from: a, to: b) else { return XCTFail("no motion for \(m)") }
            XCTAssertEqual(e.dx, m.dx, accuracy: 0.002, "\(m)")
            XCTAssertEqual(e.dy, m.dy, accuracy: 0.002, "\(m)")
            XCTAssertEqual(e.angle, m.angle, accuracy: 0.003, "\(m)")
            XCTAssertEqual(e.logScale, m.logScale, accuracy: 0.003, "\(m)")
        }
        XCTAssertNil(MotionEstimator.estimate(from: GrayImage(width: 64, height: 64, pixels: .init(repeating: 9, count: 4096)),
                                              to: GrayImage(width: 64, height: 64, pixels: .init(repeating: 9, count: 4096))),
                     "a flat frame has no measurable motion")
    }

    func testPlanRemovesShakeAndKeepsThePictureCovered() {
        // A slow pan with a fast shake on top, 30 fps.
        let count = 150
        let times = (0..<count).map { Int64($0) * 33_333 }
        let motions: [Motion?] = (0..<count).map { (i: Int) -> Motion? in
            guard i > 0 else { return nil }
            let t = Double(i)
            let dx: Double = 0.002 + 0.006 * sin(t * 1.9)
            let dy: Double = 0.005 * cos(t * 2.3)
            let angle: Double = 0.004 * sin(t * 1.3)
            return Motion(dx: dx, dy: dy, angle: angle)
        }
        func jitter(_ path: [Motion]) -> Double {
            (1..<(path.count - 1)).map { k in
                let s = path[k + 1] - path[k] * 2 + path[k - 1]
                return s.dx * s.dx + s.dy * s.dy
            }.reduce(0, +).squareRoot()
        }
        var path = [Motion.zero]
        for i in 1..<count { path.append(path[i - 1] + motions[i]!) }
        for level in 1...3 {
            let plan = Stabilizer.plan(times: times, motions: motions, level: level, aspect: 16.0 / 9)
            XCTAssertEqual(plan.corrections.count, count)
            let out = (0..<count).map { i in
                var m = path[i] + plan.corrections[i]
                m.logScale = 0
                return m
            }
            XCTAssertLessThan(jitter(out), jitter(path) / 4, "level \(level)")
            for c in plan.corrections {
                XCTAssertTrue(Stabilizer.covers(c, zoom: 1, aspect: 16.0 / 9), "level \(level): edges show")
                XCTAssertLessThanOrEqual(exp(c.logScale), Stabilizer.parameters(level: level).maxZoom + 0.01)
            }
            XCTAssertEqual(plan.correction(at: 40_000).dx, plan.corrections[1].dx, "nearest frame")
        }
    }

    func testGrayImageHelpers() {
        let g = GrayImage(width: 3, height: 2, pixels: [1, 2, 3, 4, 5, 6])
        XCTAssertEqual(g.rotated(quarterTurns: 1).pixels, [4, 1, 5, 2, 6, 3], "clockwise")
        XCTAssertEqual(g.rotated(quarterTurns: 2).pixels, [6, 5, 4, 3, 2, 1])
        XCTAssertEqual(g.rotated(quarterTurns: 3).pixels, [3, 6, 2, 5, 1, 4])
        let bytes: [UInt8] = [0, 10, 20, 30, 40, 50, 60, 70]
        let small = bytes.withUnsafeBufferPointer {
            GrayImage(downscaling: $0.baseAddress!, width: 4, height: 2, rowBytes: 4, factor: 2)
        }
        XCTAssertEqual(small.pixels, [25, 45])
    }

    func testStabilizedVideoClipsAreNotReported() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("ecc-stable-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let video = dir.appendingPathComponent("v.mov"), photo = dir.appendingPathComponent("p.png")
        try Data([0]).write(to: video)
        try Data([0]).write(to: photo)
        let json = """
        {"tracks": [{"type": "video", "segments": [
           {"id": "S1", "material_id": "V1", "target_timerange": {"start": 0, "duration": 1000000}},
           {"id": "S2", "material_id": "V2", "target_timerange": {"start": 1000000, "duration": 1000000}},
           {"id": "S3", "material_id": "V3", "target_timerange": {"start": 2000000, "duration": 1000000}}]}],
         "materials": {"videos": [
           {"id": "V1", "type": "video", "path": "\(video.path)", "stable": {"stable_level": 3, "matrix_path": ""}},
           {"id": "V2", "type": "video", "path": "\(video.path)"},
           {"id": "V3", "type": "photo", "path": "\(photo.path)", "stable": {"stable_level": 2}}]}}
        """
        let draft = try JSONDecoder().decode(Draft.self, from: Data(json.utf8))
        let timeline = TimelineBuilder.build(draft: draft, projectURL: dir)
        XCTAssertEqual(timeline.layers.map(\.stabilization), [3, 0, 0])
        XCTAssertEqual(timeline.report.unsupported.map { "\($0.category): \($0.name)" }, ["Stabilization: level 2"])
    }
}
