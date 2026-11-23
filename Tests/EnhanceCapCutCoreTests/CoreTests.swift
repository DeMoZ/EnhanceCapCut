import AVFoundation
import XCTest
@testable import EnhanceCapCutCore

final class TimeMapTests: XCTestCase {

    private let flashIn = [Point2(x: 0, y: 1), Point2(x: 0.19, y: 5.2), Point2(x: 0.78, y: 5.2),
                           Point2(x: 0.94, y: 1), Point2(x: 1, y: 1)]

}

final class KeyframeTests: XCTestCase {
}

final class DraftParsingTests: XCTestCase {
    /// A minimal synthetic draft with odd values (nulls, wrong types, unknown keys) that must not break parsing.
    let json = """
    {
      "id": "D", "name": "Test", "duration": 3000000, "fps": 30.0, "unknown_field": {"x": [1, 2]},
      "canvas_config": {"width": 1080, "height": 1920, "ratio": "9:16", "background": null},
      "config": {"video_mute": false},
      "tracks": [
        {"id": "T1", "type": "video", "attribute": 0, "flag": 0, "segments": [
          {"id": "S1", "material_id": "V1", "source_timerange": {"start": 0, "duration": 6000000},
           "target_timerange": {"start": 0, "duration": 3000000}, "speed": 2.0, "volume": 1.5,
           "clip": {"scale": {"x": 1.2, "y": 1.2}, "rotation": 90, "transform": {"x": 0.1, "y": -0.2},
                    "flip": {"horizontal": true, "vertical": false}, "alpha": 0.5},
           "extra_material_refs": ["SP1", "C1", "TR1"], "render_index": 0,
           "common_keyframes": [{"property_type": "KFTypePositionX", "keyframe_list": [
             {"time_offset": 0, "values": [0.0]}, {"time_offset": 1000000, "values": [0.5]}]}]}
        ]},
        {"id": "T2", "type": "audio", "attribute": 1, "segments": []},
        {"id": "T3", "type": "text", "attribute": 0, "segments": [
          {"id": "S2", "material_id": "X1", "target_timerange": {"start": 0, "duration": 1000000}, "clip": null}
        ]}
      ],
      "materials": {
        "videos": [{"id": "V1", "type": "video", "path": "/nonexistent/clip.mov", "width": 1920, "height": 1080,
                    "crop": {"upper_left_x": 0.1, "upper_left_y": 0.0, "lower_right_x": 0.9, "lower_right_y": 1.0}}],
        "speeds": [{"id": "SP1", "type": "speed", "mode": 0, "speed": 2.0, "curve_speed": null}],
        "canvases": [{"id": "C1", "type": "canvas_blur", "blur": 0.5, "color": ""}],
        "transitions": [{"id": "TR1", "type": "transition", "name": "Sand Spread"}],
        "texts": [{"id": "X1", "type": "subtitle", "font_size": "not a number",
                   "content": "{\\"text\\":\\"Hello\\",\\"styles\\":[{\\"range\\":[0,5],\\"size\\":8,\\"fill\\":{\\"content\\":{\\"solid\\":{\\"color\\":[1,0,0]}}}}]}"}],
        "audios": null
      }
    }
    """

    func testCutoutMaskFrames() {
        XCTAssertEqual(CutoutMask.nearest([10, 20, 40], to: 0), 10)
        XCTAssertEqual(CutoutMask.nearest([10, 20, 40], to: 15), 10, "ties go to the earlier frame")
        XCTAssertEqual(CutoutMask.nearest([10, 20, 40], to: 31), 40)
        XCTAssertEqual(CutoutMask.nearest([10, 20, 40], to: 99), 40)
        // CapCut's sizes: 448×256 for 16:9, 256×336 for 3:4, 720×720 for a square photo.
        XCTAssertEqual(CutoutMask.inferSize(bytes: 448 * 256, aspect: 16.0 / 9).map { [$0.width, $0.height] }, [448, 256])
        XCTAssertEqual(CutoutMask.inferSize(bytes: 256 * 448, aspect: 9.0 / 16).map { [$0.width, $0.height] }, [256, 448])
        XCTAssertEqual(CutoutMask.inferSize(bytes: 256 * 336, aspect: 3.0 / 4).map { [$0.width, $0.height] }, [256, 336])
        XCTAssertEqual(CutoutMask.inferSize(bytes: 720 * 720, aspect: 1).map { [$0.width, $0.height] }, [720, 720])
        XCTAssertNil(CutoutMask.inferSize(bytes: 7, aspect: 1))
    }

}

