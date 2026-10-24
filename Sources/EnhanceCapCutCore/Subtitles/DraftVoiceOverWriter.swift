import Foundation

/// One language of generated voice-over, to be added to a draft as an audio track.
public struct VoiceOverTrack {
    public var language: SubtitleLanguage
    /// The spoken track as one file aligned to the timeline (file time 0 = timeline start).
    public var url: URL
    /// Length of the file, seconds.
    public var fileDuration: Double
    /// Where each line was placed, seconds. Every line becomes its own clip on the track.
    public var lines: [ClosedRange<Double>]
    /// Muted tracks can be unmuted in CapCut with the track's speaker button.
    public var isAudible: Bool

    public init(language: SubtitleLanguage, url: URL, fileDuration: Double, lines: [ClosedRange<Double>], isAudible: Bool) {
        self.language = language
        self.url = url
        self.fileDuration = fileDuration
        self.lines = lines
        self.isAudible = isAudible
    }
}

/// What happens to the project's own sound while the voice-over speaks.
public enum OriginalAudio: String, CaseIterable, Identifiable, Sendable {
    case keep = "Keep", lower = "Lower", mute = "Mute"
    public var id: String { rawValue }

    /// Gain applied while the voice speaks; nil = untouched.
    var duckGain: Double? {
        switch self {
        case .keep: return nil
        case .lower: return DraftVoiceOverWriter.lowerGain
        case .mute: return 0
        }
    }
}

/// Adds voice-over to a CapCut draft: one audio track per language with a clip per spoken line
/// (`materials.audios` of type `record`, like CapCut's own voiceovers; see docs/FORMAT.md), and
/// optionally ducks the other audio with `KFTypeVolume` keyframes while the voice speaks.
///
/// Like `DraftSubtitleWriter`, the draft is edited in place with `JSONScanner`: only the arrays we add
/// to and the `common_keyframes` of ducked segments are rewritten.
public enum DraftVoiceOverWriter {
    /// Name prefix of the tracks we create; a later run replaces tracks with this prefix.
    public static let trackNamePrefix = "EnhanceCapCut voice-over"
    /// First block of the ids of the volume keyframe groups we add, so a later run can find and remove them.
    static let keyframeIDPrefix = "ECC0D0CC-"

    /// "Lower": −15 dB while the voice speaks, as in SubDub.
    static let lowerGain = 0.178
    /// Ducking ramps: down before a line starts, back up after it ends (µs).
    static let fadeDown: Int64 = 250_000
    static let fadeUp: Int64 = 400_000

    /// Returns the draft with `tracks` appended as audio tracks. With `replacePrevious`, tracks and
    /// ducking from an earlier run are removed first. Media paths inside `projectURL` are written with
    /// the draft's own project-folder placeholder when it has one, so they move with the project.
    public static func apply(_ tracks: [VoiceOverTrack], to draft: Data, original: OriginalAudio, replacePrevious: Bool,
                             projectURL: URL? = nil) throws -> Data {
        var data = draft
        if replacePrevious { data = try removeDucking(data) }
        data = try addTracks(tracks, to: data, replacePrevious: replacePrevious, projectURL: projectURL)
        if let gain = original.duckGain, let spoken = tracks.first(where: \.isAudible) {
            let ranges = spoken.lines.map { us($0.lowerBound)..<us($0.upperBound) }
            data = try duck(data, speech: merge(ranges, gap: fadeDown + fadeUp), gain: gain)
        }
        return data
    }

    /// The draft without the voice-over and ducking of earlier runs: what speech recognition should hear.
    public static func withoutVoiceOver(_ draft: Data) throws -> Data {
        try addTracks([], to: removeDucking(draft), replacePrevious: true, projectURL: nil)
    }

    // MARK: Tracks

