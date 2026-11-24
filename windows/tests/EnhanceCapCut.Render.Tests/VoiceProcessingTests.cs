using System.Text.Json;
using EnhanceCapCut.Core;
using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Render.Tests;

static class Voice
{
    public const int Rate = AudioMixer.SampleRate;

    public static float[] Noise(int count, ulong seed)
    {
        var state = seed * 6364136223846793005 + 1442695040888963407;
        var x = new float[count];
        for (var i = 0; i < count; i++)
        {
            state = state * 6364136223846793005 + 1442695040888963407;
            x[i] = (long)(state >> 11) % 2_000_001 / 1_000_000f - 1;
        }
        return x;
    }

    public static float[] Sine(double hz, int count, double amplitude = 0.1) =>
        Enumerable.Range(0, count).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate))).ToArray();

    public static float[][] Stereo(float[] x) => [(float[])x.Clone(), (float[])x.Clone()];

    public static double Rms(float[] x, int from = 0, int? to = null)
    {
        var end = to ?? x.Length;
        double sum = 0;
        for (var i = from; i < end; i++) sum += x[i] * (double)x[i];
        return Math.Sqrt(sum / Math.Max(end - from, 1));
    }

    public static double DB(double v) => 20 * Math.Log10(v + 1e-12);

    public static double Correlation(float[] a, float[] b, int from, int to)
    {
        double ab = 0, aa = 0, bb = 0;
        for (var i = from; i < to; i++) { ab += a[i] * (double)b[i]; aa += a[i] * (double)a[i]; bb += b[i] * (double)b[i]; }
        return ab / Math.Sqrt(aa * bb + 1e-20);
    }

    /// <summary>A preset in CapCut's mdsp layout (our own minimal graph): input → chain → wet gain, input → dry gain.</summary>
    public static string Preset(object[] chain, object[]? extraNodes = null, object[]? extraConnections = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ecc-filter-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        static object Node(string id, string name, object state) =>
            new Dictionary<string, object> { ["node_id"] = id, ["processor_name"] = name, ["processor_state"] = state };
        static object Audio(string from, string to, int port = 0) => new Dictionary<string, object>
        {
            ["upstream_node_id"] = from, ["downstream_node_id"] = to, ["downstream_port_index"] = port, ["port_type"] = "audio",
        };
        static object Transform(double[][] curve) => new { transforms = new[] { new { input_parameter_index = 0, transforms = new[] {
            new { output_parameter_index = 0, transform_curve = curve } } } } };
        var nodes = chain.Select((n, i) => Node($"p{i}", (string)n.GetType().GetProperty("name")!.GetValue(n)!,
            new { parameters = n.GetType().GetProperty("parameters")!.GetValue(n) })).ToList();
        nodes.Add(Node("dry_transform", "ParameterChangeTransform", Transform([[0, 0.4444], [0.5, 0.3715], [1, 0]])));
        nodes.Add(Node("wet_transform", "ParameterChangeTransform", Transform([[0, 0], [0.5, 0.3715], [1, 0.4444]])));
        nodes.Add(Node("gain_dry", "GainAndPan", new { parameters = new Dictionary<string, object> { ["Gain dB"] = -70.0, ["Pan"] = 0.0 } }));
        nodes.Add(Node("gain_wet", "Gain", new { parameters = new Dictionary<string, object> { ["Gain dB"] = 0.0 } }));
        nodes.Add(Node("mixer", "Mixer", new { parameters = new { } }));
        nodes.AddRange(extraNodes ?? []);
        var connections = Enumerable.Range(1, chain.Length - 1).Select(i => Audio($"p{i - 1}", $"p{i}")).ToList();
        connections.AddRange([Audio($"p{chain.Length - 1}", "gain_wet"), Audio("gain_wet", "mixer", 1), Audio("gain_dry", "mixer")]);
        connections.Add(new Dictionary<string, object> { ["upstream_node_id"] = "dry_transform", ["downstream_node_id"] = "gain_dry", ["port_type"] = "parameter_change" });
        connections.Add(new Dictionary<string, object> { ["upstream_node_id"] = "wet_transform", ["downstream_node_id"] = "gain_wet", ["port_type"] = "parameter_change" });
        connections.AddRange(extraConnections ?? []);
        var root = new
        {
            nodes, connections,
            ports = new object[]
            {
                new { internal_node_id = "p0", port_direction = "input", port_type = "audio" },
                new { internal_node_id = "gain_dry", port_direction = "input", port_type = "audio" },
                new { internal_node_id = "dry_transform", port_direction = "input", port_type = "parameter_change" },
                new { internal_node_id = "wet_transform", port_direction = "input", port_type = "parameter_change" },
                new { internal_node_id = "mixer", port_direction = "output", port_type = "audio" },
            },
        };
        File.WriteAllText(Path.Combine(dir, "mdsp_preset.json"), JsonSerializer.Serialize(root));
        return dir;
    }
}

