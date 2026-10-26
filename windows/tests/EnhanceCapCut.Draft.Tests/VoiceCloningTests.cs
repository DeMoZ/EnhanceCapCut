using EnhanceCapCut.Subtitles;

namespace EnhanceCapCut.Draft.Tests;

/// <summary>The same checks as the macOS version's VoiceCloningTests.</summary>
public class VoiceCloningTests
{
    /// <summary>A tokenizer.json with the same structure as Chatterbox's, small enough to read.</summary>
    static ChatterboxTokenizer MiniTokenizer()
    {
        const string json = """
            {"added_tokens":[{"id":0,"content":"[STOP]","normalized":false,"special":true},
                             {"id":2,"content":"[SPACE]","normalized":true,"special":true},
                             {"id":3,"content":"[en]","normalized":false,"special":false}],
             "normalizer":{"type":"Sequence","normalizers":[{"type":"NFKD"},
                           {"type":"Replace","pattern":{"String":" "},"content":"[SPACE]"}]},
             "pre_tokenizer":{"type":"Whitespace"},
             "post_processor":{"type":"TemplateProcessing",
                "single":[{"SpecialToken":{"id":"BOS","type_id":0}},{"Sequence":{"id":"A","type_id":0}},
                          {"SpecialToken":{"id":"EOS","type_id":0}}],
                "special_tokens":{"BOS":{"id":"BOS","ids":[9]},"EOS":{"id":"EOS","ids":[0]}}},
             "model":{"type":"BPE","unk_token":"[UNK]","vocab":{"[STOP]":0,"[UNK]":1,"[SPACE]":2,"a":10,"b":11,"c":12,
                      "ab":13,"abc":14,"и":15,"̆":16,"й":17,".":18},
                      "merges":["a b","ab c","и ̆"]}}
            """;
        var path = Path.Combine(Path.GetTempPath(), $"tokenizer-{Guid.NewGuid()}.json");
        File.WriteAllText(path, json);
        try { return new ChatterboxTokenizer(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TokenizerMechanics()
    {
        var t = MiniTokenizer();
        // Template around the text, raw added token, spaces as [SPACE], merges by rank, unknown characters.
        Assert.Equal([9, 3, 14, 2, 13, 18, 0], t.Encode("[en]abc ab."));
        Assert.Equal([9, 3, 1, 10, 0], t.Encode("[en]xa"));
        // NFKD splits "й" into "и" + U+0306, which the merge joins back.
        Assert.Equal([9, 17, 0], t.Encode("й"));
    }

    public static TheoryData<string, int[]> ReferenceCases => new()
    {
        { "[en]Today we are riding quad bikes through the jungle, and honestly, it is a lot more fun than I expected.", new[] { 6563, 255, 708, 296, 207, 88, 2, 100, 2, 127, 2, 31, 83, 52, 2, 194, 68, 2, 15, 22, 24, 61, 2, 40, 31, 202, 2, 42, 2, 23, 97, 20, 64, 7, 2, 53, 2, 21, 47, 218, 70, 7, 2, 60, 2, 54, 2, 14, 2, 99, 33, 2, 244, 2, 19, 97, 2, 40, 43, 2, 285, 2, 169, 124, 16, 188, 9, 0, 6561, 6561 } },
        { "[ru]Первым делом мы заезжаем в старый карьер, который набрал много воды. Это будет нашей грязевой ванной!", new[] { 6563, 255, 716, 1106, 1127, 1137, 1124, 1148, 1133, 2, 1126, 1127, 1132, 1135, 1133, 2, 1133, 1148, 2, 1129, 1122, 1127, 1129, 1128, 1122, 1127, 1133, 2, 1124, 2, 1138, 1139, 1122, 1137, 1148, 1130, 771, 2, 1131, 1122, 1137, 1149, 1127, 1137, 7, 2, 1131, 1135, 1139, 1135, 1137, 1148, 1130, 771, 2, 1134, 1122, 1123, 1137, 1122, 1132, 2, 1133, 1134, 1135, 1125, 1135, 2, 1124, 1135, 1126, 1148, 9, 2, 1119, 1139, 1135, 2, 1123, 1140, 1126, 1127, 1139, 2, 1134, 1122, 1145, 1127, 1130, 771, 2, 1125, 1137, 1152, 1129, 1127, 1124, 1135, 1130, 771, 2, 1124, 1122, 1134, 1134, 1135, 1130, 771, 3, 0, 6561, 6561 } },
        { "[ru]Ёжик и йогурт — «ёлки-палки», 2026 год.", new[] { 6563, 255, 716, 1082, 762, 1128, 1130, 1131, 2, 1130, 2, 1130, 771, 1135, 1125, 1140, 1137, 1139, 2, 317, 2, 340, 1127, 762, 1132, 1131, 1130, 8, 1136, 1122, 1132, 1131, 1130, 356, 7, 2, 265, 263, 265, 269, 2, 1125, 1135, 1126, 9, 0, 6561, 6561 } },
        { "[de]Als Erstes fahren wir in einen alten Steinbruch, der sich mit Wasser gefüllt hat. Das wird unser Schlammbad!", new[] { 6563, 255, 636, 277, 25, 32, 2, 281, 31, 63, 61, 2, 19, 14, 21, 46, 27, 2, 85, 31, 2, 41, 2, 18, 41, 50, 2, 56, 33, 50, 2, 295, 136, 41, 243, 34, 71, 7, 2, 234, 2, 212, 71, 2, 26, 60, 2, 299, 55, 32, 44, 2, 112, 19, 34, 762, 84, 33, 2, 21, 48, 9, 2, 280, 55, 2, 85, 31, 17, 2, 97, 32, 44, 2, 295, 71, 25, 122, 26, 15, 68, 3, 0, 6561, 6561 } },
        { "[fr]Ça va? C'est l'été, où êtes-vous?", new[] { 6563, 255, 634, 279, 766, 14, 2, 35, 14, 13, 2, 279, 4, 218, 2, 25, 4, 18, 764, 136, 764, 7, 2, 45, 768, 2, 18, 769, 33, 61, 8, 35, 206, 13, 0, 6561, 6561 } },
        { "[es]¿Qué tal? ¡Mañana vamos al río!", new[] { 6563, 255, 635, 360, 293, 34, 18, 764, 2, 33, 56, 13, 2, 330, 289, 43, 644, 43, 14, 2, 35, 14, 115, 32, 2, 56, 2, 101, 764, 28, 3, 0, 6561, 6561 } },
        { "[pl]Zażółć gęślą jaźń.", new[] { 6563, 255, 717, 302, 14, 39, 773, 28, 764, 782, 16, 764, 2, 112, 772, 32, 764, 25, 14, 772, 2, 23, 14, 39, 764, 27, 764, 9, 0, 6561, 6561 } },
        { "[tr]İstanbul'da ılık bir gün.", new[] { 6563, 255, 712, 285, 773, 63, 43, 15, 167, 4, 17, 14, 2, 779, 25, 779, 24, 2, 15, 98, 2, 20, 34, 762, 27, 9, 0, 6561, 6561 } },
        { "[ar]مرحبا بكم في قناتي.", new[] { 6563, 255, 721, 1491, 1471, 1467, 1462, 1456, 2, 1462, 1489, 1491, 2, 1487, 1461, 2, 1488, 1492, 1456, 1464, 1461, 9, 0, 6561, 6561 } },
        { "[hi]नमस्ते, आप कैसे हैं?", new[] { 6563, 255, 722, 1706, 1712, 1720, 1740, 1702, 1734, 7, 2, 1672, 1708, 2, 1687, 1735, 1720, 1734, 2, 1721, 1735, 1668, 13, 0, 6561, 6561 } },
        { "[ko]안녕하세요, 반갑습니다.", new[] { 6563, 255, 724, 1794, 1880, 1954, 1785, 1886, 1971, 1801, 1880, 1792, 1885, 1794, 1892, 7, 2, 1790, 1880, 1954, 1783, 1880, 1967, 1792, 1898, 1967, 1785, 1900, 1786, 1880, 9, 0, 6561, 6561 } },
        { "[el]Καλημέρα σας, τι κάνετε;", new[] { 6563, 255, 711, 1012, 1024, 1033, 1026, 765, 1025, 764, 1038, 1024, 2, 1040, 1024, 1039, 7, 2, 1041, 1006, 2, 1032, 1024, 764, 1034, 1025, 1041, 1025, 12, 0, 6561, 6561 } },
        { "[en]Price: $5.99 (50% off) & more... #wow @me ~tilde_under 3x4=12", new[] { 6563, 255, 708, 292, 31, 91, 18, 11, 2, 258, 268, 9, 272, 272, 2, 5, 268, 263, 259, 2, 58, 19, 6, 2, 260, 2, 244, 9, 9, 9, 2, 257, 36, 69, 2, 276, 80, 2, 312, 78, 79, 18, 307, 97, 234, 2, 266, 37, 267, 274, 264, 265, 0, 6561, 6561 } },
        { "[en]Tabs\tand\u00A0nbsp, emoji \U0001F600 and [laughter] tag.", new[] { 6563, 255, 708, 296, 109, 32, 53, 2, 27, 15, 248, 7, 2, 18, 115, 23, 22, 2, 1, 2, 53, 2, 607, 2, 33, 143, 9, 0, 6561, 6561 } },
    };

    /// <summary>The real tokenizer against ids from Hugging Face's <c>tokenizers</c> library (skipped until the model is downloaded).</summary>
    [Theory]
    [MemberData(nameof(ReferenceCases))]
    public void TokenizerMatchesReference(string text, int[] ids)
    {
        var path = Path.Combine(ModelStore.FolderOf(ModelStore.VoiceCloning), "tokenizer.json");
        if (!File.Exists(path)) return;
        Assert.Equal(ids, new ChatterboxTokenizer(path).Encode(text));
    }

    [Fact]
    public void TextPreparation()
    {
        Assert.Equal("Hello world.", VoiceCloner.Prepare("hello   world"));
        Assert.Equal("Wait,  what, no - yes.", VoiceCloner.Prepare("Wait… what: no — yes"));
        Assert.Equal("Really?", VoiceCloner.Prepare("Really?"));
        Assert.Equal("\"Привет\".", VoiceCloner.Prepare("«Привет»"));
        Assert.Equal(".", VoiceCloner.Prepare("  "));
    }

    [Fact]
    public void Languages()
    {
        Assert.Equal("pt", VoiceCloner.ModelLanguage("pt-BR"));
        Assert.Equal("no", VoiceCloner.ModelLanguage("nb"));
        Assert.Equal("ru", VoiceCloner.ModelLanguage("ru"));
        Assert.Null(VoiceCloner.ModelLanguage("zh-Hans"));
        Assert.Null(VoiceCloner.ModelLanguage("th"));
    }

    /// <summary>The same numbers as the macOS version's generator.</summary>
    [Fact]
    public void RandomGenerator()
    {
        var r = new SplitMix64(1);
        Assert.Equal([10_451_216_379_200_822_465UL, 13_757_245_211_066_428_519UL, 17_911_839_290_282_890_590UL], [r.Next(), r.Next(), r.Next()]);
    }

    [Fact]
    public void SamplingPenalisesRepeatsAndDropsUnlikelyTokens()
    {
        var r = new SplitMix64(7);
        var counts = new int[3];
        // Token 1 is far below min-p; token 2 was already said, which makes it less likely than token 0.
        for (var i = 0; i < 400; i++) counts[VoiceCloner.Sample([5, -10, 5], [2], ref r)]++;
        Assert.Equal(0, counts[1]);
        Assert.True(counts[0] > counts[2]);
        Assert.True(counts[2] > 0);
    }

    [Fact]
    public void TrimsQuietEnds()
    {
        const int rate = VoiceCloner.SampleRate;
        var samples = Enumerable.Repeat(0.0001f, rate).ToArray();
        for (var i = rate / 4; i < rate / 2; i++) samples[i] = MathF.Sin(i * 0.1f) * 0.5f;
        var trimmed = VoiceCloner.TrimQuietEnds(samples);
        // A quarter second of speech plus 40 ms either side (to the 10 ms frame).
        Assert.Equal(0.25 + 0.08, (double)trimmed.Length / rate, 0.011);
    }

    [Fact]
    public void LineError()
    {
        Assert.Equal(0, Dubber.LineError("Hello, world!", "hello world"));
        Assert.Equal(1.0 / 10, Dubber.LineError("Hello world", "Hallo world"), 6);
        Assert.Equal(1, Dubber.LineError("Ёлки", ""));
    }

    [Fact]
    public void ReferenceStartsAtTheLineAndIsAtLeastFourSeconds()
    {
        const int rate = VoiceCloner.SampleRate;
        var voice = new float[rate * 20];
        Assert.Equal(4 * rate, Dubber.ReferenceRange(new CaptionCue(2, 3, "x"), voice).Length);
        Assert.Equal(6 * rate, Dubber.ReferenceRange(new CaptionCue(2, 12, "x"), voice).Length);
        // Near the end the reference reaches back before the line.
        Assert.Equal(4 * rate, Dubber.ReferenceRange(new CaptionCue(19, 20, "x"), voice).Length);
    }
}

public class TimeStretchTests
{
    /// <summary>Zero crossings per second: the pitch of a pure tone.</summary>
    static double Frequency(float[] x, double rate)
    {
        var crossings = 0;
        for (var i = 1; i < x.Length; i++) if (x[i - 1] < 0 != x[i] < 0) crossings++;
        return crossings / 2.0 / (x.Length / rate);
    }

    [Fact]
    public void SpeedsUpWithoutChangingPitch()
    {
        const int rate = 24_000;
        var tone = Enumerable.Range(0, rate * 2).Select(i => (float)(Math.Sin(2 * Math.PI * 220 * i / rate) * 0.5)).ToArray();
        var fast = TimeStretch.Stretch(tone, 1.35, rate);
        Assert.Equal(tone.Length / 1.35, fast.Length, 2.0);
        // Away from the faded ends the tone keeps its pitch and level.
        var middle = fast[2400..^2400];
        Assert.Equal(220, Frequency(middle, rate), 3.0);
        Assert.Equal(0.5, middle.Max(MathF.Abs), 0.05);
    }

    [Fact]
    public void RateOneIsUnchanged()
    {
        var x = Enumerable.Range(0, 5000).Select(i => (float)(i % 7)).ToArray();
        Assert.Equal(x, TimeStretch.Stretch(x, 1, 24_000));
    }
}
