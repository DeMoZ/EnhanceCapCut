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

    func testWrapsThaiAtTheMacsWordsWithinTheLineLimit() throws {
        // The core wraps at NLTokenizer's words on the Mac (design decision 7, option b).
        NativeWordBreaker.install
        let text = "ผมได้ยินมาว่าปลาหมึกกล้วยและปลาหมึกกระดองต้องย่างแค่หนึ่งนาทีหรือยี่สิบนาทีเท่านั้น"
        let wrapped = try wrapCaption(text: text, language: "th")
        let lines = wrapped.split(separator: "\n").map(String.init)
        XCTAssertGreaterThan(lines.count, 2)
        XCTAssertEqual(lines.joined(), text)                       // nothing lost or added
        for line in lines { XCTAssertLessThanOrEqual(line.count, 17 + 4) }   // Thai's line limit
        for word in ["ปลาหมึก", "หนึ่ง", "ยี่สิบ", "เท่านั้น", "กระดอง"] {   // whole words, never cut between lines
            XCTAssertTrue(lines.contains { $0.contains(word) }, word)
        }
    }

    func testChunkerCutsInThePauses() {
        // 20 s of speech at 50 frames/s with silent half-second pauses at 5 s and 11 s.
        let energy = (0..<1000).map { i -> Float in (250..<275).contains(i) || (550..<575).contains(i) ? 0.001 : 1 }
        let cuts = SpeechChunker.cuts(energy: energy, frameDuration: 0.02)
        XCTAssertEqual(cuts.count, 3)
        XCTAssertEqual(cuts[0], 5.25, accuracy: 0.3)
        XCTAssertEqual(cuts[1], 11.25, accuracy: 0.3)
        // No pause left: the piece is cut at the longest allowed length.
        XCTAssertEqual(cuts[2], cuts[1] + SpeechChunker.Lengths.dictation.max, accuracy: 0.05)
        XCTAssertEqual(SpeechChunker.cuts(energy: Array(energy.prefix(400)), frameDuration: 0.02), [])
    }

    func testChunkerSeparatesOnlyWideStereo() {
        let voice = (0..<1000).map { Float(sin(Double($0) * 0.1)) }
        let other = (0..<1000).map { Float(cos(Double($0) * 0.37)) }
        XCTAssertFalse(SpeechChunker.isWideStereo([voice]))
        XCTAssertFalse(SpeechChunker.isWideStereo([voice, voice.map { $0 * 0.9 }]))
        XCTAssertTrue(SpeechChunker.isWideStereo([voice, zip(voice, other).map { $0 * 0.5 + $1 }]))
    }

    func testWhisperChunksStayWithinItsWindow() {
        let samples = (0..<(16_000 * 65)).map { i -> Float in
            let level: Float = i / 16_000 % 20 == 0 ? 0.01 : 1
            return Float(sin(Double(i) * 0.05)) * level
        }
        let ranges = SpeechChunker.ranges(samples, sampleRate: 16_000, lengths: .whisper)
        XCTAssertEqual(ranges.first?.lowerBound, 0)
        XCTAssertEqual(ranges.last?.upperBound, Optional(samples.count))
        for (a, b) in zip(ranges, ranges.dropFirst()) { XCTAssertEqual(a.upperBound, b.lowerBound) }
        for r in ranges { XCTAssertLessThanOrEqual(Double(r.count), 16_000 * SpeechChunker.Lengths.whisper.max + 1) }
    }

    func testWhisperAnnotationsAreDropped() {
        let words = ["*смех*", "Привет,", "[звук", "мотора]", "мир", "♪", "(аплодисменты).", "пока"]
            .enumerated().map { TimedWord(text: $1, start: Double($0), end: Double($0) + 0.5) }
        XCTAssertEqual(WhisperTranscriber.withoutAnnotations(words).map(\.text), ["Привет,", "мир", "пока"])
    }
}
