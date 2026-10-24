import AVFoundation
import CoreImage
import VideoToolbox

public struct ExportProgress: Sendable {
    public enum Phase: String, Sendable { case audio = "Mixing audio", loudness = "Normalizing loudness", video = "Rendering video", finishing = "Finishing" }
    public var phase: Phase
    /// 0...1 over the whole export.
    public var fraction: Double
    public var framesDone: Int
    public var framesTotal: Int
    public var elapsed: TimeInterval
}

/// Renders a timeline to a movie file: audio mix → optional loudness normalization → frame-by-frame
/// composition encoded with AVAssetWriter (hardware H.264/HEVC/ProRes via VideoToolbox).
public final class Exporter: @unchecked Sendable {
    public let timeline: Timeline
    public let settings: ExportSettings
    public let outputURL: URL
    /// Optional sub-range to export (µs), mainly for quick tests.
    public var range: TimeRange?
    public private(set) var warnings: [String] = []

    private let lock = NSLock()
    private var cancelled = false

    public init(timeline: Timeline, settings: ExportSettings, outputURL: URL) {
        self.timeline = settings.burnInSubtitles ? timeline : timeline.withoutSubtitles()
        self.settings = settings
        self.outputURL = outputURL
    }

    public func cancel() {
        lock.lock(); cancelled = true; lock.unlock()
    }

    private var isCancelled: Bool {
        lock.lock(); defer { lock.unlock() }
        return cancelled
    }

