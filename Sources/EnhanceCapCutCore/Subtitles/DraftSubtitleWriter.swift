import AppKit
import Foundation

/// One language of generated subtitles, to be added to a draft as a CapCut caption track.
public struct SubtitleTrack {
    public var language: SubtitleLanguage
    public var cues: [CaptionCue]
    /// The spoken language (CapCut's recognized captions) rather than a translation.
    public var isOriginal: Bool
    /// Hidden tracks can be shown in CapCut with the track's eye button.
    public var isVisible: Bool

    public init(language: SubtitleLanguage, cues: [CaptionCue], isOriginal: Bool, isVisible: Bool) {
        self.language = language
        self.cues = cues
        self.isOriginal = isOriginal
        self.isVisible = isVisible
    }

    /// CapCut's caption language: a language and region, like `ru-RU` or `en-US`.
    var capCutLanguage: String {
        let maximal = Locale.Language(identifier: language.code).maximalIdentifier
        let parts = Locale.Language(identifier: maximal)
        guard let code = parts.languageCode?.identifier, let region = parts.region?.identifier else { return language.code }
        return "\(code)-\(region)"
    }
}

public enum DraftSubtitleError: LocalizedError {
    case projectOpen(String)
    case noTracks

    public var errorDescription: String? {
        switch self {
        case let .projectOpen(name):
            return L("{0} is open in CapCut. Close the project (back to CapCut's home screen) and try again — "
                + "otherwise CapCut overwrites the subtitles when it saves.", name)
        case .noTracks: return "The draft has no tracks list."
        }
    }
}

/// Adds generated subtitles to a CapCut draft as caption tracks (`materials.texts` of type `subtitle`),
/// one text track per language, the way CapCut's own auto-captions are stored (see docs/FORMAT.md).
///
/// The draft is edited in place with `JSONScanner`: only the `tracks`, `materials.texts` and
/// `materials.material_animations` arrays are rewritten, and only by appending or dropping elements.
public enum DraftSubtitleWriter {
    /// Name prefix of the tracks we create; a later run replaces tracks with this prefix.
    public static let trackNamePrefix = "EnhanceCapCut subtitles"

    /// Defaults used when the draft has no captions to copy the style from. Measured on the author's
    /// projects: CapCut's caption preset is size 8, white with a thin outline, near the bottom.
    static let defaultStyleSize = 8.0
    static let defaultPositionY = -0.65

    public struct Result {
        /// Every draft file that was rewritten.
        public var files: [URL]
        /// The originals, saved before writing.
        public var backupFolder: URL
        public var cueCount: Int
    }

