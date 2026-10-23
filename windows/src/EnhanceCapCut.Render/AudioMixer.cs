using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Render;

/// <summary>Decoded audio: interleaved stereo float at <see cref="AudioMixer.SampleRate"/>.</summary>
/// <param name="StartUs">Source time of the first frame.</param>
public sealed record DecodedAudio(float[] Samples, long StartUs)
{
    public int Frames => Samples.Length / 2;
}

/// <summary>Decodes a range of a media file's audio (Media Foundation on Windows, synthetic in tests).</summary>
public interface IAudioDecoder
{
    /// <summary>Source times <paramref name="fromUs"/>..<paramref name="toUs"/> (µs), or null when the file has no audio.</summary>
    DecodedAudio? Decode(string path, long fromUs, long toUs);
}

/// <summary>
/// Mixes all audio clips of a timeline into one 48 kHz stereo float buffer (the counterpart of the macOS
/// AudioRenderer, which leaves the mixing to AVFoundation).
///
/// Every output frame of a clip maps to a source position through the clip's <see cref="TimeMap"/>, so constant
/// speeds and speed curves go through the same code. Like CapCut, a clip with its "pitch" toggle on plays like a
/// tape (resampled, pitch follows speed); otherwise the pitch is kept with WSOLA time-stretching. Volumes and
/// fades are linear ramps; the sum goes through the master <see cref="PeakLimiter"/>.
/// </summary>
public static class AudioMixer
{
    public const int SampleRate = 48_000;
    /// <summary>Master limiter ceiling, as in the macOS version.</summary>
    public const double CeilingDB = -0.2;
    /// <summary>Extra source decoded around a clip's range for interpolation and the time-stretch search.</summary>
    const long DecodeMarginUs = 100_000;

    public static long Frames(long us) => (long)Math.Round(us * (double)SampleRate / 1_000_000, MidpointRounding.AwayFromZero);

    /// <summary>For partial exports: the audio clips inside <paramref name="range"/>, shifted so the range starts at zero.</summary>
    public static Timeline TrimmedForAudio(Timeline timeline, TimeRange range)
    {
        var clips = new List<AudioClip>();
        foreach (var clip in timeline.AudioClips)
        {
            long start = Math.Max(clip.Target.Start, range.Start), end = Math.Min(clip.Target.End, range.End);
            if (end <= start) continue;
            clips.Add(clip with
            {
                Target = new TimeRange(start - range.Start, end - start),
                TimeMap = clip.TimeMap.Slice(start - clip.Target.Start, end - clip.Target.Start),
                FadeIn = start != clip.Target.Start ? 0 : clip.FadeIn,
                FadeOut = end != clip.Target.End ? 0 : clip.FadeOut,
            });
        }
        return new Timeline(timeline.Name, timeline.CanvasWidth, timeline.CanvasHeight, range.Duration, timeline.Fps,
            [], clips, timeline.Report);
    }

    /// <summary>The whole mix (interleaved stereo); <paramref name="warnings"/> collects clips that could not be decoded.</summary>
    public static float[] Mix(Timeline timeline, IAudioDecoder decoder, List<string> warnings,
        Action<double>? progress = null, CancellationToken cancel = default)
    {
        var total = Frames(timeline.Duration);
        var mix = new float[total * 2];
        var clips = timeline.AudioClips.OrderBy(c => c.Target.Start).ToList();
        for (var i = 0; i < clips.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var clip = clips[i];
            var map = clip.TimeMap;
            var audio = decoder.Decode(clip.Path, Math.Max(0, map.SourceStart - DecodeMarginUs),
                map.SourceStart + map.SourceDuration + DecodeMarginUs);
            if (audio is null || audio.Frames == 0)
            {
                warnings.Add($"{Path.GetFileName(clip.Path)}: no audio could be decoded");
                continue;
            }
            AddClip(mix, clip, audio);
            progress?.Invoke((i + 1.0) / clips.Count);
        }
        PeakLimiter.LimitInPlace(mix, SampleRate, CeilingDB);
        return mix;
    }

