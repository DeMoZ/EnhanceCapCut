import XCTest
@testable import EnhanceCapCutCore

final class ColorAdjustmentTests: XCTestCase {
    /// A clip with one HSL material per value of `hsl` (hue, saturation, lightness).
    private func report(hsl: [(Double, Double, Double)]) throws -> Set<String> {
        let refs = hsl.indices.map { "\"H\($0)\"" }.joined(separator: ",")
        let materials = hsl.enumerated().map { i, v in
            "{\"id\": \"H\(i)\", \"type\": \"hsl\", \"hsl_color_type\": 1, \"hue\": \(v.0), \"saturation\": \(v.1), \"lightness\": \(v.2)}"
        }.joined(separator: ",")
        let json = """
        {
          "tracks": [{"type": "video", "segments": [
            {"id": "S1", "material_id": "V1", "target_timerange": {"start": 0, "duration": 1000000},
             "extra_material_refs": [\(refs)]}]}],
          "materials": {"videos": [{"id": "V1", "type": "video", "path": "/nonexistent/clip.mov"}], "hsl": [\(materials)]}
        }
        """
        let draft = try JSONDecoder().decode(Draft.self, from: Data(json.utf8))
        let timeline = TimelineBuilder.build(draft: draft, projectURL: URL(fileURLWithPath: "/tmp"))
        return Set(timeline.report.unsupported.map { "\($0.category): \($0.name)" })
    }

    func testNeutralHSLIsNotReported() throws {
        XCTAssertFalse(try report(hsl: [(0, 0, 0)]).contains("HSL color: color adjustment"))
    }

    func testChangedHSLIsReported() throws {
        XCTAssertTrue(try report(hsl: [(0, 0, 0), (0, 25, 0)]).contains("HSL color: color adjustment"))
    }
}
