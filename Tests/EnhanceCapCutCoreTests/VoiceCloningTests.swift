import XCTest
@testable import EnhanceCapCutCore

final class VoiceCloningTests: XCTestCase {
    /// A tokenizer.json with the same structure as Chatterbox's, small enough to read.
    private func miniTokenizer() throws -> ChatterboxTokenizer {
        let json = #"""
            {"added_tokens":[{"id":0,"content":"[STOP]","normalized":false,"special":true},
                             {"id":2,"content":"[SPACE]","normalized":true,"special":true},
                             {"id":3,"content":"[en]","normalized":false,"special":false}],
             "normalizer":{"type":"Sequence","normalizers":[{"type":"NFKD"},
                           {"type":"Replace","pattern":{"String":" "},"content":"[SPACE]"}]},
             "pre_tokenizer":{"type":"Whitespace"},
             "post_processor":{"type":"TemplateProcessing",
                "single":[{"SpecialToken":{"id":"BOS","type_id":0}},{"Sequence":{"id":"A","type_id":0}},
                          {"SpecialToken":{"id":"EOS","type_id":0}}],
                "special_tokens":{"BOS":{"id":"BOS","ids":[9]},"EOS":{"id":"EOS","ids":[0]}}},
             "model":{"type":"BPE","unk_token":"[UNK]","vocab":{"[STOP]":0,"[UNK]":1,"[SPACE]":2,"a":10,"b":11,"c":12,
                      "ab":13,"abc":14,"и":15,"\u0306":16,"и\u0306":17,".":18},
                      "merges":["a b","ab c","и \u0306"]}}
            """#
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("tokenizer-\(UUID().uuidString).json")
        try Data(json.utf8).write(to: url)
        defer { try? FileManager.default.removeItem(at: url) }
        return try ChatterboxTokenizer(url: url)
    }

    func testTokenizerMechanics() throws {
        let t = try miniTokenizer()
        // Template around the text, raw added token, spaces as [SPACE], merges by rank, unknown characters.
        XCTAssertEqual(t.encode("[en]abc ab."), [9, 3, 14, 2, 13, 18, 0])
        XCTAssertEqual(t.encode("[en]xa"), [9, 3, 1, 10, 0])
        // NFKD splits "й" into "и" + U+0306, which the merge joins back.
        XCTAssertEqual(t.encode("й"), [9, 17, 0])
    }

    /// The real tokenizer against ids from Hugging Face's `tokenizers` library (skipped until the model is downloaded).
    func testTokenizerMatchesReference() throws {
        let url = ModelStore.folder(of: ModelStore.voiceCloning).appendingPathComponent("tokenizer.json")
        guard FileManager.default.fileExists(atPath: url.path) else { throw XCTSkip("voice cloning model not downloaded") }
        let t = try ChatterboxTokenizer(url: url)
        let cases: [(String, [Int])] = [
        ("[en]Today we are riding quad bikes through the jungle, and honestly, it is a lot more fun than I expected.", [6563, 255, 708, 296, 207, 88, 2, 100, 2, 127, 2, 31, 83, 52, 2, 194, 68, 2, 15, 22, 24, 61, 2, 40, 31, 202, 2, 42, 2, 23, 97, 20, 64, 7, 2, 53, 2, 21, 47, 218, 70, 7, 2, 60, 2, 54, 2, 14, 2, 99, 33, 2, 244, 2, 19, 97, 2, 40, 43, 2, 285, 2, 169, 124, 16, 188, 9, 0, 6561, 6561]),
        ("[ru]Первым делом мы заезжаем в старый карьер, который набрал много воды. Это будет нашей грязевой ванной!", [6563, 255, 716, 1106, 1127, 1137, 1124, 1148, 1133, 2, 1126, 1127, 1132, 1135, 1133, 2, 1133, 1148, 2, 1129, 1122, 1127, 1129, 1128, 1122, 1127, 1133, 2, 1124, 2, 1138, 1139, 1122, 1137, 1148, 1130, 771, 2, 1131, 1122, 1137, 1149, 1127, 1137, 7, 2, 1131, 1135, 1139, 1135, 1137, 1148, 1130, 771, 2, 1134, 1122, 1123, 1137, 1122, 1132, 2, 1133, 1134, 1135, 1125, 1135, 2, 1124, 1135, 1126, 1148, 9, 2, 1119, 1139, 1135, 2, 1123, 1140, 1126, 1127, 1139, 2, 1134, 1122, 1145, 1127, 1130, 771, 2, 1125, 1137, 1152, 1129, 1127, 1124, 1135, 1130, 771, 2, 1124, 1122, 1134, 1134, 1135, 1130, 771, 3, 0, 6561, 6561]),
        ("[ru]Ёжик и йогурт — «ёлки-палки», 2026 год.", [6563, 255, 716, 1082, 762, 1128, 1130, 1131, 2, 1130, 2, 1130, 771, 1135, 1125, 1140, 1137, 1139, 2, 317, 2, 340, 1127, 762, 1132, 1131, 1130, 8, 1136, 1122, 1132, 1131, 1130, 356, 7, 2, 265, 263, 265, 269, 2, 1125, 1135, 1126, 9, 0, 6561, 6561]),
        ("[de]Als Erstes fahren wir in einen alten Steinbruch, der sich mit Wasser gefüllt hat. Das wird unser Schlammbad!", [6563, 255, 636, 277, 25, 32, 2, 281, 31, 63, 61, 2, 19, 14, 21, 46, 27, 2, 85, 31, 2, 41, 2, 18, 41, 50, 2, 56, 33, 50, 2, 295, 136, 41, 243, 34, 71, 7, 2, 234, 2, 212, 71, 2, 26, 60, 2, 299, 55, 32, 44, 2, 112, 19, 34, 762, 84, 33, 2, 21, 48, 9, 2, 280, 55, 2, 85, 31, 17, 2, 97, 32, 44, 2, 295, 71, 25, 122, 26, 15, 68, 3, 0, 6561, 6561]),
        ("[fr]Ça va? C'est l'été, où êtes-vous?", [6563, 255, 634, 279, 766, 14, 2, 35, 14, 13, 2, 279, 4, 218, 2, 25, 4, 18, 764, 136, 764, 7, 2, 45, 768, 2, 18, 769, 33, 61, 8, 35, 206, 13, 0, 6561, 6561]),
        ("[es]¿Qué tal? ¡Mañana vamos al río!", [6563, 255, 635, 360, 293, 34, 18, 764, 2, 33, 56, 13, 2, 330, 289, 43, 644, 43, 14, 2, 35, 14, 115, 32, 2, 56, 2, 101, 764, 28, 3, 0, 6561, 6561]),
        ("[pl]Zażółć gęślą jaźń.", [6563, 255, 717, 302, 14, 39, 773, 28, 764, 782, 16, 764, 2, 112, 772, 32, 764, 25, 14, 772, 2, 23, 14, 39, 764, 27, 764, 9, 0, 6561, 6561]),
        ("[tr]İstanbul'da ılık bir gün.", [6563, 255, 712, 285, 773, 63, 43, 15, 167, 4, 17, 14, 2, 779, 25, 779, 24, 2, 15, 98, 2, 20, 34, 762, 27, 9, 0, 6561, 6561]),
        ("[ar]مرحبا بكم في قناتي.", [6563, 255, 721, 1491, 1471, 1467, 1462, 1456, 2, 1462, 1489, 1491, 2, 1487, 1461, 2, 1488, 1492, 1456, 1464, 1461, 9, 0, 6561, 6561]),
        ("[hi]नमस्ते, आप कैसे हैं?", [6563, 255, 722, 1706, 1712, 1720, 1740, 1702, 1734, 7, 2, 1672, 1708, 2, 1687, 1735, 1720, 1734, 2, 1721, 1735, 1668, 13, 0, 6561, 6561]),
        ("[ko]안녕하세요, 반갑습니다.", [6563, 255, 724, 1794, 1880, 1954, 1785, 1886, 1971, 1801, 1880, 1792, 1885, 1794, 1892, 7, 2, 1790, 1880, 1954, 1783, 1880, 1967, 1792, 1898, 1967, 1785, 1900, 1786, 1880, 9, 0, 6561, 6561]),
        ("[el]Καλημέρα σας, τι κάνετε;", [6563, 255, 711, 1012, 1024, 1033, 1026, 765, 1025, 764, 1038, 1024, 2, 1040, 1024, 1039, 7, 2, 1041, 1006, 2, 1032, 1024, 764, 1034, 1025, 1041, 1025, 12, 0, 6561, 6561]),
        ("[en]Price: $5.99 (50% off) & more... #wow @me ~tilde_under 3x4=12", [6563, 255, 708, 292, 31, 91, 18, 11, 2, 258, 268, 9, 272, 272, 2, 5, 268, 263, 259, 2, 58, 19, 6, 2, 260, 2, 244, 9, 9, 9, 2, 257, 36, 69, 2, 276, 80, 2, 312, 78, 79, 18, 307, 97, 234, 2, 266, 37, 267, 274, 264, 265, 0, 6561, 6561]),
        ("[en]Tabs\tand\u{00A0}nbsp, emoji 😀 and [laughter] tag.", [6563, 255, 708, 296, 109, 32, 53, 2, 27, 15, 248, 7, 2, 18, 115, 23, 22, 2, 1, 2, 53, 2, 607, 2, 33, 143, 9, 0, 6561, 6561]),
        ]
        for (text, ids) in cases { XCTAssertEqual(t.encode(text), ids, text) }
    }

    func testTextPreparation() {
        XCTAssertEqual(VoiceCloner.prepare("hello   world"), "Hello world.")
        XCTAssertEqual(VoiceCloner.prepare("Wait… what: no — yes"), "Wait,  what, no - yes.")
        XCTAssertEqual(VoiceCloner.prepare("Really?"), "Really?")
        XCTAssertEqual(VoiceCloner.prepare("«Привет»"), "\"Привет\".")
        XCTAssertEqual(VoiceCloner.prepare("  "), ".")
    }

    func testLanguages() {
        XCTAssertEqual(VoiceCloner.modelLanguage("pt-BR"), "pt")
        XCTAssertEqual(VoiceCloner.modelLanguage("nb"), "no")
        XCTAssertEqual(VoiceCloner.modelLanguage("ru"), "ru")
        XCTAssertNil(VoiceCloner.modelLanguage("zh-Hans"))
        XCTAssertNil(VoiceCloner.modelLanguage("th"))
    }

    /// The same numbers as the Windows version's generator.
    func testRandomGenerator() {
        var r = SplitMix64(seed: 1)
        XCTAssertEqual([r.next(), r.next(), r.next()], [10_451_216_379_200_822_465, 13_757_245_211_066_428_519, 17_911_839_290_282_890_590])
    }

    func testSamplingPenalisesRepeatsAndDropsUnlikelyTokens() {
        var r = SplitMix64(seed: 7)
        var counts = [0, 0, 0]
        // Token 1 is far below min-p; token 2 was already said, which makes it less likely than token 0.
        for _ in 0..<400 { counts[VoiceCloner.sample([5, -10, 5], previous: [2], random: &r)] += 1 }
        XCTAssertEqual(counts[1], 0)
        XCTAssertGreaterThan(counts[0], counts[2])
        XCTAssertGreaterThan(counts[2], 0)
    }

    func testTrimsQuietEnds() {
        let rate = Int(VoiceCloner.sampleRate)
        var samples = [Float](repeating: 0.0001, count: rate)
        for i in (rate / 4)..<(rate / 2) { samples[i] = sin(Float(i) * 0.1) * 0.5 }
        let trimmed = VoiceCloner.trimQuietEnds(samples)
        // A quarter second of speech plus 40 ms either side (to the 10 ms frame).
        XCTAssertEqual(Double(trimmed.count) / Double(rate), 0.25 + 0.08, accuracy: 0.011)
    }
}

final class TimeStretchTests: XCTestCase {
    /// Zero crossings per second: the pitch of a pure tone.
    private func frequency(_ x: [Float], rate: Double) -> Double {
        var crossings = 0
        for i in 1..<x.count where (x[i - 1] < 0) != (x[i] < 0) { crossings += 1 }
        return Double(crossings) / 2 / (Double(x.count) / rate)
    }

    func testSpeedsUpWithoutChangingPitch() {
        let rate = 24_000.0
        let tone = (0..<Int(rate * 2)).map { Float(sin(2 * Double.pi * 220 * Double($0) / rate) * 0.5) }
        let fast = TimeStretch.stretch(tone, rate: 1.35, sampleRate: rate)
        XCTAssertEqual(Double(fast.count), Double(tone.count) / 1.35, accuracy: 2)
        // Away from the faded ends the tone keeps its pitch and level.
        let middle = Array(fast[2400..<(fast.count - 2400)])
        XCTAssertEqual(frequency(middle, rate: rate), 220, accuracy: 3)
        let peak = middle.map(abs).max() ?? 0
        XCTAssertEqual(Double(peak), 0.5, accuracy: 0.05)
    }

    func testRateOneIsUnchanged() {
        let x: [Float] = (0..<5000).map { Float($0 % 7) }
        XCTAssertEqual(TimeStretch.stretch(x, rate: 1, sampleRate: 24_000), x)
    }
}

final class ClonedDubTests: XCTestCase {
    func testLineError() {
        XCTAssertEqual(Dubber.lineError(expected: "Hello, world!", heard: "hello world"), 0)
        XCTAssertEqual(Dubber.lineError(expected: "Hello world", heard: "Hallo world"), 1.0 / 10, accuracy: 1e-6)
        XCTAssertEqual(Dubber.lineError(expected: "Ёлки", heard: ""), 1)
    }

    func testReferenceStartsAtTheLineAndIsAtLeastFourSeconds() {
        let rate = Int(VoiceCloner.sampleRate)
        let voice = [Float](repeating: 0, count: rate * 20)
        XCTAssertEqual(Dubber.referenceRange(for: CaptionCue(start: 2, end: 3, text: "x"), in: voice).count, 4 * rate)
        XCTAssertEqual(Dubber.referenceRange(for: CaptionCue(start: 2, end: 12, text: "x"), in: voice).count, 6 * rate)
        // Near the end the reference reaches back before the line.
        XCTAssertEqual(Dubber.referenceRange(for: CaptionCue(start: 19, end: 20, text: "x"), in: voice).count, 4 * rate)
    }
}
