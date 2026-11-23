using System.Numerics;
using EnhanceCapCut.Render;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace EnhanceCapCut.Subtitles;

/// <summary>
/// Voice isolation on Windows (in place of Apple's AUSoundIsolation on the Mac): DeepFilterNet3 (Hendrik Schröter,
/// MIT / Apache-2.0), the neural graph exported to ONNX (<see cref="ModelStore.VoiceIsolation"/>), with its signal
/// processing re-implemented here from the published model contract:
/// - 48 kHz, 960-point FFT with a 480-sample hop and a Vorbis window, spectrum scaled by 1/960;
/// - 32 ERB bands (log power, running-mean normalized) and the first 96 bins (unit-normalized) as features;
/// - the model returns a gain per ERB band and a 5-tap complex deep filter for the first 96 bins (frames t−2…t+2,
///   applied to the unprocessed spectrum); overlap-add resynthesis, 480-sample delay removed.
/// The model is stateless per run, so long clips go through it in chunks with some context on both sides; the
/// feature normalization runs over the whole clip.
/// </summary>
public sealed class VoiceIsolator : IVoiceIsolator, IDisposable
{
    const int FftSize = 960, Hop = 480, Bins = FftSize / 2 + 1;
    const int ErbBands = 32, DfBins = 96, DfOrder = 5, DfLookahead = 2;
    /// <summary>Feature normalization: exp(−hop / (rate × τ)) with τ = 1 s, rounded as in DeepFilterNet.</summary>
    const float Alpha = 0.99f;
    /// <summary>Frames per model run, and the context run before / after each chunk and then dropped.</summary>
    const int ChunkFrames = 3000, ContextBefore = 300, ContextAfter = 20;

    readonly InferenceSession session;
    readonly int[] erbWidths = ErbWidths(AudioMixer.SampleRate, FftSize, ErbBands, 2);
    readonly double[] window = new double[FftSize];

