import Accelerate
import AVFoundation

/// Streaming short-time Fourier transform with overlap-add resynthesis: 2048-sample frames,
/// 75 % overlap, sqrt-Hann analysis and synthesis windows (their product sums to a constant).
///
/// Every `hop` input samples (per stream and channel) produce `hop` output samples per channel,
/// delayed by `latency` samples. Several input streams can be analysed together (e.g. a mix and
/// a voice estimate); `modify` writes the output spectrum of each channel.
final class SpectralStream {
    static let size = 2048
    static let hop = 512
    static let bins = size / 2 + 1
    static let latency = size - hop

    let streams: Int
    let channels: Int
    /// Analysis spectra, `[stream][channel]`, `bins` values each. `im[0]` holds the Nyquist bin
    /// (vDSP's packed layout) and is treated as such by the helpers below.
    private(set) var re: [[[Float]]]
    private(set) var im: [[[Float]]]
    /// Output spectra per channel, written by `modify`.
    var outRe: [[Float]]
    var outIm: [[Float]]

    private let window: [Float]
    private let forward: vDSP_DFT_Setup
    private let inverse: vDSP_DFT_Setup
    private var history: [[[Float]]]
    private var overlap: [[Float]]
    private var scratch: [Float]
    private var scratchRe: [Float]
    private var scratchIm: [Float]

    init(streams: Int, channels: Int) {
        let n = Self.size, half = n / 2
        self.streams = streams
        self.channels = channels
        // Periodic Hann, square-rooted. At 75 % overlap Hann sums to 2, hence the 1/2 below.
        window = (0..<n).map { Float(sin(Double.pi * Double($0) / Double(n))) }
        forward = vDSP_DFT_zrop_CreateSetup(nil, vDSP_Length(n), .FORWARD)!
        inverse = vDSP_DFT_zrop_CreateSetup(nil, vDSP_Length(n), .INVERSE)!
        let zeroBins = [Float](repeating: 0, count: half)
        re = Array(repeating: Array(repeating: zeroBins, count: channels), count: streams)
        im = re
        outRe = Array(repeating: zeroBins, count: channels)
        outIm = outRe
        history = Array(repeating: Array(repeating: [Float](repeating: 0, count: n), count: channels), count: streams)
        overlap = Array(repeating: [Float](repeating: 0, count: n), count: channels)
        scratch = [Float](repeating: 0, count: n)
        scratchRe = zeroBins
        scratchIm = zeroBins
    }

    deinit {
        vDSP_DFT_DestroySetup(forward)
        vDSP_DFT_DestroySetup(inverse)
    }