    /// <summary>Renders one clip into the mix at its timeline position.</summary>
    public static void AddClip(float[] mix, AudioClip clip, DecodedAudio audio)
    {
        var start = Frames(clip.Target.Start);
        var count = Math.Min(Frames(clip.Target.End), mix.Length / 2) - start;
        if (count <= 0) return;
        var map = clip.TimeMap;
        // Source position (in decoded frames) shown at output frame j of the clip.
        double SourceFrame(double j) =>
            (map.PreciseSourceTime(j * 1_000_000.0 / SampleRate) - audio.StartUs) * SampleRate / 1_000_000;

        var rendered = new float[count * 2];
        var speedChanges = !map.IsConstant || Math.Abs(map.AverageSpeed - 1) > 0.001;
        if (speedChanges && clip.PreservePitch) TimeStretch.Render(audio.Samples, SourceFrame, rendered);
        else Resample(audio.Samples, SourceFrame, rendered);

        // Volume with linear fade ramps (each at most half the clip, like the macOS mix).
        var fadeIn = Frames(Math.Min(clip.FadeIn, clip.Target.Duration / 2));
        var fadeOut = Frames(Math.Min(clip.FadeOut, clip.Target.Duration / 2));
        for (long j = 0; j < count; j++)
        {
            var g = clip.Volume;
            if (j < fadeIn) g *= (double)j / fadeIn;
            if (j >= count - fadeOut) g *= (double)(count - j) / fadeOut;
            var o = (start + j) * 2;
            if (o < 0) continue;
            mix[o] += (float)(rendered[j * 2] * g);
            mix[o + 1] += (float)(rendered[j * 2 + 1] * g);
        }
    }

    /// <summary>Varispeed: reads the source at fractional positions (cubic interpolation); pitch follows speed.</summary>
    public static void Resample(float[] source, Func<double, double> sourceFrame, float[] output)
    {
        var frames = source.Length / 2;
        for (var j = 0; j < output.Length / 2; j++)
        {
            var pos = sourceFrame(j);
            var i = (int)Math.Floor(pos);
            var f = (float)(pos - i);
            for (var c = 0; c < 2; c++)
            {
                float At(int k) => k >= 0 && k < frames ? source[k * 2 + c] : 0;
                float y0 = At(i - 1), y1 = At(i), y2 = At(i + 1), y3 = At(i + 2);
                // Catmull-Rom.
                output[j * 2 + c] = y1 + 0.5f * f * (y2 - y0 + f * (2 * y0 - 5 * y1 + 4 * y2 - y3 + f * (3 * (y1 - y2) + y3 - y0)));
            }
        }
    }
}

/// <summary>
/// WSOLA time-stretching (waveform-similarity overlap-add): changes speed and keeps the pitch. Output is built
/// from Hann-windowed source segments; each segment is taken near its ideal source position at the point that
/// best continues the previous one (cross-correlation search), which avoids phasing artifacts.
/// </summary>
public static class TimeStretch
{
    /// <summary>Output hop in frames (~10.7 ms at 48 kHz); segments are twice as long.</summary>
    const int Hop = 512;
    /// <summary>How far (frames) around the ideal position a segment may move to match the previous one.</summary>
    const int Tolerance = 384;

    public static void Render(float[] source, Func<double, double> sourceFrame, float[] output)
    {
        var frames = source.Length / 2;
        var outFrames = output.Length / 2;
        const int n = Hop * 2;
        var window = new float[n];
        for (var i = 0; i < n; i++) window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
        var norm = new float[outFrames];
        double Mono(int k) => k >= 0 && k < frames ? source[k * 2] + source[k * 2 + 1] : 0;

        var previous = int.MinValue;
        for (var outStart = -Hop; outStart < outFrames; outStart += Hop)
        {
            var ideal = (int)Math.Round(sourceFrame(Math.Max(outStart, 0)) - (outStart < 0 ? Hop : 0));
            var best = ideal;
            if (previous != int.MinValue)
            {
                // The natural continuation of the previous segment, one hop later.
                var natural = previous + Hop;
                var bestScore = double.NegativeInfinity;
                for (var delta = -Tolerance; delta <= Tolerance; delta += 4)
                {
                    var candidate = ideal + delta;
                    double score = 0;
                    for (var k = 0; k < Hop; k += 4) score += Mono(natural + k) * Mono(candidate + k);
                    if (score > bestScore) { bestScore = score; best = candidate; }
                }
            }
            previous = best;
            for (var i = 0; i < n; i++)
            {
                var o = outStart + i;
                if (o < 0 || o >= outFrames) continue;
                var s = best + i;
                if (s < 0 || s >= frames) { norm[o] += window[i]; continue; }
                output[o * 2] += source[s * 2] * window[i];
                output[o * 2 + 1] += source[s * 2 + 1] * window[i];
                norm[o] += window[i];
            }
        }
        for (var o = 0; o < outFrames; o++)
        {
            if (norm[o] <= 1e-3f) continue;
            output[o * 2] /= norm[o];
            output[o * 2 + 1] /= norm[o];
        }
    }
}

