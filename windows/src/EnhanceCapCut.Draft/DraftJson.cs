using System.Globalization;
using System.Text;
using System.Text.Json;

namespace EnhanceCapCut.Draft;

/// <summary>
/// Locates values inside a draft's JSON text so the draft can be edited in place: everything we don't touch stays
/// byte-for-byte as CapCut wrote it (key order, number formatting, unknown fields). The C# counterpart of the macOS
/// version's <c>DraftJSON.swift</c>.
/// </summary>
internal sealed class JsonScanner(byte[] bytes)
{
    public sealed class MalformedException(int offset) : Exception($"The draft is not valid JSON (at byte {offset}).");

    public byte[] Bytes { get; } = bytes;

    /// <summary>The whole document's value.</summary>
    public Range Root()
    {
        var start = SkipSpace(0);
        return new Range(start, ValueEnd(start));
    }

    /// <summary>Members of the object spanning <paramref name="range"/>, in document order.</summary>
    public List<(string Key, Range Value)> Members(Range range)
    {
        if (Bytes[range.Start.Value] != (byte)'{') throw new MalformedException(range.Start.Value);
        var result = new List<(string, Range)>();
        var i = SkipSpace(range.Start.Value + 1);
        while (i < range.End.Value && Bytes[i] != (byte)'}')
        {
            var keyEnd = StringEnd(i);
            var key = JsonSerializer.Deserialize<string>(Bytes.AsSpan(i, keyEnd - i)) ?? "";
            i = SkipSpace(keyEnd);
            if (i >= Bytes.Length || Bytes[i] != (byte)':') throw new MalformedException(i);
            var start = SkipSpace(i + 1);
            var end = ValueEnd(start);
            result.Add((key, new Range(start, end)));
            i = SkipSpace(end);
            if (i < Bytes.Length && Bytes[i] == (byte)',') i = SkipSpace(i + 1);
        }
        return result;
    }

    /// <summary>The value of <paramref name="key"/> in the object spanning <paramref name="range"/>.</summary>
    public Range? Value(string key, Range range)
    {
        foreach (var (k, v) in Members(range))
            if (k == key) return v;
        return null;
    }

    /// <summary>Elements of the array spanning <paramref name="range"/>.</summary>
    public List<Range> Elements(Range range)
    {
        if (Bytes[range.Start.Value] != (byte)'[') throw new MalformedException(range.Start.Value);
        var result = new List<Range>();
        var i = SkipSpace(range.Start.Value + 1);
        while (i < range.End.Value && Bytes[i] != (byte)']')
        {
            var end = ValueEnd(i);
            result.Add(new Range(i, end));
            i = SkipSpace(end);
            if (i < Bytes.Length && Bytes[i] == (byte)',') i = SkipSpace(i + 1);
        }
        return result;
    }

