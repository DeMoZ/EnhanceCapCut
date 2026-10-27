import EnhanceCapCutCore
import XCTest

/// The translation tables against the code: every `L("…")` key of the Swift sources has a translation with the same
/// `{n}` placeholders and `**` marks, and no table keeps keys that neither app uses any more. The Windows version's
/// tests (LocalizationTests.cs) check its keys against the same tables.
final class LocalizationTests: XCTestCase {
    static let repo = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()

    func testEveryKeyIsTranslated() throws {
        let keys = try Self.keys(in: ["Sources/EnhanceCapCut", "Sources/EnhanceCapCutCore"], fileExtension: "swift")
        XCTAssertGreaterThan(keys.count, 200)
        for language in Localization.Language.allCases where language != .english {
            let table = try XCTUnwrap(Self.table(language), "\(language.rawValue).json")
            for key in keys.sorted() {
                guard let text = table[key] else {
                    XCTFail("\(language.rawValue).json has no \(key.debugDescription)")
                    continue
                }
                XCTAssertEqual(Self.placeholders(text), Self.placeholders(key), "placeholders of \(key.debugDescription)")
                XCTAssertEqual(text.components(separatedBy: "**").count, key.components(separatedBy: "**").count,
                               "** marks of \(key.debugDescription)")
            }
        }
    }

    func testNoUnusedKeys() throws {
        var used = try Self.keys(in: ["Sources/EnhanceCapCut", "Sources/EnhanceCapCutCore"], fileExtension: "swift")
        used.formUnion(try Self.keys(in: ["windows/src"], fileExtension: "cs"))
        // The Windows version's language names are looked up by their English name (`SubtitleLanguage.Title`).
        let languages = try String(contentsOf: Self.repo.appendingPathComponent("windows/src/EnhanceCapCut.Draft/Subtitles.cs"), encoding: .utf8)
        used.formUnion(Self.matches(#"new\("[^"]+", "([^"]+)""#, in: languages))
        for language in Localization.Language.allCases where language != .english {
            let table = try XCTUnwrap(Self.table(language))
            XCTAssertEqual(Set(table.keys).subtracting(used).sorted(), [], "unused keys in \(language.rawValue).json")
        }
    }

    func testFormatting() {
        XCTAssertEqual(L("Exported {0} in {1}", "a.mp4", "0:12"), "Exported a.mp4 in 0:12")
        XCTAssertEqual(L("Tokens: {project} {date} {time} {res} {codec} {fps}"), "Tokens: {project} {date} {time} {res} {codec} {fps}")
    }

    func testSystemLanguage() {
        XCTAssertEqual(Localization.Language.system(preferred: ["ru-RU", "en-US"]), .russian)
        XCTAssertEqual(Localization.Language.system(preferred: ["de-DE", "ru"]), .russian)
        XCTAssertEqual(Localization.Language.system(preferred: ["de-DE"]), .english)
    }

    // MARK: Helpers

    static func table(_ language: Localization.Language) throws -> [String: String]? {
        let url = repo.appendingPathComponent("Localization/\(language.rawValue).json")
        return try JSONDecoder().decode([String: String].self, from: Data(contentsOf: url))
    }

    /// The keys of `L(...)` calls: a literal, or literals joined with `+` (the same rule as in LocalizationTests.cs).
    static func keys(in folders: [String], fileExtension: String) throws -> Set<String> {
        let literal = #""((?:[^"\\\n]|\\.)*)""#
        let call = try NSRegularExpression(pattern: #"(?<![A-Za-z0-9_])L\(\s*("# + literal + #"(?:\s*\+\s*"# + literal + #")*)"#)
        let part = try NSRegularExpression(pattern: literal)
        var keys = Set<String>()
        for folder in folders {
            let root = repo.appendingPathComponent(folder)
            let files = FileManager.default.enumerator(at: root, includingPropertiesForKeys: nil)?.compactMap { $0 as? URL } ?? []
            for file in files where file.pathExtension == fileExtension
                && !file.path.contains("/bin/") && !file.path.contains("/obj/") {
                let source = try String(contentsOf: file, encoding: .utf8) as NSString
                for match in call.matches(in: source as String, range: NSRange(location: 0, length: source.length)) {
                    let joined = source.substring(with: match.range(at: 1))
                    let pieces = part.matches(in: joined, range: NSRange(location: 0, length: (joined as NSString).length))
                        .map { (joined as NSString).substring(with: $0.range(at: 1)) }
                    keys.insert(unescape(pieces.joined()))
                }
            }
        }
        return keys
    }

    static func unescape(_ s: String) -> String {
        s.replacingOccurrences(of: #"\""#, with: "\"").replacingOccurrences(of: #"\\"#, with: "\\")
    }

    static func placeholders(_ s: String) -> [String] { matches(#"(\{\d+\})"#, in: s).sorted() }

    static func matches(_ pattern: String, in s: String) -> [String] {
        let regex = try! NSRegularExpression(pattern: pattern)
        let ns = s as NSString
        return regex.matches(in: s, range: NSRange(location: 0, length: ns.length)).map { ns.substring(with: $0.range(at: 1)) }
    }
}
