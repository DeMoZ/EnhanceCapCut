import AVFoundation
import XCTest
@testable import EnhanceCapCutCore

final class VoiceOverWriterTests: XCTestCase {
    private let project = URL(fileURLWithPath: "/Projects/Demo")
    private let placeholder = "##_draftpath_placeholder_0E685133-18CE-45ED-8CB8-2904A212EC80_##"

    /// A video clip over the whole 10 s, a music clip from 6 s, and one with volume keyframes of its own.
    private var draft: String {
        """
        {"id":"D","fps":30.0,"duration":10000000,"tracks":[\
        {"id":"V","type":"video","flag":0,"attribute":0,"segments":[{"id":"S1","material_id":"M1",\
        "target_timerange":{"start":0,"duration":10000000},"volume":0.5,"common_keyframes":[]}]},\
        {"id":"A","type":"audio","flag":0,"attribute":0,"segments":[{"id":"S2","material_id":"M2",\
        "target_timerange":{"start":6000000,"duration":4000000},"volume":1.0}]},\
        {"id":"B","type":"audio","flag":0,"attribute":0,"segments":[{"id":"S3","material_id":"M3",\
        "target_timerange":{"start":0,"duration":4000000},"volume":1.0,"common_keyframes":[{"id":"K",\
        "property_type":"KFTypeVolume","keyframe_list":[{"time_offset":0,"values":[1.0]}]}]}]}],\
        "materials":{"videos":[{"id":"M1","path":"\(placeholder)/clip.mp4"}],"audios":[],"speeds":[]}}
        """
    }

    private func tracks() -> [VoiceOverTrack] {
        let url = project.appendingPathComponent("EnhanceCapCut/voiceover 1/en.wav")
        return [
            VoiceOverTrack(language: SubtitleLanguage(Locale.Language(identifier: "en")), url: url, fileDuration: 10,
                           lines: [1...2.5, 3...4], isAudible: true),
            VoiceOverTrack(language: SubtitleLanguage(Locale.Language(identifier: "th")),
                           url: URL(fileURLWithPath: "/elsewhere/th.wav"), fileDuration: 10, lines: [1...2], isAudible: false),
        ]
    }

