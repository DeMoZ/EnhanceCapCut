using EnhanceCapCut.Draft;
using Whisper.net;
using static EnhanceCapCut.Draft.Localization;

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

    /// <summary>
    /// DTW places a token this long after the word is heard: measured against CapCut's own word times on four vlogs
    /// (median +0.11…0.14 s), where Apple dictation's times were within ±0.07 s.
    /// </summary>
    const double DtwLag = 0.12;

    /// <param name="samples">16 kHz mono (<see cref="AudioInput"/>).</param>
    /// <param name="progress">0…1 through the audio.</param>
    public static async Task<List<TimedWord>> TranscribeAsync(float[] samples, SubtitleLanguage language,
        Action<double>? progress = null, CancellationToken cancel = default)
    {
        using var factory = WhisperFactory.FromPath(ModelStore.PathOf(ModelStore.Whisper),
            new WhisperFactoryOptions { UseDtwTimeStamps = true, HeadsPreset = WhisperAlignmentHeadsPreset.LargeV3Turbo });
        // Progress through the whole recording: the handler reports within the current piece.
        double pieceFrom = 0, pieceShare = 1;
        await using var processor = factory.CreateBuilder()
            .WithLanguage(language.Whisper)
            .WithTokenTimestamps()
            .WithThreads(Math.Max(1, Environment.ProcessorCount - 1))
            .WithProgressHandler(p => progress?.Invoke(pieceFrom + pieceShare * p / 100.0))
            .Build();

        var words = new List<TimedWord>();
        // Pieces within Whisper's 30 s window (see SpeechChunker); times are shifted back to the recording's.
        foreach (var (pieceStart, pieceEnd) in SpeechChunker.Pieces(samples, AudioInput.SampleRate))
        {
            var offset = (double)pieceStart / AudioInput.SampleRate;
            pieceFrom = (double)pieceStart / samples.Length;
            pieceShare = (double)(pieceEnd - pieceStart) / samples.Length;
            await foreach (var segment in processor.ProcessAsync(samples[pieceStart..pieceEnd], cancel))
            {
                // Words from tokens: a token that starts with a space starts a word; others (word pieces, punctuation)
                // continue it. Times are the tokens' DTW positions (centiseconds), Whisper's own as a fallback.
                var segmentWords = new List<(string Text, double Start, double Last)>();
                foreach (var token in segment.Tokens ?? [])
                {
                    var text = token.Text ?? "";
                    // Special tokens: [_BEG_], [_TT_123], <|endoftext|>, …
                    if (text.Length == 0 || text.StartsWith("[_", StringComparison.Ordinal) || text.StartsWith("<|", StringComparison.Ordinal)) continue;
                    var time = offset + (token.DtwTimestamp >= 0 ? Math.Max(0, token.DtwTimestamp / 100.0 - DtwLag) : token.Start / 100.0);
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
                var segmentEnd = offset + segment.End.TotalSeconds;
                for (var i = 0; i < segmentWords.Count; i++)
                {
                    var (text, start, last) = segmentWords[i];
                    var next = i + 1 < segmentWords.Count ? segmentWords[i + 1].Start : segmentEnd;
                    words.Add(new TimedWord(text, start, Math.Max(start, Math.Min(last + WordTail, Math.Max(next, last)))));
                }
                // No usable tokens: the whole segment, split later with interpolated times.
                if (segmentWords.Count == 0 && segment.Text.Trim().Length > 0)
                    words.Add(new TimedWord(segment.Text.Trim(), offset + segment.Start.TotalSeconds, segmentEnd));
            }
        }
        words = WithoutAnnotations(words);
        if (Environment.GetEnvironmentVariable("ECC_DEBUG_WORDS") is { Length: > 0 })
            foreach (var w in words) Console.Error.WriteLine($"{w.Start:0.00}-{w.End:0.00} {w.Text}");
        progress?.Invoke(1);
        if (words.Count == 0) throw new InvalidOperationException(L("No speech recognized. Check the spoken language and audio level."));
        return words;
    }

    /// <summary>
    /// Without Whisper's sound annotations ("*смех*", "[музыка]", "(аплодисменты)", "♪"): over street noise or music
    /// it writes them where nobody speaks.
    /// </summary>
    public static List<TimedWord> WithoutAnnotations(List<TimedWord> words)
    {
        var pairs = new Dictionary<char, char> { ['*'] = '*', ['['] = ']', ['('] = ')', ['♪'] = '♪' };
        var result = new List<TimedWord>();
        char? closer = null;
        foreach (var word in words)
        {
            var text = word.Text.Trim('.', ',', '!', '?', '…', ':', ';');
            if (closer is { } open)
            {
                if (text.Length > 0 && text[^1] == open) closer = null;
                continue;
            }
            if (text.Length > 0 && pairs.TryGetValue(text[0], out var c))
            {
                // A lone "♪" or "*" is a whole annotation; otherwise it runs to the word with the closer.
                var closed = text.Length == 1 ? text[0] == c : text[^1] == c;
                if (!closed) closer = c;
                continue;
            }
            result.Add(word);
        }
        return result;
    }
}

/// <summary>Whisper kept loaded to read back short pieces of speech: checks that a cloned line says what it should.</summary>
public sealed class WhisperChecker : IDisposable
{
    readonly WhisperFactory factory = WhisperFactory.FromPath(ModelStore.PathOf(ModelStore.Whisper));
    readonly Dictionary<string, WhisperProcessor> processors = [];

    public void Dispose()
    {
        foreach (var p in processors.Values) p.Dispose();
        factory.Dispose();
    }

    /// <summary>The text of <paramref name="samples"/> (16 kHz mono, under 30 s) in <paramref name="language"/> (a Whisper code).</summary>
    public async Task<string> TextAsync(float[] samples, string language, CancellationToken cancel = default)
    {
        if (!processors.TryGetValue(language, out var processor))
            processors[language] = processor = factory.CreateBuilder().WithLanguage(language)
                .WithThreads(Math.Max(1, Environment.ProcessorCount - 1)).WithNoContext().WithSingleSegment().Build();
        var texts = new List<string>();
        await foreach (var segment in processor.ProcessAsync(samples, cancel)) texts.Add(segment.Text);
        return string.Join(' ', texts);
    }
}
