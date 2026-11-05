import XCTest
@testable import EnhanceCapCutCore

final class HDRModeTests: XCTestCase {
    func testHDRModeZeroTurnsToneMappingOff() throws {
        let photo = FileManager.default.temporaryDirectory.appendingPathComponent("ecc-hdr-\(UUID().uuidString).png")
        try Data([0]).write(to: photo)
        defer { try? FileManager.default.removeItem(at: photo) }
        func segment(_ id: String, _ settings: String) -> String {
            "{\"id\": \"\(id)\", \"material_id\": \"V1\", \"target_timerange\": {\"start\": 0, \"duration\": 1000000}\(settings)}"
        }
        let json = """
        {
          "tracks": [{"type": "video", "segments": [
            \(segment("Off", ", \"hdr_settings\": {\"mode\": 0, \"intensity\": 1.0, \"nits\": 1000}")),
            \(segment("On", ", \"hdr_settings\": {\"mode\": 4, \"intensity\": 1.0, \"nits\": 1000}")),
            \(segment("None", ""))]}],
          "materials": {"videos": [{"id": "V1", "type": "photo", "path": "\(photo.path)"}]}
        }
        """
        let draft = try JSONDecoder().decode(Draft.self, from: Data(json.utf8))
        let timeline = TimelineBuilder.build(draft: draft, projectURL: URL(fileURLWithPath: "/tmp"))
        let toneMaps = Dictionary(uniqueKeysWithValues: timeline.layers.map { ($0.id, $0.toneMapsHDR) })
        XCTAssertEqual(toneMaps, ["Off": false, "On": true, "None": true])
    }
}