    private static func addTracks(_ tracks: [VoiceOverTrack], to draft: Data, replacePrevious: Bool,
                                  projectURL: URL?) throws -> Data {
        let scanner = JSONScanner(draft)
        let root = try scanner.root()
        guard let tracksRange = try scanner.value("tracks", in: root) else { throw DraftSubtitleError.noTracks }
        let materialsRange = try scanner.value("materials", in: root)
        let duration = (try scanner.value("duration", in: root)).flatMap { scanner.object($0) as? Int64 } ?? .max
        let placeholder = projectURL.flatMap { _ in placeholderPrefix(in: scanner.bytes) }

        var trackElements = try scanner.elements(tracksRange)
        var removed = Set<String>()
        if replacePrevious {
            trackElements.removeAll { range in
                let track = scanner.object(range) as? [String: Any] ?? [:]
                guard (track["name"] as? String)?.hasPrefix(trackNamePrefix) == true else { return false }
                for segment in track["segments"] as? [[String: Any]] ?? [] {
                    if let id = segment["material_id"] as? String { removed.insert(id) }
                    removed.formUnion(segment["extra_material_refs"] as? [String] ?? [])
                }
                return true
            }
        }

        var newTracks: [String] = []
        var added: [String: [String]] = [:]
        for (n, track) in tracks.enumerated() {
            let trackIndex = trackElements.count + n
            let attribute = track.isAudible ? 0 : 1
            var path = track.url.path
            if let placeholder, let projectURL, path.hasPrefix(projectURL.path + "/") {
                path = placeholder + path.dropFirst(projectURL.path.count)
            }
            let fileDuration = us(track.fileDuration)
            var segments: [Any] = []
            for (i, line) in track.lines.enumerated() {
                let start = us(line.lowerBound)
                let end = min(us(line.upperBound), duration, fileDuration)
                guard end > start else { continue }
                let materialID = UUID().uuidString
                var audio = defaultAudio
                audio["id"] = materialID
                audio["name"] = "Voice-over \(track.language.code) \(i + 1)"
                audio["path"] = path
                audio["duration"] = fileDuration
                added["audios", default: []].append(JSONText.encode(audio))

                var refs: [String] = []
                for (key, template) in extraMaterials {
                    var m = template
                    let id = UUID().uuidString
                    m["id"] = id
                    refs.append(id)
                    added[key, default: []].append(JSONText.encode(m))
                }
                var s = defaultSegment
                s["id"] = UUID().uuidString
                s["material_id"] = materialID
                s["extra_material_refs"] = refs
                s["source_timerange"] = ["start": start, "duration": end - start]
                s["target_timerange"] = ["start": start, "duration": end - start]
                s["track_render_index"] = trackIndex
                s["track_attribute"] = attribute
                segments.append(s)
            }
            newTracks.append(JSONText.encode([
                "id": UUID().uuidString, "type": "audio", "flag": 0, "attribute": attribute,
                "name": "\(trackNamePrefix) · \(track.language.code)", "is_default_name": false,
                "segments": segments,
            ] as [String: Any]))
        }

        func array(_ kept: [Range<Int>], _ new: [String]) -> String {
            "[" + (kept.map { String(decoding: scanner.bytes[$0], as: UTF8.self) } + new).joined(separator: ",") + "]"
        }
        var edits = JSONEdits()
        edits.replace(tracksRange, with: array(trackElements, newTracks))
        if let materialsRange {
            var missing = added
            for (key, range) in try scanner.members(materialsRange) where scanner.bytes[range.lowerBound] == UInt8(ascii: "[") {
                missing[key] = nil
                var elements = try scanner.elements(range)
                let before = elements.count
                if !removed.isEmpty {
                    elements.removeAll { removed.contains((scanner.object($0) as? [String: Any])?["id"] as? String ?? "") }
                }
                let new = added[key] ?? []
                if elements.count != before || !new.isEmpty { edits.replace(range, with: array(elements, new)) }
            }
            if !missing.isEmpty {
                let close = materialsRange.upperBound - 1
                let empty = try scanner.members(materialsRange).isEmpty
                let members = missing.keys.sorted().map { "\"\($0)\":" + array([], missing[$0]!) }
                edits.replace(close..<close, with: (empty ? "" : ",") + members.joined(separator: ","))
            }
        } else {
            let close = root.upperBound - 1
            let members = added.keys.sorted().map { "\"\($0)\":" + array([], added[$0]!) }
            edits.replace(close..<close, with: ",\"materials\":{" + members.joined(separator: ",") + "}")
        }
        return edits.applied(to: scanner.bytes)
    }

