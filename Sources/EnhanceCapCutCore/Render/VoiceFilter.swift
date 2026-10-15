import Accelerate
import AVFoundation
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
    enum Kind: String {
        case filter = "Filter", equaliser = "Equaliser", distortion = "Distortion", compressor = "Compressor"
        case reverb = "StudioReverb", gain = "Gain", gainAndPan = "GainAndPan", mixer = "Mixer", source = "Source"
        case transform = "ParameterChangeTransform"
    }

    struct Node {
        var id: String
        var kind: Kind
        var parameters: [String: Any]
        /// ParameterChangeTransform: (input value, output value) points of the curve.
        var curve: [(Double, Double)] = []
        /// Source: the looping clip, relative to the preset directory.
        var sourceFile: URL?
    }

    struct Connection {
        var from: String
        var to: String
        var toPort: Int
        var isParameter: Bool
    }

    var nodes: [String: Node]
    var connections: [Connection]
    /// Nodes fed by the graph's audio input, and by its "strength" parameter input.
    var audioInputs: [String]
    var parameterInputs: [String]
    var output: String

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

    static func number(_ value: Any?) -> Double? {
        switch value {
        case let n as NSNumber: return n.doubleValue
        case let s as String: return Double(s)
        default: return nil
        }
    }

    // MARK: Running

    /// Runs the graph over whole channels (one clip) at `rate`; `strength` is the filter's 0...1 slider.
    public func process(_ input: [[Float]], rate: Double, strength: Double) throws -> [[Float]] {
        let frames = input.first?.count ?? 0
        var outputs: [String: [[Float]]] = [:]
        var parameterValues: [String: Double] = [:]
        for id in parameterInputs { parameterValues[id] = strength }

        var pending = Set(nodes.keys)
        while !pending.isEmpty {
            // Next node whose upstream nodes are all done.
            guard let id = pending.sorted().first(where: { id in
                connections.allSatisfy { $0.to != id || !pending.contains($0.from) }
            }) else { break }
            pending.remove(id)
            let node = nodes[id]!
            if node.kind == .transform {
                let value = parameterValues[id] ?? strength
                for c in connections where c.from == id && c.isParameter {
                    parameterValues[c.to] = Self.interpolate(node.curve, at: value)
                }
                continue
            }
            var sum: [[Float]]?
            if audioInputs.contains(id) { sum = input }
            for c in connections where c.to == id && !c.isParameter {
                guard let upstream = outputs[c.from] else { continue }
                sum = sum.map { Self.add($0, upstream) } ?? upstream
            }
            var x = sum ?? Array(repeating: [Float](repeating: 0, count: frames), count: input.count)
            try run(node, on: &x, rate: rate, gainParameter: parameterValues[id])
            outputs[id] = x
        }
        guard let result = outputs[output] else { return input }
        return result
    }

    /// The output of a ParameterChangeTransform that drives a gain node is a normalized gain: CapCut's
    /// presets set the wet gain to 0.4444 at full strength where the stored gain is 0 dB, so unity
    /// maps to 4/9 (and 0 to silence).
    static let unityNormalizedGain = 0.4444

    static func interpolate(_ curve: [(Double, Double)], at x: Double) -> Double {
        guard let first = curve.first, let last = curve.last else { return x }
        if x <= first.0 { return first.1 }
        if x >= last.0 { return last.1 }
        for (a, b) in zip(curve, curve.dropFirst()) where x <= b.0 {
            let t = b.0 > a.0 ? (x - a.0) / (b.0 - a.0) : 1
            return a.1 + t * (b.1 - a.1)
        }
        return last.1
    }

    static func add(_ a: [[Float]], _ b: [[Float]]) -> [[Float]] {
        zip(a, b).map { x, y in
            var out = x
            vDSP_vadd(x, 1, y, 1, &out, 1, vDSP_Length(min(x.count, y.count)))
            return out
        }
    }

    private func run(_ node: Node, on x: inout [[Float]], rate: Double, gainParameter: Double?) throws {
        let p = node.parameters
        func num(_ key: String, _ fallback: Double) -> Double { Self.number(p[key]) ?? fallback }
        func flag(_ key: String) -> Bool { (p[key] as? Bool) ?? ((p[key] as? NSNumber)?.boolValue ?? false) }
        if flag("Bypass") { return }

        switch node.kind {
        case .filter:
            let biquad = Biquad.make(mode: p["Filter Mode"] as? String ?? "", frequency: num("Cutoff Freq", 1000),
                                     gainDB: num("Gain dB", 0), q: num("Resonance", 1), rate: rate)
            for c in x.indices { biquad?.run(&x[c]) }
        case .equaliser:
            for band in 1...16 {
                let key = "Filter \(band) "
                guard p[key + "Mode"] != nil, !flag(key + "Bypass") else { continue }
                let biquad = Biquad.make(mode: p[key + "Mode"] as? String ?? "", frequency: num(key + "Freq", 1000),
                                         gainDB: num(key + "Gain dB", 0), q: num(key + "Q", 1), rate: rate)
                for c in x.indices { biquad?.run(&x[c]) }
            }
            Self.scale(&x, decibels: num("Gain dB", 0))
        case .distortion:
            Distortion(mode: p["Distortion Mode"] as? String ?? "", amount: num("Distortion Amount", 0.5),
                       wetness: num("Wetness", 1), postGainDB: num("Post Distortion Gain", 0)).run(&x)
        case .compressor:
            Compressor(thresholdDB: num("Threshold dB", 0), ratio: num("Ratio", 1), kneeDB: num("Knee dB", 0),
                       attackMs: num("Attack", 1), releaseMs: num("Release", 100),
                       autoMakeup: flag("Auto Makeup Gain"), outputGainDB: num("Output Gain dB", 0)).run(&x, rate: rate)
        case .reverb:
            HallReverb(preDelayMs: num("Pre Delay 1", 0) + num("Pre Delay 2", 0), crossoverHz: num("Crossover", 250),
                       dampingHz: num("HF Damping", 5000), lowRT60: num("Low RT60", 2), midRT60: num("Mid RT60", 2),
                       highRT60: num("Hi RT60", 1), wetMix: num("Wet Mix", 0.5), wetGainDB: num("Wet Gain", 0),
                       width: num("Wet Stereo Width", 1)).run(&x, rate: rate)
        case .gain, .gainAndPan:
            if let normalized = gainParameter {
                var g = Float(normalized / Self.unityNormalizedGain)
                for c in x.indices { vDSP_vsmul(x[c], 1, &g, &x[c], 1, vDSP_Length(x[c].count)) }
            } else {
                Self.scale(&x, decibels: num("Gain dB", 0))
            }
        case .mixer, .transform:
            break
        case .source:
            if let file = node.sourceFile { try Self.loop(file, into: &x, rate: rate) }
        }
    }

    static func scale(_ x: inout [[Float]], decibels: Double) {
        guard decibels != 0 else { return }
        var g = Float(pow(10, decibels / 20))
        if decibels <= -70 { g = 0 }
        for c in x.indices { vDSP_vsmul(x[c], 1, &g, &x[c], 1, vDSP_Length(x[c].count)) }
    }

    /// Fills `x` with the audio file looped from its start, converted to `rate` and x's channel count.
    static func loop(_ url: URL, into x: inout [[Float]], rate: Double) throws {
        let file = try AVAudioFile(forReading: url, commonFormat: .pcmFormatFloat32, interleaved: false)
        let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: rate, channels: AVAudioChannelCount(x.count),
                                   interleaved: false)!
        guard file.length > 0, let converter = AVAudioConverter(from: file.processingFormat, to: format),
              let source = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)) else { return }
        if file.processingFormat.channelCount == 1, x.count > 1 { converter.channelMap = Array(repeating: 0, count: x.count) }
        try file.read(into: source)
        let capacity = AVAudioFrameCount(Double(file.length) * rate / file.processingFormat.sampleRate) + 1024
        guard let converted = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: capacity) else { return }
        var fed = false
        var error: NSError?
        converter.convert(to: converted, error: &error) { _, status in
            if fed { status.pointee = .endOfStream; return nil }
            fed = true
            status.pointee = .haveData
            return source
        }
        if let error { throw error }
        let n = Int(converted.frameLength)
        guard n > 0 else { return }
        for c in x.indices {
            let data = converted.floatChannelData![c]
            for i in x[c].indices { x[c][i] = data[i % n] }
        }
    }
}

