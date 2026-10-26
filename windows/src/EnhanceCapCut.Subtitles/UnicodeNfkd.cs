using System.Globalization;
using System.Text;

namespace EnhanceCapCut.Subtitles;

/// <summary>
/// Unicode NFKD (compatibility decomposition, then canonical ordering of combining marks) from compiled-in tables
/// (<c>UnicodeNfkd.Data.cs</c>, made by <c>windows/scripts/make-nfkd.py</c>). The app runs with InvariantGlobalization,
/// where <see cref="string.Normalize(NormalizationForm)"/> leaves text unchanged; the voice cloning tokenizer needs real
/// NFKD to give the macOS version's ids.
/// </summary>
static partial class UnicodeNfkd
{
    static readonly Lazy<Dictionary<int, int[]>> decompositions = new(() => Parse(Decompositions)
        .ToDictionary(e => e.Key, e => e.Value.Split(',').Select(Hex).ToArray()));
    static readonly Lazy<Dictionary<int, int>> combiningClasses = new(() => Parse(CombiningClasses)
        .ToDictionary(e => e.Key, e => Hex(e.Value)));

    // Hangul syllables decompose by formula (Unicode §3.12).
    const int SBase = 0xAC00, LBase = 0x1100, VBase = 0x1161, TBase = 0x11A7, VCount = 21, TCount = 28, SCount = 11_172;

    public static string Normalize(string text)
    {
        var points = new List<int>(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var cp = rune.Value;
            if (cp >= SBase && cp < SBase + SCount)
            {
                var s = cp - SBase;
                points.Add(LBase + s / (VCount * TCount));
                points.Add(VBase + s % (VCount * TCount) / TCount);
                if (s % TCount != 0) points.Add(TBase + s % TCount);
            }
            else if (decompositions.Value.TryGetValue(cp, out var d))
            {
                points.AddRange(d);
            }
            else
            {
                points.Add(cp);
            }
        }
        // Canonical ordering: each run of combining marks sorted by class, stably.
        for (var i = 0; i < points.Count; i++)
        {
            if (Class(points[i]) == 0) continue;
            var end = i;
            while (end < points.Count && Class(points[end]) != 0) end++;
            if (end - i > 1)
            {
                var run = points.GetRange(i, end - i).Select((p, k) => (p, k)).OrderBy(x => Class(x.p)).ThenBy(x => x.k).Select(x => x.p).ToList();
                for (var k = 0; k < run.Count; k++) points[i + k] = run[k];
            }
            i = end;
        }
        var result = new StringBuilder(points.Count);
        foreach (var p in points) result.Append(new Rune(p).ToString());
        return result.ToString();
    }

    static int Class(int cp) => combiningClasses.Value.TryGetValue(cp, out var c) ? c : 0;

    static IEnumerable<KeyValuePair<int, string>> Parse(string table) => table.Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(e => e.Split(':')).Select(p => new KeyValuePair<int, string>(Hex(p[0]), p[1]));

    static int Hex(string s) => int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
