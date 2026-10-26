using System.Globalization;
using System.Text;
using System.Text.Json;

namespace EnhanceCapCut.Subtitles;

/// <summary>
/// The text tokenizer of Chatterbox Multilingual: the subset of a Hugging Face <c>tokenizer.json</c> that its model
/// uses — added tokens, an NFKD + "space → [SPACE]" normalizer, the <c>Whitespace</c> pre-tokenizer, a character-level
/// BPE and the template that wraps the ids (<c>&lt;EXAGGERATION&gt; &lt;s&gt; … &lt;/s&gt; &lt;START_SPEECH&gt; ×2</c>).
/// Matches the <c>tokenizers</c> library's ids (checked in the tests). A port of the macOS version's
/// <c>ChatterboxTokenizer.swift</c>. Strings compare ordinally here, so "й" and "и" + U+0306 stay apart without the
/// scalar arrays the Swift version needs. NFKD comes from <see cref="UnicodeNfkd"/> (InvariantGlobalization turns
/// .NET's own off).
/// </summary>
public sealed class ChatterboxTokenizer
{
    readonly Dictionary<string, int> vocab = new(StringComparer.Ordinal);
    readonly Dictionary<(string, string), int> ranks = [];
    readonly int unknown;
    /// <summary>Added tokens matched in the raw text ("[en]", "[laughter]", …), longest first.</summary>
    readonly List<(string Text, int Id)> rawTokens = [];
    /// <summary>Added tokens matched after normalization ("[SPACE]").</summary>
    readonly List<(string Text, int Id)> normalizedTokens = [];
    readonly bool nfkd;
    readonly List<(string Pattern, string Content)> replacements = [];
    readonly List<int> prefix = [];
    readonly List<int> suffix = [];

    public ChatterboxTokenizer(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = json.RootElement;
        var model = root.GetProperty("model");
        foreach (var entry in model.GetProperty("vocab").EnumerateObject()) vocab[entry.Name] = entry.Value.GetInt32();
        if (model.TryGetProperty("merges", out var merges))
        {
            var i = 0;
            foreach (var merge in merges.EnumerateArray())
            {
                // Merges are "a b" strings in older files and ["a", "b"] arrays in newer ones.
                string? a = null, b = null;
                if (merge.ValueKind == JsonValueKind.Array && merge.GetArrayLength() == 2)
                {
                    a = merge[0].GetString();
                    b = merge[1].GetString();
                }
                else if (merge.GetString() is { } text && text.IndexOf(' ', StringComparison.Ordinal) is var space and >= 0)
                {
                    a = text[..space];
                    b = text[(space + 1)..];
                }
                if (a is not null && b is not null) ranks.TryAdd((a, b), i);
                i++;
            }
        }
        unknown = model.TryGetProperty("unk_token", out var unk) && unk.GetString() is { } u && vocab.TryGetValue(u, out var id) ? id : 1;

        foreach (var token in root.GetProperty("added_tokens").EnumerateArray())
        {
            var entry = (token.GetProperty("content").GetString() ?? "", token.GetProperty("id").GetInt32());
            if (token.TryGetProperty("normalized", out var n) && n.ValueKind == JsonValueKind.True) normalizedTokens.Add(entry);
            else rawTokens.Add(entry);
        }
        rawTokens.Sort((x, y) => Runes(y.Text).Count.CompareTo(Runes(x.Text).Count));
        normalizedTokens.Sort((x, y) => Runes(y.Text).Count.CompareTo(Runes(x.Text).Count));

        if (root.TryGetProperty("normalizer", out var normalizer) && normalizer.ValueKind == JsonValueKind.Object)
        {
            var steps = normalizer.TryGetProperty("normalizers", out var list) ? list.EnumerateArray().ToList() : [normalizer];
            foreach (var step in steps)
            {
                switch (step.GetProperty("type").GetString())
                {
                    case "NFKD": nfkd = true; break;
                    case "Replace":
                        if (step.GetProperty("pattern").TryGetProperty("String", out var pattern))
                            replacements.Add((pattern.GetString() ?? "", step.GetProperty("content").GetString() ?? ""));
                        break;
                }
            }
        }

        // The "single" template: special tokens before and after the text.
        if (root.TryGetProperty("post_processor", out var post) && post.ValueKind == JsonValueKind.Object)
        {
            var specials = post.GetProperty("special_tokens");
            var seenText = false;
            foreach (var item in post.GetProperty("single").EnumerateArray())
            {
                if (item.TryGetProperty("Sequence", out _)) { seenText = true; continue; }
                var name = item.GetProperty("SpecialToken").GetProperty("id").GetString() ?? "";
                var ids = specials.GetProperty(name).GetProperty("ids").EnumerateArray().Select(e => e.GetInt32());
                (seenText ? suffix : prefix).AddRange(ids);
            }
        }
    }

