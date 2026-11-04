import CoreImage
import XCTest
@testable import EnhanceCapCutCore

final class VideoEffectTests: XCTestCase {
    func testClipEffectsAreRenderedOthersReported() throws {
        let fm = FileManager.default
        let dir = fm.temporaryDirectory.appendingPathComponent("ecc-fx-\(UUID().uuidString)")
        try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: dir) }
        let photo = dir.appendingPathComponent("p.png")
        try Data([0]).write(to: photo)
        let json = """
        {"tracks": [
          {"type": "video", "segments": [{"id": "S", "material_id": "V", "extra_material_refs": ["E1", "E2", "E3"],
                                          "target_timerange": {"start": 0, "duration": 2000000}}]},
          {"type": "effect", "segments": [{"id": "T", "material_id": "E4",
                                           "target_timerange": {"start": 0, "duration": 1000000}}]}],
         "materials": {"videos": [{"id": "V", "type": "photo", "path": "\(photo.path)"}],
          "video_effects": [
            {"id": "E1", "name": "Pudding Bounce", "effect_id": "7534579654615649553", "apply_target_type": 0},
            {"id": "E2", "name": "Hazy Rotation", "effect_id": "7486402432189091127", "apply_target_type": 0,
             "adjust_params": [{"name": "effects_adjust_speed", "value": 0.6666666666}]},
            {"id": "E3", "name": "Card Slash", "effect_id": "7474875426633026821", "apply_target_type": 0},
            {"id": "E4", "name": "Hazy Rotation", "effect_id": "7486402432189091127", "apply_target_type": 2}]}}
        """
        let draft = try JSONDecoder().decode(Draft.self, from: Data(json.utf8))
        let timeline = TimelineBuilder.build(draft: draft, projectURL: dir)
        let layer = try XCTUnwrap(timeline.layers.first { $0.id == "S" })
        XCTAssertEqual(layer.effects.map(\.kind), [.puddingBounce, .hazyRotation])
        XCTAssertEqual(layer.effects[1].speed, 2, accuracy: 1e-6)
        let found = Set(timeline.report.unsupported.map { "\($0.category): \($0.name)" })
        XCTAssertEqual(found, ["Video effect: Card Slash", "Video effect: Hazy Rotation"],
                       "unknown effects and effect-track effects are still reported")
    }

    func testEffectsOpenTheClipAndSettle() {
        let pudding = VideoEffect(kind: .puddingBounce)
        let first = pudding.state(atOffset: 0)
        XCTAssertGreaterThan(first.zoom, 1.3)
        XCTAssertGreaterThan(first.flash, 0.5)
        XCTAssertGreaterThan(first.blur, 0)
        XCTAssertTrue(pudding.state(atOffset: 1_000_000).isIdentity)

        let hazy = VideoEffect(kind: .hazyRotation)
        XCTAssertEqual(hazy.state(atOffset: 0).zoom, 2, accuracy: 1e-9)
        XCTAssertLessThan(hazy.state(atOffset: 0).rotation, -10)
        XCTAssertTrue(hazy.state(atOffset: 400_000).isIdentity)
        // Twice the speed: the same look at half the time.
        let fast = VideoEffect(kind: .hazyRotation, speed: 2)
        XCTAssertEqual(fast.state(atOffset: 100_000), hazy.state(atOffset: 200_000))
        XCTAssertTrue(VideoEffect(kind: .hazyRotation, start: 500_000).state(atOffset: 100_000).isIdentity)
    }

    func testRendererKeepsTheFrameAndFlashesWhite() {
        let image = CIImage(color: CIColor(red: 0, green: 0, blue: 0)).cropped(to: CGRect(x: 0, y: 0, width: 16, height: 8))
        let state = VideoEffectState(zoom: 1.5, rotation: -10, blur: 0.05, flash: 0.5)
        let out = VideoEffectRenderer.apply(state, to: image)
        XCTAssertEqual(out.extent, image.extent)
        var pixel = [UInt8](repeating: 0, count: 4)
        CIContext(options: [.workingColorSpace: NSNull()])
            .render(out, toBitmap: &pixel, rowBytes: 4, bounds: CGRect(x: 8, y: 4, width: 1, height: 1),
                    format: .RGBA8, colorSpace: nil)
        XCTAssertEqual(Double(pixel[0]), 127.5, accuracy: 2)
        XCTAssertEqual(pixel[3], 255)
        XCTAssertTrue(VideoEffectRenderer.apply(.identity, to: image) === image)
    }
}
