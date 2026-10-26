import Foundation

/// A model file downloaded on first use.
public struct ModelFile: Sendable {
    public var folder: String
    public var name: String
    public var url: URL
    public var approximateBytes: Int64
}

/// Models too big to ship, downloaded once from Hugging Face into `root`. The same files and folder as the Windows
/// version's `ModelStore` (which, run on a Mac, uses the same path).
public enum ModelStore {
    /// Whisper large-v3-turbo for whisper.cpp, 5-bit: speech recognition.
    public static let whisper = ModelFile(
        folder: "whisper", name: "ggml-large-v3-turbo-q5_0.bin",
        url: URL(string: "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin")!,
        approximateBytes: 574_041_195)

    /// Chatterbox Multilingual V3 (Resemble AI, MIT) as ONNX, with the 4-bit language model: voice cloning
    /// (`VoiceCloner`). Pinned to a revision of the export so the files never change under us.
    public static let voiceCloning: [ModelFile] = {
        let base = "https://huggingface.co/Masterx/chatterbox-multilingual-v3-ONNX/resolve/19b94afe41de738624b2f3afc75b4cd56221fbeb/"
        return [
            ("tokenizer.json", 72_765),
            ("onnx/speech_encoder.onnx", 1_204_600), ("onnx/speech_encoder.onnx_data", 591_274_880),
            ("onnx/embed_tokens.onnx", 17_616), ("onnx/embed_tokens.onnx_data", 68_808_704),
            ("onnx/language_model_q4.onnx", 228_633), ("onnx/language_model_q4.onnx_data", 353_621_248),
            ("onnx/conditional_decoder.onnx", 6_371_670), ("onnx/conditional_decoder.onnx_data", 533_970_816),
        ].map { ModelFile(folder: "chatterbox-v3", name: $0.0, url: URL(string: base + $0.0)!, approximateBytes: Int64($0.1)) }
    }()

    /// `~/Library/Application Support/EnhanceCapCut/Models`; the `ECC_MODELS` environment variable overrides it.
    public static var root: URL {
        if let custom = ProcessInfo.processInfo.environment["ECC_MODELS"], !custom.isEmpty {
            return URL(fileURLWithPath: custom, isDirectory: true)
        }
        return FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("EnhanceCapCut/Models", isDirectory: true)
    }

    public static func path(of file: ModelFile) -> URL {
        root.appendingPathComponent(file.folder, isDirectory: true).appendingPathComponent(file.name)
    }

    public static func isDownloaded(_ file: ModelFile) -> Bool {
        FileManager.default.fileExists(atPath: path(of: file).path)
    }

    public static func isDownloaded(_ files: [ModelFile]) -> Bool { files.allSatisfy { isDownloaded($0) } }

    /// The folder a set of files lives in.
    public static func folder(of files: [ModelFile]) -> URL {
        root.appendingPathComponent(files.first?.folder ?? "", isDirectory: true)
    }

    public static func approximateBytes(_ files: [ModelFile]) -> Int64 { files.reduce(0) { $0 + $1.approximateBytes } }

    /// Downloads the missing `files`; `progress` gets 0…1 over all of them.
    public static func ensure(_ files: [ModelFile], progress: @escaping @Sendable (Double) -> Void) async throws {
        let missing = files.filter { !isDownloaded($0) }
        let total = Double(max(1, approximateBytes(missing)))
        var done: Int64 = 0
        for file in missing {
            let before = Double(done)
            try await ensure(file) { p in progress(min(1, (before + p * Double(file.approximateBytes)) / total)) }
            done += file.approximateBytes
        }
        progress(1)
    }

    /// Downloads `file` unless it's there; `progress` gets 0…1. Cancelling the task cancels the download.
    public static func ensure(_ file: ModelFile, progress: @escaping @Sendable (Double) -> Void) async throws {
        let destination = path(of: file)
        guard !FileManager.default.fileExists(atPath: destination.path) else { return }
        let observer = DownloadObserver(progress: progress)
        let (temporary, response) = try await URLSession.shared.download(from: file.url, delegate: observer)
        if let http = response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
            try? FileManager.default.removeItem(at: temporary)
            throw URLError(.badServerResponse, userInfo: [NSLocalizedDescriptionKey: "Download of \(file.name) failed (HTTP \(http.statusCode))"])
        }
        try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? FileManager.default.removeItem(at: destination)
        try FileManager.default.moveItem(at: temporary, to: destination)
        progress(1)
    }
}

/// Reports a download task's progress.
private final class DownloadObserver: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    let progress: @Sendable (Double) -> Void
    var observation: NSKeyValueObservation?

    init(progress: @escaping @Sendable (Double) -> Void) { self.progress = progress }

    func urlSession(_ session: URLSession, didCreateTask task: URLSessionTask) {
        observation = task.progress.observe(\.fractionCompleted) { [progress] p, _ in progress(p.fractionCompleted) }
    }
}