    /// Consumes `hop` samples from `input[stream][channel]` at `offset`, writes `hop` samples to
    /// `output[channel]` at `offset`. Without `output` only the analysis runs.
    func step(input: [[UnsafePointer<Float>]], output: [UnsafeMutablePointer<Float>]?, offset: Int,
              modify: (SpectralStream) -> Void) {
        let n = Self.size, hop = Self.hop, half = n / 2
        for s in 0..<streams {
            for c in 0..<channels {
                history[s][c].withUnsafeMutableBufferPointer { h in
                    memmove(h.baseAddress!, h.baseAddress! + hop, (n - hop) * MemoryLayout<Float>.size)
                    memcpy(h.baseAddress! + n - hop, input[s][c] + offset, hop * MemoryLayout<Float>.size)
                    vDSP_vmul(h.baseAddress!, 1, window, 1, &scratch, 1, vDSP_Length(n))
                }
                // Even/odd split for the packed real DFT.
                scratch.withUnsafeBufferPointer { x in
                    x.baseAddress!.withMemoryRebound(to: DSPComplex.self, capacity: half) { complex in
                        scratchRe.withUnsafeMutableBufferPointer { r in
                            scratchIm.withUnsafeMutableBufferPointer { i in
                                var split = DSPSplitComplex(realp: r.baseAddress!, imagp: i.baseAddress!)
                                vDSP_ctoz(complex, 2, &split, 1, vDSP_Length(half))
                            }
                        }
                    }
                }
                vDSP_DFT_Execute(forward, scratchRe, scratchIm, &re[s][c], &im[s][c])
            }
        }
        modify(self)
        guard let output else { return }
        for c in 0..<channels {
            vDSP_DFT_Execute(inverse, outRe[c], outIm[c], &scratchRe, &scratchIm)
            scratch.withUnsafeMutableBufferPointer { x in
                x.baseAddress!.withMemoryRebound(to: DSPComplex.self, capacity: half) { complex in
                    scratchRe.withUnsafeMutableBufferPointer { r in
                        scratchIm.withUnsafeMutableBufferPointer { i in
                            var split = DSPSplitComplex(realp: r.baseAddress!, imagp: i.baseAddress!)
                            vDSP_ztoc(&split, 1, complex, 2, vDSP_Length(half))
                        }
                    }
                }
            }
            // Forward ×2, inverse ×n (vDSP scaling), Hann overlap sum 2.
            var scale = 1 / Float(4 * n)
            vDSP_vsmul(scratch, 1, &scale, &scratch, 1, vDSP_Length(n))
            vDSP_vmul(scratch, 1, window, 1, &scratch, 1, vDSP_Length(n))
            overlap[c].withUnsafeMutableBufferPointer { o in
                vDSP_vadd(o.baseAddress!, 1, scratch, 1, o.baseAddress!, 1, vDSP_Length(n))
                memcpy(output[c] + offset, o.baseAddress!, hop * MemoryLayout<Float>.size)
                memmove(o.baseAddress!, o.baseAddress! + hop, (n - hop) * MemoryLayout<Float>.size)
                memset(o.baseAddress! + n - hop, 0, hop * MemoryLayout<Float>.size)
            }
        }
    }

    /// |X|² of bin k of an analysis spectrum (k = 0 ... size/2, packed Nyquist handled).
    @inline(__always)
    func power(_ s: Int, _ c: Int, _ k: Int) -> Float {
        if k == 0 { return re[s][c][0] * re[s][c][0] }
        if k == Self.bins - 1 { return im[s][c][0] * im[s][c][0] }
        return re[s][c][k] * re[s][c][k] + im[s][c][k] * im[s][c][k]
    }

    /// Writes `gain[k] × X[stream 0]` to every output channel.
    func applyGain(_ gain: [Float], stream: Int = 0) {
        let half = Self.size / 2
        for c in 0..<channels {
            vDSP_vmul(re[stream][c], 1, gain, 1, &outRe[c], 1, vDSP_Length(half))
            vDSP_vmul(im[stream][c], 1, gain, 1, &outIm[c], 1, vDSP_Length(half))
            outIm[c][0] = im[stream][c][0] * gain[half]
        }
    }
}

