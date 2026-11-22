import CoreImage
import XCTest
@testable import EnhanceCapCutCore

final class BlendModeTests: XCTestCase {
    func testMixModeMaterialBecomesTheLayersBlendMode() throws {
        let json = #"""
        {"tracks": [{"type": "video", "segments": [
            {"id": "S0", "material_id": "V", "extra_material_refs": ["M0"], "target_timerange": {"start": 0, "duration": 1000000}},
            {"id": "S1", "material_id": "V", "extra_material_refs": ["M1"], "target_timerange": {"start": 0, "duration": 1000000}},
            {"id": "S2", "material_id": "V", "extra_material_refs": ["M2"], "target_timerange": {"start": 0, "duration": 1000000}},
            {"id": "S3", "material_id": "V", "extra_material_refs": ["M3"], "target_timerange": {"start": 0, "duration": 1000000}},
            {"id": "S4", "material_id": "V", "extra_material_refs": ["M4"], "target_timerange": {"start": 0, "duration": 1000000}},
            {"id": "S5", "material_id": "V", "target_timerange": {"start": 0, "duration": 1000000}}]}],
         "materials": {"videos": [{"id": "V", "type": "photo", "path": "/p.png"}],
          "effects": [
            {"id": "M0", "type": "mix_mode", "name": "Screen", "effect_id": "871339", "resource_id": "6758325170760323597"},
            {"id": "M1", "type": "mix_mode", "name": "", "resource_id": "6758325895519277582"},
            {"id": "M2", "type": "mix_mode", "name": "Soft light"},
            {"id": "M3", "type": "mix_mode", "name": "Normal"},
            {"id": "M4", "type": "mix_mode", "name": "Vivid", "effect_id": "1", "resource_id": "2"}]}}
        """#
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("ecc-blend-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        try Data([0]).write(to: dir.appendingPathComponent("p.png"))
        let draft = try JSONDecoder().decode(Draft.self, from: Data(json.replacingOccurrences(
            of: "/p.png", with: dir.appendingPathComponent("p.png").path).utf8))
        let timeline = TimelineBuilder.build(draft: draft, projectURL: dir)
        let modes = Dictionary(uniqueKeysWithValues: timeline.layers.map { ($0.id, $0.blendMode) })
        XCTAssertEqual(modes["S0"]!, .screen)
        XCTAssertEqual(modes["S1"]!, .multiply)
        XCTAssertEqual(modes["S2"]!, .softLight)
        XCTAssertEqual(modes["S3"]!, .normal)
        XCTAssertNil(modes["S4"]!)
        XCTAssertNil(modes["S5"]!)
        XCTAssertEqual(timeline.report.unsupported.map(\.id), ["Blend mode/Vivid"], "only the unknown mode is reported")
    }

    /// The blend runs on sRGB-encoded values (like CapCut's and Direct2D's) although the working space is linear.
    func testBlendsSRGBValues() {
        let srgb = CGColorSpace(name: CGColorSpace.sRGB)!
        let rect = CGRect(x: 0, y: 0, width: 4, height: 4)
        let backdrop = CIImage(color: CIColor(red: 0.5, green: 0.5, blue: 0.5, colorSpace: srgb)!).cropped(to: rect)
        let layer = CIImage(color: CIColor(red: 0.8, green: 0.2, blue: 0.5, colorSpace: srgb)!).cropped(to: rect)
        let context = CIContext(options: [.workingColorSpace: CGColorSpace(name: CGColorSpace.extendedLinearSRGB)!])
        func pixel(_ mode: BlendMode?, _ image: CIImage = layer) -> [Int] {
            var pixels = [UInt8](repeating: 0, count: 4 * 4 * 4)
            context.render(FrameCompositor.blended(image, over: backdrop, mode: mode), toBitmap: &pixels, rowBytes: 16,
                           bounds: rect, format: .RGBA8, colorSpace: srgb)
            return pixels[0..<4].map { Int($0) }
        }
        func assertClose(_ a: [Int], _ b: [Int], _ message: String, line: UInt = #line) {
            XCTAssertTrue(zip(a, b).allSatisfy { abs($0 - $1) <= 2 }, "\(message): \(a) vs \(b)", line: line)
        }
        assertClose(pixel(nil), [204, 51, 128, 255], "no mode: plain source-over")
        assertClose(pixel(.normal), [204, 51, 128, 255], "normal")
        assertClose(pixel(.screen), [230, 153, 191, 255], "screen = 1 − (1 − a)(1 − b)")
        assertClose(pixel(.multiply), [102, 26, 64, 255], "multiply = a · b")
        assertClose(pixel(.darken), [128, 51, 128, 255], "darken = min")
        assertClose(pixel(.lighten), [204, 128, 128, 255], "lighten = max")
        let half = layer.applyingFilter("CIColorMatrix", parameters: ["inputAVector": CIVector(x: 0, y: 0, z: 0, w: 0.5)])
        assertClose(pixel(.screen, half), [179, 140, 160, 255], "opacity mixes the blended colour with the backdrop")
    }
}
