import XCTest
@testable import EnhanceCapCutCore

final class StickerTests: XCTestCase {
    func testClockHandsLoopFromTheRestPose() {
        let start = ClockSticker.handAngles(atOffset: 0)
        XCTAssertEqual(start.minute, 0, accuracy: 0.001)
        XCTAssertEqual(start.hour, 90, accuracy: 0.001)
        // Halfway between the first two samples (1/30 s).
        let early = ClockSticker.handAngles(atOffset: 33_333)
        XCTAssertEqual(early.minute, 18.5, accuracy: 0.1)
        XCTAssertEqual(early.hour, 96.5, accuracy: 0.1)
        // The hands rest at the end of the loop, then it starts over.
        let rest = ClockSticker.handAngles(atOffset: ClockSticker.period - 50_000)
        XCTAssertEqual(rest.minute, 0, accuracy: 0.001)
        XCTAssertEqual(rest.hour, 90, accuracy: 0.001)
        let again = ClockSticker.handAngles(atOffset: ClockSticker.period + 33_333)
        XCTAssertEqual(again.minute, early.minute, accuracy: 0.001)
        XCTAssertEqual(again.hour, early.hour, accuracy: 0.001)
    }

    func testClockTimeStickersBecomeLayersOtherStickersAreReported() throws {
        let json = """
        {"tracks": [{"type": "sticker", "segments": [
            {"id": "S0", "material_id": "M0", "render_index": 14000, "target_timerange": {"start": 0, "duration": 1000000},
             "clip": {"scale": {"x": 0.25, "y": 0.25}, "transform": {"x": 0.5, "y": 0.1}}},
            {"id": "S1", "material_id": "M1", "target_timerange": {"start": 0, "duration": 1000000}},
            {"id": "S2", "material_id": "M2", "target_timerange": {"start": 0, "duration": 1000000}}]}],
         "materials": {"stickers": [
            {"id": "M0", "type": "sticker", "name": "Clock Time EN White Outline Progress Bar", "resource_id": "7528282080199904565"},
            {"id": "M1", "type": "sticker", "name": "Clock Time EN Blue White Minimalist", "resource_id": "7528280675909209405"},
            {"id": "M2", "type": "sticker", "name": "Pet Meme", "resource_id": "7616938154540469509"}]}}
        """
        let draft = try JSONDecoder().decode(Draft.self, from: Data(json.utf8))
        let timeline = TimelineBuilder.build(draft: draft, projectURL: FileManager.default.temporaryDirectory)
        XCTAssertEqual(Set(timeline.layers.map(\.id)), ["S0", "S1"])
        var styles: [String: ClockSticker.Style] = [:]
        for layer in timeline.layers {
            if case let .sticker(spec) = layer.content { styles[layer.id] = spec.clock.style }
        }
        XCTAssertEqual(styles, ["S0": .whiteOutline, "S1": .blueWhite])
        let s0 = timeline.layers.first { $0.id == "S0" }!
        XCTAssertEqual(s0.renderIndex, 14000)
        XCTAssertEqual(s0.transform(at: 0).positionX, 0.5, accuracy: 1e-9)
        XCTAssertEqual(timeline.report.unsupported.map { "\($0.category): \($0.name)" }, ["Sticker: Pet Meme"])
    }
}