    /// `##_draftpath_placeholder_<UUID>_##`, the project folder in CapCut's own media paths.
    static func placeholderPrefix(in bytes: [UInt8]) -> String? {
        let marker = Array("##_draftpath_placeholder_".utf8)
        guard let start = bytes.indices.dropLast(marker.count).first(where: { bytes[$0..<$0 + marker.count].elementsEqual(marker) })
        else { return nil }
        var i = start + marker.count
        while i + 2 < bytes.count, i - start < 80 {
            if bytes[i] == UInt8(ascii: "_"), bytes[i + 1] == UInt8(ascii: "#"), bytes[i + 2] == UInt8(ascii: "#") {
                return String(decoding: bytes[start..<i + 3], as: UTF8.self)
            }
            i += 1
        }
        return nil
    }

    // MARK: Ducking

    /// Volume keyframes on every audible segment that overlaps `speech`: down to `gain` × its volume
    /// while the voice speaks, back up in between. Segments that already have volume keyframes of
    /// their own are left alone.
    private static func duck(_ draft: Data, speech: [Range<Int64>], gain: Double) throws -> Data {
        guard !speech.isEmpty else { return draft }
        var edits = JSONEdits()
        let scanner = JSONScanner(draft)
        try forEachSegment(scanner) { track, segment, members in
            guard let type = track["type"] as? String, type == "video" || type == "audio",
                  ((track["attribute"] as? Int) ?? 0) & 1 == 0,
                  (track["name"] as? String)?.hasPrefix(trackNamePrefix) != true else { return }
            let parsed = scanner.object(segment) as? [String: Any] ?? [:]
            let groups = parsed["common_keyframes"] as? [[String: Any]] ?? []
            guard !groups.contains(where: { $0["property_type"] as? String == "KFTypeVolume" }),
                  let range = parsed["target_timerange"] as? [String: Any],
                  let start = (range["start"] as? NSNumber)?.int64Value,
                  let length = (range["duration"] as? NSNumber)?.int64Value, length > 0 else { return }
            let volume = (parsed["volume"] as? NSNumber)?.doubleValue ?? 1
            guard volume > 0 else { return }

            // Timeline points (ducked or not), then clamped into the segment; later points win on a tie.
            var points: [(t: Int64, ducked: Bool)] = []
            for r in speech where r.upperBound + fadeUp > start && r.lowerBound - fadeDown < start + length {
                points += [(r.lowerBound - fadeDown, false), (r.lowerBound, true), (r.upperBound, true), (r.upperBound + fadeUp, false)]
            }
            guard !points.isEmpty else { return }
            var keyframes: [(t: Int64, ducked: Bool)] = []
            for p in points {
                let local = min(max(p.t - start, 0), length)
                if let last = keyframes.last, last.t == local { keyframes[keyframes.count - 1].ducked = p.ducked } else {
                    keyframes.append((local, p.ducked))
                }
            }
            let group = JSONText.encode([
                "id": keyframeIDPrefix + String(UUID().uuidString.dropFirst(keyframeIDPrefix.count)),
                "material_id": "", "property_type": "KFTypeVolume",
                "keyframe_list": keyframes.map { k in
                    [
                        "id": UUID().uuidString, "curveType": "Line", "time_offset": k.t,
                        "left_control": ["x": 0.0, "y": 0.0], "right_control": ["x": 0.0, "y": 0.0],
                        "values": [volume * (k.ducked ? gain : 1)], "string_value": "", "graphID": "",
                    ] as [String: Any]
                },
            ] as [String: Any])
            if let existing = members.first(where: { $0.key == "common_keyframes" })?.value,
               scanner.bytes[existing.lowerBound] == UInt8(ascii: "[") {
                let kept = try scanner.elements(existing).map { String(decoding: scanner.bytes[$0], as: UTF8.self) }
                edits.replace(existing, with: "[" + (kept + [group]).joined(separator: ",") + "]")
            } else {
                let close = segment.upperBound - 1
                edits.replace(close..<close, with: (members.isEmpty ? "" : ",") + "\"common_keyframes\":[\(group)]")
            }
        }
        return edits.applied(to: scanner.bytes)
    }

