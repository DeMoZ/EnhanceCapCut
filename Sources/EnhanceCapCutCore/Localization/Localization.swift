import Foundation

/// The app's interface language. Texts are written in English in the code and wrapped in `L(...)`; the translations
/// are in `Localization/<language>.json` at the repo root (English text → translated text), one file shared with the
/// Windows version (`Localization.cs`), so both apps show the same words. The app bundle carries a copy in
/// `Contents/Resources/Localization`.
///
/// Until the app calls `use(_:)`, everything stays English: the `ecc` CLI's output doesn't depend on the system.
public enum Localization {
    public enum Language: String, CaseIterable, Identifiable, Sendable {
        case english = "en", russian = "ru"

        public var id: String { rawValue }
        /// The language's own name, the same in every interface language.
        public var nativeName: String {
            switch self {
            case .english: "English"
            case .russian: "Русский"
            }
        }

        /// The first of the system's preferred languages that is one of ours; English otherwise.
        public static func system(preferred: [String] = Locale.preferredLanguages) -> Language {
            for tag in preferred {
                let base = tag.lowercased().split(separator: "-").first.map(String.init) ?? ""
                if let language = Language(rawValue: base) { return language }
            }
            return .english
        }
    }

    /// The user's choice: a language, or nil to follow the system (the app's "language" default).
    public static var choice: Language? {
        get { UserDefaults.standard.string(forKey: "language").flatMap(Language.init(rawValue:)) }
        set { UserDefaults.standard.set(newValue?.rawValue, forKey: "language") }
    }

    nonisolated(unsafe) public private(set) static var language: Language = .english
    nonisolated(unsafe) private static var table: [String: String] = [:]

    /// Switches the texts to `language` (the app does this once at launch).
    public static func use(_ language: Language) {
        self.language = language
        table = language == .english ? [:] : load(language) ?? [:]
    }

    /// The translation of `english`, or `english` itself.
    public static func translate(_ english: String) -> String { table[english] ?? english }

    /// The translation table for `language`: the app bundle's copy, or the repo's for `swift build` products and tests.
    public static func load(_ language: Language) -> [String: String]? {
        guard let url = tableURL(language), let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode([String: String].self, from: data)
    }

    static func tableURL(_ language: Language) -> URL? {
        if let url = Bundle.main.url(forResource: language.rawValue, withExtension: "json", subdirectory: "Localization") {
            return url
        }
        var dir = Bundle.main.executableURL?.resolvingSymlinksInPath().deletingLastPathComponent()
        for _ in 0..<6 {
            guard let d = dir else { break }
            let url = d.appendingPathComponent("Localization/\(language.rawValue).json")
            if FileManager.default.fileExists(atPath: url.path) { return url }
            dir = d.deletingLastPathComponent()
        }
        return nil
    }
}

/// `english` in the interface language, with `{0}`, `{1}`… replaced by `args`. The key is always a literal (or literals
/// joined with `+`): the tests find every key in the code and check that each language has it.
public func L(_ english: String, _ args: CustomStringConvertible...) -> String {
    var text = Localization.translate(english)
    for (i, arg) in args.enumerated() {
        text = text.replacingOccurrences(of: "{\(i)}", with: arg.description)
    }
    return text
}
