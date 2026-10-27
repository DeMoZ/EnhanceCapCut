using System.Net.Http;
using System.Text.Json;

namespace EnhanceCapCut.Subtitles;

/// <summary>A text in several languages: language tag ("en", "ru", "pt-BR") → string.</summary>
public sealed class LocalizedText(IReadOnlyDictionary<string, string> values)
{
    public IReadOnlyDictionary<string, string> Values { get; } = values;

    /// <summary>
    /// The text for the first of <paramref name="languages"/> (most preferred first) that has one: the exact tag, then its
    /// base language ("pt-BR" → "pt"); then English. Tags compare case-insensitively, "_" counts as "-".
    /// </summary>
    public string? Resolved(IEnumerable<string> languages)
    {
        var byTag = new Dictionary<string, string>();
        foreach (var (key, value) in Values) byTag.TryAdd(Normalize(key), value);
        foreach (var language in languages)
        {
            var tag = Normalize(language);
            if (byTag.TryGetValue(tag, out var text)) return text;
            if (byTag.TryGetValue(tag.Split('-')[0], out text)) return text;
        }
        return byTag.GetValueOrDefault("en");
    }

    static string Normalize(string tag) => tag.Replace('_', '-').ToLowerInvariant();

    /// <summary>An object of strings, or a plain string (English); null for anything else or no text.</summary>
    internal static LocalizedText? Read(JsonElement e)
    {
        var values = new Dictionary<string, string>();
        if (e.ValueKind == JsonValueKind.String)
        {
            if (e.GetString() is { Length: > 0 } s) values["en"] = s;
        }
        else if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } s) values.TryAdd(p.Name, s);
        }
        return values.Count > 0 ? new LocalizedText(values) : null;
    }
}

/// <summary>One way to donate: a payment page opened in the browser.</summary>
public sealed record DonationMethod(string Id, LocalizedText Title, LocalizedText? Description, Uri Url);

/// <summary>
/// The donate window's content for one tool, from the <c>DeMoZ/donations</c> repository (see its README for the format).
/// Read leniently like drafts: unknown fields are skipped, a malformed method is dropped. Mirrors the macOS version's
/// <c>Donations.swift</c>.
/// </summary>
public sealed record DonationInfo(int Schema, LocalizedText? Message, IReadOnlyList<DonationMethod> Methods)
{
    /// <summary>Parses a donations file; null when it isn't JSON or offers no way to donate.</summary>
    public static DonationInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var schema = root.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.Number ? (int)s.GetDouble() : 1;
            var message = root.TryGetProperty("message", out var m) ? LocalizedText.Read(m) : null;
            var methods = new List<DonationMethod>();
            if (root.TryGetProperty("methods", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var e in list.EnumerateArray())
                    if (Method(e) is { } method) methods.Add(method);
            return methods.Count > 0 ? new DonationInfo(schema, message, methods) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static DonationMethod? Method(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var title = e.TryGetProperty("title", out var t) ? LocalizedText.Read(t) : null;
        var link = e.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        // Only https pages: the file comes from the network.
        if (title is null || link is null || !Uri.TryCreate(link, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            return null;
        var id = e.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? link : link;
        var description = e.TryGetProperty("description", out var d) ? LocalizedText.Read(d) : null;
        return new DonationMethod(id, title, description, url);
    }
}

/// <summary>Downloads the donations file of a tool, keeping the last good copy for when the network fails.</summary>
public static class Donations
{
    /// <summary>This app's file in the repository.</summary>
    public const string FileName = "enhancecapcut.json";

    /// <summary>GitHub first, then the jsDelivr mirror of the same repository (GitHub's raw files are sometimes unreachable).</summary>
    public static string[] Sources(string file) =>
    [
        $"https://raw.githubusercontent.com/DeMoZ/donations/main/{file}",
        $"https://cdn.jsdelivr.net/gh/DeMoZ/donations@main/{file}",
    ];

    /// <summary><c>%LOCALAPPDATA%\EnhanceCapCut\donations.json</c>.</summary>
    public static string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EnhanceCapCut", "donations.json");

    /// <summary>The last copy downloaded, if any.</summary>
    public static DonationInfo? Cached(string? cache = null)
    {
        try { return DonationInfo.Parse(File.ReadAllText(cache ?? CachePath)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Downloads the file from the first source that answers with a valid one and caches it; null when none does.</summary>
    public static async Task<DonationInfo?> DownloadAsync(string file = FileName, string? cache = null, CancellationToken cancel = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EnhanceCapCut");
        foreach (var source in Sources(file))
        {
            string json;
            try { json = await http.GetStringAsync(source, cancel); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { continue; }
            if (DonationInfo.Parse(json) is not { } info) continue;
            var path = cache ?? CachePath;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            return info;
        }
        return null;
    }
}