// MARK: Processors

extension VoiceFilterGraph {
    /// RBJ cookbook biquad. The presets' "Resonance"/"Q" is used as the filter Q: Old Hollywood's
    /// 750 Hz high-pass and 4 kHz low-pass ("Resonance" 1) match the export best with Q = 1.
    struct Biquad {
        var b0, b1, b2, a1, a2: Double

        static func make(mode: String, frequency: Double, gainDB: Double, q: Double, rate: Double) -> Biquad? {
            let f = min(max(frequency, 1), rate * 0.49)
            let w = 2 * Double.pi * f / rate, cw = cos(w), alpha = sin(w) / (2 * max(q, 0.05))
            let a = pow(10, gainDB / 40)
            let c: (Double, Double, Double, Double, Double, Double)
            switch mode {
            case "LowPass": c = ((1 - cw) / 2, 1 - cw, (1 - cw) / 2, 1 + alpha, -2 * cw, 1 - alpha)
            case "HighPass": c = ((1 + cw) / 2, -(1 + cw), (1 + cw) / 2, 1 + alpha, -2 * cw, 1 - alpha)
            case "BandPass": c = (alpha, 0, -alpha, 1 + alpha, -2 * cw, 1 - alpha)
            case "Notch": c = (1, -2 * cw, 1, 1 + alpha, -2 * cw, 1 - alpha)
            case "Bell", "Peak":
                c = (1 + alpha * a, -2 * cw, 1 - alpha * a, 1 + alpha / a, -2 * cw, 1 - alpha / a)
            case "LowShelf":
                let s = 2 * a.squareRoot() * alpha
                c = (a * ((a + 1) - (a - 1) * cw + s), 2 * a * ((a - 1) - (a + 1) * cw), a * ((a + 1) - (a - 1) * cw - s),
                     (a + 1) + (a - 1) * cw + s, -2 * ((a - 1) + (a + 1) * cw), (a + 1) + (a - 1) * cw - s)
            case "HighShelf":
                let s = 2 * a.squareRoot() * alpha
                c = (a * ((a + 1) + (a - 1) * cw + s), -2 * a * ((a - 1) + (a + 1) * cw), a * ((a + 1) + (a - 1) * cw - s),
                     (a + 1) - (a - 1) * cw + s, 2 * ((a - 1) - (a + 1) * cw), (a + 1) - (a - 1) * cw - s)
            default: return nil
            }
            return Biquad(b0: c.0 / c.3, b1: c.1 / c.3, b2: c.2 / c.3, a1: c.4 / c.3, a2: c.5 / c.3)
        }

