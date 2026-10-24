import XCTest
@testable import EnhanceCapCutCore

final class SubtitleWriterTests: XCTestCase {
    private let draft = """
        {"id":"D","fps":30.0,"duration":10000000,"tracks":[{"id":"V","type":"video","flag":0,"attribute":0,"segments":[]}],\
        "materials":{"videos":[],"texts":[],"material_animations":[]},"canvas_config":{"ratio":"original","width":1080,"height":1920}}
        """

    private func tracks() -> [SubtitleTrack] {
        let ru = SubtitleLanguage(Locale.Language(identifier: "ru"))
        let en = SubtitleLanguage(Locale.Language(identifier: "en"))
        return [
            SubtitleTrack(language: ru, cues: [CaptionCue(start: 0.5, end: 2, text: "Привет, мир"),
                                               CaptionCue(start: 2, end: 3.01, text: "Вторая\nстрока")],
                          isOriginal: true, isVisible: false),
            SubtitleTrack(language: en, cues: [CaptionCue(start: 0.5, end: 2, text: "Hello, world")],
                          isOriginal: false, isVisible: true),
        ]
    }

    private func json(_ data: Data) throws -> [String: Any] {
        try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    func testAddsCaptionTracks() throws {
        let out = try DraftSubtitleWriter.apply(tracks(), to: Data(draft.utf8), replacePrevious: true)
        let d = try json(out)
        let tracks = try XCTUnwrap(d["tracks"] as? [[String: Any]])
        XCTAssertEqual(tracks.count, 3)
        XCTAssertEqual(tracks[1]["attribute"] as? Int, 2)  // hidden
        XCTAssertEqual(tracks[2]["attribute"] as? Int, 0)
        XCTAssertEqual(tracks[1]["type"] as? String, "text")

        let segments = try XCTUnwrap(tracks[1]["segments"] as? [[String: Any]])
        XCTAssertEqual(segments.count, 2)
        let range = try XCTUnwrap(segments[1]["target_timerange"] as? [String: Any])
        XCTAssertEqual(range["start"] as? Int, 2_000_000)
        XCTAssertEqual(range["duration"] as? Int, 1_000_000)  // 3.01 s snaps to the 3 s frame
        XCTAssertEqual(segments[0]["track_render_index"] as? Int, 1)

        let materials = try XCTUnwrap(d["materials"] as? [String: Any])
        let texts = try XCTUnwrap(materials["texts"] as? [[String: Any]])
        XCTAssertEqual(texts.count, 3)
        XCTAssertEqual((materials["material_animations"] as? [Any])?.count, 3)
        let first = try XCTUnwrap(texts.first { $0["id"] as? String == segments[0]["material_id"] as? String })
        XCTAssertEqual(first["type"] as? String, "subtitle")
        XCTAssertEqual(first["language"] as? String, "ru-RU")
        let content = try json(Data(try XCTUnwrap(first["content"] as? String).utf8))
        XCTAssertEqual(content["text"] as? String, "Привет, мир")
        let style = try XCTUnwrap((content["styles"] as? [[String: Any]])?.first)
        XCTAssertEqual(style["range"] as? [Int], [0, 11])

        // Untouched parts stay byte-for-byte, and floats keep their decimal point.
        let text = String(decoding: out, as: UTF8.self)
        XCTAssertTrue(text.hasPrefix(#"{"id":"D","fps":30.0,"duration":10000000,"tracks":[{"id":"V","type":"video","flag":0,"attribute":0,"segments":[]},"#))
        XCTAssertTrue(text.hasSuffix(#""canvas_config":{"ratio":"original","width":1080,"height":1920}}"#))
        XCTAssertTrue(text.contains(#""line_max_width":0.82"#))
        XCTAssertTrue(text.contains(#""font_size":8.0"#))
    }

    func testReplacesPreviousRun() throws {
        let once = try DraftSubtitleWriter.apply(tracks(), to: Data(draft.utf8), replacePrevious: true)
        let twice = try DraftSubtitleWriter.apply(tracks(), to: once, replacePrevious: true)
        let d = try json(twice)
        XCTAssertEqual((d["tracks"] as? [Any])?.count, 3)
        let materials = try XCTUnwrap(d["materials"] as? [String: Any])
        XCTAssertEqual((materials["texts"] as? [Any])?.count, 3)
        XCTAssertEqual((materials["material_animations"] as? [Any])?.count, 3)

        let kept = try DraftSubtitleWriter.apply(tracks(), to: once, replacePrevious: false)
        XCTAssertEqual((try json(kept)["tracks"] as? [Any])?.count, 5)
    }

    func testCopiesStyleOfExistingCaption() throws {
        let styled = try DraftSubtitleWriter.apply(tracks(), to: Data(draft.utf8), replacePrevious: true)
        // Make the generated captions look like the user's own: bigger and higher.
        let text = String(decoding: styled, as: UTF8.self)
            .replacingOccurrences(of: #"\"size\":8.0"#, with: #"\"size\":13.0"#)
            .replacingOccurrences(of: #""y":-0.65"#, with: #""y":-0.4"#)
            .replacingOccurrences(of: DraftSubtitleWriter.trackNamePrefix, with: "Mine")
        let out = try DraftSubtitleWriter.apply(Array(tracks().prefix(1)), to: Data(text.utf8), replacePrevious: true)
        let d = try json(out)
        let track = try XCTUnwrap((d["tracks"] as? [[String: Any]])?.last)
        let segment = try XCTUnwrap((track["segments"] as? [[String: Any]])?.first)
        let transform = try XCTUnwrap((segment["clip"] as? [String: Any])?["transform"] as? [String: Any])
        XCTAssertEqual(transform["y"] as? Double, -0.4)
        let texts = try XCTUnwrap((d["materials"] as? [String: Any])?["texts"] as? [[String: Any]])
        let material = try XCTUnwrap(texts.first { $0["id"] as? String == segment["material_id"] as? String })
        XCTAssertEqual(material["font_size"] as? Double, 13)
    }

    func testScannerHandlesStringsWithBrackets() throws {
        let data = Data(#"{"a":"x]}\"[{","b":[1,{"c":"]"},[2]],"d":null}"#.utf8)
        let s = JSONScanner(data)
        let root = try s.root()
        XCTAssertEqual(try s.members(root).map(\.key), ["a", "b", "d"])
        let b = try XCTUnwrap(try s.value("b", in: root))
        XCTAssertEqual(try s.elements(b).count, 3)
    }

    func testSegmenterSplitsAtPauses() {
        let words = [("Привет", 0.0, 0.4), ("мир", 0.45, 0.8), ("как", 2.0, 2.2), ("дела", 2.25, 2.6)]
            .map { TimedWord(text: $0.0, start: $0.1, end: $0.2) }
        let cues = Segmenter.cues(from: words, language: "ru")
        XCTAssertEqual(cues.map(\.text), ["Привет мир.", "Как дела."])
        XCTAssertEqual(cues[0].start, 0)
    }
}