    /// <summary>The model's input ids for <paramref name="text"/> (already prefixed with the language tag, e.g. "[en]Hello.").</summary>
    public List<int> Encode(string text)
    {
        var ids = new List<int>(prefix);
        foreach (var (piece, id) in Split(text, rawTokens))
        {
            if (id is { } raw) { ids.Add(raw); continue; }
            var normalized = nfkd ? UnicodeNfkd.Normalize(piece) : piece;
            foreach (var (pattern, content) in replacements)
                if (pattern.Length > 0) normalized = normalized.Replace(pattern, content, StringComparison.Ordinal);
            foreach (var (part, partId) in Split(normalized, normalizedTokens))
            {
                if (partId is { } special) { ids.Add(special); continue; }
                foreach (var word in Words(part)) ids.AddRange(Bpe(word));
            }
        }
        ids.AddRange(suffix);
        return ids;
    }

    /// <summary>Splits <paramref name="text"/> into the given tokens (id set) and the text between them (id null).</summary>
    static List<(string Text, int? Id)> Split(string text, List<(string Text, int Id)> tokens)
    {
        var result = new List<(string, int?)>();
        var plain = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var matched = false;
            foreach (var (token, id) in tokens)
            {
                if (token.Length == 0 || string.CompareOrdinal(text, i, token, 0, token.Length) != 0) continue;
                if (plain.Length > 0) { result.Add((plain.ToString(), null)); plain.Clear(); }
                result.Add((token, id));
                i += token.Length;
                matched = true;
                break;
            }
            if (matched) continue;
            var rune = Rune.GetRuneAt(text, i);
            plain.Append(rune.ToString());
            i += rune.Utf16SequenceLength;
        }
        if (plain.Length > 0) result.Add((plain.ToString(), null));
        return result;
    }

    /// <summary>The <c>Whitespace</c> pre-tokenizer (<c>\w+|[^\w\s]+</c>): runs of word characters, runs of other non-space characters.</summary>
    internal static List<List<string>> Words(string text)
    {
        var words = new List<List<string>>();
        var current = new List<string>();
        var currentIsWord = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                if (current.Count > 0) { words.Add(current); current = []; }
                continue;
            }
            var isWord = IsWordCharacter(rune);
            if (current.Count > 0 && isWord != currentIsWord) { words.Add(current); current = []; }
            current.Add(rune.ToString());
            currentIsWord = isWord;
        }
        if (current.Count > 0) words.Add(current);
        return words;
    }

    /// <summary>Letters, letter numbers, marks, decimal digits, connector punctuation and the joiners: a regex <c>\w</c>.</summary>
    static bool IsWordCharacter(Rune rune) => rune.Value is 0x200C or 0x200D || Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation => true,
        _ => false,
    };

    /// <summary>Byte-pair merges over the word's characters, lowest rank first.</summary>
    List<int> Bpe(List<string> word)
    {
        var symbols = new List<string>(word);
        while (symbols.Count > 1)
        {
            int bestRank = int.MaxValue, bestIndex = -1;
            for (var i = 0; i < symbols.Count - 1; i++)
            {
                if (ranks.TryGetValue((symbols[i], symbols[i + 1]), out var rank) && rank < bestRank)
                {
                    bestRank = rank;
                    bestIndex = i;
                }
            }
            if (bestIndex < 0) break;
            // Merge every occurrence of the pair, left to right, like the reference implementation.
            var (a, b) = (symbols[bestIndex], symbols[bestIndex + 1]);
            var merged = new List<string>(symbols.Count);
            for (var i = 0; i < symbols.Count; i++)
            {
                if (i + 1 < symbols.Count && symbols[i] == a && symbols[i + 1] == b)
                {
                    merged.Add(a + b);
                    i++;
                }
                else
                {
                    merged.Add(symbols[i]);
                }
            }
            symbols = merged;
        }
        return symbols.Select(s => vocab.TryGetValue(s, out var id) ? id : unknown).ToList();
    }

    static List<Rune> Runes(string s) => s.EnumerateRunes().ToList();
}
