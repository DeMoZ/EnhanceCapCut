using System.Text.Json;
using System.Text.Json.Nodes;

namespace EnhanceCapCut.App;

/// <summary>
/// The window's own choices (sort order, subtitle languages, voices, window frames): the counterpart of the macOS
/// version's UserDefaults, kept in <c>%APPDATA%\EnhanceCapCut\app.json</c> next to the export settings. Keys are the
/// macOS ones. A missing or unreadable file means defaults.
/// </summary>
static class Preferences
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EnhanceCapCut", "app.json");

    static readonly JsonObject Values = Load();

    static JsonObject Load()
    {
        try { return JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, Values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    static T? Get<T>(string key)
    {
        try { return Values[key] is { } node ? node.GetValue<T>() : default; }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { return default; }
    }

    public static bool Bool(string key, bool fallback = false) => Values[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
    public static string? String(string key) => Get<string>(key);
    public static double? Double(string key) => Values[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    public static List<string>? Strings(string key) =>
        Values[key] is JsonArray a ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList() : null;

    public static Dictionary<string, string> Map(string key) =>
        Values[key] is JsonObject o
            ? o.Where(p => p.Value is JsonValue v && v.TryGetValue<string>(out _)).ToDictionary(p => p.Key, p => p.Value!.GetValue<string>())
            : [];

    public static void Set(string key, bool value) { Values[key] = value; Save(); }
    public static void Set(string key, string value) { Values[key] = value; Save(); }
    public static void Set(string key, double value) { Values[key] = value; Save(); }
    public static void Set(string key, IEnumerable<string> value) { Values[key] = new JsonArray(value.Select(s => (JsonNode)s).ToArray()); Save(); }

    public static void Set(string key, IReadOnlyDictionary<string, string> value)
    {
        var o = new JsonObject();
        foreach (var (k, v) in value) o[k] = v;
        Values[key] = o;
        Save();
    }
}