/// <summary>Decodes registered in-memory signals (interleaved stereo from source time 0).</summary>
sealed class MemoryDecoder : IAudioDecoder
{
    public readonly Dictionary<string, float[]> Files = [];

    public DecodedAudio? Decode(string path, long fromUs, long toUs)
    {
        if (!Files.TryGetValue(path, out var all)) return null;
        var frames = all.Length / 2;
        var first = (int)Math.Min(frames, AudioMixer.Frames(fromUs));
        var last = (int)Math.Min(frames, toUs == long.MaxValue ? frames : AudioMixer.Frames(toUs));
        return last <= first ? null : new DecodedAudio(all[(first * 2)..(last * 2)], first * 1_000_000L / AudioMixer.SampleRate);
    }
}

public class FftTests
{
    [Theory]
    [InlineData(960)]
    [InlineData(2048)]
    [InlineData(30)]
    public void MatchesTheDirectTransformAndInverts(int n)
    {
        var x = Voice.Noise(n * 2, 9);
        double[] re = x[..n].Select(v => (double)v).ToArray(), im = x[n..].Select(v => (double)v).ToArray();
        double[] outRe = new double[n], outIm = new double[n];
        var fft = new Fft(n);
        fft.Transform(re, im, outRe, outIm);
        for (var k = 0; k < n; k += Math.Max(1, n / 37))
        {
            double sr = 0, si = 0;
            for (var j = 0; j < n; j++)
            {
                var a = -2 * Math.PI * j * k / n;
                sr += re[j] * Math.Cos(a) - im[j] * Math.Sin(a);
                si += re[j] * Math.Sin(a) + im[j] * Math.Cos(a);
            }
            Assert.Equal(sr, outRe[k], 1e-9);
            Assert.Equal(si, outIm[k], 1e-9);
        }
        double[] backRe = new double[n], backIm = new double[n];
        fft.Transform(outRe, outIm, backRe, backIm, inverse: true);
        for (var j = 0; j < n; j++) Assert.Equal(re[j], backRe[j] / n, 1e-9);
    }
}

public class SpectralProcessingTests
{
    [Fact]
    public void UnityGainReconstructsTheInput()
    {
        var noise = Voice.Noise(20_000, 1);
        var signal = noise.Select((v, i) => 0.3f * v + 0.5f * (float)Math.Sin(i * 0.05)).ToArray();
        var ones = Enumerable.Repeat(1f, SpectralFrame.Bins).ToArray();
        var output = SpectralFrame.Run([Voice.Stereo(signal)], [0], signal.Length, true, s => s.ApplyGain(ones))!;
        var error = signal.Select((v, i) => v - output[0][i]).ToArray();
        Assert.True(Voice.Rms(error) < 1e-4, $"error {Voice.Rms(error)}");
    }

