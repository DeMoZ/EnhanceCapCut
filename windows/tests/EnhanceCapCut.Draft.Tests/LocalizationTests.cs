using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Draft.Tests;

// The translation tables against the code, as the macOS version's LocalizationTests.swift does for the Swift sources:
// every L("…") key of the C# sources has a translation with the same {n} placeholders and ** marks. (That no table keeps
// keys neither app uses is checked on the Mac side, which reads both source trees.) The tables are read through the
// embedded copies, so this also checks that they are embedded.
public class LocalizationTests
{
    static string Repo([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", ".."));

    [Fact]
    public void EveryKeyIsTranslated()
    {
        var keys = Keys(Path.Combine(Repo(), "windows", "src"));
        Assert.True(keys.Count > 200);
        foreach (var language in Enum.GetValues<Localization.Language>().Where(l => l != Localization.Language.English))
        {
            var table = Localization.Load(language);
            Assert.NotNull(table);
            var missing = keys.Where(k => !table.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
            Assert.Empty(missing);
            foreach (var key in keys)
            {
                Assert.Equal(Placeholders(key), Placeholders(table[key]));
                Assert.Equal(key.Split("**").Length, table[key].Split("**").Length);
            }
        }
    }

    [Fact]
    public void LanguageNamesAreTranslated()
    {
        var table = Localization.Load(Localization.Language.Russian)!;
        Assert.Empty(SubtitleLanguage.All.Where(l => !table.ContainsKey(l.Name)).Select(l => l.Name));
    }

    [Fact]
    public void Formatting()
    {
        Assert.Equal("Exported a.mp4 in 0:12", Localization.L("Exported {0} in {1}", "a.mp4", "0:12"));
        Assert.Equal("Tokens: {project} {date} {time} {res} {codec} {fps}", Localization.L("Tokens: {project} {date} {time} {res} {codec} {fps}"));
    }

    [Fact]
    public void SystemLanguage()
    {
        Assert.Equal(Localization.Language.Russian, Localization.System(["ru-RU", "en-US"]));
        Assert.Equal(Localization.Language.Russian, Localization.System(["de-DE", "ru"]));
        Assert.Equal(Localization.Language.English, Localization.System(["de-DE"]));
    }

    const string Literal = """
        "((?:[^"\\\n]|\\.)*)"
        """;

    /// <summary>The keys of <c>L(...)</c> calls: a literal, or literals joined with <c>+</c> (the same rule as in
    /// LocalizationTests.swift).</summary>
    static HashSet<string> Keys(string root)
    {
        var call = new Regex($@"(?<![A-Za-z0-9_])L\(\s*({Literal}(?:\s*\+\s*{Literal})*)");
        var part = new Regex(Literal);
        var keys = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            foreach (Match match in call.Matches(File.ReadAllText(file)))
            {
                var key = string.Concat(part.Matches(match.Groups[1].Value).Select(m => m.Groups[1].Value));
                keys.Add(key.Replace("\\\"", "\"").Replace("\\\\", "\\"));
            }
        }
        return keys;
    }

    static List<string> Placeholders(string s) => Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).Order(StringComparer.Ordinal).ToList();
}