/// File-to-file driver for `SpectralStream`: reads planar float files in chunks, feeds the stream,
/// compensates its latency and writes the result.
enum SpectralFileProcessor {
    /// - Parameters:
    ///   - inputs: Files with identical format; `startFrames[i]` is where reading starts in input i.
    ///   - frames: Number of frames to process (inputs that end early read as silence).
    ///   - output: nil runs the analysis only. Closed when done.
    static func run(inputs: [AVAudioFile], startFrames: [AVAudioFramePosition], frames: AVAudioFramePosition,
                    output: AVAudioFile?, isCancelled: () -> Bool, modify: (SpectralStream) -> Void) throws {
        let format = inputs[0].processingFormat
        let channels = Int(format.channelCount)
        let stream = SpectralStream(streams: inputs.count, channels: channels)
        let hop = SpectralStream.hop
        let chunk = hop * 64
        let ins = inputs.map { _ in AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(chunk))! }
        let out = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(chunk))!
        for (file, start) in zip(inputs, startFrames) { file.framePosition = max(0, start) }
        // Leading silence for inputs that start before frame 0.
        var leading = startFrames.map { Int(max(0, -$0)) }

        var toDrop = SpectralStream.latency
        var remaining = Int(frames)
        var fed = 0
        var written = 0
        let total = Int(frames) + SpectralStream.latency
        while fed < total {
            if isCancelled() { throw RenderError.cancelled }
            let count = min(chunk, ((total - fed) + hop - 1) / hop * hop)
            for (i, file) in inputs.enumerated() {
                let buffer = ins[i]
                for c in 0..<channels { memset(buffer.floatChannelData![c], 0, chunk * MemoryLayout<Float>.size) }
                var filled = min(leading[i], count)
                leading[i] -= filled
                while filled < count, file.framePosition < file.length {
                    let want = min(AVAudioFramePosition(count - filled), file.length - file.framePosition)
                    let tmp = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(want))!
                    try file.read(into: tmp, frameCount: AVAudioFrameCount(want))
                    guard tmp.frameLength > 0 else { break }
                    for c in 0..<channels {
                        memcpy(buffer.floatChannelData![c] + filled, tmp.floatChannelData![c],
                               Int(tmp.frameLength) * MemoryLayout<Float>.size)
                    }
                    filled += Int(tmp.frameLength)
                }
                // Past the requested range: silence, so the tail flushes cleanly.
                let valid = max(0, min(count, remaining))
                if valid < count {
                    for c in 0..<channels { memset(buffer.floatChannelData![c] + valid, 0, (count - valid) * MemoryLayout<Float>.size) }
                }
            }
            let inputPointers = ins.map { b in (0..<channels).map { UnsafePointer(b.floatChannelData![$0]) } }
            let outPointers = output == nil ? nil : (0..<channels).map { out.floatChannelData![$0] }
            for offset in stride(from: 0, to: count, by: hop) {
                stream.step(input: inputPointers, output: outPointers, offset: offset, modify: modify)
            }
            remaining -= count
            fed += count
            if let output {
                let drop = min(toDrop, count)
                toDrop -= drop
                let keep = min(count - drop, Int(frames) - written)
                guard keep > 0 else { continue }
                if drop > 0 {
                    for c in 0..<channels { memmove(out.floatChannelData![c], out.floatChannelData![c] + drop, keep * MemoryLayout<Float>.size) }
                }
                out.frameLength = AVAudioFrameCount(keep)
                try output.write(from: out)
                written += keep
            }
        }
        output?.close()
    }
}

/// Stationary-noise spectral gate. Used for "Reduce noise" on a clip whose voice is removed (the
/// background has no voice for the isolation-based denoiser to keep).
///
/// Bin powers are smoothed over time (`smoothing`). Pass 1 estimates a per-bin noise floor as a low
/// percentile of those powers; pass 2 applies a smoothed Wiener-style gain, limited to
/// `maxReductionDB × strength`. The gain is shared by both channels so the stereo image doesn't wander.
enum SpectralDenoiser {
    /// Percentile of smoothed frame powers taken as the noise floor (per bin).
    static let noisePercentile = 0.15
    /// Mean / 15th percentile of smoothed stationary-noise power (≈ gamma with shape 5.7): ≈ 1.8.
    static let percentileBias: Float = 1.8
    /// Recursive smoothing of bin powers across frames.
    static let smoothing: Float = 0.7
    /// Over-subtraction factor: noise power is multiplied by this before the gain is computed.
    static let overSubtraction: Float = 1.5
    /// Deepest reduction at strength 1 (CapCut's fixed strength 0.85 gives ≈ 20 dB).
    static let maxReductionDB: Float = 24
    /// Gain release per frame (attack is instant so onsets aren't clipped).
    static let release: Float = 0.55

    /// Per-bin power averaged over channels and smoothed over frames.
    private final class SmoothedPower {
        var values = [Float](repeating: 0, count: SpectralStream.bins)
        private var primed = false

        func update(_ s: SpectralStream) {
            for k in 0..<SpectralStream.bins {
                var p: Float = 0
                for c in 0..<s.channels { p += s.power(0, c, k) }
                p /= Float(s.channels)
                values[k] = primed ? smoothing * values[k] + (1 - smoothing) * p : p
            }
            primed = true
        }
    }

