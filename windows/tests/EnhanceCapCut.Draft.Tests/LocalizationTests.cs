using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using EnhanceCapCut.Draft;

namespace EnhanceCapCut.Draft.Tests;

// The translation tables against the code, as the macOS version's LocalizationTests.swift does for the Swift sources:
// every L("…") key of the C# sources and every tr!("…") key of the Rust core (whose report, errors and names the app
// shows) has a translation with the same {n} placeholders and ** marks, and no table keeps keys that neither app nor the
// core uses any more. The tables are read through the embedded copies, so this also checks that they are embedded.
public class LocalizationTests
{
    static string Repo([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", ".."));

    [Fact]
    public void EveryKeyIsTranslated()
    {
        var keys = Keys(Path.Combine(Repo(), "windows", "src"), "*.cs");
        Assert.True(keys.Count > 200);
        var core = Keys(Path.Combine(Repo(), "core"), "*.rs", "tr!");
        Assert.True(core.Count > 20);
        keys.UnionWith(core);
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
    public void NoUnusedKeys()
    {
        var used = Keys(Path.Combine(Repo(), "windows", "src"), "*.cs");
        used.UnionWith(Keys(Path.Combine(Repo(), "Sources"), "*.swift"));
        used.UnionWith(Keys(Path.Combine(Repo(), "core"), "*.rs", "tr!"));
        // Language names are looked up by their English name (SubtitleLanguage.Title).
        used.UnionWith(SubtitleLanguage.All.Select(l => l.Name));
        foreach (var language in Enum.GetValues<Localization.Language>().Where(l => l != Localization.Language.English))
            Assert.Empty(Localization.Load(language)!.Keys.Except(used).Order(StringComparer.Ordinal));
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

    /// <summary>The keys of <c>L(...)</c> calls (<paramref name="name"/>: <c>tr!</c> for the Rust core) in the
    /// <paramref name="pattern"/> files under <paramref name="root"/>: a literal, or literals joined with <c>+</c> (the
    /// same rule as in LocalizationTests.swift).</summary>
    static HashSet<string> Keys(string root, string pattern, string name = "L")
    {
        // Not a call quoted in a string or in `docs` (the core's doc comments show tr!("…") in backticks).
        var call = new Regex($@"(?<![A-Za-z0-9_`""]){Regex.Escape(name)}\(\s*({Literal}(?:\s*\+\s*{Literal})*)");
        var part = new Regex(Literal);
        var keys = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
        {
            var sep = Path.DirectorySeparatorChar;
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}") || file.Contains($"{sep}target{sep}")
                || file.Contains($"{sep}.build{sep}")) continue;
            foreach (Match match in call.Matches(File.ReadAllText(file)))
            {
                var key = string.Concat(part.Matches(match.Groups[1].Value).Select(m => m.Groups[1].Value));
                keys.Add(key.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\"));
            }
        }
        return keys;
    }

    static List<string> Placeholders(string s) => Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).Order(StringComparer.Ordinal).ToList();
}
