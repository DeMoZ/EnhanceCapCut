import Foundation

/// The text tokenizer of Chatterbox Multilingual: the subset of a Hugging Face `tokenizer.json` that its
/// model uses — added tokens, an NFKD + "space → [SPACE]" normalizer, the `Whitespace` pre-tokenizer, a
/// character-level BPE and the template that wraps the ids (`<EXAGGERATION> <s> … </s> <START_SPEECH> ×2`).
/// Matches the `tokenizers` library's ids (checked in the tests against ids it produced).
///
/// Text is handled as Unicode scalars, not Swift `String`s: string comparison in Swift treats canonically
/// equivalent text as equal, while the tokenizer must tell "й" from "и" + U+0306.
struct ChatterboxTokenizer {
    typealias Scalars = [UInt32]

    private let vocab: [Scalars: Int]
    private let ranks: [Scalars: Int]
    private let unknown: Int
    /// Added tokens matched in the raw text ("[en]", "[laughter]", …), longest first.
    private let rawTokens: [(Scalars, Int)]
    /// Added tokens matched after normalization ("[SPACE]").
    private let normalizedTokens: [(Scalars, Int)]
    private let nfkd: Bool
    private let replacements: [(Scalars, Scalars)]
    private let prefix: [Int]
    private let suffix: [Int]

    init(url: URL) throws {
        guard let json = try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? NSDictionary,
              let model = json["model"] as? NSDictionary, let vocabulary = model["vocab"] as? NSDictionary else {
            throw VoiceClonerError.badModel("tokenizer.json")
        }
        var vocab: [Scalars: Int] = [:]
        for case let (key as NSString, value as NSNumber) in vocabulary { vocab[Self.scalars(key)] = value.intValue }
        self.vocab = vocab
        var ranks: [Scalars: Int] = [:]
        for (i, merge) in (model["merges"] as? [Any] ?? []).enumerated() {
            // Merges are "a b" strings in older files and ["a", "b"] arrays in newer ones. Split on the space
            // scalar: a string search would keep a space and a following combining mark together.
            var parts: [Scalars] = (merge as? [NSString])?.map(Self.scalars) ?? []
            if let text = merge as? NSString {
                let all = Self.scalars(text)
                if let space = all.firstIndex(of: 0x20) { parts = [Array(all[..<space]), Array(all[(space + 1)...])] }
            }
            guard parts.count == 2 else { continue }
            ranks[Self.pairKey(parts[0], parts[1])] = i
        }
        self.ranks = ranks
        unknown = (model["unk_token"] as? NSString).flatMap { vocab[Self.scalars($0)] } ?? 1

        var raw: [(Scalars, Int)] = [], normalized: [(Scalars, Int)] = []
        for case let token as NSDictionary in json["added_tokens"] as? NSArray ?? [] {
            guard let content = token["content"] as? NSString, let id = (token["id"] as? NSNumber)?.intValue else { continue }
            if (token["normalized"] as? NSNumber)?.boolValue == true {
                normalized.append((Self.scalars(content), id))
            } else {
                raw.append((Self.scalars(content), id))
            }
        }
        rawTokens = raw.sorted { $0.0.count > $1.0.count }
        normalizedTokens = normalized.sorted { $0.0.count > $1.0.count }

        var nfkd = false, replacements: [(Scalars, Scalars)] = []
        let normalizer = json["normalizer"] as? NSDictionary
        for case let step as NSDictionary in normalizer?["normalizers"] as? NSArray ?? (normalizer.map { [$0] } ?? []) {
            switch step["type"] as? String {
            case "NFKD": nfkd = true
            case "Replace":
                if let pattern = (step["pattern"] as? NSDictionary)?["String"] as? NSString, let content = step["content"] as? NSString {
                    replacements.append((Self.scalars(pattern), Self.scalars(content)))
                }
            default: break
            }
        }
        self.nfkd = nfkd
        self.replacements = replacements

        // The "single" template: special tokens before and after the text.
        let post = json["post_processor"] as? NSDictionary
        let specials = post?["special_tokens"] as? NSDictionary
        var before: [Int] = [], after: [Int] = [], seenText = false
        for case let item as NSDictionary in post?["single"] as? NSArray ?? [] {
            if item["Sequence"] != nil { seenText = true; continue }
            guard let name = (item["SpecialToken"] as? NSDictionary)?["id"] as? String,
                  let ids = (specials?[name] as? NSDictionary)?["ids"] as? [NSNumber] else { continue }
            if seenText { after += ids.map(\.intValue) } else { before += ids.map(\.intValue) }
        }
        prefix = before
        suffix = after
    }

