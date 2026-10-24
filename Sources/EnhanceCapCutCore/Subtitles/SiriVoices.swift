import AppKit
import AVFoundation

public enum SiriVoicesError: LocalizedError {
    case unavailable
    case failed(String)

    public var errorDescription: String? {
        switch self {
        case .unavailable: return "Siri voices need Xcode installed."
        case .failed(let m): return "Siri voice failed: \(m)"
        }
    }
}

/// Siri voices through the Swift interpreter from Xcode. Ported from SubDub.
///
/// macOS only gives Siri voices to programs signed by Apple. The Swift interpreter is, so we run
/// Tools/SiriVoice/siri-voice.swift with it (the app bundle carries a copy in its resources).
public enum SiriVoices {
    /// DubVoice ids of Siri voices start with this prefix.
    public static let idPrefix = "siri:"

    /// Xcode's Developer folder (the Command Line Tools alone are not enough on every setup).
    static var developerDir: URL? {
        let candidates = [NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.apple.dt.Xcode"),
                          URL(fileURLWithPath: "/Applications/Xcode.app")].compactMap { $0 }
        return candidates
            .map { $0.appendingPathComponent("Contents/Developer") }
            .first { FileManager.default.fileExists(atPath: $0.appendingPathComponent("usr/bin").path) }
    }

    /// The helper: ECC_SIRI_SCRIPT, the app bundle's copy, or the repo's Tools folder for `swift build` products.
    static var scriptURL: URL? {
        if let path = ProcessInfo.processInfo.environment["ECC_SIRI_SCRIPT"] { return URL(fileURLWithPath: path) }
        if let url = Bundle.main.url(forResource: "siri-voice", withExtension: "swift") { return url }
        var dir = Bundle.main.executableURL?.resolvingSymlinksInPath().deletingLastPathComponent()
        for _ in 0..<6 {
            guard let d = dir else { break }
            let url = d.appendingPathComponent("Tools/SiriVoice/siri-voice.swift")
            if FileManager.default.fileExists(atPath: url.path) { return url }
            dir = d.deletingLastPathComponent()
        }
        return nil
    }

    public static var isAvailable: Bool { developerDir != nil && scriptURL != nil }

    /// Downloaded Siri voices. Takes a few seconds: the interpreter compiles the script first.
    public static func list() async -> [DubVoice] {
        guard isAvailable, let output = try? await run(["--list-json"]),
              let json = output.split(separator: "\n").last(where: { $0.hasPrefix("[") }),
              let items = try? JSONSerialization.jsonObject(with: Data(json.utf8)) as? [[String: String]] else { return [] }
        return items.compactMap { d in
            guard let id = d["id"], let name = d["name"], let language = d["language"] else { return nil }
            return DubVoice(siriID: id, name: name, language: language, gender: d["gender"].flatMap(VoiceGender.init(rawValue:)))
        }
    }

    /// Renders a voice-over track with a Siri voice. Same timing rules as `Dubber.render`.
    static func render(cues: [CaptionCue], voiceID: String, totalDuration: Double, to url: URL,
                       progress: @escaping (Double) -> Void) async throws -> [ClosedRange<Double>] {
        guard isAvailable else { throw SiriVoicesError.unavailable }
        let job: [String: Any] = [
            "voice": String(voiceID.dropFirst(idPrefix.count)),
            "output": url.path,
            "totalSeconds": totalDuration,
            "cues": cues.map { ["start": $0.start, "end": $0.end, "text": $0.text.replacingOccurrences(of: "\n", with: " ")] },
        ]
        let jobURL = FileManager.default.temporaryDirectory.appendingPathComponent("ecc-siri-\(UUID().uuidString).json")
        try JSONSerialization.data(withJSONObject: job).write(to: jobURL)
        defer { try? FileManager.default.removeItem(at: jobURL) }

        let output = try await run(["--render", jobURL.path]) { line in
            // "PROGRESS i n"
            let parts = line.split(separator: " ")
            if parts.count == 3, parts[0] == "PROGRESS", let i = Double(parts[1]), let n = Double(parts[2]), n > 0 {
                progress(i / n)
            }
        }
        guard let line = output.split(separator: "\n").last(where: { $0.hasPrefix("RANGES ") }),
              let ranges = try? JSONSerialization.jsonObject(with: Data(line.dropFirst(7).utf8)) as? [[Double]] else {
            throw SiriVoicesError.failed("no result from the Siri helper")
        }
        return ranges.compactMap { $0.count == 2 && $0[1] >= $0[0] ? $0[0] ... $0[1] : nil }
    }

    /// Speaks a short sample (the window's preview button).
    public static func preview(_ text: String, voiceID: String) async throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("ecc-siri-preview.wav")
        _ = try await render(cues: [CaptionCue(start: 0, end: 4, text: text)], voiceID: voiceID, totalDuration: 0.1, to: url) { _ in }
        await MainActor.run {
            previewPlayer = try? AVAudioPlayer(contentsOf: url)
            previewPlayer?.play()
        }
    }

    @MainActor private static var previewPlayer: AVAudioPlayer?

    /// Runs the helper through `xcrun swift` and returns its standard output.
    private static func run(_ arguments: [String], onLine: ((String) -> Void)? = nil) async throws -> String {
        guard let developerDir, let scriptURL else { throw SiriVoicesError.unavailable }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/xcrun")
        process.arguments = ["swift", scriptURL.path] + arguments
        var env = ProcessInfo.processInfo.environment
        env["DEVELOPER_DIR"] = developerDir.path
        process.environment = env
        let out = Pipe(), err = Pipe()
        process.standardOutput = out
        process.standardError = err

        let collected = Collector()
        out.fileHandleForReading.readabilityHandler = { handle in
            let chunk = String(decoding: handle.availableData, as: UTF8.self)
            for line in collected.append(chunk) { onLine?(line) }
        }
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                process.terminationHandler = { p in
                    out.fileHandleForReading.readabilityHandler = nil
                    let rest = String(decoding: out.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
                    for line in collected.append(rest + "\n") { onLine?(line) }
                    if p.terminationStatus == 0 {
                        continuation.resume(returning: collected.text)
                    } else if p.terminationReason == .uncaughtSignal {
                        continuation.resume(throwing: CancellationError())
                    } else {
                        let message = String(decoding: err.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
                        continuation.resume(throwing: SiriVoicesError.failed(message.trimmingCharacters(in: .whitespacesAndNewlines)))
                    }
                }
                do { try process.run() } catch { continuation.resume(throwing: error) }
            }
        } onCancel: {
            if process.isRunning { process.terminate() }
        }
    }

    /// Thread-safe line splitter for the helper's output.
    private final class Collector: @unchecked Sendable {
        private let lock = NSLock()
        private var buffer = ""
        private(set) var text = ""

        func append(_ chunk: String) -> [String] {
            lock.lock(); defer { lock.unlock() }
            text += chunk
            buffer += chunk
            var lines = buffer.components(separatedBy: "\n")
            buffer = lines.removeLast()
            return lines.filter { !$0.isEmpty }
        }
    }
}
