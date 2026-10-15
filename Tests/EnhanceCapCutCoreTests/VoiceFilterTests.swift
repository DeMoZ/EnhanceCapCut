import AVFoundation
import XCTest
@testable import EnhanceCapCutCore

final class VoiceFilterGraphTests: XCTestCase {
    private let rate = 48_000.0

    /// Writes a preset in CapCut's mdsp layout (our own minimal graph, not a CapCut file): the input
    /// goes through `chain` into a wet gain and straight into a dry gain, both driven by "strength".
    private func preset(_ chain: [[String: Any]], extraNodes: [[String: Any]] = [],
                        extraConnections: [[String: Any]] = []) throws -> URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("ecc-filter-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: dir) }
        func node(_ id: String, _ name: String, _ state: [String: Any]) -> [String: Any] {
            ["node_id": id, "node_type": "processor_node", "processor_name": name, "processor_state": state]
        }
        func audio(_ from: String, _ to: String, _ port: Int = 0) -> [String: Any] {
            ["upstream_node_id": from, "upstream_port_index": 0, "downstream_node_id": to,
             "downstream_port_index": port, "port_type": "audio"]
        }
        var nodes = chain.enumerated().map { i, n in node("p\(i)", n["name"] as! String, ["parameters": n["parameters"] ?? [:]]) }
        nodes += [
            node("dry_transform", "ParameterChangeTransform", ["transforms": [["input_parameter_index": 0, "transforms": [
                ["output_parameter_index": 0, "transform_curve": [[0.0, 0.4444], [0.5, 0.3715], [1.0, 0.0]]]]]]]),
            node("wet_transform", "ParameterChangeTransform", ["transforms": [["input_parameter_index": 0, "transforms": [
                ["output_parameter_index": 0, "transform_curve": [[0.0, 0.0], [0.5, 0.3715], [1.0, 0.4444]]]]]]]),
            node("gain_dry", "GainAndPan", ["parameters": ["Gain dB": -70.0, "Pan": 0.0]]),
            node("gain_wet", "Gain", ["parameters": ["Gain dB": 0.0]]),
            node("mixer", "Mixer", ["parameters": [:]]),
        ] + extraNodes
        var connections = (0..<chain.count).dropFirst().map { audio("p\($0 - 1)", "p\($0)") }
        connections += [audio("p\(chain.count - 1)", "gain_wet"), audio("gain_wet", "mixer", 1), audio("gain_dry", "mixer", 0)]
        connections += [["upstream_node_id": "dry_transform", "downstream_node_id": "gain_dry", "port_type": "parameter_change"],
                        ["upstream_node_id": "wet_transform", "downstream_node_id": "gain_wet", "port_type": "parameter_change"]]
        connections += extraConnections
        let root: [String: Any] = [
            "node_id": "processor_graph", "nodes": nodes, "connections": connections,
            "ports": [
                ["internal_node_id": "p0", "port_direction": "input", "port_index": 0, "port_type": "audio"],
                ["internal_node_id": "gain_dry", "port_direction": "input", "port_index": 0, "port_type": "audio"],
                ["internal_node_id": "dry_transform", "port_direction": "input", "port_type": "parameter_change"],
                ["internal_node_id": "wet_transform", "port_direction": "input", "port_type": "parameter_change"],
                ["internal_node_id": "mixer", "port_direction": "output", "port_index": 0, "port_type": "audio"],
            ],
        ]
        try JSONSerialization.data(withJSONObject: root).write(to: dir.appendingPathComponent("mdsp_preset.json"))
        return dir
    }

    private func sine(_ frequency: Double, seconds: Double = 1, amplitude: Float = 0.1) -> [[Float]] {
        let x = (0..<Int(rate * seconds)).map { amplitude * Float(sin(2 * Double.pi * frequency * Double($0) / rate)) }
        return [x, x]
    }

    private func rms(_ x: [Float], from: Int = 0) -> Double {
        let tail = x[from...]
        return 20 * log10((tail.reduce(0) { $0 + Double($1 * $1) } / Double(max(tail.count, 1))).squareRoot() + 1e-12)
    }

