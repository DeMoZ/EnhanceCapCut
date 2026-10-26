namespace EnhanceCapCut.Subtitles;

/// <summary>
/// Changes the speed of speech without changing its pitch: WSOLA (waveform-similarity overlap-add). Hann windows of
/// <c>frameLength</c> are taken from the input every <c>hop × rate</c> samples and overlapped every <c>hop</c> samples,
/// each one nudged by up to <c>tolerance</c> to where it best continues the previous window, so pitch periods line up.
/// The same algorithm as the macOS version's <c>TimeStretch.swift</c>. Used for cloned voices, which have no rate setting.
/// </summary>
public static class TimeStretch
{
    const double FrameSeconds = 0.03;
    const double ToleranceSeconds = 0.01;

    /// <summary><paramref name="samples"/> played <paramref name="rate"/> times as fast (2 = half as long).</summary>
    public static float[] Stretch(float[] samples, double rate, int sampleRate)
    {
        if (rate <= 0 || Math.Abs(rate - 1) <= 0.001 || samples.Length == 0) return samples;
        var n = (int)(sampleRate * FrameSeconds) & ~1;
        var hop = n / 2;
        var tolerance = (int)(sampleRate * ToleranceSeconds);
        var outCount = (int)Math.Round(samples.Length / rate, MidpointRounding.AwayFromZero);
        if (samples.Length <= n + 2 * tolerance) return samples;

        // Periodic Hann: windows hop/frame apart sum to exactly 1.
        var window = new float[n];
        for (var i = 0; i < n; i++) window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
        // Padding on both sides so every window and search stays inside the buffer.
        var pad = n + tolerance;
        var input = new float[pad + samples.Length + pad + n];
        samples.CopyTo(input, pad);
        var output = new float[outCount + 2 * n];

        // Window k starts at input `start + k·hop·rate` and output `k·hop`; the first one is half padding, so the
        // signal's first sample lands at output `hop`.
        var start = pad - hop;
        var previous = start;   // input position of the previous window
        for (var k = 0; k * hop < outCount + hop; k++)
        {
            var nominal = start + (int)Math.Round(k * hop * rate, MidpointRounding.AwayFromZero);
            var best = nominal;
            if (k > 0)
            {
                // The natural continuation of the previous window, compared over the half that overlaps.
                var natural = previous + hop;
                var bestScore = float.NegativeInfinity;
                for (var d = -tolerance; d <= tolerance; d++)
                {
                    var p = nominal + d;
                    if (p < 0 || p + hop > input.Length || natural + hop > input.Length) continue;
                    var score = 0f;
                    for (var i = 0; i < hop; i++) score += input[p + i] * input[natural + i];
                    if (score > bestScore) { bestScore = score; best = p; }
                }
            }
            var at = k * hop;
            for (var i = 0; i < n && at + i < output.Length && best + i < input.Length; i++) output[at + i] += input[best + i] * window[i];
            previous = best;
        }
        return output[hop..Math.Min(output.Length, hop + outCount)];
    }
}
