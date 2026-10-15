import AVFoundation

/// Mixes all audio clips of a timeline into a 48 kHz stereo float file.
///
/// AVFoundation does the heavy lifting: clips go into an AVMutableComposition (speed via
/// `scaleTimeRange`, speed curves as many short constant-speed pieces), volumes and fades into an
/// AVAudioMix. AVAudioMix volumes are limited to 0...1, so all volumes are divided by the loudest
/// clip's gain and the mixed float samples are multiplied back.
enum AudioRenderer {
    static let sampleRate = 48_000.0

    static func render(_ timeline: Timeline, to url: URL, isCancelled: () -> Bool,
                       progress: (Double) -> Void) async throws {
        let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: sampleRate, channels: 2, interleaved: true)!
        let file = try AVAudioFile(forWriting: url, settings: format.settings, commonFormat: .pcmFormatFloat32,
                                   interleaved: true)
        let totalFrames = AVAudioFramePosition(Double(timeline.duration) / 1_000_000 * sampleRate)
        let out = MixWriter(file: file, format: format, totalFrames: totalFrames,
                            limiter: PeakLimiter(sampleRate: sampleRate))

        let composition = AVMutableComposition()
        let mix = AVMutableAudioMix()
        let gain = max(1, timeline.audioClips.map(\.volume).max() ?? 1)
        // Each lane is one composition track with its own pitch algorithm: CapCut keeps the pitch of
        // sped-up clips unless the clip's "pitch" toggle is on, which plays them like a tape (varispeed).
        var lanes: [(track: AVMutableCompositionTrack, params: AVMutableAudioMixInputParameters, end: Int64,
                     varispeed: Bool)] = []
        // Clips with "Isolate voice" / "Reduce noise" play a processed copy of their source.
        let voice = VoiceProcessor(directory: url.deletingLastPathComponent()
            .appendingPathComponent("voice-\(UUID().uuidString)"))
        defer { voice.removeFiles() }

        for clip in timeline.audioClips.sorted(by: { $0.target.start < $1.target.start }) {
            let processed = try await voice.process(clip, isCancelled: isCancelled)
            let sourceShift = processed?.sourceOffset ?? 0
            let asset = AVURLAsset(url: processed?.url ?? clip.url)
            guard let source = try? await asset.loadTracks(withMediaType: .audio).first,
                  let sourceRange = try? await source.load(.timeRange) else { continue }

            let varispeed = !clip.preservePitch
            let laneIndex: Int
            if let free = lanes.firstIndex(where: { $0.end <= clip.target.start && $0.varispeed == varispeed }) {
                laneIndex = free
            } else {
                guard let track = composition.addMutableTrack(withMediaType: .audio,
                                                              preferredTrackID: kCMPersistentTrackID_Invalid) else { continue }
                let params = AVMutableAudioMixInputParameters(track: track)
                params.audioTimePitchAlgorithm = varispeed ? .varispeed : .spectral
                lanes.append((track, params, 0, varispeed))
                laneIndex = lanes.count - 1
            }
            let track = lanes[laneIndex].track

            // Pieces of (source range, timeline range); a speed curve is approximated in 50 ms steps.
            let map = clip.timeMap
            let steps = map.isConstant ? 1 : max(1, Int(clip.target.duration / 50_000))
            var inserted = false
            for k in 0..<steps {
                let o0 = clip.target.duration * Int64(k) / Int64(steps)
                let o1 = clip.target.duration * Int64(k + 1) / Int64(steps)
                let s0 = map.sourceTime(atOffset: o0) - sourceShift, s1 = map.sourceTime(atOffset: o1) - sourceShift
                let src = CMTimeRange(start: us(s0), end: us(s1)).intersection(sourceRange)
                guard src.duration > .zero, o1 > o0 else { continue }
                let at = us(clip.target.start + o0)
                do {
                    try track.insertTimeRange(src, of: source, at: at)
                    let targetDuration = us(o1 - o0)
                    if CMTimeCompare(src.duration, targetDuration) != 0 {
                        track.scaleTimeRange(CMTimeRange(start: at, duration: src.duration), toDuration: targetDuration)
                    }
                    inserted = true
                } catch {
                    continue
                }
            }
            guard inserted else { continue }

            let params = lanes[laneIndex].params
            let v = Float(clip.volume / gain)
            let start = clip.target.start, end = clip.target.end
            let fadeIn = min(clip.fadeIn, clip.target.duration / 2), fadeOut = min(clip.fadeOut, clip.target.duration / 2)
            if fadeIn > 0 {
                params.setVolumeRamp(fromStartVolume: 0, toEndVolume: v, timeRange: CMTimeRange(start: us(start), duration: us(fadeIn)))
            } else {
                params.setVolume(v, at: us(start))
            }
            if fadeOut > 0 {
                params.setVolumeRamp(fromStartVolume: v, toEndVolume: 0,
                                     timeRange: CMTimeRange(start: us(end - fadeOut), duration: us(fadeOut)))
            }
            lanes[laneIndex].end = end
        }
        mix.inputParameters = lanes.map(\.params)