    func testStrengthCrossfadesDryAndWet() throws {
        let dir = try preset([["name": "Gain", "parameters": ["Gain dB": -12.0]]])
        let graph = try VoiceFilterGraph.load(directory: dir)
        let input = sine(1000)
        // Full strength: only the wet path (−12 dB); zero strength: only the dry path (unity).
        XCTAssertEqual(rms(try graph.process(input, rate: rate, strength: 1)[0]) - rms(input[0]), -12, accuracy: 0.1)
        XCTAssertEqual(rms(try graph.process(input, rate: rate, strength: 0)[1]) - rms(input[1]), 0, accuracy: 0.1)
    }

    func testFiltersFollowTheCookbookResponse() throws {
        let dir = try preset([
            ["name": "Filter", "parameters": ["Cutoff Freq": 80.0, "Filter Mode": "Bell", "Gain dB": 10.0, "Resonance": 1.0]],
            ["name": "Equaliser", "parameters": ["Filter 1 Mode": "HighPass", "Filter 1 Freq": 750.0, "Filter 1 Q": 1.0,
                                                 "Filter 1 Bypass": false, "Filter 2 Mode": "LowPass", "Filter 2 Freq": 10.0,
                                                 "Filter 2 Bypass": true, "Gain dB": 0.0]],
        ])
        let graph = try VoiceFilterGraph.load(directory: dir)
        let bell = VoiceFilterGraph.Biquad.make(mode: "Bell", frequency: 80, gainDB: 10, q: 1, rate: rate)!
        let highPass = VoiceFilterGraph.Biquad.make(mode: "HighPass", frequency: 750, gainDB: 0, q: 1, rate: rate)!
        XCTAssertEqual(bell.response(at: 80, rate: rate), 10, accuracy: 0.01)
        XCTAssertEqual(highPass.response(at: 750, rate: rate), 0, accuracy: 0.01) // Q 1 peaks at 0 dB at the corner
        XCTAssertEqual(highPass.response(at: 375, rate: rate), -11.2, accuracy: 0.2)
        for f in [100.0, 400, 2000] {
            let input = sine(f)
            let out = try graph.process(input, rate: rate, strength: 1)[0]
            let skip = Int(rate / 4)
            let expected = bell.response(at: f, rate: rate) + highPass.response(at: f, rate: rate)
            XCTAssertEqual(rms(out, from: skip) - rms(input[0], from: skip), expected, accuracy: 0.3, "at \(f) Hz")
        }
    }

    func testUnknownProcessorIsRejected() throws {
        let dir = try preset([["name": "PitchShifter", "parameters": [:]]])
        XCTAssertThrowsError(try VoiceFilterGraph.load(directory: dir))
        XCTAssertThrowsError(try VoiceFilterGraph.load(directory: dir.appendingPathComponent("missing")))
    }

    func testReverbTailDecaysAtTheMidRT60() throws {
        let dir = try preset([["name": "StudioReverb", "parameters": [
            "Reverb Mode": "Hall", "Pre Delay 1": 25.0, "Crossover": 250.0, "HF Damping": 8000.0,
            "Low RT60": 1.0, "Mid RT60": 1.0, "Hi RT60": 1.0, "Wet Mix": 1.0, "Wet Gain": 0.0, "Wet Stereo Width": 1.0,
        ]]])
        let graph = try VoiceFilterGraph.load(directory: dir)
        // A 100 ms noise burst, then silence: the tail should fall ~60 dB per RT60.
        var input = [[Float]](repeating: [Float](repeating: 0, count: Int(rate * 2)), count: 2)
        for (i, v) in VoiceSignal.noise(count: Int(rate / 10), seed: 1).enumerated() {
            input[0][i] = 0.1 * v
            input[1][i] = 0.1 * v
        }
        let out = try graph.process(input, rate: rate, strength: 1)[0]
        func level(_ t: Double) -> Double { rms(Array(out[Int(t * rate)..<Int((t + 0.1) * rate)])) }
        let decayPerSecond = (level(0.4) - level(0.9)) / 0.5
        XCTAssertEqual(decayPerSecond, 60, accuracy: 12)
    }