        func run(_ x: inout [Float]) {
            var x1 = 0.0, x2 = 0.0, y1 = 0.0, y2 = 0.0
            for i in x.indices {
                let input = Double(x[i])
                let y = b0 * input + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
                x2 = x1; x1 = input; y2 = y1; y1 = y
                x[i] = Float(y)
            }
        }

        /// Magnitude response in dB at `frequency`.
        func response(at frequency: Double, rate: Double) -> Double {
            let w = 2 * Double.pi * frequency / rate
            func mag(_ c0: Double, _ c1: Double, _ c2: Double) -> Double {
                let re = c0 + c1 * cos(w) + c2 * cos(2 * w), im = -(c1 * sin(w) + c2 * sin(2 * w))
                return (re * re + im * im).squareRoot()
            }
            return 20 * log10(mag(b0, b1, b2) / mag(1, a1, a2))
        }
    }

    /// Waveshaper mixed with the dry signal (`wetness`), then `postGainDB`.
    struct Distortion {
        var mode: String
        var amount: Double
        var wetness: Double
        var postGainDB: Double

        /// Drive at `amount` 1 (exponential in between, 1 at amount 0). Calibrated on Old Hollywood
        /// (amount 0.527, so a drive of ~2.3): drives from 3 to 30 at amount 1 all land within 1 dB of the
        /// export's level; 5 fits its spectrum best (more drive adds highs above 5 kHz the export lacks).
        static let maxDrive = 5.0

