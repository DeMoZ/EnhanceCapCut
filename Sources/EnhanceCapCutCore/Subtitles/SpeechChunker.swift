import AVFoundation

/// Cuts a long recording into short pieces at its quietest moments, for recognisers made for short utterances
/// (DictationTranscriber). Calibrated on a noisy street-market vlog (KohChangSquid): fed whole, dictation kept
/// about half of the words; in 3–8 s pieces it kept nearly all, while longer pieces lost words again.
///
/// Phone footage often has wide stereo (left and right barely alike), and the average of such channels smears the
/// voice: on the same vlog one channel alone gave whole phrases that the average lost. So unless the channels are
/// nearly the same, each piece is written once per channel and the caller keeps the better recognition.
public enum SpeechChunker {
    /// A piece is at least this long (seconds), unless the recording is shorter…
    static let minPiece = 3.0
    /// …and at most this long: the cut goes at the quietest moment between `minPiece` and `maxPiece`.
    static let maxPiece = 8.0
    /// Loudness is measured in frames of this length…
    static let frameDuration = 0.02
    /// …and averaged over ± this much, so a cut lands in a pause rather than between two syllables.
    static let smoothing = 0.25
    /// Channels count as the same when their difference is this much quieter than their sum (energy ratio, −10 dB).
    static let monoSideRatio: Float = 0.1

    public struct Piece {
        /// The piece as one mono file, or one file per channel for wide stereo.
        public var urls: [URL]
        public var start: Double
        public var end: Double
    }

    /// Writes the pieces of `url` into `folder` (mono files at the source's sample rate) and returns them in order.
    public static func split(_ url: URL, into folder: URL) throws -> [Piece] {
        let file = try AVAudioFile(forReading: url)
        let rate = file.processingFormat.sampleRate
        let channels = try channelSamples(file)
        guard let first = channels.first else { return [] }
        let samples = channels.count == 1 ? first : (0..<first.count).map { i in channels.reduce(0) { $0 + $1[i] } / Float(channels.count) }
        let sources = isWideStereo(channels) ? Array(channels.prefix(2)) : [samples]
        let frame = max(1, Int(rate * frameDuration))
        let energy = stride(from: 0, to: samples.count, by: frame).map { i -> Float in
            let chunk = samples[i..<min(i + frame, samples.count)]
            return chunk.reduce(0) { $0 + $1 * $1 } / Float(chunk.count)
        }
        let bounds = [0] + cuts(energy: energy, frameDuration: Double(frame) / rate) + [Double(samples.count) / rate]

        let format = AVAudioFormat(standardFormatWithSampleRate: rate, channels: 1)!
        var pieces: [Piece] = []
        for (n, (start, end)) in zip(bounds, bounds.dropFirst()).enumerated() {
            let from = Int(start * rate), to = min(samples.count, Int(end * rate))
            guard to > from, let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(to - from)) else { continue }
            buffer.frameLength = AVAudioFrameCount(to - from)
            var urls: [URL] = []
            for (c, source) in sources.enumerated() {
                source[from..<to].withUnsafeBufferPointer { buffer.floatChannelData![0].update(from: $0.baseAddress!, count: to - from) }
                let pieceURL = folder.appendingPathComponent(String(format: "piece-%04d-%d.caf", n, c))
                let out = try AVAudioFile(forWriting: pieceURL, settings: format.settings)
                try out.write(from: buffer)
                urls.append(pieceURL)
            }
            pieces.append(Piece(urls: urls, start: start, end: end))
        }
        return pieces
    }

    /// Whether the first two channels differ enough that each should be recognised on its own.
    static func isWideStereo(_ channels: [[Float]]) -> Bool {
        guard channels.count >= 2 else { return false }
        var side: Float = 0, mid: Float = 0
        for (l, r) in zip(channels[0], channels[1]) {
            side += (l - r) * (l - r)
            mid += (l + r) * (l + r)
        }
        return mid > 0 && side > mid * monoSideRatio
    }

    /// Cut times (seconds) for a recording with the given per-frame energy.
    static func cuts(energy: [Float], frameDuration: Double) -> [Double] {
        let radius = max(0, Int(smoothing / frameDuration))
        // Moving average through a prefix sum.
        var prefix = [Double](repeating: 0, count: energy.count + 1)
        for (i, e) in energy.enumerated() { prefix[i + 1] = prefix[i] + Double(e) }
        func smoothed(_ i: Int) -> Double {
            let lo = max(0, i - radius), hi = min(energy.count, i + radius + 1)
            return (prefix[hi] - prefix[lo]) / Double(hi - lo)
        }

        let minFrames = Int(minPiece / frameDuration), maxFrames = Int(maxPiece / frameDuration)
        var cuts: [Double] = []
        var position = 0
        while energy.count - position > maxFrames {
            // The quietest frame; a later one wins ties, for longer pieces.
            var best = position + maxFrames
            var bestEnergy = Double.infinity
            for i in (position + minFrames)..<(position + maxFrames) where smoothed(i) <= bestEnergy {
                bestEnergy = smoothed(i)
                best = i
            }
            cuts.append(Double(best) * frameDuration)
            position = best
        }
        return cuts
    }

    /// The samples of each channel of `file`.
    private static func channelSamples(_ file: AVAudioFile) throws -> [[Float]] {
        let length = AVAudioFrameCount(file.length)
        guard length > 0, let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: length) else { return [] }
        try file.read(into: buffer)
        guard let data = buffer.floatChannelData else { return [] }
        let count = Int(buffer.frameLength)
        return (0..<Int(buffer.format.channelCount)).map { Array(UnsafeBufferPointer(start: data[$0], count: count)) }
    }
}
