using System.Globalization;
using System.Text;

namespace EnhanceCapCut.Draft;

/// <summary>A subtitle line on the timeline, in seconds from the timeline start.</summary>
public sealed record CaptionCue(double Start, double End, string Text);

/// <summary>A recognised word (or punctuation-attached token) with its position in the audio, in seconds.</summary>
public sealed record TimedWord(string Text, double Start, double End);

/// <summary>
/// A language subtitles can be made in. The macOS version takes the list from Apple's frameworks; here it's a fixed
/// table of languages both Whisper (recognition) and NLLB (translation) handle well.
/// </summary>
/// <param name="Code">File and track name code, as on macOS: "en", "th", "zh-Hans", …</param>
/// <param name="CapCut">CapCut's caption language: language and region, like "ru-RU".</param>
/// <param name="Nllb">The NLLB-200 language token, like "rus_Cyrl".</param>
public sealed record SubtitleLanguage(string Code, string Name, string NativeName, string CapCut, string Nllb)
{
    /// <summary>Whisper's language code (ISO 639-1).</summary>
    public string Whisper => Code[..2];

    /// <summary>The name in the interface language (<see cref="Localization"/>; every name is in the tables).</summary>
    public string Title => Localization.L(Name);

    public static readonly IReadOnlyList<SubtitleLanguage> All =
    [
        new("ar", "Arabic", "العربية", "ar-SA", "arb_Arab"),
        new("cs", "Czech", "Čeština", "cs-CZ", "ces_Latn"),
        new("de", "German", "Deutsch", "de-DE", "deu_Latn"),
        new("en", "English", "English", "en-US", "eng_Latn"),
        new("es", "Spanish", "Español", "es-ES", "spa_Latn"),
        new("fr", "French", "Français", "fr-FR", "fra_Latn"),
        new("he", "Hebrew", "עברית", "he-IL", "heb_Hebr"),
        new("hi", "Hindi", "हिन्दी", "hi-IN", "hin_Deva"),
        new("id", "Indonesian", "Bahasa Indonesia", "id-ID", "ind_Latn"),
        new("it", "Italian", "Italiano", "it-IT", "ita_Latn"),
        new("ja", "Japanese", "日本語", "ja-JP", "jpn_Jpan"),
        new("ko", "Korean", "한국어", "ko-KR", "kor_Hang"),
        new("ms", "Malay", "Bahasa Melayu", "ms-MY", "zsm_Latn"),
        new("nl", "Dutch", "Nederlands", "nl-NL", "nld_Latn"),
        new("pl", "Polish", "Polski", "pl-PL", "pol_Latn"),
        new("pt-BR", "Portuguese (Brazil)", "Português (Brasil)", "pt-BR", "por_Latn"),
        new("ru", "Russian", "Русский", "ru-RU", "rus_Cyrl"),
        new("sv", "Swedish", "Svenska", "sv-SE", "swe_Latn"),
        new("th", "Thai", "ไทย", "th-TH", "tha_Thai"),
        new("tr", "Turkish", "Türkçe", "tr-TR", "tur_Latn"),
        new("uk", "Ukrainian", "Українська", "uk-UA", "ukr_Cyrl"),
        new("vi", "Vietnamese", "Tiếng Việt", "vi-VN", "vie_Latn"),
        new("zh-Hans", "Chinese, Simplified", "中文（简体）", "zh-CN", "zho_Hans"),
        new("zh-Hant", "Chinese, Traditional", "中文（繁體）", "zh-TW", "zho_Hant"),
    ];