        if !lanes.isEmpty {
            let reader = try AVAssetReader(asset: composition)
            reader.timeRange = CMTimeRange(start: .zero, duration: us(timeline.duration))
            let output = AVAssetReaderAudioMixOutput(audioTracks: lanes.map(\.track), audioSettings: [
                AVFormatIDKey: kAudioFormatLinearPCM,
                AVSampleRateKey: sampleRate,
                AVNumberOfChannelsKey: 2,
                AVLinearPCMBitDepthKey: 32,
                AVLinearPCMIsFloatKey: true,
                AVLinearPCMIsNonInterleaved: false,
                AVLinearPCMIsBigEndianKey: false,
            ])
            output.audioMix = mix
            output.audioTimePitchAlgorithm = .spectral
            reader.add(output)
            guard reader.startReading() else { throw RenderError.writerFailed(reader.error?.localizedDescription ?? "audio") }

            while let sample = output.copyNextSampleBuffer() {
                if isCancelled() { reader.cancelReading(); throw RenderError.cancelled }
                let frames = AVAudioFrameCount(CMSampleBufferGetNumSamples(sample))
                guard frames > 0, let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: frames) else { continue }
                buffer.frameLength = frames
                guard CMSampleBufferCopyPCMDataIntoAudioBufferList(sample, at: 0, frameCount: Int32(frames),
                                                                   into: buffer.mutableAudioBufferList) == noErr else { continue }
                let pts = CMSampleBufferGetPresentationTimeStamp(sample)
                let position = AVAudioFramePosition((pts.seconds * sampleRate).rounded())
                if position > out.position { try out.silence(position - out.position) }
                // Skip any overlap with what's already written.
                let skip = max(0, out.position - position)
                guard skip < AVAudioFramePosition(frames) else { continue }
                let data = buffer.floatChannelData![0]
                if gain != 1 {
                    let g = Float(gain)
                    for i in 0..<Int(frames) * 2 { data[i] *= g }
                }
                if skip > 0 {
                    let rest = Int(frames) - Int(skip)
                    memmove(data, data + Int(skip) * 2, rest * 2 * MemoryLayout<Float>.size)
                    buffer.frameLength = AVAudioFrameCount(rest)
                }
                if try !out.write(buffer) { break }
                progress(Double(out.position) / Double(max(totalFrames, 1)))
            }
            if reader.status == .failed { throw RenderError.writerFailed(reader.error?.localizedDescription ?? "audio mix") }
        }
        try out.finish()
        progress(1)
    }

    /// Writes the mix through the master limiter, compensating its latency so timing is unchanged.
    private final class MixWriter {
        let file: AVAudioFile
        let format: AVAudioFormat
        let totalFrames: AVAudioFramePosition
        let limiter: PeakLimiter
        /// Timeline frames consumed so far.
        private(set) var position: AVAudioFramePosition = 0
        private var toDrop: Int
        private var fileFrames: AVAudioFramePosition = 0

        init(file: AVAudioFile, format: AVAudioFormat, totalFrames: AVAudioFramePosition, limiter: PeakLimiter) {
            self.file = file
            self.format = format
            self.totalFrames = totalFrames
            self.limiter = limiter
            toDrop = limiter.latency
        }

        /// Returns false once the timeline's end is reached.
        @discardableResult
        func write(_ buffer: AVAudioPCMBuffer) throws -> Bool {
            let remaining = totalFrames - position
            guard remaining > 0 else { return false }
            if AVAudioFramePosition(buffer.frameLength) > remaining { buffer.frameLength = AVAudioFrameCount(remaining) }
            position += AVAudioFramePosition(buffer.frameLength)
            try emit(buffer)
            return position < totalFrames
        }

        func silence(_ frames: AVAudioFramePosition) throws {
            var left = min(frames, totalFrames - position)
            let chunk: AVAudioFrameCount = 48_000
            guard left > 0, let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: chunk) else { return }
            while left > 0 {
                memset(buffer.floatChannelData![0], 0, Int(chunk) * 2 * MemoryLayout<Float>.size)
                buffer.frameLength = AVAudioFrameCount(min(AVAudioFramePosition(chunk), left))
                left -= AVAudioFramePosition(buffer.frameLength)
                try write(buffer)
            }
        }

        /// Pads to the timeline's length and flushes the limiter's delay line.
        func finish() throws {
            try silence(totalFrames - position)
            var flush = AVAudioFrameCount(limiter.latency)
            guard flush > 0, let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: flush) else { return }
            memset(buffer.floatChannelData![0], 0, Int(flush) * 2 * MemoryLayout<Float>.size)
            flush = AVAudioFrameCount(min(AVAudioFramePosition(flush), totalFrames - fileFrames))
            buffer.frameLength = flush
            try emit(buffer)
        }

        private func emit(_ buffer: AVAudioPCMBuffer) throws {
            let data = buffer.floatChannelData![0]
            var frames = Int(buffer.frameLength)
            limiter.process(data, frames: frames)
            if toDrop > 0 {
                let drop = min(toDrop, frames)
                toDrop -= drop
                frames -= drop
                if frames > 0 { memmove(data, data + drop * 2, frames * 2 * MemoryLayout<Float>.size) }
                buffer.frameLength = AVAudioFrameCount(frames)
            }
            guard frames > 0 else { return }
            try file.write(from: buffer)
            fileFrames += AVAudioFramePosition(frames)
        }
    }

    static func us(_ v: Int64) -> CMTime { CMTime(value: v, timescale: 1_000_000) }
}

