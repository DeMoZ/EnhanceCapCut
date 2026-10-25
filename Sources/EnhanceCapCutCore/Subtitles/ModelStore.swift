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