    static func noiseProfile(_ file: AVAudioFile, isCancelled: () -> Bool) throws -> [Float] {
        let bins = SpectralStream.bins
        // Histogram of 10·log10(power) in 0.5 dB steps.
        let lo: Float = -200, step: Float = 0.5, steps = 520
        var histogram = [[Int32]](repeating: [Int32](repeating: 0, count: steps), count: bins)
        var counted = 0
        let power = SmoothedPower()
        try SpectralFileProcessor.run(inputs: [file], startFrames: [0], frames: file.length, output: nil,
                                      isCancelled: isCancelled) { s in
            power.update(s)
            // Digital silence (padding, muted parts) says nothing about the noise.
            guard power.values.reduce(0, +) > 1e-12 else { return }
            counted += 1
            for k in 0..<bins {
                let db = 10 * log10(max(power.values[k], 1e-20))
                histogram[k][min(max(Int((db - lo) / step), 0), steps - 1)] += 1
            }
        }
        file.framePosition = 0
        guard counted > 0 else { return [Float](repeating: 0, count: bins) }
        let target = Int32(max(1, Double(counted) * noisePercentile))
        return histogram.map { h in
            var sum: Int32 = 0
            for (i, n) in h.enumerated() {
                sum += n
                if sum >= target { return percentileBias * pow(10, ((Float(i) + 0.5) * step + lo) / 10) }
            }
            return 0
        }
    }

    static func process(_ input: AVAudioFile, to output: AVAudioFile, strength: Double,
                        isCancelled: () -> Bool) throws {
        let noise = try noiseProfile(input, isCancelled: isCancelled)
        let bins = SpectralStream.bins
        let floor = pow(10, -maxReductionDB * Float(strength) / 20)
        let power = SmoothedPower()
        var previous = [Float](repeating: 1, count: bins)
        var raw = [Float](repeating: 1, count: bins)
        var gain = [Float](repeating: 1, count: bins)
        try SpectralFileProcessor.run(inputs: [input], startFrames: [0], frames: input.length, output: output,
                                      isCancelled: isCancelled) { s in
            power.update(s)
            for k in 0..<bins {
                let p = power.values[k]
                raw[k] = max(floor, p > 0 ? 1 - overSubtraction * noise[k] / p : 0)
            }
            // Smooth across neighbouring bins (1-2-3-2-1) against isolated "musical noise" peaks.
            for k in 0..<bins {
                var sum: Float = 0, weight: Float = 0
                for d in -2...2 {
                    let j = k + d
                    guard j >= 0, j < bins else { continue }
                    let w = Float(3 - abs(d))
                    sum += raw[j] * w
                    weight += w
                }
                let g = sum / weight
                gain[k] = max(g, previous[k] * release + g * (1 - release))
            }
            previous = gain
            s.applyGain(gain)
        }
    }
}

/// "Remove vocal": the mix minus a voice estimate, as a Wiener mask on the mix. Stream 0 is the mix,
/// stream 1 the time-aligned voice estimate; background power is estimated from their difference.
///
/// The voice estimate comes from a speech model: speech is removed well, a sung vocal in a song only
/// partly (centre-channel suppression doesn't help — songs are mixed more mono than camera audio).
enum VocalRemover {
    /// Weight of the voice estimate in the mask; >1 removes voice residue more aggressively.
    static let voiceWeight: Float = 2

    static func process(mix: AVAudioFile, voice: AVAudioFile, voiceStart: AVAudioFramePosition,
                        to output: AVAudioFile, isCancelled: () -> Bool) throws {
        let bins = SpectralStream.bins
        var gain = [Float](repeating: 1, count: bins)
        try SpectralFileProcessor.run(inputs: [mix, voice], startFrames: [0, voiceStart], frames: mix.length,
                                      output: output, isCancelled: isCancelled) { s in
            for k in 0..<bins {
                var pv: Float = 0, pb: Float = 0
                for c in 0..<s.channels {
                    pv += s.power(1, c, k)
                    let dr: Float, di: Float
                    if k == 0 { dr = s.re[0][c][0] - s.re[1][c][0]; di = 0 }
                    else if k == bins - 1 { dr = s.im[0][c][0] - s.im[1][c][0]; di = 0 }
                    else { dr = s.re[0][c][k] - s.re[1][c][k]; di = s.im[0][c][k] - s.im[1][c][k] }
                    pb += dr * dr + di * di
                }
                let sum = pb + voiceWeight * pv
                gain[k] = sum > 0 ? pb / sum : 1
            }
            s.applyGain(gain)
        }
    }
}
