using System.Text.Json;
using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Render;

/// <summary>
/// Runs a voice filter's processor graph (<see cref="VoiceFilterGraph"/>, read from the user's CapCut cache) with
/// our own DSP — the port of the macOS <c>VoiceFilterGraph.process</c> and its processors. Behaviour the preset
/// doesn't pin down (filter Q, the distortion curve, makeup gain, the reverb) is calibrated against the author's
/// exports on the Mac side; the constants are the same here (see docs/FORMAT.md).
/// </summary>
public static class VoiceFilterProcessing
{
    /// <summary>
    /// The output of a ParameterChangeTransform that drives a gain node is a normalized gain: CapCut's presets set
    /// the wet gain to 0.4444 at full strength where the stored gain is 0 dB, so unity maps to 4/9.
    /// </summary>
    public const double UnityNormalizedGain = 0.4444;

    /// <summary>
    /// Runs the graph over whole planar channels (one clip) at <paramref name="rate"/>; <paramref name="strength"/> is
    /// the filter's 0...1 slider. <paramref name="loadLoop"/> decodes a Source node's looping clip to planar channels
    /// at <paramref name="rate"/> (null when it can't be read: the node stays silent).
    /// </summary>
    public static float[][] Process(this VoiceFilterGraph graph, float[][] input, double rate, double strength,
        Func<string, float[][]?>? loadLoop = null)
    {
        var frames = input.Length > 0 ? input[0].Length : 0;
        var outputs = new Dictionary<string, float[][]>();
        var parameterValues = graph.ParameterInputs.ToDictionary(id => id, _ => strength);
        var pending = new SortedSet<string>(graph.Nodes.Keys, StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            // Next node whose upstream nodes are all done.
            var id = pending.FirstOrDefault(id => graph.Connections.All(c => c.To != id || !pending.Contains(c.From)));
            if (id is null) break;
            pending.Remove(id);
            var node = graph.Nodes[id];
            if (node.Kind == VoiceFilterGraph.Kind.Transform)
            {
                var value = parameterValues.TryGetValue(id, out var v) ? v : strength;
                foreach (var c in graph.Connections.Where(c => c.From == id && c.IsParameter))
                    parameterValues[c.To] = Interpolate(node.Curve, value);
                continue;
            }
            float[][]? sum = graph.AudioInputs.Contains(id) ? input.Select(c => (float[])c.Clone()).ToArray() : null;
            foreach (var c in graph.Connections.Where(c => c.To == id && !c.IsParameter))
            {
                if (!outputs.TryGetValue(c.From, out var upstream)) continue;
                if (sum is null) sum = upstream.Select(x => (float[])x.Clone()).ToArray();
                else Add(sum, upstream);
            }
            var x = sum ?? input.Select(_ => new float[frames]).ToArray();
            Run(node, x, rate, parameterValues.TryGetValue(id, out var g) ? g : null, loadLoop);
            outputs[id] = x;
        }
        return outputs.TryGetValue(graph.Output, out var result) ? result : input;
    }

    public static double Interpolate(IReadOnlyList<(double X, double Y)> curve, double x)
    {
        if (curve.Count == 0) return x;
        if (x <= curve[0].X) return curve[0].Y;
        if (x >= curve[^1].X) return curve[^1].Y;
        for (var i = 1; i < curve.Count; i++)
        {
            var (a, b) = (curve[i - 1], curve[i]);
            if (x > b.X) continue;
            var t = b.X > a.X ? (x - a.X) / (b.X - a.X) : 1;
            return a.Y + t * (b.Y - a.Y);
        }
        return curve[^1].Y;
    }

    static void Add(float[][] a, float[][] b)
    {
        for (var c = 0; c < Math.Min(a.Length, b.Length); c++)
        {
            var n = Math.Min(a[c].Length, b[c].Length);
            for (var i = 0; i < n; i++) a[c][i] += b[c][i];
        }
    }

    static void Run(VoiceFilterGraph.Node node, float[][] x, double rate, double? gainParameter, Func<string, float[][]?>? loadLoop)
    {
        var p = node.Parameters;
        double Num(string key, double fallback) => p.TryGetValue(key, out var v) ? VoiceFilterGraph.Number(v) ?? fallback : fallback;
        bool Flag(string key) => p.TryGetValue(key, out var v) && v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => v.GetDouble() != 0,
            _ => false,
        };
        string Text(string key) => p.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        if (Flag("Bypass")) return;