        var drive: Double { pow(Self.maxDrive, min(max(amount, 0), 1)) }

        func shape(_ v: Double) -> Double {
            let d = drive
            switch mode {
            case "FoldBackSinArctan":
                // The arctangent saturates at ±π/2; sin() folds back once the drive pushes past ±1.
                return sin(2 * atan(d * v))
            case "HardClip": return min(max(d * v, -1), 1)
            default: return tanh(d * v)
            }
        }

        func run(_ x: inout [[Float]]) {
            let wet = min(max(wetness, 0), 1), post = pow(10, postGainDB / 20)
            for c in x.indices {
                for i in x[c].indices {
                    let v = Double(x[c][i])
                    x[c][i] = Float(((1 - wet) * v + wet * shape(v)) * post)
                }
            }
        }
    }

    /// Feed-forward compressor with a soft knee, stereo-linked peak detection and attack/release
    /// smoothing of the gain reduction.
    struct Compressor {
        var thresholdDB, ratio, kneeDB, attackMs, releaseMs: Double
        var autoMakeup: Bool
        var outputGainDB: Double


        func reduction(_ level: Double) -> Double {
            let over = level - thresholdDB, slope = 1 - 1 / max(ratio, 1)
            if kneeDB > 0, abs(over) <= kneeDB / 2 {
                let k = over + kneeDB / 2
                return -slope * k * k / (2 * kneeDB)
            }
            return over > 0 ? -slope * over : 0
        }

        func run(_ x: inout [[Float]], rate: Double) {
            let attack = exp(-1 / max(attackMs / 1000 * rate, 1)), release = exp(-1 / max(releaseMs / 1000 * rate, 1))
            var makeup = outputGainDB
            // Auto makeup gives back the gain reduction a 0 dBFS signal would get (fits Old Hollywood's level).
            if autoMakeup { makeup -= reduction(0) }
            var g = 0.0
            let frames = x.first?.count ?? 0
            for i in 0..<frames {
                var peak: Float = 0
                for c in x.indices { peak = max(peak, abs(x[c][i])) }
                let target = reduction(20 * log10(max(Double(peak), 1e-9)))
                g = target < g ? attack * g + (1 - attack) * target : release * g + (1 - release) * target
                let gain = Float(pow(10, (g + makeup) / 20))
                for c in x.indices { x[c][i] *= gain }
            }
        }
    }

    /// Our own hall: pre-delay, input diffusion, and an 8-line feedback delay network with
    /// three-band (low/mid/high) decay set from the preset's RT60s. `wetMix` crossfades dry and wet.
    struct HallReverb {
        var preDelayMs, crossoverHz, dampingHz, lowRT60, midRT60, highRT60, wetMix, wetGainDB, width: Double