    func testCompressorAutoMakeupKeepsQuietSignalsLouder() throws {
        let dir = try preset([["name": "Compressor", "parameters": [
            "Threshold dB": -12.0, "Ratio": 4.0, "Knee dB": 0.0, "Attack": 1.0, "Release": 50.0,
            "Auto Makeup Gain": true, "Output Gain dB": 0.0,
        ]]])
        let graph = try VoiceFilterGraph.load(directory: dir)
        // Below the threshold only the makeup applies: 12 × (1 − 1/4) = 9 dB.
        let quiet = sine(1000, amplitude: 0.05)
        XCTAssertEqual(rms(try graph.process(quiet, rate: rate, strength: 1)[0]) - rms(quiet[0]), 9, accuracy: 0.1)
    }

    func testLoopingSourceIsMixedIn() throws {
        let dir = try preset([["name": "Gain", "parameters": ["Gain dB": 0.0]]],
                             extraNodes: [["node_id": "noise", "processor_name": "Source", "processor_state": [
                                 "sequenced_clip": ["clip": ["audio_file_uri": "file://tone.caf", "is_looping": true]]]],
                                          ["node_id": "noise_gain", "processor_name": "Gain",
                                           "processor_state": ["parameters": ["Gain dB": -6.0]]]],
                             extraConnections: [
                                ["upstream_node_id": "noise", "downstream_node_id": "noise_gain", "port_type": "audio"],
                                ["upstream_node_id": "noise_gain", "downstream_node_id": "gain_wet", "port_type": "audio"],
                             ])
        // A 0.1 s, 44.1 kHz tone file that the graph must loop and resample.
        let tone = (0..<4410).map { Float(0.5 * sin(2 * Double.pi * 441 * Double($0) / 44_100)) }
        let file = try AVAudioFile(forWriting: dir.appendingPathComponent("tone.caf"), settings: [
            AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: 44_100, AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 32, AVLinearPCMIsFloatKey: true,
        ], commonFormat: .pcmFormatFloat32, interleaved: false)
        let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(tone.count))!
        buffer.frameLength = AVAudioFrameCount(tone.count)
        tone.withUnsafeBufferPointer { buffer.floatChannelData![0].update(from: $0.baseAddress!, count: tone.count) }
        try file.write(from: buffer)
        file.close()
        let graph = try VoiceFilterGraph.load(directory: dir)
        let silence = [[Float]](repeating: [Float](repeating: 0, count: Int(rate)), count: 2)
        let out = try graph.process(silence, rate: rate, strength: 1)
        XCTAssertEqual(rms(out[1], from: Int(rate / 2)), 20 * log10(0.5 / 2.squareRoot()) - 6, accuracy: 0.3)
    }
}

