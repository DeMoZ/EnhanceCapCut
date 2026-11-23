namespace EnhanceCapCut.Render;

/// <summary>
/// Short-time Fourier transform with overlap-add resynthesis (the counterpart of the macOS SpectralStream and
/// SpectralFileProcessor, in memory): 2048-sample frames, 75 % overlap, sqrt-Hann analysis and synthesis windows
/// (their product sums to a constant). Several streams can be analysed together (e.g. a mix and a voice
/// estimate); <c>modify</c> writes the output spectrum of each channel, and the output is latency-compensated.
/// </summary>
public sealed class SpectralFrame
{
    public const int Size = 2048;
    public const int Hop = 512;
    public const int Bins = Size / 2 + 1;
    public const int Latency = Size - Hop;

    public int Streams { get; }
    public int Channels { get; }
    /// <summary>Analysis spectra, <c>[stream][channel][bin]</c>.</summary>
    public double[][][] Re { get; }
    public double[][][] Im { get; }
    /// <summary>Output spectra per channel, written by <c>modify</c>.</summary>
    public double[][] OutRe { get; }
    public double[][] OutIm { get; }

    internal SpectralFrame(int streams, int channels)
    {
        Streams = streams;
        Channels = channels;
        double[][] Bank() => Enumerable.Range(0, channels).Select(_ => new double[Size]).ToArray();
        Re = Enumerable.Range(0, streams).Select(_ => Bank()).ToArray();
        Im = Enumerable.Range(0, streams).Select(_ => Bank()).ToArray();
        OutRe = Bank();
        OutIm = Bank();
    }

    /// <summary>|X|² of bin <paramref name="k"/> of an analysis spectrum.</summary>
    public double Power(int s, int c, int k) => Re[s][c][k] * Re[s][c][k] + Im[s][c][k] * Im[s][c][k];

    /// <summary>Writes <c>gain[k] × X[stream]</c> to every output channel.</summary>
    public void ApplyGain(float[] gain, int stream = 0)
    {
        for (var c = 0; c < Channels; c++)
        {
            for (var k = 0; k < Bins; k++)
            {
                OutRe[c][k] = Re[stream][c][k] * gain[k];
                OutIm[c][k] = Im[stream][c][k] * gain[k];
            }
        }
    }

    /// <param name="inputs">Planar channels per stream; all streams have the same channel count.</param>
    /// <param name="startFrames">Where reading starts in each input (may be negative: leading silence).</param>
    /// <param name="frames">Frames to process; inputs that end early read as silence.</param>
    /// <param name="output">False runs the analysis only (returns null).</param>
    public static float[][]? Run(float[][][] inputs, int[] startFrames, int frames, bool output,
        Action<SpectralFrame> modify, CancellationToken cancel = default)
    {
        var channels = inputs[0].Length;
        var frame = new SpectralFrame(inputs.Length, channels);
        var fft = new Fft(Size);
        var window = new double[Size];
        // Periodic Hann, square-rooted.
        for (var i = 0; i < Size; i++) window[i] = Math.Sin(Math.PI * i / Size);
        var re = new double[Size];
        var im = new double[Size];
        var outRe = new double[Size];
        var outIm = new double[Size];
        var overlap = Enumerable.Range(0, channels).Select(_ => new double[Size]).ToArray();
        var result = output ? Enumerable.Range(0, channels).Select(_ => new float[frames]).ToArray() : null;
        var total = frames + Latency;
        // The frame ending at sample `end` (exclusive) of the stream, i.e. `end - Size .. end`.
        for (var end = Hop; end - Hop < total; end += Hop)
        {
            if ((end / Hop) % 256 == 0) cancel.ThrowIfCancellationRequested();
            for (var s = 0; s < inputs.Length; s++)
            {
                for (var c = 0; c < channels; c++)
                {
                    var x = inputs[s][c];
                    for (var i = 0; i < Size; i++)
                    {
                        var t = end - Size + i;
                        var j = startFrames[s] + t;
                        re[i] = t >= 0 && t < frames && j >= 0 && j < x.Length ? x[j] * window[i] : 0;
                        im[i] = 0;
                    }
                    fft.Transform(re, im, frame.Re[s][c], frame.Im[s][c]);
                }
            }
            modify(frame);
            if (result is null) continue;
            for (var c = 0; c < channels; c++)
            {
                // Mirror the half spectrum (real output).
                for (var k = 0; k < Bins; k++)
                {
                    re[k] = frame.OutRe[c][k];
                    im[k] = frame.OutIm[c][k];
                }
                im[0] = 0;
                im[Size / 2] = 0;
                for (var k = Bins; k < Size; k++)
                {
                    re[k] = re[Size - k];
                    im[k] = -im[Size - k];
                }
                fft.Transform(re, im, outRe, outIm, inverse: true);
                // Inverse ×Size, Hann overlap sum 2.
                var o = overlap[c];
                const double scale = 1.0 / (2 * Size);
                for (var i = 0; i < Size; i++) o[i] += outRe[i] * scale * window[i];
                // Samples end - Size .. end - Size + Hop are complete; they show input time t - Latency.
                for (var i = 0; i < Hop; i++)
                {
                    var t = end - Size + i;
                    if (t >= 0 && t < frames) result[c][t] = (float)o[i];
                }
                Array.Copy(o, Hop, o, 0, Size - Hop);
                Array.Clear(o, Size - Hop, Hop);
            }
        }
        return result;
    }
}