    [Fact]
    public void DenoiserRemovesStationaryNoiseAndKeepsTheTone()
    {
        var noise = Voice.Noise(Voice.Rate * 3, 2).Select(v => v * 0.05f).ToArray();
        var signal = noise.Select((n, i) => n + (i > noise.Length / 2 ? 0.3f * (float)Math.Sin(2 * Math.PI * 1000 * i / Voice.Rate) : 0)).ToArray();
        var output = SpectralDenoiser.Process(Voice.Stereo(signal), 0.85)[0];
        Assert.True(Voice.DB(Voice.Rms(output, 1_000, 60_000) / Voice.Rms(signal, 1_000, 60_000)) < -12, "noise-only part attenuated");
        Assert.True(Voice.DB(Voice.Rms(output, 100_000, 140_000) / Voice.Rms(signal, 100_000, 140_000)) > -1, "tone kept");
    }

    [Fact]
    public void VocalRemoverMask()
    {
        var mix = Voice.Noise(30_000, 3).Select(v => v * 0.3f).ToArray();
        var removed = VocalRemover.Process(Voice.Stereo(mix), Voice.Stereo(mix))[0];
        Assert.True(Voice.Rms(removed) < Voice.Rms(mix) * 0.01);
        var kept = VocalRemover.Process(Voice.Stereo(mix), Voice.Stereo(new float[mix.Length]))[0];
        Assert.Equal(Voice.Rms(mix), Voice.Rms(kept), 4);
    }
}

public class VoiceEnhancerTests
{
    [Fact]
    public void AmbientGainMapping()
    {
        Assert.Equal(VoiceEnhancer.AmbientAt75, VoiceEnhancer.AmbientGain(0.75), 9);
        Assert.Equal(1, VoiceEnhancer.AmbientGain(1), 9);
        Assert.Equal(0, VoiceEnhancer.AmbientGain(0), 9);
        Assert.True(VoiceEnhancer.AmbientGain(0.5) < VoiceEnhancer.AmbientGain(0.75));
    }

    static double GainDB(double hz)
    {
        var tone = Voice.Sine(hz, Voice.Rate);
        var x = (float[])tone.Clone();
        VoiceEnhancer.Apply(x, Voice.Rate);
        return Voice.DB(Voice.Rms(x, 12_000, 36_000) / Voice.Rms(tone, 12_000, 36_000));
    }

    [Fact]
    public void EqMatchesTheMeasuredCurve()
    {
        var b = Voice.DB(VoiceEnhancer.Gain);
        Assert.Equal(b, GainDB(1_000), 0.3);
        Assert.InRange(GainDB(4_000) - b, -0.6, 0.6);
        Assert.True(GainDB(50) - b < -9, "rumble cut");
        Assert.InRange(GainDB(12_800) - b, 9.5, 12.5);
    }

    [Fact]
    public void HighPassIsZeroPhase()
    {
        var tone = Voice.Sine(120, Voice.Rate);
        var x = (float[])tone.Clone();
        VoiceEnhancer.Apply(x, Voice.Rate);
        Assert.True(Voice.Correlation(x, tone, 12_000, 36_000) > 0.999);
    }
}

public class VoiceFilterProcessingTests
{
    const double Rate = Voice.Rate;

    static object Step(string name, object parameters) => new { name, parameters };

    [Fact]
    public void StrengthCrossfadesDryAndWet()
    {
        var graph = VoiceFilterGraph.Load(Voice.Preset([Step("Gain", new Dictionary<string, object> { ["Gain dB"] = -12.0 })]));
        var input = Voice.Stereo(Voice.Sine(1000, Voice.Rate));
        Assert.Equal(-12, Voice.DB(Voice.Rms(graph.Process(input, Rate, 1)[0]) / Voice.Rms(input[0])), 0.1);
        Assert.Equal(0, Voice.DB(Voice.Rms(graph.Process(input, Rate, 0)[1]) / Voice.Rms(input[1])), 0.1);
    }