    public func run(progress: @escaping (ExportProgress) -> Void) async throws {
        let started = Date()
        let size = settings.outputSize(canvas: timeline.canvasSize)
        let fps = settings.fps(project: timeline.fps)
        let frameDuration = Self.frameDuration(fps)
        let span = range ?? TimeRange(start: 0, duration: timeline.duration)
        let frameCount = max(1, Int((Double(span.duration) / 1_000_000 * fps).rounded(.down)))

        let temp = FileManager.default.temporaryDirectory.appendingPathComponent("EnhanceCapCut-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: temp, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: temp) }

        func report(_ phase: ExportProgress.Phase, _ fraction: Double, _ done: Int = 0) {
            progress(ExportProgress(phase: phase, fraction: min(max(fraction, 0), 1), framesDone: done,
                                    framesTotal: frameCount, elapsed: Date().timeIntervalSince(started)))
        }

        // 1. Audio.
        report(.audio, 0)
        var audioURL = temp.appendingPathComponent("mix.caf")
        let audioTimeline = range.map { trimmedForAudio(timeline, to: $0) } ?? timeline
        try await AudioRenderer.render(audioTimeline, to: audioURL, isCancelled: { self.isCancelled }) { p in
            report(.audio, p * 0.08)
        }
        if settings.normalizeLoudness {
            report(.loudness, 0.08)
            let normalized = temp.appendingPathComponent("normalized.wav")
            try LoudnessNormalizer.normalize(input: audioURL, output: normalized, targetLUFS: settings.targetLUFS)
            audioURL = normalized
        }
        if isCancelled { throw RenderError.cancelled }

        // 2. Writer.
        try? FileManager.default.removeItem(at: outputURL)
        try FileManager.default.createDirectory(at: outputURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        let writer = try AVAssetWriter(outputURL: outputURL, fileType: settings.codec.fileType)
        let videoInput = AVAssetWriterInput(mediaType: .video, outputSettings: videoSettings(size: size, fps: fps))
        videoInput.expectsMediaDataInRealTime = false
        let pixelFormat = settings.codec == .h264 || settings.codec == .hevc
            ? kCVPixelFormatType_32BGRA : kCVPixelFormatType_64RGBAHalf
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: videoInput, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: pixelFormat,
            kCVPixelBufferWidthKey as String: Int(size.width),
            kCVPixelBufferHeightKey as String: Int(size.height),
            kCVPixelBufferIOSurfacePropertiesKey as String: [:],
            kCVPixelBufferMetalCompatibilityKey as String: true,
        ])
        guard writer.canAdd(videoInput) else { throw RenderError.writerFailed("video settings rejected") }
        writer.add(videoInput)

        let audioInput = AVAssetWriterInput(mediaType: .audio, outputSettings: audioSettings())
        audioInput.expectsMediaDataInRealTime = false
        guard writer.canAdd(audioInput) else { throw RenderError.writerFailed("audio settings rejected") }
        writer.add(audioInput)

        let audioReader = try AVAssetReader(asset: AVURLAsset(url: audioURL))
        guard let audioTrack = try await audioReader.asset.loadTracks(withMediaType: .audio).first else {
            throw RenderError.writerFailed("no mixed audio")
        }
        let audioOutput = AVAssetReaderTrackOutput(track: audioTrack, outputSettings: [
            AVFormatIDKey: kAudioFormatLinearPCM, AVLinearPCMBitDepthKey: 32, AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsNonInterleaved: false, AVLinearPCMIsBigEndianKey: false,
        ])
        audioReader.add(audioOutput)
        audioReader.startReading()

        guard writer.startWriting() else { throw RenderError.writerFailed(writer.error?.localizedDescription ?? "start") }
        writer.startSession(atSourceTime: .zero)

        // 3. Frames.
        let context = CIContext(options: [
            .workingColorSpace: CGColorSpace(name: CGColorSpace.extendedLinearSRGB)!,
            .workingFormat: CIFormat.RGBAh,
            .cacheIntermediates: false,
        ])
        let outputSpace = FrameCompositor.outputColorSpace
        let compositor = FrameCompositor(timeline: timeline, size: size)
        var audioDone = false
        var audioTime = CMTime.zero
        let audioEnd = CMTime(value: span.duration, timescale: 1_000_000)

        func pumpAudio(until limit: CMTime) {
            while !audioDone && audioInput.isReadyForMoreMediaData && (limit == .positiveInfinity || audioTime < limit) {
                guard let sample = audioOutput.copyNextSampleBuffer() else {
                    audioDone = true
                    audioInput.markAsFinished()
                    return
                }
                let pts = CMSampleBufferGetPresentationTimeStamp(sample)
                if pts >= audioEnd {
                    audioDone = true
                    audioInput.markAsFinished()
                    return
                }
                audioInput.append(sample)
                audioTime = pts + CMSampleBufferGetDuration(sample)
            }
        }

        for index in 0..<frameCount {
            if isCancelled {
                writer.cancelWriting()
                throw RenderError.cancelled
            }
            let pts = CMTimeMultiply(frameDuration, multiplier: Int32(index))
            // Keep audio about a second ahead of video so the writer can interleave.
            pumpAudio(until: pts + CMTime(seconds: 1, preferredTimescale: 600))
            while !videoInput.isReadyForMoreMediaData {
                if writer.status == .failed { throw RenderError.writerFailed(writer.error?.localizedDescription ?? "") }
                pumpAudio(until: .positiveInfinity)
                try await Task.sleep(nanoseconds: 2_000_000)
            }
            let t = span.start + Int64((pts.seconds * 1_000_000).rounded())
            let image = await compositor.frame(at: t)
            guard let pool = adaptor.pixelBufferPool else { throw RenderError.writerFailed("no pixel buffer pool") }
            var buffer: CVPixelBuffer?
            CVPixelBufferPoolCreatePixelBuffer(nil, pool, &buffer)
            guard let buffer else { throw RenderError.writerFailed("pixel buffer allocation") }
            CVBufferSetAttachment(buffer, kCVImageBufferColorPrimariesKey, kCVImageBufferColorPrimaries_ITU_R_709_2, .shouldPropagate)
            CVBufferSetAttachment(buffer, kCVImageBufferTransferFunctionKey, kCVImageBufferTransferFunction_ITU_R_709_2, .shouldPropagate)
            CVBufferSetAttachment(buffer, kCVImageBufferYCbCrMatrixKey, kCVImageBufferYCbCrMatrix_ITU_R_709_2, .shouldPropagate)
            context.render(image, to: buffer, bounds: CGRect(origin: .zero, size: size), colorSpace: outputSpace)
            if !adaptor.append(buffer, withPresentationTime: pts) {
                throw RenderError.writerFailed(writer.error?.localizedDescription ?? "append frame \(index)")
            }
            if index % 5 == 0 || index == frameCount - 1 {
                report(.video, 0.1 + 0.88 * Double(index + 1) / Double(frameCount), index + 1)
            }
        }
        videoInput.markAsFinished()
        while !audioDone {
            pumpAudio(until: .positiveInfinity)
            if !audioDone { try await Task.sleep(nanoseconds: 2_000_000) }
            if writer.status == .failed { break }
        }
        report(.finishing, 0.99, frameCount)
        await writer.finishWriting()
        if writer.status != .completed {
            throw RenderError.writerFailed(writer.error?.localizedDescription ?? "status \(writer.status.rawValue)")
        }
        warnings = compositor.warnings
        report(.finishing, 1, frameCount)
    }

