using System.Globalization;
using System.Text.Json;

namespace EnhanceCapCut.Draft;

/// <summary>
/// CapCut's "Voice filters" (<c>materials.audio_effects</c>, "Booming", "Old Hollywood", "Live House"…).
///
/// Each filter is a small processor graph that CapCut downloads into its effect cache as
/// <c>mdsp_preset.json</c> (nodes such as Filter, Equaliser, Distortion, Compressor, StudioReverb, Gain,
/// Mixer, a looping Source, and parameter transforms that map the filter's "strength" slider to the
/// dry and wet gains). We read the user's cached preset at render time; nothing from the cache is bundled.
/// This is the preset model; running the graph is the renderer's job.
/// </summary>
public sealed class VoiceFilterGraph
{
    public enum Kind { Filter, Equaliser, Distortion, Compressor, Reverb, Gain, GainAndPan, Mixer, Source, Transform }

    static readonly Dictionary<string, Kind> KindNames = new()
    {
        ["Filter"] = Kind.Filter, ["Equaliser"] = Kind.Equaliser, ["Distortion"] = Kind.Distortion,
        ["Compressor"] = Kind.Compressor, ["StudioReverb"] = Kind.Reverb, ["Gain"] = Kind.Gain,
        ["GainAndPan"] = Kind.GainAndPan, ["Mixer"] = Kind.Mixer, ["Source"] = Kind.Source,
        ["ParameterChangeTransform"] = Kind.Transform,
    };

    public sealed class Node(string id, Kind kind, IReadOnlyDictionary<string, JsonElement> parameters)
    {
        public string Id { get; } = id;
        public Kind Kind { get; } = kind;
        public IReadOnlyDictionary<string, JsonElement> Parameters { get; } = parameters;
        /// <summary>ParameterChangeTransform: (input value, output value) points of the curve.</summary>
        public List<(double X, double Y)> Curve { get; } = [];
        /// <summary>Source: the looping clip, relative to the preset directory.</summary>
        public string? SourceFile { get; set; }
    }

    public sealed record Connection(string From, string To, int ToPort, bool IsParameter);

    public Dictionary<string, Node> Nodes { get; } = [];
    public List<Connection> Connections { get; } = [];
    /// <summary>Nodes fed by the graph's audio input, and by its "strength" parameter input.</summary>
    public List<string> AudioInputs { get; } = [];
    public List<string> ParameterInputs { get; } = [];
    public string Output { get; private set; } = "";

    public sealed class LoadException(string message) : Exception(message);

    /// <summary>Loads <c>mdsp_preset.json</c> from a cached effect directory. Throws on processors we don't implement.</summary>
    public static VoiceFilterGraph Load(string directory)
    {
        var path = System.IO.Path.Combine(directory, "mdsp_preset.json");
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            root = doc.RootElement.Clone();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new LoadException($"Can't read voice filter preset {path}");
        }
        if (root.ValueKind != JsonValueKind.Object) throw new LoadException($"Can't read voice filter preset {path}");

        var graph = new VoiceFilterGraph();
        foreach (var raw in Objects(root, "nodes"))
        {
            if (raw.Str("node_id") is not { } id) continue;
            var name = raw.Str("processor_name") ?? "";
            if (!KindNames.TryGetValue(name, out var kind)) throw new LoadException($"Unsupported voice filter processor {name}");
            var state = raw.Obj("processor_state");
            var parameters = new Dictionary<string, JsonElement>();
            if (state?.Obj("parameters") is { } p)
            {
                foreach (var property in p.EnumerateObject()) parameters[property.Name] = property.Value.Clone();
            }
            var node = new Node(id, kind, parameters);
            if (kind == Kind.Transform && state is { } s && FirstObject(s, "transforms") is { } first
                && FirstObject(first, "transforms") is { } inner
                && inner.Get("transform_curve") is { ValueKind: JsonValueKind.Array } points)
            {
                foreach (var point in points.EnumerateArray())
                {
                    if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 2) continue;
                    if (Number(point[0]) is { } x && Number(point[1]) is { } y) node.Curve.Add((x, y));
                }
            }
            if (kind == Kind.Source && state?.Obj("sequenced_clip")?.Obj("clip")?.Str("audio_file_uri") is { } uri)
            {
                node.SourceFile = System.IO.Path.Combine(directory, uri.StartsWith("file://") ? uri["file://".Length..] : uri);
            }
            graph.Nodes[id] = node;
        }
        foreach (var raw in Objects(root, "connections"))
        {
            if (raw.Str("upstream_node_id") is not { } from || raw.Str("downstream_node_id") is not { } to
                || !graph.Nodes.ContainsKey(from) || !graph.Nodes.ContainsKey(to)) continue;
            graph.Connections.Add(new(from, to, (int)(Number(raw.Get("downstream_port_index")) ?? 0),
                raw.Str("port_type") == "parameter_change"));
        }
        string? output = null;
        foreach (var port in Objects(root, "ports"))
        {
            if (port.Str("internal_node_id") is not { } id || !graph.Nodes.ContainsKey(id)) continue;
            var isAudio = port.Str("port_type") != "parameter_change";
            if (port.Str("port_direction") == "output")
            {
                if (isAudio) output = id;
            }
            else if (isAudio) graph.AudioInputs.Add(id);
            else graph.ParameterInputs.Add(id);
        }
        if (output is null || graph.AudioInputs.Count == 0) throw new LoadException($"Can't read voice filter preset {path}");
        graph.Output = output;
        return graph;
    }

    /// <summary>A number, also accepted as a numeric string.</summary>
    public static double? Number(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.Number } v => v.GetDouble(),
        { ValueKind: JsonValueKind.String } v when double.TryParse(v.GetString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    static IEnumerable<JsonElement> Objects(JsonElement o, string key) =>
        o.Get(key) is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : [];

    static JsonElement? FirstObject(JsonElement o, string key) =>
        o.Get(key) is { ValueKind: JsonValueKind.Array } array && array.GetArrayLength() > 0
            && array[0].ValueKind == JsonValueKind.Object ? array[0] : null;
}