    [Fact]
    public void FiltersFollowTheCookbookResponse()
    {
        var graph = VoiceFilterGraph.Load(Voice.Preset([
            Step("Filter", new Dictionary<string, object> { ["Cutoff Freq"] = 80.0, ["Filter Mode"] = "Bell", ["Gain dB"] = 10.0, ["Resonance"] = 1.0 }),
            Step("Equaliser", new Dictionary<string, object>
            {
                ["Filter 1 Mode"] = "HighPass", ["Filter 1 Freq"] = 750.0, ["Filter 1 Q"] = 1.0, ["Filter 1 Bypass"] = false,
                ["Filter 2 Mode"] = "LowPass", ["Filter 2 Freq"] = 10.0, ["Filter 2 Bypass"] = true, ["Gain dB"] = 0.0,
            }),
        ]));
        var bell = VoiceFilterProcessing.Biquad.Make("Bell", 80, 10, 1, Rate)!;
        var highPass = VoiceFilterProcessing.Biquad.Make("HighPass", 750, 0, 1, Rate)!;
        Assert.Equal(10, bell.Response(80, Rate), 0.01);
        Assert.Equal(0, highPass.Response(750, Rate), 0.01);
        Assert.Equal(-11.2, highPass.Response(375, Rate), 0.2);
        foreach (var f in new[] { 100.0, 400, 2000 })
        {
            var input = Voice.Stereo(Voice.Sine(f, Voice.Rate));
            var output = graph.Process(input, Rate, 1)[0];
            var skip = Voice.Rate / 4;
            var expected = bell.Response(f, Rate) + highPass.Response(f, Rate);
            Assert.Equal(expected, Voice.DB(Voice.Rms(output, skip) / Voice.Rms(input[0], skip)), 0.3);
        }
    }

    [Fact]
    public void ReverbTailDecaysAtTheMidRT60()
    {
        var graph = VoiceFilterGraph.Load(Voice.Preset([Step("StudioReverb", new Dictionary<string, object>
        {
            ["Reverb Mode"] = "Hall", ["Pre Delay 1"] = 25.0, ["Crossover"] = 250.0, ["HF Damping"] = 8000.0,
            ["Low RT60"] = 1.0, ["Mid RT60"] = 1.0, ["Hi RT60"] = 1.0, ["Wet Mix"] = 1.0, ["Wet Gain"] = 0.0, ["Wet Stereo Width"] = 1.0,
        })]));
        var burst = new float[Voice.Rate * 2];
        var noise = Voice.Noise(Voice.Rate / 10, 1);
        for (var i = 0; i < noise.Length; i++) burst[i] = 0.1f * noise[i];
        var output = graph.Process(Voice.Stereo(burst), Rate, 1)[0];
        double Level(double t) => Voice.DB(Voice.Rms(output, (int)(t * Rate), (int)((t + 0.1) * Rate)));
        Assert.InRange((Level(0.4) - Level(0.9)) / 0.5, 48, 72);
    }

    [Fact]
    public void CompressorAutoMakeupKeepsQuietSignalsLouder()
    {
        var graph = VoiceFilterGraph.Load(Voice.Preset([Step("Compressor", new Dictionary<string, object>
        {
            ["Threshold dB"] = -12.0, ["Ratio"] = 4.0, ["Knee dB"] = 0.0, ["Attack"] = 1.0, ["Release"] = 50.0,
            ["Auto Makeup Gain"] = true, ["Output Gain dB"] = 0.0,
        })]));
        var quiet = Voice.Stereo(Voice.Sine(1000, Voice.Rate, 0.05));
        Assert.Equal(9, Voice.DB(Voice.Rms(graph.Process(quiet, Rate, 1)[0]) / Voice.Rms(quiet[0])), 0.1);
    }