final class LayerTests: XCTestCase {
}

final class ExportSettingsTests: XCTestCase {
    func testOutputSize() {
        var s = ExportSettings()
        XCTAssertEqual(s.outputSize(canvas: CGSize(width: 1080, height: 1920)), CGSize(width: 1080, height: 1920))
        s.aspect = .landscape
        XCTAssertEqual(s.outputSize(canvas: CGSize(width: 1080, height: 1920)), CGSize(width: 1920, height: 1080))
        s.aspect = .square
        s.resolution = .p720
        XCTAssertEqual(s.outputSize(canvas: CGSize(width: 1080, height: 1920)), CGSize(width: 720, height: 720))
        s.aspect = .project
        s.resolution = .p2160
        XCTAssertEqual(s.outputSize(canvas: CGSize(width: 1440, height: 1920)), CGSize(width: 2160, height: 2880))
    }

    func testFileNameTemplate() {
        var s = ExportSettings()
        s.codec = .prores422
        s.fileNameTemplate = "{project}-{res}-{fps}-{codec}"
        XCTAssertEqual(s.fileName(project: "Trip", size: CGSize(width: 1080, height: 1920), fps: 30),
                       "Trip-1080x1920-30-prores422.mov")
    }

    func testFileNameUsesGregorianDates() {
        var s = ExportSettings()
        s.fileNameTemplate = "{date}"
        let date = ISO8601DateFormatter().date(from: "2026-10-15T12:00:00Z")!
        XCTAssertTrue(s.fileName(project: "P", size: .zero, fps: 30, date: date).hasPrefix("2026-10-1"))
    }

    func testDecodingOldSettingsKeepsDefaults() throws {
        let decoded = try JSONDecoder().decode(ExportSettings.self, from: Data(#"{"codec":"h264","quality":55}"#.utf8))
        XCTAssertEqual(decoded.codec, .h264)
        XCTAssertEqual(decoded.quality, 55)
        XCTAssertEqual(decoded.burnInSubtitles, true)
    }
}

final class ColorTests: XCTestCase {
    func testHexColors() {
        let c = TextRasterizer.hexColor("#FF000080")
        XCTAssertEqual(c.components?[0] ?? 0, 0, accuracy: 0.01)
        XCTAssertEqual(c.alpha, 1, accuracy: 0.01)
        let d = TextRasterizer.hexColor("#80FF0000")
        XCTAssertEqual(d.alpha, 128.0 / 255, accuracy: 0.01)
        XCTAssertEqual(d.components?[0] ?? 0, 1, accuracy: 0.01)
    }
}

final class AudioSpeedTests: XCTestCase {
}

final class ProjectTimelinesTests: XCTestCase {
    private func makeProject() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        try Data("{}".utf8).write(to: url.appendingPathComponent("draft_info.json"))
        addTeardownBlock { try? FileManager.default.removeItem(at: url) }
        return url
    }

}

/// CapCut for macOS names a draft `draft_info.json`, CapCut for Windows `draft_content.json`.
final class DraftFileNameTests: XCTestCase {
    private func makeRoot() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: url) }
        return url
    }

    private func makeProject(_ name: String, in root: URL, draftFile: String) throws -> URL {
        let url = root.appendingPathComponent(name, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        try Data("{}".utf8).write(to: url.appendingPathComponent(draftFile))
        return url
    }

}
