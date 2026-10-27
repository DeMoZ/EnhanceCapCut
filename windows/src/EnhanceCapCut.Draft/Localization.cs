using System.Globalization;
using System.Text.Json;

namespace EnhanceCapCut.Draft;

/// <summary>
/// The app's interface language: the counterpart of the macOS version's <c>Localization.swift</c>. Texts are written in
/// English in the code and wrapped in <c>L(...)</c> (<c>using static EnhanceCapCut.Draft.Localization;</c>); the
/// translations are in <c>Localization/&lt;language&gt;.json</c> at the repo root (English text → translated text), the
/// same file as on the Mac, embedded in this assembly.
///
/// Until the app calls <see cref="Use"/>, everything stays English: the <c>ecc</c> CLI's output doesn't depend on the
/// system.
/// </summary>
public static class Localization
{
    public enum Language { English, Russian }

    public static string Code(this Language language) => language == Language.Russian ? "ru" : "en";

    /// <summary>The language's own name, the same in every interface language.</summary>
    public static string NativeName(this Language language) => language == Language.Russian ? "Русский" : "English";

    public static Language? FromCode(string? code) => code switch
    {
        "en" => Language.English,
        "ru" => Language.Russian,
        _ => null,
    };

    /// <summary>The first of the user's preferred languages (tags such as "ru-RU", most preferred first) that is one of
    /// ours; English otherwise. The app passes Windows' language list: with invariant globalization .NET's cultures don't
    /// know it.</summary>
    public static Language System(IEnumerable<string> preferred)
    {
        foreach (var tag in preferred)
            if (FromCode(tag.Split('-', '_')[0].ToLowerInvariant()) is { } language) return language;
        return Language.English;
    }

    public static Language Current { get; private set; } = Language.English;
    static IReadOnlyDictionary<string, string> table = new Dictionary<string, string>();

    /// <summary>Switches the texts to <paramref name="language"/> (the app does this once at launch).</summary>
    public static void Use(Language language)
    {
        Current = language;
        table = language == Language.English ? new Dictionary<string, string>() : Load(language) ?? new Dictionary<string, string>();
    }

    /// <summary>The translation table for <paramref name="language"/>, from the embedded copy of the repo's file.</summary>
    public static IReadOnlyDictionary<string, string>? Load(Language language)
    {
        using var stream = typeof(Localization).Assembly.GetManifestResourceStream($"Localization.{language.Code()}.json");
        return stream is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
    }

    /// <summary><paramref name="english"/> in the interface language, with <c>{0}</c>, <c>{1}</c>… replaced by
    /// <paramref name="args"/>. The key is always a literal (or literals joined with <c>+</c>): the tests find every key
    /// in the code and check that each language has it.</summary>
    public static string L(string english, params object[] args)
    {
        var text = table.GetValueOrDefault(english, english);
        for (var i = 0; i < args.Length; i++) text = text.Replace($"{{{i}}}", Convert.ToString(args[i], CultureInfo.CurrentCulture));
        return text;
    }
}
