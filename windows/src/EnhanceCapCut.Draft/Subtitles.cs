namespace EnhanceCapCut.Draft;

/// <summary>
/// A language subtitles can be made in. The macOS version takes the list from Apple's frameworks; here it's a fixed
/// table of languages both Whisper (recognition) and NLLB (translation) handle well.
/// </summary>
/// <param name="Code">File and track name code, as on macOS: "en", "th", "zh-Hans", …</param>
/// <param name="CapCut">Language and region, like "ru-RU": for looking the language up and finding Windows voices (the
/// caption language written into drafts comes from the core, <c>EccCore.CapcutLanguage</c>).</param>
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
