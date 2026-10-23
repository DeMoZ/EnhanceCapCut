using System.Globalization;
using System.Text.Json;

namespace EnhanceCapCut.Draft;

/// <summary>
/// CapCut drafts change between app versions. Every field is read leniently: a missing key, a <c>null</c>
/// or a value of an unexpected type yields <c>null</c> instead of failing the whole draft
/// (the C# counterpart of the macOS version's <c>Lenient.swift</c>).
/// </summary>
internal static class Lenient
{
    /// <summary>The value at <paramref name="key"/>, or null when it's missing or JSON null.</summary>
    public static JsonElement? Get(this JsonElement o, string key) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null
            ? v : null;

    public static JsonElement? Obj(this JsonElement o, string key) =>
        o.Get(key) is { ValueKind: JsonValueKind.Object } v ? v : null;

    public static string? Str(this JsonElement o, string key) =>
        o.Get(key) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    /// <summary>A number, also accepted as a numeric string.</summary>
    public static double? Num(this JsonElement o, string key) => o.Get(key) switch
    {
        { ValueKind: JsonValueKind.Number } v => v.GetDouble(),
        { ValueKind: JsonValueKind.String } v when double.TryParse(v.GetString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    public static long? Int64(this JsonElement o, string key) => o.Get(key) switch
    {
        { ValueKind: JsonValueKind.Number } v => v.TryGetInt64(out var i) ? i : Round(v.GetDouble()),
        _ => null,
    };

    /// <summary>A boolean, also accepted as an integer (non-zero = true).</summary>
    public static bool? Bool(this JsonElement o, string key) => o.Get(key) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.Number } v when v.TryGetInt64(out var i) => i != 0,
        _ => null,
    };

    /// <summary>Reads an array, skipping elements that <paramref name="read"/> rejects (returns null for).</summary>
    public static List<T> List<T>(this JsonElement o, string key, Func<JsonElement, T?> read) where T : class
    {
        var result = new List<T>();
        if (o.Get(key) is not { ValueKind: JsonValueKind.Array } array) return result;
        foreach (var item in array.EnumerateArray())
        {
            if (read(item) is { } value) result.Add(value);
        }
        return result;
    }

    /// <summary>An array of numbers; null when it's missing or any element isn't a number.</summary>
    public static List<double>? Doubles(this JsonElement o, string key)
    {
        if (o.Get(key) is not { ValueKind: JsonValueKind.Array } array) return null;
        var result = new List<double>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number) return null;
            result.Add(item.GetDouble());
        }
        return result;
    }

    /// <summary>An array of strings; null when it's missing or any element isn't a string.</summary>
    public static List<string>? Strings(this JsonElement o, string key)
    {
        if (o.Get(key) is not { ValueKind: JsonValueKind.Array } array) return null;
        var result = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return null;
            result.Add(item.GetString()!);
        }
        return result;
    }

    /// <summary>An array of integers; null when it's missing or any element isn't an integer.</summary>
    public static List<int>? Ints(this JsonElement o, string key)
    {
        if (o.Get(key) is not { ValueKind: JsonValueKind.Array } array) return null;
        var result = new List<int>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var i)) return null;
            result.Add(i);
        }
        return result;
    }

    /// <summary>Rounds half away from zero, like Swift's <c>rounded()</c>.</summary>
    public static long Round(double value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>An upper-case UUID string, like Swift's <c>UUID().uuidString</c>.</summary>
    public static string NewId() => Guid.NewGuid().ToString().ToUpperInvariant();
}