    public VoiceIsolator(string modelPath)
    {
        session = new InferenceSession(modelPath, new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL });
        for (var i = 0; i < FftSize; i++)
        {
            var s = Math.Sin(0.5 * Math.PI * (i + 0.5) / Hop);
            window[i] = Math.Sin(0.5 * Math.PI * s * s);
        }
    }

    /// <summary>The isolator from the downloaded model, or null when it isn't there or can't be loaded.</summary>
    public static VoiceIsolator? Load()
    {
        var path = ModelStore.PathOf(ModelStore.VoiceIsolation);
        if (!File.Exists(path)) return null;
        try { return new VoiceIsolator(path); }
        catch (OnnxRuntimeException) { return null; }
    }

    public void Dispose() => session.Dispose();

    /// <summary>
    /// Widths (in FFT bins) of the ERB bands: equal steps on the ERB scale, each at least <paramref name="minWidth"/>
    /// bins (DeepFilterNet's <c>erb_fb</c>).
    /// </summary>
    public static int[] ErbWidths(int rate, int fftSize, int bands, int minWidth)
    {
        static double FreqToErb(double hz) => 9.265 * Math.Log(1 + hz / (24.7 * 9.265));
        static double ErbToFreq(double erb) => 24.7 * 9.265 * (Math.Exp(erb / 9.265) - 1);
        var binWidth = (float)rate / fftSize;
        var high = (float)FreqToErb(rate / 2);
        var step = high / bands;
        var widths = new int[bands];
        int previous = 0, over = 0;
        for (var i = 1; i <= bands; i++)
        {
            var f = (float)ErbToFreq((float)(i * step));
            var bin = (int)MathF.Round(f / binWidth, MidpointRounding.AwayFromZero);
            var width = bin - previous - over;
            if (width < minWidth)
            {
                over = minWidth - width;
                width = minWidth;
            }
            else over = 0;
            widths[i - 1] = width;
            previous = bin;
        }
        widths[^1] += 1; // fft_size / 2 + 1 bins
        var tooMany = widths.Sum() - (fftSize / 2 + 1);
        if (tooMany > 0) widths[^1] -= tooMany;
        return widths;
    }

    public float[] Isolate(float[] channel, CancellationToken cancel = default)
    {
        var length = channel.Length;
        if (length == 0) return [];
        // One analysis tail past the end so the last samples are complete.
        var frames = (length + FftSize + Hop - 1) / Hop;
        var fft = new Fft(FftSize);
        var spec = new Complex[frames][];
        var featErb = new float[frames * ErbBands];
        var featSpec = new float[2 * frames * DfBins];
        var erbState = new float[ErbBands];
        for (var b = 0; b < ErbBands; b++) erbState[b] = -60f - 30f * b / (ErbBands - 1);
        var unitState = new float[DfBins];
        for (var k = 0; k < DfBins; k++) unitState[k] = 0.001f - 0.0009f * k / (DfBins - 1);
        double[] re = new double[FftSize], im = new double[FftSize], outRe = new double[FftSize], outIm = new double[FftSize];

        // Analysis and features.
        for (var t = 0; t < frames; t++)
        {
            if (t % 512 == 0) cancel.ThrowIfCancellationRequested();
            for (var i = 0; i < FftSize; i++)
            {
                var j = t * Hop - Hop + i;
                re[i] = j >= 0 && j < length ? channel[j] * window[i] : 0;
                im[i] = 0;
            }
            fft.Transform(re, im, outRe, outIm);
            var s = spec[t] = new Complex[Bins];
            for (var k = 0; k < Bins; k++) s[k] = new Complex(outRe[k] / FftSize, outIm[k] / FftSize);
            var k0 = 0;
            for (var b = 0; b < ErbBands; b++)
            {
                double power = 0;
                for (var k = k0; k < k0 + erbWidths[b]; k++) power += s[k].Real * s[k].Real + s[k].Imaginary * s[k].Imaginary;
                k0 += erbWidths[b];
                var db = (float)(10 * Math.Log10(power / erbWidths[b] + 1e-10));
                erbState[b] = db * (1 - Alpha) + erbState[b] * Alpha;
                featErb[t * ErbBands + b] = (db - erbState[b]) / 40;
            }
            for (var k = 0; k < DfBins; k++)
            {
                unitState[k] = (float)s[k].Magnitude * (1 - Alpha) + unitState[k] * Alpha;
                var norm = MathF.Sqrt(unitState[k]);
                featSpec[t * DfBins + k] = (float)(s[k].Real / norm);
                featSpec[frames * DfBins + t * DfBins + k] = (float)(s[k].Imaginary / norm);
            }
        }

        // Model, in chunks.
        var mask = new float[frames * ErbBands];
        var coefs = new float[frames * DfOrder * DfBins * 2];
        for (var c0 = 0; c0 < frames; c0 += ChunkFrames)
        {
            cancel.ThrowIfCancellationRequested();
            var c1 = Math.Min(frames, c0 + ChunkFrames);
            int r0 = Math.Max(0, c0 - ContextBefore), r1 = Math.Min(frames, c1 + ContextAfter), n = r1 - r0;
            var erb = new DenseTensor<float>([1, 1, n, ErbBands]);
            var cplx = new DenseTensor<float>([1, 2, n, DfBins]);
            featErb.AsSpan(r0 * ErbBands, n * ErbBands).CopyTo(erb.Buffer.Span);
            for (var ch = 0; ch < 2; ch++)
                featSpec.AsSpan(ch * frames * DfBins + r0 * DfBins, n * DfBins).CopyTo(cplx.Buffer.Span[(ch * n * DfBins)..]);
            using var results = session.Run([NamedOnnxValue.CreateFromTensor("feat_erb", erb), NamedOnnxValue.CreateFromTensor("feat_spec", cplx)]);
            var m = results.First(v => v.Name == "erb_mask").AsTensor<float>().ToDenseTensor().Buffer.Span;
            var d = results.First(v => v.Name == "df_coefs").AsTensor<float>().ToDenseTensor().Buffer.Span;
            m.Slice((c0 - r0) * ErbBands, (c1 - c0) * ErbBands).CopyTo(mask.AsSpan(c0 * ErbBands));
            // df_coefs [1, order, n, 96, 2] → ours [frame, order, 96, 2].
            for (var t = c0; t < c1; t++)
                for (var o = 0; o < DfOrder; o++)
                    d.Slice(((o * n) + (t - r0)) * DfBins * 2, DfBins * 2).CopyTo(coefs.AsSpan(((t * DfOrder) + o) * DfBins * 2));
        }

        // Enhancement and synthesis.
        var output = new float[length];
        var overlap = new double[Hop];
        for (var t = 0; t < frames; t++)
        {
            var s = spec[t];
            var k0 = 0;
            for (var b = 0; b < ErbBands; b++)
            {
                var g = mask[t * ErbBands + b];
                for (var k = k0; k < k0 + erbWidths[b]; k++)
                {
                    var v = k >= DfBins ? s[k] * g : DeepFilter(spec, coefs, t, k);
                    re[k] = v.Real;
                    im[k] = v.Imaginary;
                }
                k0 += erbWidths[b];
            }
            im[0] = 0;
            im[Bins - 1] = 0;
            for (var k = Bins; k < FftSize; k++)
            {
                re[k] = re[FftSize - k];
                im[k] = -im[FftSize - k];
            }
            fft.Transform(re, im, outRe, outIm, inverse: true);
            // Samples t·hop − hop … t·hop are complete now: the STFT delay is removed here.
            for (var i = 0; i < Hop; i++)
            {
                var j = t * Hop - Hop + i;
                var v = outRe[i] * window[i] + overlap[i];
                if (j >= 0 && j < length) output[j] = (float)v;
                overlap[i] = outRe[Hop + i] * window[Hop + i];
            }
        }
        return output;
    }

    static Complex DeepFilter(Complex[][] spec, float[] coefs, int t, int k)
    {
        var sum = Complex.Zero;
        for (var o = 0; o < DfOrder; o++)
        {
            var source = t - (DfOrder - 1 - DfLookahead) + o;
            if (source < 0 || source >= spec.Length) continue;
            var i = ((t * DfOrder) + o) * DfBins * 2 + k * 2;
            sum += spec[source][k] * new Complex(coefs[i], coefs[i + 1]);
        }
        return sum;
    }
}