    /// <summary>Parses the value spanning <paramref name="range"/> (for reading; edits go through <see cref="JsonEdits"/>).</summary>
    public JsonElement? Parse(Range range)
    {
        try
        {
            using var doc = JsonDocument.Parse(Bytes.AsMemory()[range]);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string Text(Range range) => Encoding.UTF8.GetString(Bytes.AsSpan()[range]);

    int SkipSpace(int start)
    {
        var i = start;
        while (i < Bytes.Length && Bytes[i] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r') i++;
        return i;
    }

    int StringEnd(int start)
    {
        if (start >= Bytes.Length || Bytes[start] != (byte)'"') throw new MalformedException(start);
        var i = start + 1;
        while (i < Bytes.Length)
        {
            switch (Bytes[i])
            {
                case (byte)'\\': i += 2; break;
                case (byte)'"': return i + 1;
                default: i++; break;
            }
        }
        throw new MalformedException(start);
    }

    int ValueEnd(int start)
    {
        if (start >= Bytes.Length) throw new MalformedException(start);
        switch (Bytes[start])
        {
            case (byte)'"':
                return StringEnd(start);
            case (byte)'{' or (byte)'[':
            {
                var depth = 0;
                var i = start;
                while (i < Bytes.Length)
                {
                    switch (Bytes[i])
                    {
                        case (byte)'"': i = StringEnd(i); continue;
                        case (byte)'{' or (byte)'[': depth++; break;
                        case (byte)'}' or (byte)']':
                            if (--depth == 0) return i + 1;
                            break;
                    }
                    i++;
                }
                throw new MalformedException(start);
            }
            default:
            {
                // Number, true, false or null.
                var i = start;
                while (i < Bytes.Length && Bytes[i] is not ((byte)',' or (byte)'}' or (byte)']' or (byte)' ' or (byte)'\t'
                           or (byte)'\r' or (byte)'\n')) i++;
                if (i == start) throw new MalformedException(start);
                return i;
            }
        }
    }
}

/// <summary>Replacements of byte ranges, applied back to front.</summary>
internal sealed class JsonEdits
{
    readonly List<(Range Range, byte[] Text)> edits = [];

    public void Replace(Range range, string text) => edits.Add((range, Encoding.UTF8.GetBytes(text)));

    public byte[] AppliedTo(byte[] bytes)
    {
        var result = new List<byte>(bytes);
        foreach (var (range, text) in edits.OrderByDescending(e => e.Range.Start.Value))
        {
            var (offset, length) = range.GetOffsetAndLength(bytes.Length);
            result.RemoveRange(offset, length);
            result.InsertRange(offset, text);
        }
        return [.. result];
    }
}

/// <summary>
/// Mutable JSON values for building draft elements: <see cref="SortedDictionary{TKey,TValue}"/> objects,
/// <see cref="List{T}"/> arrays, strings, bools, integers, doubles, null, and <see cref="JsonRaw"/> for numbers copied
/// from a template (their text is kept, so <c>1.0</c> stays <c>1.0</c>).
/// </summary>
internal sealed record JsonRaw(string Text);

/// <summary>
/// Serializes <see cref="JsonText"/> values like CapCut writes them: compact, slashes and non-ASCII unescaped, and
/// floating-point numbers with a decimal point (<c>1.0</c>, not <c>1</c>). Keys are sorted, as in the macOS version.
/// </summary>
internal static class JsonText
{
    /// <summary>A mutable copy of a parsed value (objects become sorted dictionaries, numbers <see cref="JsonRaw"/>).</summary>
    public static object? From(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => new SortedDictionary<string, object?>(
            e.EnumerateObject().ToDictionary(p => p.Name, p => From(p.Value)), StringComparer.Ordinal),
        JsonValueKind.Array => e.EnumerateArray().Select(From).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => new JsonRaw(e.GetRawText()),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    public static SortedDictionary<string, object?> Object(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return (SortedDictionary<string, object?>)From(doc.RootElement)!;
    }

    public static string Encode(object? value)
    {
        var sb = new StringBuilder();
        Write(value, sb);
        return sb.ToString();
    }

    /// <summary>Shortest round-trip text of a double, with ".0" for whole numbers like Swift prints them.</summary>
    public static string Double(double d)
    {
        if (!double.IsFinite(d)) return "0.0";
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        return s.Contains('.') || s.Contains('E') ? s : s + ".0";
    }

    static void Write(object? value, StringBuilder sb)
    {
        switch (value)
        {
            case null: sb.Append("null"); break;
            case string s: WriteString(s, sb); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case JsonRaw raw: sb.Append(raw.Text); break;
            case double d: sb.Append(Double(d)); break;
            case int or long: sb.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)); break;
            case IDictionary<string, object?> o:
            {
                sb.Append('{');
                var first = true;
                foreach (var key in o.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(key, sb);
                    sb.Append(':');
                    Write(o[key], sb);
                }
                sb.Append('}');
                break;
            }
            case System.Collections.IEnumerable a:
            {
                sb.Append('[');
                var first = true;
                foreach (var element in a)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(element, sb);
                }
                sb.Append(']');
                break;
            }
            default: sb.Append("null"); break;
        }
    }

    static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case < ' ': sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
    }
}