        /// Delay line lengths (ms) — mutually prime-ish, spread over a hall-sized range.
        static let lineMs: [Double] = [31.3, 37.9, 43.1, 49.7, 56.3, 63.1, 71.9, 79.3]
        static let diffuserMs: [Double] = [4.7, 7.1, 11.3, 15.7]
        /// FDN output level, calibrated on Live House: in the export the tail starts ~12 dB below the
        /// speech in 1–8 kHz (and the whole clip is ~6 dB below the unprocessed voice, i.e. the dry half).
        static let wetLevel = 0.12
        /// Where the high band (decaying at "Hi RT60") starts, relative to "HF Damping": the export's
        /// tail decays at ~1.3 s from 1 kHz up, not just above the 3.5 kHz damping frequency.
        static let highBandShare = 0.4

        func run(_ x: inout [[Float]], rate: Double) {
            let frames = x.first?.count ?? 0
            guard frames > 0 else { return }
            let channels = x.count
            // Mono input with pre-delay.
            let pre = Int(preDelayMs / 1000 * rate)
            var input = [Double](repeating: 0, count: frames)
            for c in 0..<channels { for i in 0..<frames - min(pre, frames) { input[i + pre] += Double(x[c][i]) / Double(channels) } }
            // Input diffusion: series all-passes.
            for ms in Self.diffuserMs {
                let n = max(1, Int(ms / 1000 * rate)), g = 0.6
                var buffer = [Double](repeating: 0, count: n)
                var j = 0
                for i in 0..<frames {
                    let delayed = buffer[j], v = input[i] + g * delayed
                    buffer[j] = v
                    input[i] = delayed - g * v
                    j = (j + 1) % n
                }
            }
            // FDN with a Householder feedback matrix.
            let lengths = Self.lineMs.map { max(1, Int($0 / 1000 * rate)) }
            let n = lengths.count
            var lines = lengths.map { [Double](repeating: 0, count: $0) }
            var positions = [Int](repeating: 0, count: n)
            func gain(_ rt60: Double, _ length: Int) -> Double { pow(10, -3 * Double(length) / rate / max(rt60, 0.05)) }
            let gLow = lengths.map { gain(lowRT60, $0) }, gMid = lengths.map { gain(midRT60, $0) }, gHigh = lengths.map { gain(highRT60, $0) }
            let kLow = 1 - exp(-2 * Double.pi * crossoverHz / rate), kHigh = 1 - exp(-2 * Double.pi * dampingHz * Self.highBandShare / rate)
            var lowState = [Double](repeating: 0, count: n), highState = [Double](repeating: 0, count: n)
            var outL = [Float](repeating: 0, count: frames), outR = [Float](repeating: 0, count: frames)
            var taps = [Double](repeating: 0, count: n)
            let level = Self.wetLevel * pow(10, wetGainDB / 20)
            for i in 0..<frames {
                var sum = 0.0
                for k in 0..<n {
                    let v = lines[k][positions[k]]
                    // Split into bands with one-pole filters and decay each band at its own rate.
                    lowState[k] += kLow * (v - lowState[k])
                    highState[k] += kHigh * (v - highState[k])
                    let low = lowState[k], high = v - highState[k], mid = v - low - high
                    taps[k] = gLow[k] * low + gMid[k] * mid + gHigh[k] * high
                    sum += taps[k]
                }
                let reflect = 2 / Double(n) * sum
                var l = 0.0, r = 0.0
                for k in 0..<n {
                    lines[k][positions[k]] = input[i] + taps[k] - reflect
                    positions[k] = (positions[k] + 1) % lengths[k]
                    if k % 2 == 0 { l += taps[k] } else { r += taps[k] }
                }
                outL[i] = Float(l * level)
                outR[i] = Float(r * level)
            }
            let mix = min(max(wetMix, 0), 1), spread = min(max(width, 0), 1)
            for c in 0..<channels {
                for i in 0..<frames {
                    let a = Double(c == 0 ? outL[i] : outR[i]), b = Double(c == 0 ? outR[i] : outL[i])
                    let wet = channels > 1 ? a * (1 + spread) / 2 + b * (1 - spread) / 2 : (a + b) / 2
                    x[c][i] = Float((1 - mix) * Double(x[c][i]) + mix * wet)
                }
            }
        }
    }
}