final class VoiceFilterParsingTests: XCTestCase {
    func testAudioEffectsBecomeFiltersOrAreReported() throws {
        let dir = try VoiceSignal.tempDir()
        defer { try? FileManager.default.removeItem(at: dir) }
        let media = dir.appendingPathComponent("a.caf")
        try VoiceSignal.write([Float](repeating: 0, count: 4_800), to: media)
        // A cache with one usable preset (found by resource id) and one using an unknown processor.
        let cache = dir.appendingPathComponent("cache")
        let good = cache.appendingPathComponent("111/hash")
        let bad = cache.appendingPathComponent("222/hash")
        for (folder, processor) in [(good, "Gain"), (bad, "PitchShifter")] {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let preset: [String: Any] = [
                "nodes": [["node_id": "p", "processor_name": processor, "processor_state": ["parameters": ["Gain dB": -6.0]]]],
                "connections": [],
                "ports": [["internal_node_id": "p", "port_direction": "input", "port_type": "audio"],
                          ["internal_node_id": "p", "port_direction": "output", "port_type": "audio"]],
            ]
            try JSONSerialization.data(withJSONObject: preset).write(to: folder.appendingPathComponent("mdsp_preset.json"))
        }
        func path(_ url: URL?) -> String? { url?.resolvingSymlinksInPath().path }
        XCTAssertEqual(path(VoiceFilter.locate(path: nil, resourceID: "111", cacheRoots: [cache])), path(good))
        XCTAssertEqual(path(VoiceFilter.locate(path: cache.appendingPathComponent("111"), resourceID: "")), path(good))
        XCTAssertNil(VoiceFilter.locate(path: nil, resourceID: "333", cacheRoots: [cache]))

        let json = """
        {
          "materials": {
            "audios": [{"id": "A", "path": "\(media.path)", "type": "record"}],
            "audio_effects": [
              {"id": "FX-GOOD", "type": "audio_effect", "name": "Booming", "path": "\(good.path)", "resource_id": "111",
               "category_name": "Voice filters",
               "audio_adjust_params": [{"name": "strength", "value": "0.5", "default_value": 1.0}]},
              {"id": "FX-BAD", "name": "Robot", "path": "\(bad.path)", "resource_id": "222"},
              {"id": "FX-MISSING", "name": "Old Hollywood", "path": "/nonexistent/effect", "resource_id": null}
            ]
          },
          "tracks": [{"type": "audio", "segments": [
            {"material_id": "A", "extra_material_refs": ["FX-GOOD"], "target_timerange": {"start": 0, "duration": 100000}},
            {"material_id": "A", "extra_material_refs": ["FX-BAD"], "target_timerange": {"start": 100000, "duration": 100000}},
            {"material_id": "A", "extra_material_refs": ["FX-MISSING"], "target_timerange": {"start": 200000, "duration": 100000}}
          ]}]
        }
        """
        let draft = try JSONDecoder().decode(Draft.self, from: Data(json.utf8))
        XCTAssertEqual(draft.materials.audioEffects.map(\.strength), [0.5, 1, 1])
        XCTAssertEqual(draft.materials.audioEffects.map(\.resourceID), ["111", "222", ""])
        let timeline = TimelineBuilder.build(draft: draft, projectURL: dir)
        let filters = timeline.audioClips.sorted { $0.target.start < $1.target.start }.map(\.voice.filter)
        XCTAssertEqual(filters.map { $0?.name }, ["Booming", nil, nil])
        XCTAssertEqual(filters.first??.strength, 0.5)
        XCTAssertEqual(path(filters.first??.directory), path(good))
        let reported = timeline.report.unsupported.filter { $0.category == "Audio effect" }.map(\.name).sorted()
        XCTAssertEqual(reported, ["Old Hollywood", "Robot"])
    }

    func testVoiceProcessorAppliesTheFilterFromTheClipStart() async throws {
        let dir = try VoiceSignal.tempDir()
        defer { try? FileManager.default.removeItem(at: dir) }
        let media = dir.appendingPathComponent("a.caf")
        let sr = VoiceProcessor.sampleRate
        try VoiceSignal.write((0..<Int(sr * 3)).map { Float(0.2 * sin(2 * Double.pi * 500 * Double($0) / sr)) }, to: media)
        let preset = dir.appendingPathComponent("fx")
        try FileManager.default.createDirectory(at: preset, withIntermediateDirectories: true)
        try JSONSerialization.data(withJSONObject: [
            "nodes": [["node_id": "p", "processor_name": "Gain", "processor_state": ["parameters": ["Gain dB": -20.0]]]],
            "connections": [],
            "ports": [["internal_node_id": "p", "port_direction": "input", "port_type": "audio"],
                      ["internal_node_id": "p", "port_direction": "output", "port_type": "audio"]],
        ] as [String: Any]).write(to: preset.appendingPathComponent("mdsp_preset.json"))

        let clip = AudioClip(url: media, target: TimeRange(start: 0, duration: 1_000_000),
                             timeMap: TimeMap(sourceStart: 1_500_000, sourceDuration: 1_000_000, targetDuration: 1_000_000),
                             volume: 1, fadeIn: 0, fadeOut: 0, preservePitch: true,
                             voice: VoiceEffects(filter: VoiceFilter(name: "Quiet", directory: preset)))
        let processor = VoiceProcessor(directory: dir.appendingPathComponent("work"))
        defer { processor.removeFiles() }
        let output = try await processor.process(clip, isCancelled: { false })
        let result = try XCTUnwrap(output)
        let samples = try VoiceSignal.read(result.url)
        func level(atSource us: Int64) -> Float {
            let i = Int(Double(us - result.sourceOffset) / 1_000_000 * sr)
            return VoiceSignal.rms(samples[i..<(i + 4_800)])
        }
        // Before the clip (pre-roll) untouched, inside it 20 dB down.
        XCTAssertEqual(20 * log10(level(atSource: 1_000_000) / level(atSource: 2_000_000)), 20, accuracy: 0.1)
    }
}