    /// Drops the volume keyframe groups an earlier run added.
    private static func removeDucking(_ draft: Data) throws -> Data {
        var edits = JSONEdits()
        var changed = false
        let scanner = JSONScanner(draft)
        try forEachSegment(scanner) { _, _, members in
            guard let range = members.first(where: { $0.key == "common_keyframes" })?.value,
                  scanner.bytes[range.lowerBound] == UInt8(ascii: "[") else { return }
            let groups = try scanner.elements(range)
            let kept = groups.filter {
                !((scanner.object($0) as? [String: Any])?["id"] as? String ?? "").hasPrefix(keyframeIDPrefix)
            }
            guard kept.count != groups.count else { return }
            changed = true
            edits.replace(range, with: "[" + kept.map { String(decoding: scanner.bytes[$0], as: UTF8.self) }.joined(separator: ",") + "]")
        }
        return changed ? edits.applied(to: scanner.bytes) : draft
    }

    /// Calls `body` with each top-level track (parsed, without its segments), segment range and the segment's members.
    private static func forEachSegment(_ scanner: JSONScanner,
                                       _ body: ([String: Any], Range<Int>, [(key: String, value: Range<Int>)]) throws -> Void) throws {
        let root = try scanner.root()
        guard let tracksRange = try scanner.value("tracks", in: root) else { return }
        for trackRange in try scanner.elements(tracksRange) {
            let members = try scanner.members(trackRange)
            var track: [String: Any] = [:]
            for (key, value) in members where key != "segments" { track[key] = scanner.object(value) }
            guard let segments = members.first(where: { $0.key == "segments" })?.value,
                  scanner.bytes[segments.lowerBound] == UInt8(ascii: "[") else { continue }
            for segment in try scanner.elements(segments) {
                try body(track, segment, try scanner.members(segment))
            }
        }
    }

    static func merge(_ ranges: [Range<Int64>], gap: Int64) -> [Range<Int64>] {
        var out: [Range<Int64>] = []
        for r in ranges.sorted(by: { $0.lowerBound < $1.lowerBound }) {
            if let last = out.last, r.lowerBound - last.upperBound < gap {
                out[out.count - 1] = last.lowerBound..<max(last.upperBound, r.upperBound)
            } else {
                out.append(r)
            }
        }
        return out
    }

    private static func us(_ seconds: Double) -> Int64 { Int64((max(0, seconds) * 1_000_000).rounded()) }

    // MARK: Defaults
    //
    // Field sets of a voiceover clip as CapCut 9.5 for macOS writes it (see docs/FORMAT.md).
    // Floating-point fields are written with a decimal point on purpose.

