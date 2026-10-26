using Microsoft.ML.OnnxRuntime;

namespace EnhanceCapCut.Subtitles;

/// <summary>
/// Speaks text in the voice of a short reference recording, keeping its manner (tone, energy, emotion): Chatterbox
/// Multilingual V3 by Resemble AI (MIT), run with ONNX Runtime from the four-graph export in
/// <see cref="ModelStore.VoiceCloning"/>. A port of the macOS version's <c>VoiceCloner.swift</c>; the two must stay in step.
/// <list type="number">
/// <item><c>speech_encoder</c>: the reference (24 kHz, first 6 s) → conditioning for the language model, its speech
/// tokens, a speaker embedding and mel features.</item>
/// <item><c>embed_tokens</c> + <c>language_model</c>: text tokens → speech tokens (25 per second), sampled one at a time
/// with a KV cache until the stop token.</item>
/// <item><c>conditional_decoder</c>: reference tokens + new tokens → 24 kHz waveform of the new tokens only.</item>
/// </list>
/// </summary>
public sealed class VoiceCloner : IDisposable
{
    public const int SampleRate = 24_000;

    /// <summary>
    /// Languages the model speaks well, as Chatterbox codes. Chinese, Japanese and Hebrew are left out: the export lacks
    /// their text front ends (Cangjie, kana, diacritics), so they come out garbled.
    /// </summary>
    static readonly HashSet<string> Languages = ["ar", "da", "de", "el", "en", "es", "fi", "fr", "hi", "it", "ko", "ms", "nl",
        "no", "pl", "pt", "ru", "sv", "sw", "tr"];

    /// <summary>The model's code for a subtitle language code ("pt-BR" → "pt", "nb" → "no"), null when it can't speak it.</summary>
    public static string? ModelLanguage(string code)
    {
        var lang = code.Split('-')[0].ToLowerInvariant();
        if (lang is "nb" or "nn") lang = "no";
        return Languages.Contains(lang) ? lang : null;
    }

    /// <summary>
    /// Sampling settings, as in Resemble AI's own multilingual pipeline except the repetition penalty: 1.2 (the ONNX
    /// export's choice) — 2.0 made lines run on past their end without classifier-free guidance.
    /// </summary>
    internal const float Temperature = 0.8f;
    internal const float MinP = 0.05f;
    internal const float RepetitionPenalty = 1.2f;

    const int StartSpeech = 6561;
    const int StopSpeech = 6562;
    /// <summary>The decoder's output for the last token before the stop token is noise (upstream V3 drops it too).</summary>
    const int SamplesPerToken = 960;
    const double TokensPerSecond = 25;
    const int Layers = 30, KvHeads = 16, HeadDim = 64, Hidden = 1024;

    readonly InferenceSession encoder;
    readonly InferenceSession embedder;
    readonly InferenceSession languageModel;
    readonly InferenceSession decoder;
    readonly ChatterboxTokenizer tokenizer;
    readonly string[] presentNames;

    /// <summary>A reference voice, encoded once and reused for every line.</summary>
    public sealed class Voice
    {
        internal float[] Conditioning { get; init; } = [];
        internal long[] ConditioningShape { get; init; } = [];
        internal long[] PromptTokens { get; init; } = [];
        internal float[] SpeakerEmbedding { get; init; } = [];
        internal long[] SpeakerEmbeddingShape { get; init; } = [];
        internal float[] SpeakerFeatures { get; init; } = [];
        internal long[] SpeakerFeaturesShape { get; init; } = [];
    }

    /// <param name="Samples">24 kHz mono, quiet ends trimmed.</param>
    /// <param name="Finished">False when generation hit the length cap instead of the stop token (the line likely went wrong).</param>
    public sealed record Speech(float[] Samples, bool Finished)
    {
        public double Duration => (double)Samples.Length / SampleRate;
    }