    private func json(_ data: Data) throws -> [String: Any] {
        try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    private func segments(_ d: [String: Any], track: Int) throws -> [[String: Any]] {
        let tracks = try XCTUnwrap(d["tracks"] as? [[String: Any]])
        return try XCTUnwrap(tracks[track]["segments"] as? [[String: Any]])
    }

    private func volumeKeyframes(_ segment: [String: Any]) -> [[String: Any]] {
        (segment["common_keyframes"] as? [[String: Any]] ?? []).filter { $0["property_type"] as? String == "KFTypeVolume" }
    }

    /// Replace voice: the original is muted like Mute and a background track (always audible) fills in.
    func testReplaceAddsAnAudibleBackgroundAndMutesTheOriginal() throws {
        var all = tracks()
        all.append(VoiceOverTrack(language: all[0].language, url: project.appendingPathComponent("EnhanceCapCut/voiceover 1/background.wav"),
                                  fileDuration: 10, lines: [0.75...4.4], isAudible: true, isBackground: true))
        let out = try DraftVoiceOverWriter.apply(all, to: Data(draft.utf8), original: .replace, replacePrevious: true,
                                                 projectURL: project)
        let d = try json(out)
        let tracks = try XCTUnwrap(d["tracks"] as? [[String: Any]])
        XCTAssertEqual(tracks.count, 6)
        XCTAssertEqual(tracks[5]["name"] as? String, "EnhanceCapCut voice-over · background")
        XCTAssertEqual(tracks[5]["attribute"] as? Int, 0)
        // Ducked to silence under the voice's lines, not the background's.
        let video = volumeKeyframes(try segments(d, track: 0)[0])
        let values = (video.first?["keyframe_list"] as? [[String: Any]] ?? []).compactMap { ($0["values"] as? [Double])?.first }
        XCTAssertEqual(values, [0.5, 0, 0, 0.5])
        // A later run takes the background away too.
        let cleaned = try json(try DraftVoiceOverWriter.withoutVoiceOver(out))
        XCTAssertEqual((cleaned["tracks"] as? [Any])?.count, 3)
    }

    func testBackgroundGainCrossFadesWithTheDucking() {
        let r: Range<Int64> = 1_000_000..<2_000_000
        XCTAssertEqual(DraftVoiceOverWriter.backgroundGain(at: 500_000, range: r), 0)
        XCTAssertEqual(DraftVoiceOverWriter.backgroundGain(at: 875_000, range: r), 0.5, accuracy: 1e-9)
        XCTAssertEqual(DraftVoiceOverWriter.backgroundGain(at: 1_500_000, range: r), 1)
        XCTAssertEqual(DraftVoiceOverWriter.backgroundGain(at: 2_200_000, range: r), 0.5, accuracy: 1e-9)
        XCTAssertEqual(DraftVoiceOverWriter.backgroundGain(at: 2_400_000, range: r), 0)
    }

    func testAddsAudioTracksWithAClipPerLine() throws {
        let out = try DraftVoiceOverWriter.apply(tracks(), to: Data(draft.utf8), original: .keep, replacePrevious: true,
                                                 projectURL: project)
        let d = try json(out)
        let all = try XCTUnwrap(d["tracks"] as? [[String: Any]])
        XCTAssertEqual(all.count, 5)
        XCTAssertEqual(all[3]["type"] as? String, "audio")
        XCTAssertEqual(all[3]["attribute"] as? Int, 0)
        XCTAssertEqual(all[4]["attribute"] as? Int, 1)  // muted
        XCTAssertEqual(all[3]["name"] as? String, "EnhanceCapCut voice-over · en")

        let lines = try segments(d, track: 3)
        XCTAssertEqual(lines.count, 2)
        let target = try XCTUnwrap(lines[0]["target_timerange"] as? [String: Any])
        XCTAssertEqual(target["start"] as? Int, 1_000_000)
        XCTAssertEqual(target["duration"] as? Int, 1_500_000)
        XCTAssertEqual(lines[0]["source_timerange"] as? [String: Int], ["start": 1_000_000, "duration": 1_500_000])
        XCTAssertEqual(lines[0]["track_render_index"] as? Int, 3)
        XCTAssertEqual((lines[0]["extra_material_refs"] as? [String])?.count, 5)

        let materials = try XCTUnwrap(d["materials"] as? [String: Any])
        let audios = try XCTUnwrap(materials["audios"] as? [[String: Any]])
        XCTAssertEqual(audios.count, 3)
        let first = try XCTUnwrap(audios.first { $0["id"] as? String == lines[0]["material_id"] as? String })
        XCTAssertEqual(first["type"] as? String, "record")
        XCTAssertEqual(first["duration"] as? Int, 10_000_000)
        // Inside the project: written with the draft's placeholder; elsewhere: absolute.
        XCTAssertEqual(first["path"] as? String, placeholder + "/EnhanceCapCut/voiceover 1/en.wav")
        XCTAssertTrue(audios.contains { $0["path"] as? String == "/elsewhere/th.wav" })
        XCTAssertEqual((materials["speeds"] as? [Any])?.count, 3)
        XCTAssertEqual((materials["vocal_separations"] as? [Any])?.count, 3)

        // Keep: nothing else changes.
        XCTAssertTrue(volumeKeyframes(try segments(d, track: 0)[0]).isEmpty)
        let text = String(decoding: out, as: UTF8.self)
        XCTAssertTrue(text.hasPrefix(#"{"id":"D","fps":30.0,"duration":10000000,"tracks":[{"id":"V","type":"video""#))
    }

    func testDucksOtherAudioWhileTheVoiceSpeaks() throws {
        let out = try DraftVoiceOverWriter.apply(tracks(), to: Data(draft.utf8), original: .lower, replacePrevious: true,
                                                 projectURL: project)
        let d = try json(out)
        // The two lines (1–2.5 s, 3–4 s) are less than the ramps apart, so they duck as one: 1–4 s.
        let video = try XCTUnwrap(volumeKeyframes(try segments(d, track: 0)[0]).first)
        XCTAssertTrue((video["id"] as? String)?.hasPrefix(DraftVoiceOverWriter.idPrefix) == true)
        let list = try XCTUnwrap(video["keyframe_list"] as? [[String: Any]])
        XCTAssertEqual(list.map { $0["time_offset"] as? Int }, [750_000, 1_000_000, 4_000_000, 4_400_000])
        let values = list.map { ($0["values"] as? [Double])?.first ?? -1 }
        XCTAssertEqual(values[0], 0.5)
        XCTAssertEqual(values[1], 0.5 * DraftVoiceOverWriter.lowerGain, accuracy: 1e-9)
        XCTAssertEqual(values[3], 0.5)

        // Music from 6 s: no speech there. The clip with its own volume keyframes is left alone.
        XCTAssertTrue(volumeKeyframes(try segments(d, track: 1)[0]).isEmpty)
        XCTAssertEqual(volumeKeyframes(try segments(d, track: 2)[0]).map { $0["id"] as? String }, ["K"])
        // Voice-over tracks aren't ducked.
        XCTAssertTrue(volumeKeyframes(try segments(d, track: 3)[0]).isEmpty)
    }

    func testReplacesPreviousRun() throws {
        let once = try DraftVoiceOverWriter.apply(tracks(), to: Data(draft.utf8), original: .mute, replacePrevious: true,
                                                  projectURL: project)
        let twice = try DraftVoiceOverWriter.apply(tracks(), to: once, original: .mute, replacePrevious: true,
                                                   projectURL: project)
        let d = try json(twice)
        XCTAssertEqual((d["tracks"] as? [Any])?.count, 5)
        let materials = try XCTUnwrap(d["materials"] as? [String: Any])
        XCTAssertEqual((materials["audios"] as? [Any])?.count, 3)
        XCTAssertEqual((materials["beats"] as? [Any])?.count, 3)
        let video = volumeKeyframes(try segments(d, track: 0)[0])
        XCTAssertEqual(video.count, 1)
        XCTAssertEqual(((video[0]["keyframe_list"] as? [[String: Any]])?[1]["values"] as? [Double])?.first, 0)

        // Keep the original sound this time: the earlier ducking goes too.
        let kept = try DraftVoiceOverWriter.apply(tracks(), to: once, original: .keep, replacePrevious: true, projectURL: project)
        XCTAssertTrue(volumeKeyframes(try segments(try json(kept), track: 0)[0]).isEmpty)
    }

    /// A video clip with its own (off) vocal separation, a compound clip, CapCut music and an extracted sound, all under
    /// the voice, and a clip after it.
    private var vocalDraft: String {
        """
        {"id":"D","duration":10000000,"tracks":[\
        {"type":"video","attribute":0,"segments":[{"id":"S1","material_id":"M1","extra_material_refs":["VS1"],\
        "target_timerange":{"start":0,"duration":5000000},"source_timerange":{"start":2000000,"duration":5000000},\
        "volume":1.0},{"id":"S2","material_id":"M1","extra_material_refs":[],\
        "target_timerange":{"start":6000000,"duration":4000000},"volume":1.0}]},\
        {"type":"video","attribute":0,"segments":[{"id":"S3","material_id":"M1","extra_material_refs":["C1"],\
        "target_timerange":{"start":0,"duration":5000000},"volume":1.0}]},\
        {"type":"audio","attribute":0,"segments":[{"id":"S4","material_id":"A1",\
        "target_timerange":{"start":0,"duration":5000000},"volume":1.0},{"id":"S5","material_id":"A2",\
        "target_timerange":{"start":5000000,"duration":1000000},"volume":1.0}]}],\
        "materials":{"videos":[{"id":"M1","type":"video","duration":20000000}],\
        "audios":[{"id":"A1","type":"music","duration":5000000},{"id":"A2","type":"extract_music","duration":1000000}],\
        "drafts":[{"id":"C1"}],"vocal_separations":[{"id":"VS1","type":"vocal_separation","choice":0}]}}
        """
    }

    private func separation(_ d: [String: Any], of segment: [String: Any]) throws -> [String: Any]? {
        let all = try XCTUnwrap((d["materials"] as? [String: Any])?["vocal_separations"] as? [[String: Any]])
        let refs = segment["extra_material_refs"] as? [String] ?? []
        return all.first { refs.contains($0["id"] as? String ?? "") }
    }

    /// Remove vocal: CapCut's "Remove vocal" on the clips under the voice, muting where CapCut can't separate.
    func testRemoveVocalTurnsOnCapCutsSeparation() throws {
        let line = [VoiceOverTrack(language: tracks()[0].language, url: tracks()[0].url, fileDuration: 10,
                                   lines: [1...5.5], isAudible: true)]
        let once = try DraftVoiceOverWriter.apply(line, to: Data(vocalDraft.utf8), original: .removeVocal,
                                                  replacePrevious: true, projectURL: project)
        var d = try json(once)
        let s1 = try segments(d, track: 0)[0], s2 = try segments(d, track: 0)[1]
        let own = try XCTUnwrap(separation(d, of: s1))
        XCTAssertTrue((own["id"] as? String)?.hasPrefix(DraftVoiceOverWriter.idPrefix) == true)
        XCTAssertEqual(own["choice"] as? Int, 1)
        XCTAssertEqual(own["final_algorithm"] as? String, "vocal_separate")
        XCTAssertEqual((own["time_range"] as? [String: Any])?["start"] as? Int, 2_000_000)
        XCTAssertTrue(volumeKeyframes(s1).isEmpty)
        // The clip's own entry stays, unchanged, for whatever else refers to it.
        let all = try XCTUnwrap((d["materials"] as? [String: Any])?["vocal_separations"] as? [[String: Any]])
        XCTAssertEqual(all.first { $0["id"] as? String == "VS1" }?["choice"] as? Int, 0)
        // No speech over the clip from 6 s.
        XCTAssertNil(try separation(d, of: s2))
        // Compound clips and CapCut music can't be separated: muted instead.
        for (track, index) in [(1, 0), (2, 0)] {
            let segment = try segments(d, track: track)[index]
            XCTAssertNil(try separation(d, of: segment))
            XCTAssertEqual(volumeKeyframes(segment).count, 1)
        }
        // A segment without separation material gets one.
        let s5 = try segments(d, track: 2)[1]
        XCTAssertEqual(try separation(d, of: s5)?["choice"] as? Int, 1)
        XCTAssertTrue(volumeKeyframes(s5).isEmpty)

        // Running again reuses our entries; keeping the original sound turns them off.
        let twice = try DraftVoiceOverWriter.apply(line, to: once, original: .removeVocal, replacePrevious: true,
                                                   projectURL: project)
        d = try json(twice)
        let ours = { (d: [String: Any]) in
            ((d["materials"] as? [String: Any])?["vocal_separations"] as? [[String: Any]] ?? [])
                .filter { ($0["id"] as? String)?.hasPrefix(DraftVoiceOverWriter.idPrefix) == true }
        }
        XCTAssertEqual(ours(d).map { $0["choice"] as? Int }, [1, 1])
        XCTAssertEqual(try separation(d, of: try segments(d, track: 0)[0])?["id"] as? String, own["id"] as? String)
        let kept = try json(try DraftVoiceOverWriter.apply(line, to: twice, original: .keep, replacePrevious: true,
                                                           projectURL: project))
        XCTAssertEqual(ours(kept).map { $0["choice"] as? Int }, [0, 0])
        XCTAssertTrue(volumeKeyframes(try segments(kept, track: 1)[0]).isEmpty)
        let restored = try json(try DraftVoiceOverWriter.withoutVoiceOver(twice))
        XCTAssertEqual(ours(restored).map { $0["choice"] as? Int }, [0, 0])
    }

    func testDuckingReachesTheTimeline() throws {
        let media = FileManager.default.temporaryDirectory.appendingPathComponent("ecc-duck-\(UUID().uuidString).m4a")
        try Data([0]).write(to: media)
        defer { try? FileManager.default.removeItem(at: media) }
        let source = """
            {"duration":10000000,"tracks":[{"type":"audio","segments":[{"id":"S","material_id":"A",\
            "target_timerange":{"start":0,"duration":10000000},"volume":0.5}]}],\
            "materials":{"audios":[{"id":"A","path":"\(media.path)"}]}}
            """
        let ducked = try DraftVoiceOverWriter.apply([tracks()[0]], to: Data(source.utf8), original: .lower,
                                                    replacePrevious: true)
        let timeline = TimelineBuilder.build(draft: try JSONDecoder().decode(Draft.self, from: ducked),
                                             projectURL: URL(fileURLWithPath: "/tmp"))
        let clip = try XCTUnwrap(timeline.audioClips.first { $0.url == media })
        XCTAssertEqual(clip.volume, 0.5)
        let envelope = try XCTUnwrap(clip.volumeEnvelope)
        XCTAssertEqual(envelope.value(at: 0), 1)
        XCTAssertEqual(envelope.value(at: 2_000_000), DraftVoiceOverWriter.lowerGain, accuracy: 1e-9)
        XCTAssertFalse(timeline.report.unsupported.contains { $0.category == "Keyframes" })
    }
}

final class VolumeEnvelopeTests: XCTestCase {
    func testRendererFollowsTheEnvelope() async throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        let rate = 48_000.0
        let format = AVAudioFormat(standardFormatWithSampleRate: rate, channels: 1)!
        let sine = dir.appendingPathComponent("sine.caf")
        let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(rate * 3))!
        buffer.frameLength = buffer.frameCapacity
        for i in 0..<Int(buffer.frameLength) {
            buffer.floatChannelData![0][i] = Float(0.5 * sin(2 * Double.pi * 440 * Double(i) / rate))
        }
        try AVAudioFile(forWriting: sine, settings: format.settings).write(from: buffer)

        // Full level for 1 s, a 0.1 s ramp down to a quarter, back up at 2 s.
        var clip = AudioClip(url: sine, target: TimeRange(start: 0, duration: 3_000_000),
                             timeMap: TimeMap(sourceStart: 0, sourceDuration: 3_000_000, targetDuration: 3_000_000),
                             volume: 1, fadeIn: 0, fadeOut: 0, preservePitch: true)
        clip.volumeEnvelope = KeyframeTrack(times: [900_000, 1_000_000, 2_000_000, 2_100_000], values: [1, 0.25, 0.25, 1])
        let timeline = Timeline(name: "t", canvasSize: CGSize(width: 2, height: 2), duration: 3_000_000, fps: 30,
                                layers: [], audioClips: [clip], report: RenderReport())
        let out = dir.appendingPathComponent("mix.caf")
        try await AudioRenderer.render(timeline, to: out, isCancelled: { false }, progress: { _ in })

        let file = try AVAudioFile(forReading: out)
        let mixed = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length))!
        try file.read(into: mixed)
        func peak(_ from: Double, _ to: Double) -> Float {
            (Int(from * rate)..<Int(to * rate)).map { abs(mixed.floatChannelData![0][$0]) }.max() ?? 0
        }
        XCTAssertEqual(peak(0.2, 0.8), 0.5, accuracy: 0.03)
        XCTAssertEqual(peak(1.2, 1.8), 0.125, accuracy: 0.02)
        XCTAssertEqual(peak(2.2, 2.8), 0.5, accuracy: 0.03)
    }
}
