import Foundation

/// Stereo-linked look-ahead peak limiter for interleaved float audio.
///
/// CapCut's exports never exceed 0 dBFS even when overlapping clips sum far above it, and their
/// loudness matches a limited sum — so its master bus has a limiter. This one keeps the mix under
/// `ceiling` without clicks: the required gain is min-filtered over the look-ahead window, then
/// box-averaged over the same window (so it ramps down exactly in time for each peak), then released
/// exponentially.
final class PeakLimiter {
    let ceiling: Float
    private let window: Int
    private let releaseCoefficient: Float

    private var inputs: [Float]          // ring of the last `window` stereo frames
    private var required: [Float]        // ring of required gains, same indexing
    private var minDeque: [Int] = []     // indices into the stream, increasing required gain
    private var minHead = 0
    private var mins: [Float]            // ring of window-minimum gains
    private var minSum: Float = 0
    private var gain: Float = 1
    private var n = 0                    // frames received

    init(sampleRate: Double, ceilingDB: Double = -0.2, lookahead: Double = 0.005, release: Double = 0.08) {
        ceiling = Float(pow(10, ceilingDB / 20))
        window = max(2, Int(sampleRate * lookahead))
        releaseCoefficient = Float(1 - exp(-1 / (sampleRate * release)))
        inputs = [Float](repeating: 0, count: window * 2)
        required = [Float](repeating: 1, count: window)
        mins = [Float](repeating: 1, count: window)
        minSum = Float(window)
    }

    /// Latency in frames: output frame `k` corresponds to input frame `k - latency`.
    var latency: Int { window - 1 }

    /// Processes `frames` interleaved stereo frames in place (output is delayed by `latency`).
    func process(_ data: UnsafeMutablePointer<Float>, frames: Int) {
        for i in 0..<frames {
            let l = data[i * 2], r = data[i * 2 + 1]
            let peak = max(abs(l), abs(r))
            let req: Float = peak > ceiling ? ceiling / peak : 1

            let slot = n % window
            required[slot] = req
            // Sliding minimum of required gain over the last `window` frames.
            while minDeque.count > minHead, required[minDeque[minDeque.count - 1] % window] >= req { minDeque.removeLast() }
            minDeque.append(n)
            if minDeque[minHead] <= n - window { minHead += 1 }
            if minHead > 4096 { minDeque.removeFirst(minHead); minHead = 0 }
            let windowMin = required[minDeque[minHead] % window]

            // Box average of the window minimum; the result is ≤ the required gain at the delayed frame.
            minSum += windowMin - mins[slot]
            mins[slot] = windowMin
            let target = min(minSum / Float(window), 1)
            gain = target < gain ? target : gain + (target - gain) * releaseCoefficient

            // Emit the frame from `latency` frames ago.
            let outSlot = (n + 1) % window
            let dl = inputs[outSlot * 2], dr = inputs[outSlot * 2 + 1]
            inputs[slot * 2] = l
            inputs[slot * 2 + 1] = r
            data[i * 2] = dl * gain
            data[i * 2 + 1] = dr * gain
            n += 1
        }
    }
}