    [Fact]
    public void LoopingSourceIsMixedIn()
    {
        var dir = Voice.Preset([Step("Gain", new Dictionary<string, object> { ["Gain dB"] = 0.0 })],
            extraNodes:
            [
                new Dictionary<string, object> { ["node_id"] = "noise", ["processor_name"] = "Source", ["processor_state"] =
                    new { sequenced_clip = new { clip = new { audio_file_uri = "file://tone.wav", is_looping = true } } } },
                new Dictionary<string, object> { ["node_id"] = "noise_gain", ["processor_name"] = "Gain", ["processor_state"] =
                    new { parameters = new Dictionary<string, object> { ["Gain dB"] = -6.0 } } },
            ],
            extraConnections:
            [
                new { upstream_node_id = "noise", downstream_node_id = "noise_gain", port_type = "audio" },
                new { upstream_node_id = "noise_gain", downstream_node_id = "gain_wet", port_type = "audio" },
            ]);
        var graph = VoiceFilterGraph.Load(dir);
        // A 0.1 s tone that the graph must loop.
        var tone = Voice.Sine(480, Voice.Rate / 10, 0.5);
        var loaded = new List<string>();
        var output = graph.Process(Voice.Stereo(new float[Voice.Rate]), Rate, 1, path =>
        {
            loaded.Add(Path.GetFileName(path));
            return Voice.Stereo(tone);
        });
        Assert.Equal(["tone.wav"], loaded);
        Assert.Equal(Voice.DB(0.5 / Math.Sqrt(2)) - 6, Voice.DB(Voice.Rms(output[1], Voice.Rate / 2)), 0.3);
    }
}

public class VoiceProcessorTests
{
    /// <summary>A stand-in model: the "voice" is the signal above 1 kHz (a crude band split).</summary>
    sealed class HighBandIsolator : IVoiceIsolator
    {
        public int Calls;

        public float[] Isolate(float[] channel, CancellationToken cancel = default)
        {
            Calls++;
            var x = (float[])channel.Clone();
            var hp = VoiceFilterProcessing.Biquad.HighPass(1000, Voice.Rate);
            hp.Run(x);
            hp.Run(x, reverse: true);
            return x;
        }
    }

    static AudioClip Clip(string path, long sourceStart, long duration, VoiceEffects voice) =>
        Core.Clip(path, new TimeRange(0, duration), TimeMap.Constant(sourceStart, duration, duration), 1, 0, 0, true, voice: voice);