/// <summary>
/// Stationary-noise spectral gate (port of the macOS SpectralDenoiser). Used for "Reduce noise" on a clip whose
/// voice is removed. Bin powers are smoothed over time; pass 1 estimates a per-bin noise floor as a low percentile
/// of those powers, pass 2 applies a smoothed Wiener-style gain limited to <see cref="MaxReductionDB"/> × strength,
/// shared by both channels.
/// </summary>
public static class SpectralDenoiser
{
    public const double NoisePercentile = 0.15;
    public const float PercentileBias = 1.8f;
    public const float Smoothing = 0.7f;
    public const float OverSubtraction = 1.5f;
    public const float MaxReductionDB = 24;
    public const float Release = 0.55f;

    sealed class SmoothedPower
    {
        public readonly float[] Values = new float[SpectralFrame.Bins];
        bool primed;

        public void Update(SpectralFrame s)
        {
            for (var k = 0; k < SpectralFrame.Bins; k++)
            {
                double p = 0;
                for (var c = 0; c < s.Channels; c++) p += s.Power(0, c, k);
                var v = (float)(p / s.Channels);
                Values[k] = primed ? Smoothing * Values[k] + (1 - Smoothing) * v : v;
            }
            primed = true;
        }
    }

    /// <summary>Per-bin noise floor: a low percentile of the smoothed powers of the non-silent frames.</summary>
    static float[] NoiseProfile(float[][] input, CancellationToken cancel)
    {
        const int bins = SpectralFrame.Bins;
        const float lo = -200, step = 0.5f;
        const int steps = 520;
        var histogram = new int[bins, steps];
        var counted = 0;
        var power = new SmoothedPower();
        SpectralFrame.Run([input], [0], input[0].Length, false, s =>
        {
            power.Update(s);
            if (power.Values.Sum() <= 1e-12f) return;
            counted++;
            for (var k = 0; k < bins; k++)
            {
                var db = 10 * MathF.Log10(MathF.Max(power.Values[k], 1e-20f));
                histogram[k, Math.Clamp((int)((db - lo) / step), 0, steps - 1)]++;
            }
        }, cancel);
        var noise = new float[bins];
        if (counted == 0) return noise;
        var target = Math.Max(1, (int)(counted * NoisePercentile));
        for (var k = 0; k < bins; k++)
        {
            var sum = 0;
            for (var i = 0; i < steps; i++)
            {
                sum += histogram[k, i];
                if (sum < target) continue;
                noise[k] = PercentileBias * MathF.Pow(10, ((i + 0.5f) * step + lo) / 10);
                break;
            }
        }
        return noise;
    }

    public static float[][] Process(float[][] input, double strength, CancellationToken cancel = default)
    {
        var noise = NoiseProfile(input, cancel);
        const int bins = SpectralFrame.Bins;
        var floor = MathF.Pow(10, -MaxReductionDB * (float)strength / 20);
        var power = new SmoothedPower();
        var previous = Enumerable.Repeat(1f, bins).ToArray();
        var raw = new float[bins];
        var gain = new float[bins];
        return SpectralFrame.Run([input], [0], input[0].Length, true, s =>
        {
            power.Update(s);
            for (var k = 0; k < bins; k++)
            {
                var p = power.Values[k];
                raw[k] = MathF.Max(floor, p > 0 ? 1 - OverSubtraction * noise[k] / p : 0);
            }
            // Smooth across neighbouring bins (1-2-3-2-1) against isolated "musical noise" peaks.
            for (var k = 0; k < bins; k++)
            {
                float sum = 0, weight = 0;
                for (var d = -2; d <= 2; d++)
                {
                    var j = k + d;
                    if (j < 0 || j >= bins) continue;
                    var w = 3 - Math.Abs(d);
                    sum += raw[j] * w;
                    weight += w;
                }
                var g = sum / weight;
                gain[k] = MathF.Max(g, previous[k] * Release + g * (1 - Release));
            }
            Array.Copy(gain, previous, bins);
            s.ApplyGain(gain);
        }, cancel)!;
    }
}

/// <summary>
/// "Remove vocal" (port of the macOS VocalRemover): the mix minus a voice estimate, as a Wiener mask on the mix.
/// Stream 0 is the mix, stream 1 the time-aligned voice estimate.
/// </summary>
public static class VocalRemover
{
    /// <summary>Weight of the voice estimate in the mask; &gt;1 removes voice residue more aggressively.</summary>
    public const double VoiceWeight = 2;

    public static float[][] Process(float[][] mix, float[][] voice, int voiceStart = 0, CancellationToken cancel = default)
    {
        var gain = new float[SpectralFrame.Bins];
        return SpectralFrame.Run([mix, voice], [0, voiceStart], mix[0].Length, true, s =>
        {
            for (var k = 0; k < SpectralFrame.Bins; k++)
            {
                double pv = 0, pb = 0;
                for (var c = 0; c < s.Channels; c++)
                {
                    pv += s.Power(1, c, k);
                    double dr = s.Re[0][c][k] - s.Re[1][c][k], di = s.Im[0][c][k] - s.Im[1][c][k];
                    pb += dr * dr + di * di;
                }
                var sum = pb + VoiceWeight * pv;
                gain[k] = sum > 0 ? (float)(pb / sum) : 1;
            }
            s.ApplyGain(gain);
        }, cancel)!;
    }
}
