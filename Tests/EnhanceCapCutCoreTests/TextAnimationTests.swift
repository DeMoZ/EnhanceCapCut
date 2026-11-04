@testable import EnhanceCapCutCore
import XCTest

final class TextAnimationTests: XCTestCase {
    func draft(_ animations: String) -> String {
        """
        {"tracks": [{"type": "text", "segments": [
           {"id": "S1", "material_id": "X1", "target_timerange": {"start": 0, "duration": 2000000},
            "extra_material_refs": ["M1"]}]}],
         "materials": {
           "texts": [{"id": "X1", "content": "{\\"text\\":\\"Hello\\",\\"styles\\":[{\\"range\\":[0,5]}]}"}],
           "material_animations": [{"id": "M1", "type": "sticker_animation", "animations": [\(animations)]}]}}
        """
    }

    func build(_ json: String) throws -> Timeline {
        let draft = try JSONDecoder().decode(Draft.self, from: Data(json.utf8))
        return TimelineBuilder.build(draft: draft, projectURL: URL(fileURLWithPath: "/tmp"))
    }

    func testParsesAnimationsAndReportsTheRestByName() throws {
        let timeline = try build(draft("""
            {"type": "in", "name": "Typewriter", "duration": 500000, "resource_id": "1"},
            {"type": "out", "name": "渐隐", "duration": 400000},
            {"type": "loop", "name": "Swing", "duration": 600000}
            """))
        guard case let .text(spec) = timeline.layers.first?.content else { return XCTFail("no text layer") }
        XCTAssertEqual(spec.animations.intro, TextAnimation(kind: .intro, effect: .typewriter, duration: 500000))
        XCTAssertEqual(spec.animations.outro, TextAnimation(kind: .outro, effect: .fade, duration: 400000))
        XCTAssertNil(spec.animations.loop)
        XCTAssertEqual(timeline.report.unsupported.map { "\($0.category): \($0.name)" }, ["Animation: Swing"])
    }

    func testRecognizesPresetNames() {
        typealias E = TextAnimation.Effect
        let d = TextAnimations.slideDistance
        XCTAssertEqual(E.recognize("Fade In", kind: .intro), .fade)
        XCTAssertEqual(E.recognize("Slide Left", kind: .intro), .slide(dx: -d, dy: 0))
        XCTAssertEqual(E.recognize("向上滑动", kind: .outro), .slide(dx: 0, dy: d))
        XCTAssertEqual(E.recognize("Zoom In", kind: .intro), .zoom(scale: 0, fades: false))
        XCTAssertEqual(E.recognize("Zoom Out", kind: .outro), .zoom(scale: 0, fades: false))
        XCTAssertEqual(E.recognize("Pop Up", kind: .intro), .pop)
        XCTAssertEqual(E.recognize("Blink", kind: .loop), .blink)
        XCTAssertNil(E.recognize("Blink", kind: .intro))
        XCTAssertNil(E.recognize("Spin", kind: .intro))
    }

    func testStateOverTime() {
        let animations = TextAnimations(
            intro: TextAnimation(kind: .intro, effect: .slide(dx: 0.2, dy: 0), duration: 500_000),
            outro: TextAnimation(kind: .outro, effect: .fade, duration: 500_000),
            loop: TextAnimation(kind: .loop, effect: .pulse, duration: 400_000))
        let start = animations.state(atOffset: 0, duration: 2_000_000)
        XCTAssertEqual(start.offsetX, -0.2, accuracy: 1e-9, "an intro sliding right arrives from the left")
        XCTAssertEqual(start.alpha, 0, accuracy: 1e-9)
        // Loop between intro and outro: a cycle starts at rest and peaks halfway.
        XCTAssertEqual(animations.state(atOffset: 500_000, duration: 2_000_000).scale, 1, accuracy: 1e-9)
        XCTAssertEqual(animations.state(atOffset: 700_000, duration: 2_000_000).scale, 1 + TextAnimations.pulseScale,
                       accuracy: 1e-9)
        XCTAssertEqual(animations.state(atOffset: 1_750_000, duration: 2_000_000).alpha, 0.5, accuracy: 1e-9)
        // Intro and outro longer than the clip are squeezed into it.
        XCTAssertEqual(animations.state(atOffset: 250_000, duration: 500_000).alpha, 1, accuracy: 1e-9)
        XCTAssertEqual(animations.state(atOffset: 375_000, duration: 500_000).alpha, 0.5, accuracy: 1e-9)
    }

    func testTypewriterRevealsWholeCharacters() {
        let typewriter = TextAnimations(intro: TextAnimation(kind: .intro, effect: .typewriter, duration: 1_000_000))
        XCTAssertEqual(typewriter.state(atOffset: 500_000, duration: 2_000_000).reveal, 0.5, accuracy: 1e-9)
        XCTAssertEqual(TextAnimationState.visibleLength(of: "Hi 👋🏽!", reveal: 0.5), 2)
        XCTAssertEqual(TextAnimationState.visibleLength(of: "Hi 👋🏽!", reveal: 0.8), 7, "an emoji is one character")
        XCTAssertEqual(TextAnimationState.visibleLength(of: "Hi 👋🏽!", reveal: 1), 8)
        XCTAssertEqual(TextAnimationState.visibleLength(of: "Hi", reveal: 0), 0)
    }
}