    [Fact]
    public void AppliesTheFilterFromTheClipStart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ecc-fx-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "mdsp_preset.json"), """
            {"nodes": [{"node_id": "p", "processor_name": "Gain", "processor_state": {"parameters": {"Gain dB": -20.0}}}],
             "connections": [],
             "ports": [{"internal_node_id": "p", "port_direction": "input", "port_type": "audio"},
                       {"internal_node_id": "p", "port_direction": "output", "port_type": "audio"}]}
            """);
        var decoder = new MemoryDecoder();
        decoder.Files["a"] = VoiceProcessor.Interleaved(Voice.Stereo(Voice.Sine(500, Voice.Rate * 3, 0.2)));
        var warnings = new List<string>();
        var processor = new VoiceProcessor(decoder, null, warnings);
        var result = processor.Process(Clip("a", 1_500_000, 1_000_000, Core.Effects(filter: new VoiceFilter("Quiet", dir, 1))))!;
        Assert.Empty(warnings);
        Assert.Equal(500_000, result.StartUs);
        var left = VoiceProcessor.Planar(result.Samples)[0];
        double Level(long us)
        {
            var i = (int)AudioMixer.Frames(us - result.StartUs);
            return Voice.Rms(left, i, i + 4_800);
        }
        Assert.Equal(20, Voice.DB(Level(1_000_000) / Level(2_000_000)), 0.1);
    }

    [Fact]
    public void SeparationAndDenoiseUseTheIsolator()
    {
        var low = Voice.Sine(200, Voice.Rate * 3, 0.2);
        var high = Voice.Sine(4000, Voice.Rate * 3, 0.2);
        var decoder = new MemoryDecoder();
        decoder.Files["a"] = VoiceProcessor.Interleaved(Voice.Stereo(low.Select((v, i) => v + high[i]).ToArray()));
        var isolator = new HighBandIsolator();
        var processor = new VoiceProcessor(decoder, isolator, []);
        double Share(VoiceEffects effects, float[] part)
        {
            var result = processor.Process(Clip("a", 1_000_000, 1_000_000, effects))!;
            var x = VoiceProcessor.Planar(result.Samples)[0];
            var i = (int)AudioMixer.Frames(1_000_000 - result.StartUs);
            var j = (int)AudioMixer.Frames(1_000_000);
            double dot = 0, pp = 0;
            for (var k = 0; k < 24_000; k++) { dot += x[i + k] * (double)part[j + k]; pp += part[j + k] * (double)part[j + k]; }
            return dot / pp;
        }
        Assert.Equal(1, Share(Core.Effects(separation: VoiceSeparation.KeepVoice), high), 0.02);
        Assert.Equal(0, Share(Core.Effects(separation: VoiceSeparation.KeepVoice), low), 0.02);
        Assert.Equal(1, Share(Core.Effects(separation: VoiceSeparation.RemoveVoice), low), 0.05);
        Assert.Equal(0, Share(Core.Effects(separation: VoiceSeparation.RemoveVoice), high), 0.05);
        // Reduce noise 0.85: the "ambience" kept at 15 %.
        Assert.Equal(0.15, Share(Core.Effects(denoise: 0.85), low), 0.02);
        Assert.Equal(1, Share(Core.Effects(denoise: 0.85), high), 0.02);
        // Mono sources are isolated once per clip, and each clip once per render.
        var calls = isolator.Calls;
        Share(Core.Effects(denoise: 0.85), low);
        Assert.Equal(calls, isolator.Calls);
    }

    [Fact]
    public void WithoutAModelOnlyTheOtherStagesRunAndItIsReported()
    {
        var tone = Voice.Sine(1000, Voice.Rate * 2, 0.1);
        var decoder = new MemoryDecoder();
        decoder.Files["a"] = VoiceProcessor.Interleaved(Voice.Stereo(tone));
        var warnings = new List<string>();
        var processor = new VoiceProcessor(decoder, null, warnings);
        var result = processor.Process(Clip("a", 0, 2_000_000, Core.Effects(denoise: 0.85, enhance: 0.75)))!;
        Assert.Single(warnings);
        var x = VoiceProcessor.Planar(result.Samples)[0];
        Assert.Equal(Voice.DB(VoiceEnhancer.Gain), Voice.DB(Voice.Rms(x, 24_000, 72_000) / Voice.Rms(tone, 24_000, 72_000)), 0.3);
        Assert.True(VoiceProcessor.NeedsIsolation(Core.Effects(enhance: 0.75)));
        Assert.False(VoiceProcessor.NeedsIsolation(Core.Effects(enhance: 1)));
        Assert.False(VoiceProcessor.NeedsIsolation(Core.Effects(filter: new VoiceFilter("x", "/", 1))));
    }

    [Fact]
    public void MixerRoutesVoiceClipsThroughTheProcessor()
    {
        var tone = Voice.Sine(1000, Voice.Rate * 2, 0.1);
        var decoder = new MemoryDecoder();
        decoder.Files["a"] = VoiceProcessor.Interleaved(Voice.Stereo(tone));
        var clip = Clip("a", 0, 1_000_000, Core.Effects(separation: VoiceSeparation.KeepVoice));
        var plain = AudioMixer.Mix([clip], 1_000_000, decoder, []);
        var voiced = AudioMixer.Mix([clip], 1_000_000, decoder, [], voice: new VoiceProcessor(decoder, new HighBandIsolator(), []));
        Assert.True(Voice.Rms(plain) > 0.05);
        // The 1 kHz tone is at the stand-in model's cutoff: −6 dB.
        Assert.Equal(-6, Voice.DB(Voice.Rms(voiced, 20_000, 60_000) / Voice.Rms(plain, 20_000, 60_000)), 0.5);
    }
}