    public static var backupRoot: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/EnhanceCapCut/Backups", isDirectory: true)
    }

    /// CapCut keeps a `.locked` file in an open project's folder, but a crash leaves stale ones,
    /// so the lock only counts while CapCut is running.
    public static func isOpenInCapCut(_ project: CapCutProject) -> Bool {
        let fresh = CapCutProject(url: project.url)
        guard fresh.isOpen else { return false }
        return NSWorkspace.shared.runningApplications.contains {
            $0.bundleIdentifier == "com.lemon.lvoverseas" || $0.localizedName == "CapCut"
        }
    }

    /// Writes `tracks` into the timeline's draft. The timeline's draft and every other copy of it
    /// that CapCut keeps (the root draft of the main timeline, `template-2.tmp`) are
    /// rewritten; their previous contents go to `backupRoot` first.
    public static func write(_ tracks: [SubtitleTrack], project: CapCutProject, timeline: ProjectTimeline,
                             replacePrevious: Bool) throws -> Result {
        try write(project: project, timeline: timeline, cueCount: tracks.reduce(0) { $0 + $1.cues.count }) {
            try apply(tracks, to: $0, replacePrevious: replacePrevious)
        }
    }

    /// Rewrites the timeline's draft (and its copies, see above) with `transform`, after a backup.
    public static func write(project: CapCutProject, timeline: ProjectTimeline, cueCount: Int,
                             transform: (Data) throws -> Data) throws -> Result {
        if isOpenInCapCut(project) { throw DraftSubtitleError.projectOpen(project.name) }
        let fm = FileManager.default
        let original = try Data(contentsOf: timeline.draftURL)
        let updated = try transform(original)

        // Copies of this draft: identical files next to it and, for the main timeline, the root draft.
        var candidates = [timeline.draftURL, timeline.draftURL.deletingLastPathComponent().appendingPathComponent("template-2.tmp")]
        if timeline.isMain {
            candidates += [project.draftURL, project.url.appendingPathComponent("template-2.tmp")]
        }
        var files: [URL] = []
        for url in candidates where !files.contains(url) {
            if url == timeline.draftURL || (try? Data(contentsOf: url)) == original { files.append(url) }
        }

        let stamp = ISO8601DateFormatter().string(from: Date()).replacingOccurrences(of: ":", with: "-")
        let backup = backupRoot.appendingPathComponent(project.name, isDirectory: true)
            .appendingPathComponent(stamp, isDirectory: true)
        try fm.createDirectory(at: backup, withIntermediateDirectories: true)
        for url in files {
            let relative = String(url.path.dropFirst(project.url.path.count + 1))
            let dest = backup.appendingPathComponent(relative)
            try fm.createDirectory(at: dest.deletingLastPathComponent(), withIntermediateDirectories: true)
            try fm.copyItem(at: url, to: dest)
        }
        for url in files { try updated.write(to: url, options: .atomic) }
        return Result(files: files, backupFolder: backup, cueCount: cueCount)
    }

    /// Returns the draft with `tracks` appended as caption tracks. With `replacePrevious`, tracks from
    /// an earlier run (named with `trackNamePrefix`) are removed together with their materials.
    public static func apply(_ tracks: [SubtitleTrack], to draft: Data, replacePrevious: Bool,
                             now: Date = Date()) throws -> Data {
        let scanner = JSONScanner(draft)
        let root = try scanner.root()
        guard let tracksRange = try scanner.value("tracks", in: root) else { throw DraftSubtitleError.noTracks }
        let materialsRange = try scanner.value("materials", in: root)
        let fps = (try scanner.value("fps", in: root)).flatMap { scanner.object($0) as? Double } ?? 30
        let duration = (try scanner.value("duration", in: root)).flatMap { scanner.object($0) as? Int64 } ?? .max

        var trackElements = try scanner.elements(tracksRange)
        let textsRange = try materialsRange.flatMap { try scanner.value("texts", in: $0) }
        let animationsRange = try materialsRange.flatMap { try scanner.value("material_animations", in: $0) }
        var textElements = try textsRange.map(scanner.elements) ?? []
        var animationElements = try animationsRange.map(scanner.elements) ?? []

        // Drop our tracks from an earlier run, and the materials only they used.
        let parsedTracks = trackElements.map { scanner.object($0) as? [String: Any] ?? [:] }
        if replacePrevious {
            var removed = Set<String>()
            var kept: [Range<Int>] = []
            for (range, track) in zip(trackElements, parsedTracks) {
                if (track["name"] as? String)?.hasPrefix(trackNamePrefix) == true {
                    for segment in track["segments"] as? [[String: Any]] ?? [] {
                        if let id = segment["material_id"] as? String { removed.insert(id) }
                        removed.formUnion(segment["extra_material_refs"] as? [String] ?? [])
                    }
                } else {
                    kept.append(range)
                }
            }
            trackElements = kept
            let id = { (range: Range<Int>) in (scanner.object(range) as? [String: Any])?["id"] as? String ?? "" }
            textElements.removeAll { removed.contains(id($0)) }
            animationElements.removeAll { removed.contains(id($0)) }
        }

        // Style and position: copied from a caption already in the draft, else CapCut's defaults.
        let materials = textElements.compactMap { scanner.object($0) as? [String: Any] }
        let styleMaterial = materials.first { $0["type"] as? String == "subtitle" }
        var styleSegment: [String: Any]?
        if let id = styleMaterial?["id"] as? String {
            for track in parsedTracks {
                if let s = (track["segments"] as? [[String: Any]])?.first(where: { $0["material_id"] as? String == id }) {
                    styleSegment = s
                    break
                }
            }
        }
        var renderIndex = 14000
        for track in parsedTracks {
            for segment in track["segments"] as? [[String: Any]] ?? [] {
                if let i = segment["render_index"] as? Int, i >= 14000 { renderIndex = max(renderIndex, i + 1) }
            }
        }

        let groupStamp = Int64(now.timeIntervalSince1970 * 1000)
        var newTracks: [String] = []
        var newTexts: [String] = []
        var newAnimations: [String] = []
        for (n, track) in tracks.enumerated() {
            let trackIndex = trackElements.count + n
            let attribute = track.isVisible ? 0 : 2
            var segments: [Any] = []
            for cue in track.cues {
                let start = frameAligned(cue.start, fps: fps)
                let end = min(frameAligned(cue.end, fps: fps), duration)
                guard end > start else { continue }
                let materialID = UUID().uuidString
                let animationID = UUID().uuidString
                newTexts.append(JSONText.encode(material(track, cue: cue, id: materialID, duration: end - start,
                                                         group: "\(track.capCutLanguage)_\(groupStamp + Int64(n))",
                                                         template: styleMaterial)))
                newAnimations.append(JSONText.encode([
                    "id": animationID, "type": "sticker_animation", "animations": [Any](),
                    "multi_language_current": "none",
                ] as [String: Any]))
                segments.append(segment(id: UUID().uuidString, materialID: materialID, animationID: animationID,
                                        start: start, duration: end - start, renderIndex: renderIndex,
                                        trackIndex: trackIndex, attribute: attribute, template: styleSegment))
                renderIndex += 1
            }
            newTracks.append(JSONText.encode([
                "id": UUID().uuidString, "type": "text", "flag": 1, "attribute": attribute,
                "name": "\(trackNamePrefix) · \(track.language.code)", "is_default_name": false,
                "segments": segments,
            ] as [String: Any]))
        }

        func array(_ kept: [Range<Int>], _ added: [String]) -> String {
            "[" + (kept.map { String(decoding: scanner.bytes[$0], as: UTF8.self) } + added).joined(separator: ",") + "]"
        }
        var edits = JSONEdits()
        edits.replace(tracksRange, with: array(trackElements, newTracks))
        if let materialsRange {
            var additions: [String] = []
            if let textsRange { edits.replace(textsRange, with: array(textElements, newTexts)) } else {
                additions.append("\"texts\":" + array([], newTexts))
            }
            if let animationsRange { edits.replace(animationsRange, with: array(animationElements, newAnimations)) } else {
                additions.append("\"material_animations\":" + array([], newAnimations))
            }
            if !additions.isEmpty {
                let close = materialsRange.upperBound - 1
                let empty = try scanner.members(materialsRange).isEmpty
                edits.replace(close..<close, with: (empty ? "" : ",") + additions.joined(separator: ","))
            }
        } else {
            let close = root.upperBound - 1
            edits.replace(close..<close, with: ",\"materials\":{\"texts\":\(array([], newTexts)),"
                + "\"material_animations\":\(array([], newAnimations))}")
        }
        return edits.applied(to: scanner.bytes)
    }

    /// Microseconds, on a frame boundary of the draft's frame rate.
    private static func frameAligned(_ seconds: Double, fps: Double) -> Int64 {
        Int64(((max(0, seconds) * fps).rounded() / fps * 1_000_000).rounded())
    }

    private static func material(_ track: SubtitleTrack, cue: CaptionCue, id: String, duration: Int64, group: String,
                                 template: [String: Any]?) -> [String: Any] {
        var m = template ?? defaultMaterial
        let text = cue.text
        // The style run of the template's content, stretched over the new text (ranges are UTF-16 offsets).
        let templateContent = (m["content"] as? String).flatMap {
            try? JSONSerialization.jsonObject(with: Data($0.utf8)) as? [String: Any]
        }
        var style = (templateContent?["styles"] as? [[String: Any]])?.first ?? defaultStyle
        style["range"] = [0, text.utf16.count]
        m["content"] = JSONText.encode(["styles": [style], "text": text] as [String: Any])
        if let size = style["size"] as? Double { m["font_size"] = size }

        // Word timings in milliseconds from the clip start; CapCut spreads translated words evenly too.
        let words = text.split(whereSeparator: \.isWhitespace).map(String.init)
        let step = Double(duration) / 1000 / Double(max(words.count, 1))
        m["words"] = [
            "start_time": words.indices.map { Int((Double($0) * step).rounded()) },
            "end_time": words.indices.map { Int((Double($0 + 1) * step).rounded()) },
            "text": words,
        ] as [String: Any]
        m["current_words"] = ["start_time": [Int](), "end_time": [Int](), "text": [String]()] as [String: Any]
        m["id"] = id
        m["type"] = "subtitle"
        m["recognize_text"] = text.replacingOccurrences(of: "\n", with: " ")
        m["base_content"] = ""
        m["language"] = track.capCutLanguage
        m["group_id"] = group
        m["recognize_task_id"] = ""
        m["relevance_segment"] = [String]()
        m["translate_original_text"] = ""
        // As in CapCut's own captions: recognized ones are sub type 0 / add type 1, translations 5 / 0.
        m["sub_type"] = track.isOriginal ? 0 : 5
        m["add_type"] = track.isOriginal ? 1 : 0
        return m
    }

    private static func segment(id: String, materialID: String, animationID: String, start: Int64, duration: Int64,
                                renderIndex: Int, trackIndex: Int, attribute: Int, template: [String: Any]?) -> [String: Any] {
        var s = defaultSegment
        if let template {
            for key in ["clip", "uniform_scale"] where template[key] != nil { s[key] = template[key] }
        }
        s["id"] = id
        s["material_id"] = materialID
        s["extra_material_refs"] = [animationID]
        s["target_timerange"] = ["start": start, "duration": duration]
        s["render_index"] = renderIndex
        s["track_render_index"] = trackIndex
        s["track_attribute"] = attribute
        s["common_keyframes"] = [Any]()
        s["keyframe_refs"] = [Any]()
        s["visible"] = true
        return s
    }

    // MARK: Defaults
    //
    // Field sets of a caption as CapCut 9.5 for macOS writes it (see docs/FORMAT.md), used when the
    // draft has no caption to copy. Floating-point fields are written with a decimal point on purpose.

    private static func parse(_ json: String) -> [String: Any] {
        (try? JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any]) ?? [:]
    }

    static let defaultStyle = parse("""
        {"fill":{"content":{"render_type":"solid","solid":{"color":[1.0,1.0,1.0]}}},\
        "font":{"id":"","path":"/Applications/CapCut.app/Contents/Resources/Font/SystemFont/en.ttf"},\
        "range":[0,0],"size":\(defaultStyleSize)}
        """)

    static let defaultMaterial = parse("""
        {"add_type":0,"alignment":1,"autoAdaptCanvasEnabled":false,"background_alpha":1.0,"background_color":"",\
        "background_fill":"","background_height":0.14,"background_horizontal_offset":0.0,"background_round_radius":0.0,\
        "background_style":0,"background_vertical_offset":0.0,"background_width":0.14,"base_content":"",\
        "bold_width":0.0,"border_alpha":1.0,"border_color":"","border_mode":0,"border_width":0.08,\
        "caption_template_info":{"category_id":"","category_name":"","effect_id":"","is_new":false,"path":"",\
        "request_id":"","resource_id":"","resource_name":"","source_platform":0,"third_resource_id":""},\
        "check_flag":7,"combo_info":{"text_templates":[]},"content":"","current_words":{"end_time":[],"start_time":[],"text":[]},\
        "cutoff_postfix":"","enable_path_typesetting":false,"fixed_height":-1.0,"fixed_width":-1.0,\
        "font_category_id":"","font_category_name":"","font_id":"",\
        "font_name":"","font_path":"/Applications/CapCut.app/Contents/Resources/Font/SystemFont/en.ttf",\
        "font_resource_id":"","font_size":\(defaultStyleSize),"font_source_platform":0,"font_team_id":"",\
        "font_third_resource_id":"","font_title":"none","font_url":"","fonts":[],"force_apply_line_max_width":false,\
        "global_alpha":1.0,"group_id":"","has_shadow":false,"id":"","initial_scale":1.0,"inner_padding":-1.0,\
        "is_batch_replace":false,"is_lyric_effect":false,"is_rich_text":false,"is_words_linear":false,\
        "italic_degree":0,"ktv_color":"","language":"","layer_weight":1,"letter_spacing":0.0,"line_feed":1,\
        "line_max_width":0.82,"line_spacing":0.02,"lyric_group_id":"","lyrics_template":{"category_id":"",\
        "category_name":"","effect_id":"","panel":"","path":"","request_id":"","resource_id":"","resource_name":""},\
        "multi_language_current":"none","name":"","offset_on_path":0.0,"oneline_cutoff":false,"operation_type":0,\
        "original_size":[],"preset_category":"","preset_category_id":"","preset_has_set_alignment":false,"preset_id":"",\
        "preset_index":0,"preset_name":"","punc_model":"","recognize_model":"","recognize_task_id":"","recognize_text":"",\
        "recognize_type":0,"relevance_segment":[],"shadow_alpha":0.9,"shadow_angle":-45.0,"shadow_color":"",\
        "shadow_distance":5.0,"shadow_point":{"x":0.6363961030678928,"y":-0.6363961030678927},"shadow_smoothing":0.45,\
        "shadow_thickness_projection_angle":0.0,"shadow_thickness_projection_distance":0.0,\
        "shadow_thickness_projection_enable":false,"shape_clip_x":false,"shape_clip_y":false,\
        "single_char_bg_alpha":1.0,"single_char_bg_color":"","single_char_bg_enable":false,"single_char_bg_height":0.0,\
        "single_char_bg_horizontal_offset":0.0,"single_char_bg_round_radius":0.3,"single_char_bg_vertical_offset":0.0,\
        "single_char_bg_width":0.0,"source_from":"","ssml_content":"","style_name":"","sub_template_id":-1,"sub_type":0,\
        "subtitle_keywords":null,"subtitle_keywords_config":null,"subtitle_template_original_fontsize":0.0,\
        "text_alpha":1.0,"text_color":"#FFFFFF","text_curve":null,"text_exceeds_path_process_type":0,\
        "text_loop_on_path":false,"text_preset_resource_id":"","text_size":30,"text_to_audio_ids":[],\
        "text_typesetting_path_index":0,"text_typesetting_paths":null,"text_typesetting_paths_file":"",\
        "translate_original_text":"","tts_auto_update":false,"type":"subtitle","typesetting":0,"underline":false,\
        "underline_offset":0.22,"underline_width":0.05,"use_effect_default_color":true,"words":{"end_time":[],\
        "start_time":[],"text":[]}}
        """)

    static let defaultSegment = parse("""
        {"caption_info":null,"cartoon":false,"clip":{"alpha":1.0,"flip":{"horizontal":false,"vertical":false},\
        "rotation":0.0,"scale":{"x":1.0,"y":1.0},"transform":{"x":0.0,"y":\(defaultPositionY)}},\
        "color_correct_alg_result":"","common_keyframes":[],"desc":"","digital_human_template_group_id":"",\
        "enable_adjust":false,"enable_adjust_mask":false,"enable_color_adjust_pro":false,\
        "enable_color_correct_adjust":false,"enable_color_curves":true,"enable_color_match_adjust":false,\
        "enable_color_wheels":true,"enable_hsl":false,"enable_hsl_curves":true,"enable_lut":false,\
        "enable_mask_shadow":false,"enable_mask_stroke":false,"enable_smart_color_adjust":false,\
        "enable_video_mask":true,"extra_material_refs":[],"group_id":"","hdr_settings":null,\
        "hdr_vivid_settings":null,"id":"","intensifies_audio":false,"is_loop":false,"is_placeholder":false,\
        "is_tone_modify":false,"keyframe_refs":[],"last_nonzero_volume":1.0,"lyric_keyframes":null,"material_id":"",\
        "raw_segment_id":"","render_index":14000,"render_timerange":{"duration":0,"start":0},\
        "responsive_layout":{"enable":false,"horizontal_pos_layout":0,"size_layout":0,"target_follow":"",\
        "vertical_pos_layout":0},"reverse":false,"segment_color_tag":"","source":"segmentsourcenormal",\
        "source_timerange":null,"speed":1.0,"state":0,"target_timerange":{"duration":0,"start":0},\
        "template_id":"","template_scene":"default","track_attribute":0,"track_render_index":0,\
        "uniform_scale":{"on":true,"value":1.0},"visible":true,"volume":1.0}
        """)
}
