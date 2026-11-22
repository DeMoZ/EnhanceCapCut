@testable import EnhanceCapCutCore
import XCTest

final class ClipAnimationTests: XCTestCase {
    /// A 2 s photo clip on the main track with the given `animations`.
    func build(_ animations: String) throws -> Timeline {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("ecc-anim-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: dir) }
        let photo = dir.appendingPathComponent("p.png")
        try Data([0]).write(to: photo)
        let json = """
        {"tracks": [{"type": "video", "segments": [
           {"id": "S1", "material_id": "V1", "target_timerange": {"start": 1000000, "duration": 2000000},
            "clip": {"scale": {"x": 0.8, "y": 0.8}, "transform": {"x": 0.1, "y": 0}, "alpha": 1},
            "extra_material_refs": ["M1"]}]}],
         "materials": {
           "videos": [{"id": "V1", "type": "photo", "path": "\(photo.path)"}],
           "material_animations": [{"id": "M1", "type": "sticker_animation", "animations": [\(animations)]}]}}
        """
        let draft = try JSONDecoder().decode(Draft.self, from: Data(json.utf8))
        return TimelineBuilder.build(draft: draft, projectURL: dir)
    }

    func testParsesInAndOutAndReportsTheRestByName() throws {
        let timeline = try build("""
            {"type": "in", "name": "Zoom In", "duration": 500000, "start": 0},
            {"type": "out", "name": "Fade Out", "duration": 400000, "start": 0},
            {"type": "group", "name": "Swing", "duration": 2000000}
            """)
        let layer = try XCTUnwrap(timeline.layers.first)
        XCTAssertEqual(layer.animations, [
            ClipAnimation(kind: .in, preset: .zoomIn, start: 0, end: 500_000),
            ClipAnimation(kind: .out, preset: .fade, start: 1_600_000, end: 2_000_000),
        ])
        XCTAssertEqual(timeline.report.unsupported.map { "\($0.category): \($0.name)" }, ["Animation: Swing"])
    }

    func testTransformOverTime() throws {
        let timeline = try build("""
            {"type": "in", "name": "Slide Right", "duration": 500000},
            {"type": "out", "name": "Fade Out", "duration": 500000}
            """)
        let layer = try XCTUnwrap(timeline.layers.first)
        // An In "Slide Right" arrives from the left, off-canvas, and ends at the clip's own position.
        XCTAssertEqual(layer.transform(at: 1_000_000).positionX, 0.1 - ClipAnimation.slideDistance, accuracy: 1e-9)
        XCTAssertEqual(layer.transform(at: 1_250_000).positionX, 0.1 - ClipAnimation.slideDistance / 8, accuracy: 1e-9)
        XCTAssertEqual(layer.transform(at: 1_500_000).positionX, 0.1, accuracy: 1e-9)
        XCTAssertEqual(layer.transform(at: 2_000_000), layer.transform(at: 1_500_000), "nothing runs mid-clip")
        XCTAssertEqual(layer.transform(at: 2_000_000).scaleX, 0.8, accuracy: 1e-9)
        // The Out fades in the clip's last 0.5 s.
        XCTAssertEqual(layer.transform(at: 2_750_000).alpha, 1 - 0.125, accuracy: 1e-9)
        XCTAssertEqual(layer.transform(at: 2_999_999).alpha, 0, accuracy: 1e-4)
    }

    func testInAndOutLongerThanTheClipAreSqueezed() throws {
        let timeline = try build("""
            {"type": "in", "name": "Fade In", "duration": 3000000},
            {"type": "out", "name": "Zoom Out", "duration": 1000000}
            """)
        XCTAssertEqual(timeline.layers.first?.animations.map { [$0.start, $0.end] }, [[0, 1_500_000], [1_500_000, 2_000_000]])
    }

    func testZoomAndRotate() {
        let identity = LayerTransform(scaleX: 1, scaleY: 1, rotation: 0, positionX: 0, positionY: 0,
                                      flipH: false, flipV: false, alpha: 1)
        func at(_ kind: ClipAnimation.Kind, _ preset: ClipAnimation.Preset, _ p: Double) -> LayerTransform {
            ClipAnimation(kind: kind, preset: preset, start: 0, end: 1).apply(to: identity, progress: p)
        }
        XCTAssertEqual(at(.in, .zoomIn, 0).scaleX, ClipAnimation.zoomInScale, accuracy: 1e-9, "Zoom In grows into the clip")
        XCTAssertEqual(at(.in, .zoomOut, 0).scaleX, ClipAnimation.zoomOutScale, accuracy: 1e-9)
        XCTAssertEqual(at(.out, .zoomIn, 1).scaleX, ClipAnimation.zoomOutScale, accuracy: 1e-9, "and keeps growing out of it")
        XCTAssertEqual(at(.out, .zoomOut, 1).scaleX, ClipAnimation.zoomInScale, accuracy: 1e-9)
        XCTAssertEqual(at(.in, .rotate, 0).rotation, -ClipAnimation.rotateDegrees, accuracy: 1e-9)
        XCTAssertEqual(at(.out, .rotateCounterclockwise, 1).rotation, -ClipAnimation.rotateDegrees, accuracy: 1e-9)
        XCTAssertEqual(at(.in, .shake, 0), identity)
        XCTAssertEqual(at(.in, .fade, 1), identity)
    }

    func testRecognizesPresetNames() {
        XCTAssertEqual(ClipAnimation.preset(named: "Fade In"), .fade)
        XCTAssertEqual(ClipAnimation.preset(named: "渐隐"), .fade)
        XCTAssertEqual(ClipAnimation.preset(named: "Zoom In 2"), .zoomIn)
        XCTAssertEqual(ClipAnimation.preset(named: "缩小"), .zoomOut)
        XCTAssertEqual(ClipAnimation.preset(named: "向上滑动"), .slideUp)
        XCTAssertEqual(ClipAnimation.preset(named: "Slide Left"), .slideLeft)
        XCTAssertEqual(ClipAnimation.preset(named: "Rotate Counterclockwise"), .rotateCounterclockwise)
        XCTAssertEqual(ClipAnimation.preset(named: "Spin"), .rotate)
        XCTAssertEqual(ClipAnimation.preset(named: "Shake 1"), .shake)
        XCTAssertNil(ClipAnimation.preset(named: "Swing"))
        XCTAssertNil(ClipAnimation.preset(named: "Slide"), "a slide without a direction isn't guessed")
    }
}