/// <summary>
/// EBU R128 / ITU-R BS.1770-4 integrated loudness (K-weighting, 400 ms blocks with 75 % overlap, absolute and
/// relative gating) and normalization with a linear gain followed by a limiter — the counterpart of the macOS
/// version's two-pass ffmpeg <c>loudnorm</c>, which also prefers a linear gain.
/// </summary>
public static class Loudness
{
    /// <summary>Ceiling after normalization (sample peak), like loudnorm's -1 dBTP.</summary>
    public const double NormalizedCeilingDB = -1;

    public static double Integrated(float[] stereo, int sampleRate = AudioMixer.SampleRate)
    {
        if (sampleRate != 48_000) throw new ArgumentException("K-weighting coefficients are for 48 kHz");
        var frames = stereo.Length / 2;
        var weighted = new double[2][];
        for (var c = 0; c < 2; c++)
        {
            var x = new double[frames];
            for (var i = 0; i < frames; i++) x[i] = stereo[i * 2 + c];
            // Stage 1: high shelf (head effect); stage 2: high pass (RLB). BS.1770 coefficients at 48 kHz.
            x = Biquad(x, [1.53512485958697, -2.69169618940638, 1.19839281085285], [-1.69065929318241, 0.73248077421585]);
            x = Biquad(x, [1.0, -2.0, 1.0], [-1.99004745483398, 0.99007225036621]);
            weighted[c] = x;
        }
        int block = sampleRate * 4 / 10, step = block / 4;
        var powers = new List<double>();
        for (var s = 0; s + block <= frames; s += step)
        {
            double sum = 0;
            for (var c = 0; c < 2; c++)
            {
                double ms = 0;
                for (var i = s; i < s + block; i++) ms += weighted[c][i] * weighted[c][i];
                sum += ms / block;
            }
            powers.Add(sum);
        }
        static double Lufs(double power) => -0.691 + 10 * Math.Log10(Math.Max(power, 1e-20));
        var absolute = powers.Where(p => Lufs(p) > -70).ToList();
        if (absolute.Count == 0) return double.NegativeInfinity;
        var relativeGate = Lufs(absolute.Average()) - 10;
        var gated = absolute.Where(p => Lufs(p) > relativeGate).ToList();
        return gated.Count == 0 ? double.NegativeInfinity : Lufs(gated.Average());
    }

    /// <summary>Brings the mix to <paramref name="targetLUFS"/>; silent mixes are left alone.</summary>
    public static void Normalize(float[] stereo, double targetLUFS)
    {
        var measured = Integrated(stereo);
        if (double.IsNegativeInfinity(measured) || measured < -69) return;
        var gain = (float)Math.Pow(10, (targetLUFS - measured) / 20);
        for (var i = 0; i < stereo.Length; i++) stereo[i] *= gain;
        PeakLimiter.LimitInPlace(stereo, AudioMixer.SampleRate, NormalizedCeilingDB);
    }

    static double[] Biquad(double[] x, double[] b, double[] a)
    {
        var y = new double[x.Length];
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var v = b[0] * x[i] + b[1] * x1 + b[2] * x2 - a[0] * y1 - a[1] * y2;
            x2 = x1; x1 = x[i]; y2 = y1; y1 = v;
            y[i] = v;
        }
        return y;
    }
}