    /// <summary>
    /// Loads the model from <paramref name="folder"/> (see <see cref="ModelStore.VoiceCloning"/>). Takes up to a minute:
    /// ONNX Runtime optimizes the graphs on load.
    /// </summary>
    public VoiceCloner(string? folder = null)
    {
        folder ??= ModelStore.FolderOf(ModelStore.VoiceCloning);
        InferenceSession Session(string name)
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) throw new FileNotFoundException($"The voice cloning model is incomplete ({name}).", path);
            return new InferenceSession(path, new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL });
        }
        encoder = Session("onnx/speech_encoder.onnx");
        embedder = Session("onnx/embed_tokens.onnx");
        languageModel = Session("onnx/language_model_q4.onnx");
        decoder = Session("onnx/conditional_decoder.onnx");
        tokenizer = new ChatterboxTokenizer(Path.Combine(folder, "tokenizer.json"));
        presentNames = languageModel.OutputNames.Where(n => n.StartsWith("present.", StringComparison.Ordinal)).ToArray();
    }

    public void Dispose()
    {
        encoder.Dispose();
        embedder.Dispose();
        languageModel.Dispose();
        decoder.Dispose();
    }

    /// <summary>Encodes a reference recording: 24 kHz mono, the model uses its first 6 s.</summary>
    public Voice VoiceFrom(float[] reference)
    {
        var samples = reference.Length == 0 ? new float[SampleRate] : (float[])reference.Clone();
        // The model expects a normal speaking level.
        var peak = samples.Length == 0 ? 0 : samples.Max(MathF.Abs);
        if (peak > 1e-4f) for (var i = 0; i < samples.Length; i++) samples[i] *= 0.7f / peak;
        using var input = OrtValue.CreateTensorValueFromMemory(samples, [1, samples.Length]);
        using var outputs = encoder.Run(new RunOptions(), ["audio_values"], [input],
            ["audio_features", "audio_tokens", "speaker_embeddings", "speaker_features"]);
        return new Voice
        {
            Conditioning = outputs[0].GetTensorDataAsSpan<float>().ToArray(),
            ConditioningShape = outputs[0].GetTensorTypeAndShape().Shape,
            PromptTokens = outputs[1].GetTensorDataAsSpan<long>().ToArray(),
            SpeakerEmbedding = outputs[2].GetTensorDataAsSpan<float>().ToArray(),
            SpeakerEmbeddingShape = outputs[2].GetTensorTypeAndShape().Shape,
            SpeakerFeatures = outputs[3].GetTensorDataAsSpan<float>().ToArray(),
            SpeakerFeaturesShape = outputs[3].GetTensorTypeAndShape().Shape,
        };
    }

    /// <summary>
    /// Speaks <paramref name="text"/> in <paramref name="voice"/>. <paramref name="language"/> is a Chatterbox code
    /// (<see cref="ModelLanguage"/>); <paramref name="exaggeration"/> 0…1 scales how expressive the delivery is (0.5 =
    /// like the reference). The same seed gives the same tokens.
    /// </summary>
    public Speech Speak(string text, string language, Voice voice, float exaggeration, ulong seed, CancellationToken cancel = default)
    {
        var prepared = Prepare(text);
        var ids = tokenizer.Encode($"[{language}]" + prepared).Select(i => (long)i).ToArray();
        // Text positions count from the first text token; the special speech tokens sit at position 0.
        var positions = ids.Select((id, i) => id >= StartSpeech ? 0L : i - 1L).ToArray();
        var random = new SplitMix64(seed);
        var cap = TokenCap(prepared);

        var embeds = Embed(ids, positions, exaggeration);
        // The first step sees the reference conditioning, then the text.
        embeds = [.. voice.Conditioning, .. embeds];
        var length = embeds.Length / Hidden;
        var newTokens = length;
        var cache = new Dictionary<string, OrtValue>();
        for (var layer = 0; layer < Layers; layer++)
        {
            cache[$"past_key_values.{layer}.key"] = OrtValue.CreateTensorValueFromMemory(Array.Empty<float>(), [1, KvHeads, 0, HeadDim]);
            cache[$"past_key_values.{layer}.value"] = OrtValue.CreateTensorValueFromMemory(Array.Empty<float>(), [1, KvHeads, 0, HeadDim]);
        }

        var generated = new List<int> { StartSpeech };
        var finished = false;
        var outputNames = new[] { "logits" }.Concat(presentNames).ToArray();
        try
        {
            for (var step = 0; step < cap; step++)
            {
                cancel.ThrowIfCancellationRequested();
                using var embedValue = OrtValue.CreateTensorValueFromMemory(embeds, [1, newTokens, Hidden]);
                using var mask = OrtValue.CreateTensorValueFromMemory(Enumerable.Repeat(1L, length).ToArray(), [1, length]);
                var names = new List<string> { "inputs_embeds", "attention_mask" };
                var values = new List<OrtValue> { embedValue, mask };
                foreach (var (name, value) in cache) { names.Add(name); values.Add(value); }
                var outputs = languageModel.Run(new RunOptions(), names, values, outputNames);
                var logits = outputs[0].GetTensorDataAsSpan<float>();
                var vocabulary = (int)outputs[0].GetTensorTypeAndShape().Shape[2];
                var next = Sample(logits[^vocabulary..].ToArray(), generated, ref random);
                outputs[0].Dispose();
                generated.Add(next);
                // The new cache replaces the old one; the old values go.
                for (var i = 0; i < presentNames.Length; i++)
                {
                    var key = presentNames[i].Replace("present.", "past_key_values.", StringComparison.Ordinal);
                    cache[key].Dispose();
                    cache[key] = outputs[1 + i];
                }
                if (next == StopSpeech) { finished = true; break; }
                embeds = Embed([next], [step + 1L], exaggeration);
                newTokens = 1;
                length++;
            }
        }
        finally
        {
            foreach (var value in cache.Values) value.Dispose();
        }

        var speech = generated.Skip(1).Where(t => t < StartSpeech).Select(t => (long)t).ToArray();
        if (speech.Length == 0) return new Speech([], finished);
        long[] tokens = [.. voice.PromptTokens, .. speech];
        using var tokenValue = OrtValue.CreateTensorValueFromMemory(tokens, [1, tokens.Length]);
        using var embedding = OrtValue.CreateTensorValueFromMemory(voice.SpeakerEmbedding, voice.SpeakerEmbeddingShape);
        using var features = OrtValue.CreateTensorValueFromMemory(voice.SpeakerFeatures, voice.SpeakerFeaturesShape);
        using var wave = decoder.Run(new RunOptions(), ["speech_tokens", "speaker_embeddings", "speaker_features"],
            [tokenValue, embedding, features], ["waveform"]);
        var samples = wave[0].GetTensorDataAsSpan<float>().ToArray();
        samples = samples[..Math.Max(0, samples.Length - SamplesPerToken)];
        return new Speech(TrimQuietEnds(samples), finished);
    }

    float[] Embed(long[] ids, long[] positions, float exaggeration)
    {
        using var idValue = OrtValue.CreateTensorValueFromMemory(ids, [1, ids.Length]);
        using var positionValue = OrtValue.CreateTensorValueFromMemory(positions, [1, positions.Length]);
        using var exaggerationValue = OrtValue.CreateTensorValueFromMemory(new[] { exaggeration }, [1]);
        using var outputs = embedder.Run(new RunOptions(), ["input_ids", "position_ids", "exaggeration"],
            [idValue, positionValue, exaggerationValue], ["inputs_embeds"]);
        return outputs[0].GetTensorDataAsSpan<float>().ToArray();
    }

    // MARK: Text

    /// <summary>
    /// Resemble AI's punctuation clean-up: one space between words, a capital first letter, unusual punctuation replaced,
    /// a full stop added when the line has no ending punctuation.
    /// </summary>
    internal static string Prepare(string text)
    {
        var t = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (t.Length == 0) return ".";
        if (char.IsLower(t[0])) t = char.ToUpperInvariant(t[0]) + t[1..];
        foreach (var (old, replacement) in new[] { ("...", ", "), ("…", ", "), (":", ","), (" - ", ", "), (";", ", "), ("—", "-"),
                     ("–", "-"), (" ,", ","), ("“", "\""), ("”", "\""), ("‘", "'"), ("’", "'"), ("«", "\""), ("»", "\"") })
            t = t.Replace(old, replacement, StringComparison.Ordinal);
        t = t.TrimEnd(' ');
        if (!(t.EndsWith('.') || t.EndsWith('!') || t.EndsWith('?') || t.EndsWith('-') || t.EndsWith(','))) t += ".";
        return t;
    }

    /// <summary>Most tokens a line may take: room for very slow speech (5 characters a second) plus 2 s, at most 40 s.</summary>
    internal static int TokenCap(string text) => Math.Min(1000, (int)(TokensPerSecond * (2 + new System.Globalization.StringInfo(text).LengthInTextElements / 5.0)));

    // MARK: Sampling

    /// <summary>Repetition penalty, temperature, min-p filter, then a draw from what's left.</summary>
    internal static int Sample(float[] logits, IReadOnlyCollection<int> previous, ref SplitMix64 random)
    {
        var scores = (float[])logits.Clone();
        foreach (var token in previous.Distinct())
            if (token < scores.Length) scores[token] = scores[token] < 0 ? scores[token] * RepetitionPenalty : scores[token] / RepetitionPenalty;
        var top = scores.Max();
        var weights = scores.Select(s => Math.Exp((s - top) / Temperature)).ToArray();
        // min-p: drop tokens less than 5% as likely as the best one.
        for (var i = 0; i < weights.Length; i++) if (weights[i] < MinP) weights[i] = 0;
        var target = random.NextUnit() * weights.Sum();
        for (var i = 0; i < weights.Length; i++)
        {
            if (weights[i] <= 0) continue;
            target -= weights[i];
            if (target <= 0) return i;
        }
        return Array.FindLastIndex(weights, w => w > 0) is var last and >= 0 ? last : 0;
    }

    // MARK: Audio

    /// <summary>Cuts the near-silent start and end (frames 35 dB below the loudest 10 ms), keeping 40 ms around the speech.</summary>
    internal static float[] TrimQuietEnds(float[] samples)
    {
        const int frame = SampleRate / 100;
        if (samples.Length <= frame) return samples;
        var levels = new List<float>();
        for (var i = 0; i + frame <= samples.Length; i += frame)
        {
            var sum = 0f;
            for (var j = i; j < i + frame; j++) sum += samples[j] * samples[j];
            levels.Add(MathF.Sqrt(sum / frame));
        }
        var floor = levels.Max() * MathF.Pow(10, -35f / 20);
        var first = levels.FindIndex(l => l > floor);
        var last = levels.FindLastIndex(l => l > floor);
        if (first < 0) return [];
        const int pad = SampleRate * 4 / 100;
        return samples[Math.Max(0, first * frame - pad)..Math.Min(samples.Length, (last + 1) * frame + pad)];
    }
}

/// <summary>A small seeded random generator (SplitMix64), the same in the macOS version.</summary>
public struct SplitMix64(ulong seed)
{
    ulong state = seed;

    public ulong Next()
    {
        state += 0x9E3779B97F4A7C15;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double NextUnit() => (Next() >> 11) / (double)(1UL << 53);
}