    /// The model's input ids for `text` (already prefixed with the language tag, e.g. "[en]Hello.").
    func encode(_ text: String) -> [Int] {
        var ids = prefix
        for (piece, id) in split(Self.scalars(text as NSString), on: rawTokens) {
            if let id { ids.append(id); continue }
            var normalized = piece
            if nfkd { normalized = Self.scalars(Self.string(piece).decomposedStringWithCompatibilityMapping as NSString) }
            for (pattern, content) in replacements { normalized = Self.replace(pattern, with: content, in: normalized) }
            for (part, id) in split(normalized, on: normalizedTokens) {
                if let id { ids.append(id); continue }
                for word in Self.words(part) { ids += bpe(word) }
            }
        }
        return ids + suffix
    }

    /// Splits `text` into the given tokens (id set) and the text between them (id nil).
    private func split(_ text: Scalars, on tokens: [(Scalars, Int)]) -> [(Scalars, Int?)] {
        var result: [(Scalars, Int?)] = []
        var plain: Scalars = []
        var i = 0
        outer: while i < text.count {
            for (token, id) in tokens where text.count - i >= token.count && text[i..<(i + token.count)].elementsEqual(token) {
                if !plain.isEmpty { result.append((plain, nil)); plain = [] }
                result.append((token, id))
                i += token.count
                continue outer
            }
            plain.append(text[i])
            i += 1
        }
        if !plain.isEmpty { result.append((plain, nil)) }
        return result
    }

    /// The `Whitespace` pre-tokenizer (`\w+|[^\w\s]+`): runs of word characters, runs of other non-space characters.
    static func words(_ text: Scalars) -> [Scalars] {
        var words: [Scalars] = []
        var current: Scalars = [], currentIsWord = false
        for value in text {
            guard let scalar = Unicode.Scalar(value) else { continue }
            let p = scalar.properties
            if p.isWhitespace {
                if !current.isEmpty { words.append(current); current = [] }
                continue
            }
            let isWord = p.isAlphabetic || p.generalCategory == .decimalNumber || p.generalCategory == .connectorPunctuation
                || [.nonspacingMark, .spacingMark, .enclosingMark].contains(p.generalCategory)
                || value == 0x200C || value == 0x200D
            if !current.isEmpty && isWord != currentIsWord { words.append(current); current = [] }
            current.append(value)
            currentIsWord = isWord
        }
        if !current.isEmpty { words.append(current) }
        return words
    }

    /// Byte-pair merges over the word's characters (Unicode scalars), lowest rank first.
    private func bpe(_ word: Scalars) -> [Int] {
        var symbols = word.map { [$0] }
        while symbols.count > 1 {
            var best: (rank: Int, index: Int)?
            for i in 0..<(symbols.count - 1) {
                if let rank = ranks[Self.pairKey(symbols[i], symbols[i + 1])], rank < (best?.rank ?? .max) {
                    best = (rank, i)
                }
            }
            guard let best else { break }
            // Merge every occurrence of the pair, left to right, like the reference implementation.
            let a = symbols[best.index], b = symbols[best.index + 1]
            var merged: [Scalars] = []
            var i = 0
            while i < symbols.count {
                if i + 1 < symbols.count && symbols[i] == a && symbols[i + 1] == b {
                    merged.append(a + b)
                    i += 2
                } else {
                    merged.append(symbols[i])
                    i += 1
                }
            }
            symbols = merged
        }
        return symbols.map { vocab[$0] ?? unknown }
    }

    /// A merge pair as one key: the two halves joined by a value no scalar has.
    private static func pairKey(_ a: Scalars, _ b: Scalars) -> Scalars { a + [UInt32.max] + b }

    private static func scalars(_ s: NSString) -> Scalars { (s as String).unicodeScalars.map(\.value) }

    private static func string(_ s: Scalars) -> String {
        var view = String.UnicodeScalarView()
        view.append(contentsOf: s.compactMap(Unicode.Scalar.init))
        return String(view)
    }

    private static func replace(_ pattern: Scalars, with content: Scalars, in text: Scalars) -> Scalars {
        guard !pattern.isEmpty else { return text }
        var out: Scalars = []
        var i = 0
        while i < text.count {
            if text.count - i >= pattern.count && text[i..<(i + pattern.count)].elementsEqual(pattern) {
                out += content
                i += pattern.count
            } else {
                out.append(text[i])
                i += 1
            }
        }
        return out
    }
}
