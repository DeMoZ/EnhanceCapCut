import Foundation
import Translation

/// A subtitle language option shown in the subtitles window.
public struct SubtitleLanguage: Identifiable, Hashable, Sendable {
    /// Code used for file names and track names: "en", "th", "zh-Hans", …
    public let code: String
    public let language: Locale.Language
    public let displayName: String

    public var id: String { code }

    public init(_ language: Locale.Language) {
        self.language = language
        let lang = language.languageCode?.identifier ?? "und"
        switch (lang, language.script?.identifier, language.region?.identifier) {
        case ("zh", "Hant", _), ("zh", _, "TW"), ("zh", _, "HK"): code = "zh-Hant"
        case ("zh", _, _): code = "zh-Hans"
        case ("pt", _, "PT"): code = "pt-PT"
        case ("pt", _, _): code = "pt-BR"
        case ("en", _, "GB"): code = "en-GB"
        default: code = lang
        }
        let name = Locale(identifier: Localization.language.rawValue).localizedString(forIdentifier: code) ?? code
        displayName = name.prefix(1).uppercased() + name.dropFirst()
    }

    public var nativeName: String {
        let name = Locale(identifier: code).localizedString(forIdentifier: code) ?? code
        return name.prefix(1).uppercased() + name.dropFirst()
    }
}

public enum TranslatorError: LocalizedError {
    case unsupportedPair(String, String)
    case notInstalled(String)

    public var errorDescription: String? {
        switch self {
        case .unsupportedPair(let a, let b): return L("Apple Translation doesn't support {0} → {1}.", a, b)
        case .notInstalled(let l): return L("{0} isn't downloaded for offline translation.", l)
        }
    }
}

/// Something that can translate a batch of strings. The app provides an implementation
/// backed by SwiftUI's `translationTask`, which can show the system download prompt.
public protocol TranslationProvider: AnyObject {
    func translate(_ texts: [String], from source: Locale.Language, to target: SubtitleLanguage) async throws -> [String]
}

/// Offline translation using languages that are already installed. Works without any UI.
@available(macOS 26, *)
public final class InstalledTranslationProvider: TranslationProvider {
    public init() {}

    public func translate(_ texts: [String], from source: Locale.Language, to target: SubtitleLanguage) async throws -> [String] {
        let status = await LanguageAvailability().status(from: source, to: target.language)
        switch status {
        case .installed: break
        case .supported: throw TranslatorError.notInstalled(target.displayName)
        default: throw TranslatorError.unsupportedPair(source.minimalIdentifier, target.code)
        }
        let session = TranslationSession(installedSource: source, target: target.language)
        return try await Translator.run(session: session, texts: texts)
    }
}

public enum Translator {
    public static func supportedTargets() async -> [SubtitleLanguage] {
        var seen = Set<String>()
        return await LanguageAvailability().supportedLanguages
            .map(SubtitleLanguage.init)
            .filter { seen.insert($0.code).inserted }
            .sorted { $0.displayName < $1.displayName }
    }

    public static func run(session: TranslationSession, texts: [String]) async throws -> [String] {
        let requests = texts.enumerated().map {
            TranslationSession.Request(sourceText: $0.element.replacingOccurrences(of: "\n", with: " "),
                                       clientIdentifier: String($0.offset))
        }
        let responses = try await session.translations(from: requests)
        var result = texts
        for r in responses {
            if let id = r.clientIdentifier, let i = Int(id), i < result.count { result[i] = r.targetText }
        }
        return result
    }
}
