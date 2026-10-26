import Accelerate

/// Changes the speed of speech without changing its pitch: WSOLA (waveform-similarity overlap-add). Hann windows of
/// `frameLength` are taken from the input every `hop × rate` samples and overlapped every `hop` samples, each one
/// nudged by up to `tolerance` to where it best continues the previous window, so pitch periods line up. The same
/// algorithm as the Windows version's `TimeStretch`. Used for cloned voices, which have no rate setting.
enum TimeStretch {
    static let frameSeconds = 0.03
    static let toleranceSeconds = 0.01

    /// `samples` played `rate` times as fast (2 = half as long).
    static func stretch(_ samples: [Float], rate: Double, sampleRate: Double) -> [Float] {
        guard rate > 0, abs(rate - 1) > 0.001, samples.count > 0 else { return samples }
        let n = Int(sampleRate * frameSeconds) & ~1
        let hop = n / 2
        let tolerance = Int(sampleRate * toleranceSeconds)
        let outCount = Int((Double(samples.count) / rate).rounded())
        guard samples.count > n + 2 * tolerance else { return samples }

        // Periodic Hann: windows hop/frame apart sum to exactly 1.
        let window = (0..<n).map { Float(0.5 - 0.5 * cos(2 * Double.pi * Double($0) / Double(n))) }
        // Padding on both sides so every window and search stays inside the buffer.
        let pad = n + tolerance
        let input = [Float](repeating: 0, count: pad) + samples + [Float](repeating: 0, count: pad + n)
        var output = [Float](repeating: 0, count: outCount + 2 * n)

        // Window k starts at input `start + k·hop·rate` and output `k·hop`; the first one is half padding, so the
        // signal's first sample lands at output `hop`.
        let start = pad - hop
        var previous = start   // input position of the previous window
        var k = 0
        while k * hop < outCount + hop {
            let nominal = start + Int((Double(k * hop) * rate).rounded())
            var best = nominal
            if k > 0 {
                // The natural continuation of the previous window, compared over the half that overlaps.
                let natural = previous + hop
                var bestScore = -Float.infinity
                input.withUnsafeBufferPointer { x in
                    for d in -tolerance...tolerance {
                        let p = nominal + d
                        guard p >= 0, p + hop <= x.count, natural + hop <= x.count else { continue }
                        var score: Float = 0
                        vDSP_dotpr(x.baseAddress! + p, 1, x.baseAddress! + natural, 1, &score, vDSP_Length(hop))
                        if score > bestScore { bestScore = score; best = p }
                    }
                }
            }
            let at = k * hop
            for i in 0..<n where at + i < output.count && best + i < input.count {
                output[at + i] += input[best + i] * window[i]
            }
            previous = best
            k += 1
        }
        return Array(output[hop..<min(output.count, hop + outCount)])
    }
}
