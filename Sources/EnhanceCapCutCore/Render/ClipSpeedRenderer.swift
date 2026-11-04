import AVFoundation

/// Renders the audio of clips whose speed isn't 1× into files that play at timeline speed.
///
/// AVFoundation's offline mixer (AVAssetReaderAudioMixOutput) deadlocks on composition tracks with
/// `scaleTimeRange` edits: copyNextSampleBuffer waits forever at 0% CPU, every time for a sped-up varispeed
/// clip and every few runs for slowed-down clips among many others (issue #55). So speed changes never reach the
/// composition: such clips are decoded, re-timed here with the same algorithms as the Windows mixer
/// (`AudioMixer.Resample` / `TimeStretch` in `windows/src/EnhanceCapCut.Render/AudioMixer.cs`) and inserted 1:1.
enum ClipSpeedRenderer {
    /// Extra source decoded around a clip's range for interpolation and the time-stretch search.
    static let decodeMargin: Int64 = 100_000

    /// Whether the clip plays its source at anything other than 1×.
    static func changesSpeed(_ map: TimeMap) -> Bool {
        !map.isConstant || map.sourceDuration != map.targetDuration
    }

    /// Writes the clip at timeline speed to a new file of `voice`'s directory: frame 0 is the clip's first
    /// frame. `processed` is the clip's voice-processed copy, if any. Returns nil when there's no audio.
    static func render(_ clip: AudioClip, processed: VoiceProcessor.Output?, files voice: VoiceProcessor,
                       isCancelled: () -> Bool) async throws -> URL? {
        let map = clip.timeMap
        let sourceURL: URL, sourceStart: Int64
        if let processed {
            (sourceURL, sourceStart) = (processed.url, processed.sourceOffset)
        } else {
            try FileManager.default.createDirectory(at: voice.directory, withIntermediateDirectories: true)
            let from = max(0, map.sourceStart - decodeMargin), to = map.sourceStart + map.sourceDuration + decodeMargin
            guard let decoded = try await voice.decode(clip.url, from: from, to: to) else { return nil }
            (sourceURL, sourceStart) = decoded
        }
        if isCancelled() { throw RenderError.cancelled }
        let source = try interleaved(VoiceProcessor.read(sourceURL))
        guard !source.isEmpty else { return nil }

        let rate = VoiceProcessor.sampleRate
        let count = Int((Double(clip.target.duration) / 1_000_000 * rate).rounded())
        guard count > 0 else { return nil }
        // Source position (in decoded frames) heard at output frame j of the clip.
        let sourceFrame = { (j: Double) -> Double in
            (map.preciseSourceTime(atOffset: j * 1_000_000 / rate) - Double(sourceStart)) * rate / 1_000_000
        }
        var output = [Float](repeating: 0, count: count * 2)
        if clip.preservePitch {
            timeStretch(source, sourceFrame: sourceFrame, into: &output)
        } else {
            resample(source, sourceFrame: sourceFrame, into: &output)
        }

        let url = voice.newFile("speed")
        let file = try VoiceProcessor.write(url)
        guard let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(count))
        else { throw RenderError.writerFailed("audio buffer") }
        buffer.frameLength = AVAudioFrameCount(count)
        for c in 0..<2 {
            let dst = buffer.floatChannelData![c]
            for j in 0..<count { dst[j] = output[j * 2 + c] }
        }
        try file.write(from: buffer)
        file.close()
        return url
    }

    /// The whole file as interleaved stereo.
    private static func interleaved(_ file: AVAudioFile) throws -> [Float] {
        let frames = Int(file.length)
        guard frames > 0, let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat,
                                                        frameCapacity: AVAudioFrameCount(frames)) else { return [] }
        try file.read(into: buffer)
        let n = Int(buffer.frameLength), channels = Int(file.processingFormat.channelCount)
        var out = [Float](repeating: 0, count: n * 2)
        for c in 0..<2 {
            let src = buffer.floatChannelData![min(c, channels - 1)]
            for i in 0..<n { out[i * 2 + c] = src[i] }
        }
        return out
    }

    /// Varispeed: reads the source at fractional positions (Catmull-Rom); pitch follows speed.
    static func resample(_ source: [Float], sourceFrame: (Double) -> Double, into output: inout [Float]) {
        let frames = source.count / 2
        for j in 0..<(output.count / 2) {
            let pos = sourceFrame(Double(j))
            let i = Int(pos.rounded(.down))
            let f = Float(pos - Double(i))
            for c in 0..<2 {
                func at(_ k: Int) -> Float { k >= 0 && k < frames ? source[k * 2 + c] : 0 }
                let y0 = at(i - 1), y1 = at(i), y2 = at(i + 1), y3 = at(i + 2)
                let cubic = 3 * (y1 - y2) + y3 - y0
                output[j * 2 + c] = y1 + 0.5 * f * (y2 - y0 + f * (2 * y0 - 5 * y1 + 4 * y2 - y3 + f * cubic))
            }
        }
    }

    /// Output hop of the time-stretch in frames (~10.7 ms at 48 kHz); segments are twice as long.
    static let stretchHop = 512
    /// How far (frames) around the ideal position a segment may move to match the previous one.
    static let stretchTolerance = 384

    /// WSOLA time-stretching (waveform-similarity overlap-add): changes speed and keeps the pitch. Output is
    /// built from Hann-windowed source segments; each is taken near its ideal source position at the point that
    /// best continues the previous one (cross-correlation search), which avoids phasing artifacts.
    static func timeStretch(_ source: [Float], sourceFrame: (Double) -> Double, into output: inout [Float]) {
        let frames = source.count / 2, outFrames = output.count / 2
        let hop = stretchHop, n = hop * 2
        let window = (0..<n).map { Float(0.5 - 0.5 * cos(2 * Double.pi * Double($0) / Double(n))) }
        var norm = [Float](repeating: 0, count: outFrames)
        func mono(_ k: Int) -> Double { k >= 0 && k < frames ? Double(source[k * 2] + source[k * 2 + 1]) : 0 }

        var previous: Int?
        var outStart = -hop
        while outStart < outFrames {
            let ideal = Int((sourceFrame(Double(max(outStart, 0))) - Double(outStart < 0 ? hop : 0)).rounded())
            var best = ideal
            if let previous {
                // The natural continuation of the previous segment, one hop later.
                let natural = previous + hop
                var bestScore = -Double.infinity
                for delta in stride(from: -stretchTolerance, through: stretchTolerance, by: 4) {
                    let candidate = ideal + delta
                    var score = 0.0
                    for k in stride(from: 0, to: hop, by: 4) { score += mono(natural + k) * mono(candidate + k) }
                    if score > bestScore { bestScore = score; best = candidate }
                }
            }
            previous = best
            for i in 0..<n {
                let o = outStart + i
                guard o >= 0, o < outFrames else { continue }
                let s = best + i
                norm[o] += window[i]
                guard s >= 0, s < frames else { continue }
                output[o * 2] += source[s * 2] * window[i]
                output[o * 2 + 1] += source[s * 2 + 1] * window[i]
            }
            outStart += hop
        }
        for o in 0..<outFrames where norm[o] > 1e-3 {
            output[o * 2] /= norm[o]
            output[o * 2 + 1] /= norm[o]
        }
    }
}