        switch (node.Kind)
        {
            case VoiceFilterGraph.Kind.Filter:
            {
                var biquad = Biquad.Make(Text("Filter Mode"), Num("Cutoff Freq", 1000), Num("Gain dB", 0), Num("Resonance", 1), rate);
                foreach (var c in x) biquad?.Run(c);
                break;
            }
            case VoiceFilterGraph.Kind.Equaliser:
                for (var band = 1; band <= 16; band++)
                {
                    var key = $"Filter {band} ";
                    if (!p.ContainsKey(key + "Mode") || Flag(key + "Bypass")) continue;
                    var biquad = Biquad.Make(Text(key + "Mode"), Num(key + "Freq", 1000), Num(key + "Gain dB", 0), Num(key + "Q", 1), rate);
                    foreach (var c in x) biquad?.Run(c);
                }
                Scale(x, Num("Gain dB", 0));
                break;
            case VoiceFilterGraph.Kind.Distortion:
                new Distortion(Text("Distortion Mode"), Num("Distortion Amount", 0.5), Num("Wetness", 1),
                    Num("Post Distortion Gain", 0)).Run(x);
                break;
            case VoiceFilterGraph.Kind.Compressor:
                new Compressor(Num("Threshold dB", 0), Num("Ratio", 1), Num("Knee dB", 0), Num("Attack", 1),
                    Num("Release", 100), Flag("Auto Makeup Gain"), Num("Output Gain dB", 0)).Run(x, rate);
                break;
            case VoiceFilterGraph.Kind.Reverb:
                new HallReverb(Num("Pre Delay 1", 0) + Num("Pre Delay 2", 0), Num("Crossover", 250), Num("HF Damping", 5000),
                    Num("Low RT60", 2), Num("Mid RT60", 2), Num("Hi RT60", 1), Num("Wet Mix", 0.5), Num("Wet Gain", 0),
                    Num("Wet Stereo Width", 1)).Run(x, rate);
                break;
            case VoiceFilterGraph.Kind.Gain:
            case VoiceFilterGraph.Kind.GainAndPan:
                if (gainParameter is { } normalized)
                {
                    var g = (float)(normalized / UnityNormalizedGain);
                    foreach (var c in x) for (var i = 0; i < c.Length; i++) c[i] *= g;
                }
                else Scale(x, Num("Gain dB", 0));
                break;
            case VoiceFilterGraph.Kind.Source:
                if (node.SourceFile is { } file && loadLoop?.Invoke(file) is { } loop) Loop(loop, x);
                break;
        }
    }

    static void Scale(float[][] x, double decibels)
    {
        if (decibels == 0) return;
        var g = decibels <= -70 ? 0f : (float)Math.Pow(10, decibels / 20);
        foreach (var c in x) for (var i = 0; i < c.Length; i++) c[i] *= g;
    }

    /// <summary>Fills <paramref name="x"/> with <paramref name="loop"/> repeated from its start (mono spread to all channels).</summary>
    static void Loop(float[][] loop, float[][] x)
    {
        if (loop.Length == 0 || loop[0].Length == 0) return;
        for (var c = 0; c < x.Length; c++)
        {
            var source = loop[Math.Min(c, loop.Length - 1)];
            for (var i = 0; i < x[c].Length; i++) x[c][i] = source[i % source.Length];
        }
    }

    /// <summary>RBJ cookbook biquad; the presets' "Resonance"/"Q" is the filter Q (see the macOS version).</summary>
    public sealed record Biquad(double B0, double B1, double B2, double A1, double A2)
    {
        public static Biquad? Make(string mode, double frequency, double gainDB, double q, double rate)
        {
            var f = Math.Clamp(frequency, 1, rate * 0.49);
            double w = 2 * Math.PI * f / rate, cw = Math.Cos(w), alpha = Math.Sin(w) / (2 * Math.Max(q, 0.05));
            var a = Math.Pow(10, gainDB / 40);
            (double, double, double, double, double, double) c;
            switch (mode)
            {
                case "LowPass": c = ((1 - cw) / 2, 1 - cw, (1 - cw) / 2, 1 + alpha, -2 * cw, 1 - alpha); break;
                case "HighPass": c = ((1 + cw) / 2, -(1 + cw), (1 + cw) / 2, 1 + alpha, -2 * cw, 1 - alpha); break;
                case "BandPass": c = (alpha, 0, -alpha, 1 + alpha, -2 * cw, 1 - alpha); break;
                case "Notch": c = (1, -2 * cw, 1, 1 + alpha, -2 * cw, 1 - alpha); break;
                case "Bell" or "Peak": c = (1 + alpha * a, -2 * cw, 1 - alpha * a, 1 + alpha / a, -2 * cw, 1 - alpha / a); break;
                case "LowShelf":
                {
                    var s = 2 * Math.Sqrt(a) * alpha;
                    c = (a * ((a + 1) - (a - 1) * cw + s), 2 * a * ((a - 1) - (a + 1) * cw), a * ((a + 1) - (a - 1) * cw - s),
                        (a + 1) + (a - 1) * cw + s, -2 * ((a - 1) + (a + 1) * cw), (a + 1) + (a - 1) * cw - s);
                    break;
                }
                case "HighShelf":
                {
                    var s = 2 * Math.Sqrt(a) * alpha;
                    c = (a * ((a + 1) + (a - 1) * cw + s), -2 * a * ((a - 1) + (a + 1) * cw), a * ((a + 1) + (a - 1) * cw - s),
                        (a + 1) - (a - 1) * cw + s, 2 * ((a - 1) - (a + 1) * cw), (a + 1) - (a - 1) * cw - s);
                    break;
                }
                default: return null;
            }
            return new(c.Item1 / c.Item4, c.Item2 / c.Item4, c.Item3 / c.Item4, c.Item5 / c.Item4, c.Item6 / c.Item4);
        }

        public static Biquad HighPass(double f, double rate, double q = 0.70710678118654752) => Make("HighPass", f, 0, q, rate)!;
        public static Biquad Peak(double f, double gainDB, double q, double rate) => Make("Bell", f, gainDB, q, rate)!;

        /// <summary>Filters <paramref name="x"/> in place, forward or backward.</summary>
        public void Run(float[] x, bool reverse = false)
        {
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (var j = 0; j < x.Length; j++)
            {
                var i = reverse ? x.Length - 1 - j : j;
                double input = x[i];
                var y = B0 * input + B1 * x1 + B2 * x2 - A1 * y1 - A2 * y2;
                x2 = x1; x1 = input; y2 = y1; y1 = y;
                x[i] = (float)y;
            }
        }

        /// <summary>Magnitude response in dB at <paramref name="frequency"/>.</summary>
        public double Response(double frequency, double rate)
        {
            var w = 2 * Math.PI * frequency / rate;
            double Mag(double c0, double c1, double c2)
            {
                double re = c0 + c1 * Math.Cos(w) + c2 * Math.Cos(2 * w), im = -(c1 * Math.Sin(w) + c2 * Math.Sin(2 * w));
                return Math.Sqrt(re * re + im * im);
            }
            return 20 * Math.Log10(Mag(B0, B1, B2) / Mag(1, A1, A2));
        }
    }

    /// <summary>Waveshaper mixed with the dry signal (<c>wetness</c>), then <c>postGainDB</c>.</summary>
    sealed record Distortion(string Mode, double Amount, double Wetness, double PostGainDB)
    {
        /// <summary>Drive at amount 1 (exponential in between), calibrated on Old Hollywood (macOS version).</summary>
        const double MaxDrive = 5.0;

        double Drive => Math.Pow(MaxDrive, Math.Clamp(Amount, 0, 1));

        double Shape(double v) => Mode switch
        {
            "FoldBackSinArctan" => Math.Sin(2 * Math.Atan(Drive * v)),
            "HardClip" => Math.Clamp(Drive * v, -1, 1),
            _ => Math.Tanh(Drive * v),
        };

        public void Run(float[][] x)
        {
            double wet = Math.Clamp(Wetness, 0, 1), post = Math.Pow(10, PostGainDB / 20);
            foreach (var c in x)
            {
                for (var i = 0; i < c.Length; i++)
                {
                    double v = c[i];
                    c[i] = (float)(((1 - wet) * v + wet * Shape(v)) * post);
                }
            }
        }
    }

    /// <summary>Feed-forward compressor: soft knee, stereo-linked peak detection, attack/release smoothing.</summary>
    sealed record Compressor(double ThresholdDB, double Ratio, double KneeDB, double AttackMs, double ReleaseMs,
        bool AutoMakeup, double OutputGainDB)
    {
        double Reduction(double level)
        {
            double over = level - ThresholdDB, slope = 1 - 1 / Math.Max(Ratio, 1);
            if (KneeDB > 0 && Math.Abs(over) <= KneeDB / 2)
            {
                var k = over + KneeDB / 2;
                return -slope * k * k / (2 * KneeDB);
            }
            return over > 0 ? -slope * over : 0;
        }

        public void Run(float[][] x, double rate)
        {
            double attack = Math.Exp(-1 / Math.Max(AttackMs / 1000 * rate, 1)), release = Math.Exp(-1 / Math.Max(ReleaseMs / 1000 * rate, 1));
            var makeup = OutputGainDB;
            // Auto makeup gives back the gain reduction a 0 dBFS signal would get.
            if (AutoMakeup) makeup -= Reduction(0);
            var g = 0.0;
            var frames = x.Length > 0 ? x[0].Length : 0;
            for (var i = 0; i < frames; i++)
            {
                float peak = 0;
                foreach (var c in x) peak = Math.Max(peak, Math.Abs(c[i]));
                var target = Reduction(20 * Math.Log10(Math.Max(peak, 1e-9)));
                g = target < g ? attack * g + (1 - attack) * target : release * g + (1 - release) * target;
                var gain = (float)Math.Pow(10, (g + makeup) / 20);
                foreach (var c in x) c[i] *= gain;
            }
        }
    }

    /// <summary>
    /// Our own hall: pre-delay, input diffusion and an 8-line feedback delay network with three-band decay set from
    /// the preset's RT60s; <c>wetMix</c> crossfades dry and wet. Constants as in the macOS version.
    /// </summary>
    sealed record HallReverb(double PreDelayMs, double CrossoverHz, double DampingHz, double LowRT60, double MidRT60,
        double HighRT60, double WetMix, double WetGainDB, double Width)
    {
        static readonly double[] LineMs = [31.3, 37.9, 43.1, 49.7, 56.3, 63.1, 71.9, 79.3];
        static readonly double[] DiffuserMs = [4.7, 7.1, 11.3, 15.7];
        const double WetLevel = 0.12;
        const double HighBandShare = 0.4;

        public void Run(float[][] x, double rate)
        {
            var frames = x.Length > 0 ? x[0].Length : 0;
            if (frames == 0) return;
            var channels = x.Length;
            var pre = (int)(PreDelayMs / 1000 * rate);
            var input = new double[frames];
            for (var c = 0; c < channels; c++)
                for (var i = 0; i < frames - Math.Min(pre, frames); i++) input[i + pre] += x[c][i] / (double)channels;
            foreach (var ms in DiffuserMs)
            {
                var n = Math.Max(1, (int)(ms / 1000 * rate));
                const double g = 0.6;
                var buffer = new double[n];
                var j = 0;
                for (var i = 0; i < frames; i++)
                {
                    double delayed = buffer[j], v = input[i] + g * delayed;
                    buffer[j] = v;
                    input[i] = delayed - g * v;
                    j = (j + 1) % n;
                }
            }
            var lengths = LineMs.Select(ms => Math.Max(1, (int)(ms / 1000 * rate))).ToArray();
            var count = lengths.Length;
            var lines = lengths.Select(l => new double[l]).ToArray();
            var positions = new int[count];
            double Gain(double rt60, int length) => Math.Pow(10, -3 * (double)length / rate / Math.Max(rt60, 0.05));
            var gLow = lengths.Select(l => Gain(LowRT60, l)).ToArray();
            var gMid = lengths.Select(l => Gain(MidRT60, l)).ToArray();
            var gHigh = lengths.Select(l => Gain(HighRT60, l)).ToArray();
            double kLow = 1 - Math.Exp(-2 * Math.PI * CrossoverHz / rate), kHigh = 1 - Math.Exp(-2 * Math.PI * DampingHz * HighBandShare / rate);
            var lowState = new double[count];
            var highState = new double[count];
            var outL = new float[frames];
            var outR = new float[frames];
            var taps = new double[count];
            var level = WetLevel * Math.Pow(10, WetGainDB / 20);
            for (var i = 0; i < frames; i++)
            {
                var sum = 0.0;
                for (var k = 0; k < count; k++)
                {
                    var v = lines[k][positions[k]];
                    lowState[k] += kLow * (v - lowState[k]);
                    highState[k] += kHigh * (v - highState[k]);
                    double low = lowState[k], high = v - highState[k], mid = v - low - high;
                    taps[k] = gLow[k] * low + gMid[k] * mid + gHigh[k] * high;
                    sum += taps[k];
                }
                var reflect = 2.0 / count * sum;
                double l = 0, r = 0;
                for (var k = 0; k < count; k++)
                {
                    lines[k][positions[k]] = input[i] + taps[k] - reflect;
                    positions[k] = (positions[k] + 1) % lengths[k];
                    if (k % 2 == 0) l += taps[k]; else r += taps[k];
                }
                outL[i] = (float)(l * level);
                outR[i] = (float)(r * level);
            }
            double mix = Math.Clamp(WetMix, 0, 1), spread = Math.Clamp(Width, 0, 1);
            for (var c = 0; c < channels; c++)
            {
                for (var i = 0; i < frames; i++)
                {
                    double a = c == 0 ? outL[i] : outR[i], b = c == 0 ? outR[i] : outL[i];
                    var wet = channels > 1 ? a * (1 + spread) / 2 + b * (1 - spread) / 2 : (a + b) / 2;
                    x[c][i] = (float)((1 - mix) * x[c][i] + mix * wet);
                }
            }
        }
    }
}
