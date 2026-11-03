import CoreImage
import XCTest
@testable import EnhanceCapCutCore

final class ChromaKeyTests: XCTestCase {
    func testChromaMaterialBecomesTheLayersKey() throws {
        let json = #"""
        {"tracks": [{"type": "video", "segments": [
            {"id": "S0", "material_id": "V", "extra_material_refs": ["C0"], "target_timerange": {"start": 0, "duration": 1000000}},
            {"id": "S1", "material_id": "V", "extra_material_refs": ["C1"], "target_timerange": {"start": 0, "duration": 1000000}},
            {"id": "S2", "material_id": "V", "extra_material_refs": ["C2"], "target_timerange": {"start": 0, "duration": 1000000}}]}],
         "materials": {"videos": [{"id": "V", "type": "photo", "path": "/p.png"}],
          "chromas": [
            {"id": "C0", "type": "chroma", "color": "#FF00FF00", "intensity_value": 0.4, "shadow_value": 0.2,
             "edge_smooth_value": 0.3, "spill_value": 1.5},
            {"id": "C1", "type": "chroma", "color": "", "intensity_value": 0.4},
            {"id": "C2", "type": "chroma", "color": "#00FF00", "intensity_value": 0}]}}
        """#
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("ecc-chroma-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        try Data([0]).write(to: dir.appendingPathComponent("p.png"))
        let draft = try JSONDecoder().decode(Draft.self, from: Data(json.replacingOccurrences(
            of: "/p.png", with: dir.appendingPathComponent("p.png").path).utf8))
        let timeline = TimelineBuilder.build(draft: draft, projectURL: dir)
        let keys = Dictionary(uniqueKeysWithValues: timeline.layers.map { ($0.id, $0.chromaKey) })
        XCTAssertEqual(keys["S0"]!, ChromaKey(red: 0, green: 1, blue: 0, intensity: 0.4, shadow: 0.2,
                                              edgeSmooth: 0.3, spill: 1))
        XCTAssertNil(keys["S1"]!, "no colour picked: the clip is shown as it is")
        XCTAssertNil(keys["S2"]!, "intensity 0: the clip is shown as it is")
        XCTAssertTrue(timeline.report.unsupported.isEmpty)
    }

    func testMatteAndSpill() {
        let green = ChromaKey(red: 0.1, green: 0.8, blue: 0.2, intensity: 0.5)
        XCTAssertEqual(ChromaKeyMath.alpha(green, 0.1, 0.8, 0.2), 0, "the picked colour is removed")
        XCTAssertEqual(ChromaKeyMath.alpha(green, 0.15, 0.7, 0.2), 0, "nearby greens are removed")
        XCTAssertEqual(ChromaKeyMath.alpha(green, 0.9, 0.6, 0.5), 1, "skin is kept")
        XCTAssertEqual(ChromaKeyMath.alpha(green, 0.2, 0.3, 0.9), 1, "blue is kept")
        XCTAssertEqual(ChromaKeyMath.alpha(ChromaKey(red: 0.1, green: 0.8, blue: 0.2, intensity: 0), 0.1, 0.8, 0.2), 1)
        let weak = ChromaKey(red: 0.1, green: 0.8, blue: 0.2, intensity: 0.05)
        XCTAssertGreaterThan(ChromaKeyMath.alpha(weak, 0.15, 0.6, 0.2), 0.5, "low intensity removes only the colour itself")
        let darkGreen = (0.02, 0.2, 0.05)
        XCTAssertEqual(ChromaKeyMath.alpha(ChromaKey(red: 0.1, green: 0.8, blue: 0.2, intensity: 1), darkGreen.0, darkGreen.1, darkGreen.2), 0)
        XCTAssertEqual(ChromaKeyMath.alpha(ChromaKey(red: 0.1, green: 0.8, blue: 0.2, intensity: 1, shadow: 1),
                                           darkGreen.0, darkGreen.1, darkGreen.2), 1, "Shadow keeps dark pixels")
        // A white screen is keyed by brightness.
        let white = ChromaKey(red: 1, green: 1, blue: 1, intensity: 0.3)
        XCTAssertEqual(ChromaKeyMath.alpha(white, 0.95, 0.95, 0.95), 0)
        XCTAssertEqual(ChromaKeyMath.alpha(white, 0.3, 0.3, 0.3), 1)

        let spill = ChromaKey(red: 0.1, green: 0.8, blue: 0.2, intensity: 0.5, spill: 1)
        let fixed = ChromaKeyMath.despill(spill, 0.6, 0.7, 0.5)
        XCTAssertLessThan(fixed.1, 0.7, "the green cast is taken out")
        XCTAssertEqual(0.299 * fixed.0 + 0.587 * fixed.1 + 0.114 * fixed.2, 0.299 * 0.6 + 0.587 * 0.7 + 0.114 * 0.5,
                       accuracy: 0.01, "brightness stays")
        let magenta = ChromaKeyMath.despill(spill, 0.8, 0.2, 0.8)
        XCTAssertEqual(magenta.0, 0.8, accuracy: 1e-9, "colours away from the key are left alone")
        XCTAssertEqual(magenta.1, 0.2, accuracy: 1e-9)
    }

    func testFilterRemovesTheScreen() throws {
        let screen = CIImage(color: CIColor(red: 0.1, green: 0.8, blue: 0.2, colorSpace: CGColorSpace(name: CGColorSpace.sRGB)!)!)
            .cropped(to: CGRect(x: 0, y: 0, width: 8, height: 8))
        let subject = CIImage(color: CIColor(red: 0.9, green: 0.6, blue: 0.5, colorSpace: CGColorSpace(name: CGColorSpace.sRGB)!)!)
            .cropped(to: CGRect(x: 8, y: 0, width: 8, height: 8))
        let image = subject.composited(over: screen)
        for key in [ChromaKey(red: 0.1, green: 0.8, blue: 0.2, intensity: 0.5),
                    ChromaKey(red: 0.1, green: 0.8, blue: 0.2, intensity: 0.5, edgeSmooth: 0.5, spill: 1)] {
            let keyed = ChromaKeyFilter(key).apply(image)
            XCTAssertEqual(keyed.extent, image.extent)
            var pixels = [UInt8](repeating: 0, count: 16 * 8 * 4)
            CIContext().render(keyed, toBitmap: &pixels, rowBytes: 16 * 4, bounds: keyed.extent, format: .RGBA8,
                               colorSpace: CGColorSpace(name: CGColorSpace.sRGB))
            let row = 4 * 16 * 4
            XCTAssertEqual(pixels[row + 1 * 4 + 3], 0, "screen is transparent")
            XCTAssertEqual(pixels[row + 14 * 4 + 3], 255, "subject is opaque")
            XCTAssertEqual(Int(pixels[row + 14 * 4]), 230, accuracy: 3, "subject colour is kept")
        }
    }
}
