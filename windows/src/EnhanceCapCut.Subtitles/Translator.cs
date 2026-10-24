using System.Text.Json;
using System.Text.RegularExpressions;
using EnhanceCapCut.Draft;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace EnhanceCapCut.Subtitles;

/// <summary>
/// Offline translation with NLLB-200 (distilled 600M, 8-bit ONNX) in ONNX Runtime — the counterpart of the macOS
/// version's Apple Translation. Greedy decoding, cues in batches.
/// </summary>
public sealed class Translator : IDisposable
{
    // NLLB's ids are SentencePiece ids + 1 (fairseq's offset); 0–3 are <s>, <pad>, </s>, <unk>.
    const long Pad = 1, Eos = 2, Unk = 3;
    const int BatchSize = 8;
    const int MaxTokens = 200;
    /// <summary>Greedy decoding loops on small models ("ก็มี ก็มี …"); like HF's <c>no_repeat_ngram_size</c>.</summary>
    const int NoRepeatNgram = 3;

    readonly InferenceSession encoder;
    readonly InferenceSession decoder;
    readonly SentencePieceTokenizer tokenizer;
    readonly Dictionary<string, long> languageIds = [];

    public Translator()
    {
        string P(string name) => ModelStore.PathOf(ModelStore.Translation.First(f => f.Name == name));
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        encoder = new InferenceSession(P("encoder_model_quantized.onnx"), options);
        decoder = new InferenceSession(P("decoder_model_quantized.onnx"), options);
        using (var model = File.OpenRead(P("sentencepiece.bpe.model")))
            tokenizer = SentencePieceTokenizer.Create(model, false, false);
        // Language tokens (eng_Latn, …) are added tokens after the SentencePiece vocabulary.
        using var json = JsonDocument.Parse(File.ReadAllBytes(P("tokenizer.json")));
        foreach (var t in json.RootElement.GetProperty("added_tokens").EnumerateArray())
            languageIds[t.GetProperty("content").GetString()!] = t.GetProperty("id").GetInt64();
    }

    public void Dispose()
    {
        encoder.Dispose();
        decoder.Dispose();
    }

    public List<string> Translate(IReadOnlyList<string> texts, SubtitleLanguage from, SubtitleLanguage to,
        Action<double>? progress = null, CancellationToken cancel = default)
    {
        var source = languageIds[from.Nllb];
        var target = languageIds[to.Nllb];
        var result = new List<string>();
        for (var i = 0; i < texts.Count; i += BatchSize)
        {
            cancel.ThrowIfCancellationRequested();
            result.AddRange(Batch(texts.Skip(i).Take(BatchSize).ToList(), source, target));
            progress?.Invoke(Math.Min(1, (i + BatchSize) / (double)texts.Count));
        }
        return result;
    }

    List<string> Batch(List<string> texts, long source, long target)
    {
        // Encoder input: <source language> tokens </s>, padded.
        var inputs = texts.Select(t =>
            new[] { source }.Concat(tokenizer.EncodeToIds(t.Replace('\n', ' ')).Select(id => id == 0 ? Unk : id + 1L)).Append(Eos).ToArray()).ToList();
        var batch = inputs.Count;
        var length = inputs.Max(x => x.Length);
        var ids = new DenseTensor<long>([batch, length]);
        var mask = new DenseTensor<long>([batch, length]);
        for (var b = 0; b < batch; b++)
            for (var j = 0; j < length; j++)
            {
                ids[b, j] = j < inputs[b].Length ? inputs[b][j] : Pad;
                mask[b, j] = j < inputs[b].Length ? 1 : 0;
            }
        using var encoded = encoder.Run([NamedOnnxValue.CreateFromTensor("input_ids", ids), NamedOnnxValue.CreateFromTensor("attention_mask", mask)]);
        var hidden = encoded.First(v => v.Name == "last_hidden_state").AsTensor<float>().ToDenseTensor();

        // Decoder: starts with </s> <target language>, then greedy until </s>.
        var outputs = Enumerable.Range(0, batch).Select(_ => new List<long> { Eos, target }).ToList();
        var done = new bool[batch];
        var limit = Math.Min(MaxTokens, length * 3 + 10);
        for (var step = 0; step < limit && done.Contains(false); step++)
        {
            var current = outputs[0].Count;
            var decoderIds = new DenseTensor<long>([batch, current]);
            for (var b = 0; b < batch; b++)
                for (var j = 0; j < current; j++) decoderIds[b, j] = outputs[b][j];
            using var run = decoder.Run([
                NamedOnnxValue.CreateFromTensor("input_ids", decoderIds),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hidden),
                NamedOnnxValue.CreateFromTensor("encoder_attention_mask", mask),
            ], ["logits"]);
            var logits = run[0].AsTensor<float>();
            var vocab = logits.Dimensions[2];
            for (var b = 0; b < batch; b++)
            {
                if (done[b]) { outputs[b].Add(Pad); continue; }
                var banned = BannedTokens(outputs[b]);
                long best = Eos;
                var bestScore = float.NegativeInfinity;
                for (var v = 0; v < vocab; v++)
                {
                    // Never another language token or padding.
                    if (v == 0 || v == Pad || v > tokenizer.Vocabulary.Count || banned.Contains(v)) continue;
                    var score = logits[b, current - 1, v];
                    if (score > bestScore) { bestScore = score; best = v; }
                }
                outputs[b].Add(best);
                if (best == Eos) done[b] = true;
            }
        }
        return outputs.Select(o => Detokenize(tokenizer.Decode(o.Skip(2).TakeWhile(id => id != Eos).Where(id => id > Unk).Select(id => (int)(id - 1))))).ToList();
    }

    /// <summary>Tokens that would repeat an n-gram already in <paramref name="sequence"/> (after the two start tokens).</summary>
    static HashSet<long> BannedTokens(List<long> sequence)
    {
        var banned = new HashSet<long>();
        var count = sequence.Count;
        if (count - 2 < NoRepeatNgram) return banned;
        for (var i = 2; i + NoRepeatNgram - 1 < count; i++)
        {
            var match = true;
            for (var k = 0; k < NoRepeatNgram - 1 && match; k++)
                match = sequence[i + k] == sequence[count - NoRepeatNgram + 1 + k];
            if (match) banned.Add(sequence[i + NoRepeatNgram - 1]);
        }
        return banned;
    }

    /// <summary>
    /// NLLB was partly trained on tokenized text and sometimes answers like it ("That 's it ."): glue punctuation and
    /// English contractions back on.
    /// </summary>
    internal static string Detokenize(string text)
    {
        text = Regex.Replace(text, @"\s+([.,!?;:%…)\]»”。，！？、])", "$1");
        text = Regex.Replace(text, @"([(\[«“])\s+", "$1");
        text = Regex.Replace(text, @"(\w)\s+'(s|re|ve|ll|d|m|t)\b", "$1'$2");
        text = Regex.Replace(text, @"(\w)\s+n't\b", "$1n't");
        return text.Trim();
    }
}
