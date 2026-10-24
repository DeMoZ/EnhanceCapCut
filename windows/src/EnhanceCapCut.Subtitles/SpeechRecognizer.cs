using EnhanceCapCut.Draft;
using Whisper.net;

namespace EnhanceCapCut.Subtitles;

/// <summary>
/// Offline speech recognition with Whisper (whisper.cpp through Whisper.net) — the counterpart of the macOS version's
/// <c>Transcriber</c> (Apple's SpeechAnalyzer). Word times come from DTW token alignment: Whisper's own token
/// timestamps stretch the first words of a segment back to its start, over any silence before them.
/// </summary>
public static class SpeechRecognizer
{
    /// <summary>A word ends this long after its last token's aligned time, unless the next word starts sooner.</summary>
    const double WordTail = 0.3;

    /// <param name="samples">16 kHz mono (<see cref="AudioInput"/>).</param>
    /// <param name="progress">0…1 through the audio.</param>
    public static async Task<List<TimedWord>> TranscribeAsync(float[] samples, SubtitleLanguage language,
        Action<double>? progress = null, CancellationToken cancel = default)
    {
        using var factory = WhisperFactory.FromPath(ModelStore.PathOf(ModelStore.Whisper),
            new WhisperFactoryOptions { UseDtwTimeStamps = true, HeadsPreset = WhisperAlignmentHeadsPreset.LargeV3Turbo });
        await using var processor = factory.CreateBuilder()
            .WithLanguage(language.Whisper)
            .WithTokenTimestamps()
            .WithThreads(Math.Max(1, Environment.ProcessorCount - 1))
            .WithProgressHandler(p => progress?.Invoke(p / 100.0))
            .Build();

        var words = new List<TimedWord>();
        await foreach (var segment in processor.ProcessAsync(samples, cancel))
        {
            // Words from tokens: a token that starts with a space starts a word; others (word pieces, punctuation)
            // continue it. Times are the tokens' DTW positions (centiseconds), Whisper's own as a fallback.
            var segmentWords = new List<(string Text, double Start, double Last)>();
            foreach (var token in segment.Tokens ?? [])
            {
                var text = token.Text ?? "";
                // Special tokens: [_BEG_], [_TT_123], <|endoftext|>, …
                if (text.Length == 0 || text.StartsWith("[_", StringComparison.Ordinal) || text.StartsWith("<|", StringComparison.Ordinal)) continue;
                var time = (token.DtwTimestamp >= 0 ? token.DtwTimestamp : token.Start) / 100.0;
                if (segmentWords.Count > 0 && !char.IsWhiteSpace(text[0]))
                {
                    var last = segmentWords[^1];
                    segmentWords[^1] = (last.Text + text, last.Start, Math.Max(last.Last, time));
                }
                else if (text.Trim().Length > 0)
                {
                    segmentWords.Add((text.Trim(), time, time));
                }
            }
            var segmentEnd = segment.End.TotalSeconds;
            for (var i = 0; i < segmentWords.Count; i++)
            {
                var (text, start, last) = segmentWords[i];
                var next = i + 1 < segmentWords.Count ? segmentWords[i + 1].Start : segmentEnd;
                words.Add(new TimedWord(text, start, Math.Max(start, Math.Min(last + WordTail, Math.Max(next, last)))));
            }
            // No usable tokens: the whole segment, split later with interpolated times.
            if (segmentWords.Count == 0 && segment.Text.Trim().Length > 0)
                words.Add(new TimedWord(segment.Text.Trim(), segment.Start.TotalSeconds, segmentEnd));
        }
        if (Environment.GetEnvironmentVariable("ECC_DEBUG_WORDS") is { Length: > 0 })
            foreach (var w in words) Console.Error.WriteLine($"{w.Start:0.00}-{w.End:0.00} {w.Text}");
        progress?.Invoke(1);
        if (words.Count == 0) throw new InvalidOperationException("No speech recognized. Check the spoken language and audio level.");
        return words;
    }
}
