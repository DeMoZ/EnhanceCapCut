import EccCore
import Foundation
import NaturalLanguage

/// The Mac's word breaks for captions in scripts without spaces (Thai, Lao, Khmer, Burmese, Chinese, Japanese):
/// `NLTokenizer`, as before the Rust core (design decision 7, option b). Installed once; the core asks it when it
/// wraps caption lines and falls back to ICU4X when it returns nothing.
final class NativeWordBreaker: WordBreaker, @unchecked Sendable {
    /// Hands the breaker to the core (once per process).
    static let install: Void = setWordBreaker(breaker: NativeWordBreaker())

    func words(text: String, language: String) -> [WordRange]? {
        let tokenizer = NLTokenizer(unit: .word)
        tokenizer.string = text
        tokenizer.setLanguage(NLLanguage(rawValue: String(language.prefix(2))))
        var words: [WordRange] = []
        let utf8 = text.utf8
        tokenizer.enumerateTokens(in: text.startIndex..<text.endIndex) { range, _ in
            words.append(WordRange(start: UInt64(utf8.distance(from: utf8.startIndex, to: range.lowerBound)),
                                   end: UInt64(utf8.distance(from: utf8.startIndex, to: range.upperBound))))
            return true
        }
        return words
    }
}
