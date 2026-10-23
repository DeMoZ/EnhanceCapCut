namespace EnhanceCapCut.Render;

/// <summary>
/// Stereo-linked look-ahead peak limiter for interleaved float audio (port of the macOS PeakLimiter.swift).
///
/// CapCut's exports never exceed 0 dBFS even when overlapping clips sum far above it, and their
/// loudness matches a limited sum — so its master bus has a limiter. This one keeps the mix under
/// <c>ceiling</c> without clicks: the required gain is min-filtered over the look-ahead window, then
/// box-averaged over the same window (so it ramps down exactly in time for each peak), then released
/// exponentially.
/// </summary>
public sealed class PeakLimiter
{
    public float Ceiling { get; }
    readonly int window;
    readonly float releaseCoefficient;

    readonly float[] inputs;     // ring of the last `window` stereo frames
    readonly float[] required;   // ring of required gains, same indexing
    readonly List<long> minDeque = []; // indices into the stream, increasing required gain
    int minHead;
    readonly float[] mins;       // ring of window-minimum gains
    float minSum;
    float gain = 1;
    long n;                      // frames received

    public PeakLimiter(double sampleRate, double ceilingDB = -0.2, double lookahead = 0.005, double release = 0.08)
    {
        Ceiling = (float)Math.Pow(10, ceilingDB / 20);
        window = Math.Max(2, (int)(sampleRate * lookahead));
        releaseCoefficient = (float)(1 - Math.Exp(-1 / (sampleRate * release)));
        inputs = new float[window * 2];
        required = Enumerable.Repeat(1f, window).ToArray();
        mins = Enumerable.Repeat(1f, window).ToArray();
        minSum = window;
    }

    /// <summary>Latency in frames: output frame <c>k</c> corresponds to input frame <c>k - latency</c>.</summary>
    public int Latency => window - 1;

    /// <summary>Processes interleaved stereo frames in place (output is delayed by <see cref="Latency"/>).</summary>
    public void Process(Span<float> data)
    {
        var frames = data.Length / 2;
        for (var i = 0; i < frames; i++)
        {
            float l = data[i * 2], r = data[i * 2 + 1];
            var peak = Math.Max(Math.Abs(l), Math.Abs(r));
            var req = peak > Ceiling ? Ceiling / peak : 1f;

            var slot = (int)(n % window);
            required[slot] = req;
            // Sliding minimum of required gain over the last `window` frames.
            while (minDeque.Count > minHead && required[(int)(minDeque[^1] % window)] >= req) minDeque.RemoveAt(minDeque.Count - 1);
            minDeque.Add(n);
            if (minDeque[minHead] <= n - window) minHead++;
            if (minHead > 4096) { minDeque.RemoveRange(0, minHead); minHead = 0; }
            var windowMin = required[(int)(minDeque[minHead] % window)];

            // Box average of the window minimum; the result is ≤ the required gain at the delayed frame.
            minSum += windowMin - mins[slot];
            mins[slot] = windowMin;
            var target = Math.Min(minSum / window, 1f);
            gain = target < gain ? target : gain + (target - gain) * releaseCoefficient;

            // Emit the frame from `latency` frames ago.
            var outSlot = (int)((n + 1) % window);
            float dl = inputs[outSlot * 2], dr = inputs[outSlot * 2 + 1];
            inputs[slot * 2] = l;
            inputs[slot * 2 + 1] = r;
            data[i * 2] = dl * gain;
            data[i * 2 + 1] = dr * gain;
            n++;
        }
    }

    /// <summary>Limits a whole interleaved stereo buffer in place, compensating the latency so timing is unchanged.</summary>
    public static void LimitInPlace(float[] mix, double sampleRate, double ceilingDB)
    {
        var limiter = new PeakLimiter(sampleRate, ceilingDB);
        var latency = limiter.Latency;
        var padded = new float[mix.Length + latency * 2];
        mix.CopyTo(padded, 0);
        limiter.Process(padded);
        Array.Copy(padded, latency * 2, mix, 0, mix.Length);
    }
}
