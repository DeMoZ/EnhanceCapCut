import Foundation

/// A timeline-long buffer of interleaved stereo float samples on disk that partial mixes are added into.
///
/// AudioRenderer mixes large timelines in groups of tracks (one AVAssetReader can't take dozens of them, see
/// `AudioRenderer.maxTracksPerMix`); each group's output is summed here, then the total is read back in order.
/// The file is created sparse, so untouched stretches cost no disk space and read back as silence.
final class MixAccumulator {
    let url: URL
    let totalFrames: Int64
    private let handle: FileHandle
    private static let frameBytes = 2 * MemoryLayout<Float>.size

    init(url: URL, totalFrames: Int64) throws {
        self.url = url
        self.totalFrames = totalFrames
        guard FileManager.default.createFile(atPath: url.path, contents: nil) else {
            throw RenderError.writerFailed("could not create \(url.lastPathComponent)")
        }
        handle = try FileHandle(forUpdating: url)
        try handle.truncate(atOffset: UInt64(max(0, totalFrames)) * UInt64(Self.frameBytes))
    }

    deinit {
        try? handle.close()
        try? FileManager.default.removeItem(at: url)
    }

    /// Adds `frames` interleaved stereo frames starting at timeline frame `position`; frames outside the
    /// timeline are dropped.
    func add(_ samples: UnsafePointer<Float>, frames: Int, at position: Int64) throws {
        let start = max(position, 0), end = min(position + Int64(frames), totalFrames)
        guard end > start else { return }
        let count = Int(end - start) * 2
        var existing = try read(from: start, frames: Int(end - start))
        let offset = Int(start - position) * 2
        for i in 0..<count { existing[i] += samples[offset + i] }
        try handle.seek(toOffset: UInt64(start) * UInt64(Self.frameBytes))
        try existing.withUnsafeBytes { try handle.write(contentsOf: Data($0)) }
    }

    /// Reads `frames` interleaved stereo frames from timeline frame `position` (clamped to the timeline).
    func read(from position: Int64, frames: Int) throws -> [Float] {
        let frames = max(0, min(Int64(frames), totalFrames - position))
        var out = [Float](repeating: 0, count: Int(frames) * 2)
        guard frames > 0 else { return out }
        try handle.seek(toOffset: UInt64(position) * UInt64(Self.frameBytes))
        let data = try handle.read(upToCount: Int(frames) * Self.frameBytes) ?? Data()
        out.withUnsafeMutableBytes { dst in data.copyBytes(to: dst.bindMemory(to: UInt8.self), count: min(data.count, dst.count)) }
        return out
    }
}
