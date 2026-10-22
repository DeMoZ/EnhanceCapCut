import Foundation

/// CapCut's "Voice filters" (`materials.audio_effects`, "Booming", "Old Hollywood", "Live House"…).
///
/// Each filter is a small processor graph that CapCut downloads into its effect cache as
/// `mdsp_preset.json` (nodes such as Filter, Equaliser, Distortion, Compressor, StudioReverb, Gain,
/// Mixer, a looping Source, and parameter transforms that map the filter's "strength" slider to the
/// dry and wet gains). We read the user's cached preset at render time and run the graph with our own
/// DSP; nothing from the cache is bundled. Processor behaviour that the preset doesn't pin down
/// (filter Q, the distortion curve, makeup gain, the reverb's structure and level) is calibrated
/// against the author's exports — see docs/FORMAT.md.
public struct VoiceFilterGraph {
    package enum Kind: String {
        case filter = "Filter", equaliser = "Equaliser", distortion = "Distortion", compressor = "Compressor"
        case reverb = "StudioReverb", gain = "Gain", gainAndPan = "GainAndPan", mixer = "Mixer", source = "Source"
        case transform = "ParameterChangeTransform"
    }

    package struct Node {
        package var id: String
        package var kind: Kind
        package var parameters: [String: Any]
        /// ParameterChangeTransform: (input value, output value) points of the curve.
        package var curve: [(Double, Double)] = []
        /// Source: the looping clip, relative to the preset directory.
        package var sourceFile: URL?
    }

    package struct Connection {
        package var from: String
        package var to: String
        package var toPort: Int
        package var isParameter: Bool
    }

    package var nodes: [String: Node]
    package var connections: [Connection]
    /// Nodes fed by the graph's audio input, and by its "strength" parameter input.
    package var audioInputs: [String]
    package var parameterInputs: [String]
    package var output: String

    public enum LoadError: Error, CustomStringConvertible {
        case unreadable(URL)
        case unsupported(String)
        public var description: String {
            switch self {
            case let .unreadable(url): return "Can't read voice filter preset \(url.path)"
            case let .unsupported(name): return "Unsupported voice filter processor \(name)"
            }
        }
    }

    /// Loads `mdsp_preset.json` from a cached effect directory. Throws on processors we don't implement.
    public static func load(directory: URL) throws -> VoiceFilterGraph {
        let url = directory.appendingPathComponent("mdsp_preset.json")
        guard let data = try? Data(contentsOf: url),
              let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else {
            throw LoadError.unreadable(url)
        }
        var nodes: [String: Node] = [:]
        for raw in root["nodes"] as? [[String: Any]] ?? [] {
            guard let id = raw["node_id"] as? String else { continue }
            let name = raw["processor_name"] as? String ?? ""
            guard let kind = Kind(rawValue: name) else { throw LoadError.unsupported(name) }
            let state = raw["processor_state"] as? [String: Any] ?? [:]
            var node = Node(id: id, kind: kind, parameters: state["parameters"] as? [String: Any] ?? [:])
            if kind == .transform,
               let first = (state["transforms"] as? [[String: Any]])?.first,
               let inner = (first["transforms"] as? [[String: Any]])?.first,
               let points = inner["transform_curve"] as? [[Any]] {
                node.curve = points.compactMap { p in
                    guard p.count == 2, let x = number(p[0]), let y = number(p[1]) else { return nil }
                    return (x, y)
                }
            }
            if kind == .source,
               let sequenced = state["sequenced_clip"] as? [String: Any],
               let clip = sequenced["clip"] as? [String: Any],
               let uri = clip["audio_file_uri"] as? String {
                let name = uri.hasPrefix("file://") ? String(uri.dropFirst("file://".count)) : uri
                node.sourceFile = directory.appendingPathComponent(name)
            }
            nodes[id] = node
        }
        var connections: [Connection] = []
        for raw in root["connections"] as? [[String: Any]] ?? [] {
            guard let from = raw["upstream_node_id"] as? String, let to = raw["downstream_node_id"] as? String,
                  nodes[from] != nil, nodes[to] != nil else { continue }
            connections.append(Connection(from: from, to: to, toPort: Int(number(raw["downstream_port_index"]) ?? 0),
                                          isParameter: (raw["port_type"] as? String) == "parameter_change"))
        }
        var audioInputs: [String] = [], parameterInputs: [String] = [], output: String?
        for port in root["ports"] as? [[String: Any]] ?? [] {
            guard let id = port["internal_node_id"] as? String, nodes[id] != nil else { continue }
            let isAudio = (port["port_type"] as? String) != "parameter_change"
            if (port["port_direction"] as? String) == "output" {
                if isAudio { output = id }
            } else if isAudio {
                audioInputs.append(id)
            } else {
                parameterInputs.append(id)
            }
        }
        guard let output, !audioInputs.isEmpty else { throw LoadError.unreadable(url) }
        return VoiceFilterGraph(nodes: nodes, connections: connections, audioInputs: audioInputs,
                                parameterInputs: parameterInputs, output: output)
    }

    package static func number(_ value: Any?) -> Double? {
        switch value {
        case let n as NSNumber: return n.doubleValue
        case let s as String: return Double(s)
        default: return nil
        }
    }
}