/// EBU R128 loudness normalization with ffmpeg's two-pass `loudnorm` (linear gain when possible).
public enum LoudnessNormalizer {
    public static var ffmpegPath: String? {
        ["/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/usr/bin/ffmpeg"]
            .first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    static func normalize(input: URL, output: URL, targetLUFS: Double, truePeak: Double = -1) throws {
        guard let ffmpeg = ffmpegPath else { throw RenderError.ffmpeg("ffmpeg not found (brew install ffmpeg)") }
        let base = "I=\(targetLUFS):TP=\(truePeak):LRA=11"
        let measure = try run(ffmpeg, ["-hide_banner", "-nostats", "-i", input.path,
                                       "-af", "loudnorm=\(base):print_format=json", "-f", "null", "-"])
        guard let open = measure.lastIndex(of: "{"), let close = measure.lastIndex(of: "}"),
              let data = String(measure[open...close]).data(using: .utf8),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: String] else {
            throw RenderError.ffmpeg("could not parse loudnorm measurement")
        }
        func value(_ key: String) -> String { json[key] ?? "0" }
        // Silent input: nothing to normalize.
        if let i = Double(value("input_i")), i < -69 {
            try? FileManager.default.removeItem(at: output)
            try FileManager.default.copyItem(at: input, to: output)
            return
        }
        let second = "loudnorm=\(base):measured_I=\(value("input_i")):measured_TP=\(value("input_tp"))"
            + ":measured_LRA=\(value("input_lra")):measured_thresh=\(value("input_thresh"))"
            + ":offset=\(value("target_offset")):linear=true"
        _ = try run(ffmpeg, ["-hide_banner", "-nostats", "-y", "-i", input.path, "-af", second,
                             "-ar", "48000", "-c:a", "pcm_f32le", output.path])
    }

    private static func run(_ tool: String, _ arguments: [String]) throws -> String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: tool)
        process.arguments = arguments
        let pipe = Pipe()
        process.standardError = pipe
        process.standardOutput = FileHandle.nullDevice
        try process.run()
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        let text = String(decoding: data, as: UTF8.self)
        guard process.terminationStatus == 0 else {
            throw RenderError.ffmpeg(text.split(separator: "\n").suffix(3).joined(separator: " "))
        }
        return text
    }
}