    /// <summary>Looks a language up by code ("ru"), CapCut/BCP 47 form ("ru-RU") or name, case-insensitively.</summary>
    public static SubtitleLanguage? Find(string s)
    {
        bool Is(string a) => string.Equals(a, s, StringComparison.OrdinalIgnoreCase);
        return All.FirstOrDefault(l => Is(l.Code) || Is(l.CapCut) || Is(l.Name))
            ?? All.FirstOrDefault(l => s.Length >= 2 && Is(l.Code[..2]) && l.Code.Length == 2)
            ?? All.FirstOrDefault(l => s.Length > 2 && string.Equals(l.Code, s[..2], StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Groups recognised words into readable subtitle cues. A port of the macOS version's <c>Segmenter.swift</c> (itself
/// from SubDub, the Final Cut Pro version); lengths count grapheme clusters like Swift's <c>String.count</c>.
/// </summary>
public static class Segmenter
{
    public const int MaxCharsPerLine = 42;
    public const int MaxLines = 2;
    public const double MaxDuration = 6.0;
    public const double MinDuration = 1.0;
    public const double PauseBreak = 0.8;
    /// <summary>
    /// Some recognisers return no punctuation at all. In that case sentences end at long pauses so cues and
    /// translations read naturally.
    /// </summary>
    public const double SentencePause = 0.45;

    internal static int Length(string s) => new StringInfo(s).LengthInTextElements;

    public static List<CaptionCue> Cues(IReadOnlyList<TimedWord> rawWords, string? language)
    {
        var words = PunctuateIfNeeded(Explode(rawWords), language);
        var cues = new List<CaptionCue>();
        var current = new List<TimedWord>();
        var separator = language is not null && LineWrapper.NoSpaceLanguages.Contains(language[..Math.Min(2, language.Length)]) ? "" : " ";

        void Emit(IReadOnlyList<TimedWord> group)
        {
            if (group.Count == 0) return;
            var first = group[0];
            var last = group[^1];
            cues.Add(new CaptionCue(first.Start, Math.Max(last.End, first.Start + 0.3), string.Join(separator, group.Select(w => w.Text))));
        }

        void Flush()
        {
            Emit(current);
            current = [];
        }

        // The cue is full mid-phrase: cut at the longest pause in its second part (later cut wins ties) and carry
        // the rest over into the next cue.
        void SplitAtBestPause()
        {
            if (current.Count < 3)
            {
                Flush();
                return;
            }
            var best = current.Count;
            var bestGap = double.NegativeInfinity;
            for (var k = Math.Max(1, current.Count / 3); k < current.Count; k++)
            {
                var gap = current[k].Start - current[k - 1].End;
                if (gap >= bestGap) { bestGap = gap; best = k; }
            }
            Emit(current[..best]);
            current = current[best..];
        }

        var lineLimit = LineWrapper.LineLimit(language);
        var maxChars = lineLimit * MaxLines;
        foreach (var word in words)
        {
            if (current.Count > 0)
            {
                var last = current[^1];
                var length = current.Sum(w => Length(w.Text) + 1) + Length(word.Text);
                var gap = word.Start - last.End;
                var lastText = last.Text;
                var sentenceEnd = lastText.Length > 0 && ".?!…。？！".Contains(lastText[^1]);
                var clauseEnd = lastText.Length > 0 && ",;:—，、".Contains(lastText[^1]);
                var curLen = length - Length(word.Text);

                if (gap > PauseBreak || (sentenceEnd && curLen >= 15) || (clauseEnd && curLen >= lineLimit))
                    Flush();
                else if (length > maxChars || word.End - current[0].Start > MaxDuration)
                    SplitAtBestPause();
            }
            current.Add(word);
        }
        Flush();

        // Give short cues enough reading time without overlapping the next one.
        for (var i = 0; i < cues.Count; i++)
        {
            var nextStart = i + 1 < cues.Count ? cues[i + 1].Start : double.PositiveInfinity;
            var end = cues[i].End;
            if (end - cues[i].Start < MinDuration) end = Math.Min(cues[i].Start + MinDuration, nextStart);
            // Close tiny gaps so captions don't flicker.
            if (nextStart - end < 0.25 && nextStart > end) end = nextStart;
            cues[i] = cues[i] with { End = Math.Min(end, nextStart) };
        }
        return cues.Select(c => c with { Text = LineWrapper.Wrap(c.Text, language) }).ToList();
    }

    static List<TimedWord> PunctuateIfNeeded(List<TimedWord> words, string? language)
    {
        static bool Terminated(string t) => t.Length > 0 && ".?!…。？！".Contains(t[^1]);
        if (words.Count <= 3 || words.Any(w => Terminated(w.Text))) return words;
        if (language is not null && LineWrapper.NoSpaceLanguages.Contains(language[..Math.Min(2, language.Length)])) return words;

        var result = new List<TimedWord>(words);
        var capitalizeNext = true;
        for (var i = 0; i < result.Count; i++)
        {
            var text = result[i].Text;
            if (capitalizeNext && text.Length > 0 && char.IsLower(text[0]))
                text = char.ToUpperInvariant(text[0]) + text[1..];
            var isLast = i == result.Count - 1;
            capitalizeNext = isLast || result[i + 1].Start - result[i].End >= SentencePause;
            if (capitalizeNext && text.Length > 0 && char.IsLetterOrDigit(text[^1])) text += ".";
            result[i] = result[i] with { Text = text };
        }
        return result;
    }

    /// <summary>Tokens that contain several words get split with interpolated timing.</summary>
    static List<TimedWord> Explode(IReadOnlyList<TimedWord> words)
    {
        var result = new List<TimedWord>();
        foreach (var w in words)
        {
            var parts = w.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1)
            {
                if (parts.Length == 1) result.Add(w with { Text = parts[0] });
                continue;
            }
            double total = parts.Sum(Length);
            var t = w.Start;
            foreach (var p in parts)
            {
                var d = (w.End - w.Start) * Length(p) / total;
                result.Add(new TimedWord(p, t, t + d));
                t += d;
            }
        }
        return result;
    }
}

/// <summary>Breaks subtitle text into at most two balanced lines.</summary>
public static class LineWrapper
{
    /// <summary>Languages written without spaces between words.</summary>
    public static readonly HashSet<string> NoSpaceLanguages = ["th", "zh", "ja", "lo", "km", "my"];

    public static int LineLimit(string? language) => language is null ? Segmenter.MaxCharsPerLine : language[..Math.Min(2, language.Length)] switch
    {
        "zh" or "ja" => 18,
        "ko" => 24,
        "th" => 35,
        _ => Segmenter.MaxCharsPerLine,
    };

    public static string Wrap(string text, string? language)
    {
        var flat = text.Replace("\n", " ").Trim(' ');
        var limit = LineLimit(language);
        if (Segmenter.Length(flat) <= limit) return flat;

        var noSpaces = language is not null && NoSpaceLanguages.Contains(language[..Math.Min(2, language.Length)]);
        var tokens = noSpaces ? WordUnits(flat) : flat.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t + " ").ToList();

        // Find the split point that balances both lines best.
        var best = (Score: int.MaxValue, Index: tokens.Count / 2);
        var left = 0;
        var total = tokens.Sum(Segmenter.Length);
        for (var i = 1; i < tokens.Count; i++)
        {
            left += Segmenter.Length(tokens[i - 1]);
            var score = Math.Abs(total - 2 * left);
            if (score < best.Score) best = (score, i);
        }
        var line1 = string.Concat(tokens.Take(best.Index)).Trim(' ');
        var line2 = string.Concat(tokens.Skip(best.Index)).Trim(' ');
        return line2.Length == 0 ? line1 : $"{line1}\n{line2}";
    }

    /// <summary>
    /// Units to break scripts without spaces at. The macOS version uses the system word tokenizer; .NET has none,
    /// so text breaks at the spaces it has (Thai separates phrases with them), otherwise between characters.
    /// </summary>
    static List<string> WordUnits(string text)
    {
        if (text.Contains(' ')) return text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t + " ").ToList();
        var units = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) units.Add((string)e.Current);
        return units;
    }
}

public static class SrtWriter
{
    public static string Srt(IReadOnlyList<CaptionCue> cues)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < cues.Count; i++)
            sb.Append($"{i + 1}\n{Stamp(cues[i].Start)} --> {Stamp(cues[i].End)}\n{cues[i].Text}\n\n");
        return sb.ToString();
    }

    static string Stamp(double t)
    {
        var ms = (long)Math.Round(Math.Max(0, t) * 1000, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}");
    }
}