    private static func parse(_ json: String) -> [String: Any] {
        (try? JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any]) ?? [:]
    }

    static let defaultAudio = parse("""
        {"id":"","unique_id":"","type":"record","name":"","duration":0,"path":"","category_name":"","wave_points":[],\
        "music_id":"","app_id":0,"text_id":"","tone_type":"","source_platform":0,"video_id":"","effect_id":"",\
        "resource_id":"","third_resource_id":"","category_id":"","intensifies_path":"","formula_id":"","check_flag":1,\
        "team_id":"","local_material_id":"","tone_speaker":"","mock_tone_speaker":"","tone_effect_id":"",\
        "tone_effect_name":"","tone_platform":"","cloned_model_type":"","tone_category_id":"","tone_category_name":"",\
        "tone_second_category_id":"","tone_second_category_name":"","tone_emotion_name_key":"","tone_emotion_style":"",\
        "tone_emotion_role":"","tone_emotion_selection":"","tone_emotion_scale":0.0,"moyin_emotion":"","request_id":"",\
        "query":"","search_id":"","sound_separate_type":"","is_text_edit_overdub":false,"is_ugc":false,\
        "is_ai_clone_tone":false,"is_ai_clone_tone_post":false,"source_from":"","copyright_limit_type":"none",\
        "aigc_history_id":"","aigc_item_id":"","music_source":"","pgc_id":"","pgc_name":"",\
        "similiar_music_info":{"original_song_id":"","original_song_name":""},"ai_music_type":0,"ai_music_enter_from":"",\
        "lyric_type":0,"tts_task_id":"","tts_generate_scene":"","ai_music_generate_scene":0,\
        "tts_benefit_info":{"benefit_type":"none","benefit_log_id":"","benefit_log_extra":"","benefit_amount":-1}}
        """)

    /// Per-clip materials CapCut gives a voiceover clip, by `materials` key.
    static let extraMaterials: [(String, [String: Any])] = [
        ("speeds", parse(#"{"id":"","type":"speed","mode":0,"speed":1.0,"curve_speed":null}"#)),
        ("placeholder_infos", parse("""
            {"id":"","type":"placeholder_info","meta_type":"none","res_path":"","res_text":"","error_path":"","error_text":""}
            """)),
        ("beats", parse("""
            {"id":"","type":"beats","enable_ai_beats":false,"gear":404,"gear_count":0,"mode":404,"user_beats":[],\
            "user_delete_ai_beats":null,"ai_beats":{"melody_url":"","melody_path":"","beats_url":"","beats_path":"",\
            "melody_percents":[0.0],"beat_speed_infos":[]}}
            """)),
        ("sound_channel_mappings", parse(#"{"id":"","type":"","audio_channel_mapping":0,"is_config_open":false}"#)),
        ("vocal_separations", parse("""
            {"id":"","type":"vocal_separation","choice":0,"removed_sounds":[],"time_range":null,"production_path":"",\
            "final_algorithm":"","enter_from":""}
            """)),
    ]

    static let defaultSegment = parse("""
        {"id":"","source_timerange":{"start":0,"duration":0},"target_timerange":{"start":0,"duration":0},\
        "render_timerange":{"start":0,"duration":0},"desc":"","state":0,"speed":1.0,"is_loop":false,\
        "is_tone_modify":false,"reverse":false,"intensifies_audio":false,"cartoon":false,"volume":1.0,\
        "last_nonzero_volume":1.0,"clip":null,"uniform_scale":null,"material_id":"","extra_material_refs":[],\
        "render_index":0,"keyframe_refs":[],"enable_lut":false,"enable_adjust":false,"enable_hsl":false,"visible":true,\
        "group_id":"","enable_color_curves":true,"enable_hsl_curves":true,"track_render_index":0,"hdr_settings":null,\
        "enable_color_wheels":true,"track_attribute":0,"is_placeholder":false,"template_id":"",\
        "enable_smart_color_adjust":false,"template_scene":"default","common_keyframes":[],"caption_info":null,\
        "responsive_layout":{"enable":false,"target_follow":"","size_layout":0,"horizontal_pos_layout":0,\
        "vertical_pos_layout":0},"enable_color_match_adjust":false,"enable_color_correct_adjust":false,\
        "enable_adjust_mask":false,"raw_segment_id":"","lyric_keyframes":null,"enable_video_mask":true,\
        "digital_human_template_group_id":"","color_correct_alg_result":"","source":"segmentsourcenormal",\
        "enable_mask_stroke":false,"enable_mask_shadow":false,"enable_color_adjust_pro":false}
        """)
}
