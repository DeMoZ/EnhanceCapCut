import CoreImage
import XCTest
@testable import EnhanceCapCutCore

final class RetouchTests: XCTestCase {
    func testSkinRetouchIsRenderedAndMakeupLooksAreReported() throws {
        let json = #"""
        {"tracks": [{"type": "video", "segments": [
            {"id": "S0", "material_id": "V", "extra_material_refs": ["E0", "E1", "R"], "target_timerange": {"start": 0, "duration": 1000000}},
            {"id": "S1", "material_id": "V", "extra_material_refs": ["E2", "R", "D"], "target_timerange": {"start": 1000000, "duration": 1000000}}]}],
         "materials": {"videos": [{"id": "V", "type": "photo", "path": "/p.png"}],
          "effects": [
            {"id": "E0", "type": "figure", "sub_type": "auto_beauty", "name": "Sparkly Skin"},
            {"id": "E1", "type": "figure", "sub_type": "auto_beauty", "name": "", "intensity_key": "face_adjust_fuling"},
            {"id": "E2", "type": "figure", "sub_type": "exclusion_face", "name": "Cherry Cola"},
            {"id": "R", "type": "makeup_root", "sub_type": "auto_beauty", "name": "makeup-root"}],
          "manual_deformations": [{"id": "D", "type": "manual_deformation"}]}}
        """#
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("ecc-retouch-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        try Data([0]).write(to: dir.appendingPathComponent("p.png"))
        let draft = try JSONDecoder().decode(Draft.self, from: Data(json.replacingOccurrences(
            of: "/p.png", with: dir.appendingPathComponent("p.png").path).utf8))
        let timeline = TimelineBuilder.build(draft: draft, projectURL: dir)
        let retouch = Dictionary(uniqueKeysWithValues: timeline.layers.map { ($0.id, $0.retouchesSkin) })
        XCTAssertEqual(retouch["S0"], true)
        XCTAssertEqual(retouch["S1"], false, "a makeup look alone is not a skin retouch")
        XCTAssertEqual(Set(timeline.report.unsupported.map { "\($0.category): \($0.name) ×\($0.count)" }),
                       ["Beauty / makeup: Cherry Cola ×1", "Manual deformation: body/face reshape ×1"])
    }

    func testSkinWeight() {
        XCTAssertEqual(SkinRetouchMath.weight(0.545, 0.361, 0.337), SkinRetouchMath.strength, accuracy: 1e-9, "skin")
        XCTAssertGreaterThan(SkinRetouchMath.weight(0.85, 0.65, 0.55), 0.4, "light skin")
        XCTAssertGreaterThan(SkinRetouchMath.weight(0.35, 0.22, 0.16), 0.4, "dark skin")
        XCTAssertEqual(SkinRetouchMath.weight(0.31, 0.48, 0.69), 0, "sky")
        XCTAssertEqual(SkinRetouchMath.weight(0.5, 0.5, 0.5), 0, "grey")
        XCTAssertEqual(SkinRetouchMath.weight(0.78, 0.16, 0.2), 0, "saturated red")
        XCTAssertEqual(SkinRetouchMath.weight(0.94, 0.59, 0.16), 0, "orange")
        XCTAssertEqual(SkinRetouchMath.weight(0.14, 0.33, 0.29), 0, "green")
    }

    func testRetouchSoftensSkinOnly() {
        // A checkerboard of two skin tones next to a checkerboard of two blues.
        let w = 800, h = 400  // blur sigma = 1 px
        var pixels = [UInt8](repeating: 255, count: w * h * 4)
        for y in 0..<h {
            for x in 0..<w {
                let i = (y * w + x) * 4, odd = (x + y) % 2 == 1
                let c: (UInt8, UInt8, UInt8) = x < w / 2 ? (odd ? (150, 100, 90) : (130, 85, 78)) : (odd ? (60, 90, 200) : (40, 70, 180))
                pixels[i] = c.0; pixels[i + 1] = c.1; pixels[i + 2] = c.2
            }
        }
        let sRGB = CGColorSpace(name: CGColorSpace.sRGB)!
        let image = CIImage(bitmapData: Data(pixels), bytesPerRow: w * 4, size: CGSize(width: w, height: h),
                            format: .RGBA8, colorSpace: sRGB)
        let out = SkinRetouch().apply(image)
        XCTAssertEqual(out.extent, image.extent)
        var result = [UInt8](repeating: 0, count: w * h * 4)
        CIContext().render(out, toBitmap: &result, rowBytes: w * 4, bounds: out.extent, format: .RGBA8, colorSpace: sRGB)
        func contrast(_ x0: Int) -> Int {
            let i = (200 * w + x0) * 4, j = (200 * w + x0 + 1) * 4
            return abs(Int(result[i]) - Int(result[j]))
        }
        XCTAssertLessThan(contrast(200), 15, "skin is smoothed (was 20)")
        XCTAssertEqual(contrast(600), 20, "blue is kept")
    }
}