    // MARK: Settings

    static func frameDuration(_ fps: Double) -> CMTime {
        // NTSC rates are exact fractions.
        for (rate, num, den) in [(23.976, 1001, 24000), (29.97, 1001, 30000), (59.94, 1001, 60000)] where abs(fps - rate) < 0.01 {
            return CMTime(value: CMTimeValue(num), timescale: CMTimeScale(den))
        }
        let rounded = fps.rounded()
        if abs(fps - rounded) < 0.001 { return CMTime(value: 1, timescale: CMTimeScale(rounded)) }
        return CMTime(value: 1000, timescale: CMTimeScale((fps * 1000).rounded()))
    }

    func videoSettings(size: CGSize, fps: Double) -> [String: Any] {
        var compression: [String: Any] = [:]
        if !settings.codec.isProRes {
            compression[AVVideoExpectedSourceFrameRateKey] = fps
            compression[AVVideoMaxKeyFrameIntervalKey] = Int((fps * 2).rounded())
            compression[AVVideoAllowFrameReorderingKey] = true
            if settings.rateControl == .quality {
                compression[AVVideoQualityKey] = min(max(settings.quality / 100, 0), 1)
            } else {
                compression[AVVideoAverageBitRateKey] = Int(settings.bitrateMbps * 1_000_000)
            }
            switch settings.codec {
            case .h264: compression[AVVideoProfileLevelKey] = AVVideoProfileLevelH264HighAutoLevel
            case .hevc: compression[AVVideoProfileLevelKey] = kVTProfileLevel_HEVC_Main_AutoLevel as String
            case .hevc10: compression[AVVideoProfileLevelKey] = kVTProfileLevel_HEVC_Main10_AutoLevel as String
            default: break
            }
        }
        var result: [String: Any] = [
            AVVideoCodecKey: settings.codec.avCodec,
            AVVideoWidthKey: Int(size.width),
            AVVideoHeightKey: Int(size.height),
            AVVideoColorPropertiesKey: [
                AVVideoColorPrimariesKey: AVVideoColorPrimaries_ITU_R_709_2,
                AVVideoTransferFunctionKey: AVVideoTransferFunction_ITU_R_709_2,
                AVVideoYCbCrMatrixKey: AVVideoYCbCrMatrix_ITU_R_709_2,
            ],
        ]
        if !compression.isEmpty { result[AVVideoCompressionPropertiesKey] = compression }
        return result
    }

    func audioSettings() -> [String: Any] {
        if settings.codec.isProRes {
            return [AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: 48_000, AVNumberOfChannelsKey: 2,
                    AVLinearPCMBitDepthKey: 24, AVLinearPCMIsFloatKey: false, AVLinearPCMIsBigEndianKey: false,
                    AVLinearPCMIsNonInterleaved: false]
        }
        return [AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 48_000, AVNumberOfChannelsKey: 2,
                AVEncoderBitRateKey: 256_000]
    }

    /// For partial exports: shift audio clips so the range starts at zero.
    private func trimmedForAudio(_ timeline: Timeline, to range: TimeRange) -> Timeline {
        var clips: [AudioClip] = []
        for clip in timeline.audioClips {
            let start = max(clip.target.start, range.start), end = min(clip.target.end, range.end)
            guard end > start else { continue }
            var copy = clip
            copy.target = TimeRange(start: start - range.start, duration: end - start)
            copy.timeMap = clip.timeMap.slice(from: start - clip.target.start, to: end - clip.target.start)
            copy.volumeEnvelope = clip.volumeEnvelope?.shifted(by: start - clip.target.start)
            if start != clip.target.start { copy.fadeIn = 0 }
            if end != clip.target.end { copy.fadeOut = 0 }
            clips.append(copy)
        }
        return Timeline(name: timeline.name, canvasSize: timeline.canvasSize, duration: range.duration, fps: timeline.fps,
                        layers: [], audioClips: clips, report: timeline.report)
    }
}
