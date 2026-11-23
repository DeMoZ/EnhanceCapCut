import AppKit
import EccCore
import Foundation

// Swift conveniences on the Rust core's generated types (EccCore, openspec/changes/rust-core task 7.1): file URLs for
// the paths, CGSize for sizes, Date for times, the conformances SwiftUI and dictionaries need. No core logic here —
// that lives in core/ and is reached through the generated functions.

extension String {
    /// A path from the core as a file URL.
    public var fileURL: URL { URL(fileURLWithPath: self) }
}

// MARK: Folders and projects

extension CoreEnvironment {
    /// The folders the app and the CLI use: this Mac's defaults (`ECC_PROJECTS` overrides CapCut's projects folder,
    /// for the tests and the CLI).
    public static var current: CoreEnvironment {
        var env = defaultEnvironment()
        if let root = ProcessInfo.processInfo.environment["ECC_PROJECTS"], !root.isEmpty { env.projectsRoot = root }
        return env
    }
}

extension CapCutProject: Identifiable {
    public init(url: URL) { self = projectAt(path: url.path) }

    public var url: URL { path.fileURL }
    public var id: String { path }
    /// When the draft (or the folder) last changed.
    public var modified: Date { Date(timeIntervalSince1970: Double(modifiedMs ?? 0) / 1000) }

    /// CapCut 9 projects can hold several timelines (`Timelines/project.json`); others have the root draft only.
    public func timelines() -> [ProjectTimeline] { projectTimelines(projectPath: path) }

    /// A timeline by name (any case), id or 1-based number; the main one for `nil`.
    public func timeline(named nameOrNumber: String?) -> ProjectTimeline? {
        projectTimeline(projectPath: path, nameOrNumber: nameOrNumber)
    }

    /// The project is open in CapCut: its lock file is there while CapCut runs (a crash leaves stale ones).
    public var isOpenInCapCut: Bool { isOpenInCapcut(projectPath: path, capcutRunning: CapCutApp.isRunning) }
}

extension ProjectTimeline: Identifiable {
    public var draftURL: URL { draftPath.fileURL }
}

/// Projects in CapCut's folder.
public enum ProjectLocator {
    public static var defaultProjectsRoot: URL { CoreEnvironment.current.projectsRoot.fileURL }

    /// All projects that have a draft, most recently modified first.
    public static func allProjects(root: URL = defaultProjectsRoot) -> [CapCutProject] {
        EccCore.allProjects(root: root.path)
    }

    /// The project CapCut has open, if any.
    public static func openProject(root: URL = defaultProjectsRoot) -> CapCutProject? {
        EccCore.openProject(root: root.path)
    }

    /// A project by path (`~` allowed, a draft file's path too) or by folder name.
    public static func resolve(_ nameOrPath: String, root: URL = defaultProjectsRoot) -> CapCutProject? {
        resolveProject(nameOrPath: nameOrPath, root: root.path)
    }
}

/// CapCut itself, which the core can't see: whether it runs decides if a project's lock file counts.
public enum CapCutApp {
    public static var isRunning: Bool {
        NSWorkspace.shared.runningApplications.contains {
            $0.bundleIdentifier == "com.lemon.lvoverseas" || $0.localizedName == "CapCut"
        }
    }
}

// MARK: Timeline

extension Timeline {
    /// Builds `timeline` of the project (the main one for `nil`) with this Mac's folders.
    public static func build(project: CapCutProject, timeline: ProjectTimeline? = nil) throws -> Timeline {
        try Timeline.build(projectPath: project.path, timeline: timeline, env: .current)
    }

    /// The draft's canvas in pixels.
    public var canvasSize: CGSize { canvas().cgSize }
}

extension Size {
    public var cgSize: CGSize { CGSize(width: width, height: height) }
}

extension TimeRange {
    public var end: Int64 { start + duration }
    public func contains(_ t: Int64) -> Bool { t >= start && t < end }
}

extension VisualLayer: Identifiable {
    public func isActive(at t: Int64) -> Bool { target.contains(t) }

    /// The largest scale the layer reaches (clip scale or keyframes), 0.05…8.
    public var maxScale: Double {
        var scale = max(abs(clip.scale.x), abs(clip.scale.y))
        for p in [LayerProperty.scaleX, .scaleY] {
            if let values = keyframes[p]?.values() { scale = max(scale, values.map(abs).max() ?? 0) }
        }
        return min(max(scale, 0.05), 8)
    }
}

extension AudioClip {
    public var url: URL { path.fileURL }
}

extension Crop {
    public var isIdentity: Bool {
        upperLeft.x <= 0.0001 && upperLeft.y <= 0.0001 && lowerRight.x >= 0.9999 && lowerRight.y >= 0.9999
    }
}

extension VideoEffectState {
    /// Too small to see (the thresholds of the core's `VideoEffectState::is_identity`).
    public var isIdentity: Bool { abs(zoom - 1) < 0.0005 && abs(rotation) < 0.01 && blur < 0.0002 && flash < 0.002 }
}

extension VoiceEffects {
    /// Only a voice separation (the other stages off).
    public static func only(_ separation: VoiceSeparation) -> VoiceEffects {
        VoiceEffects(separation: separation, denoise: nil, enhance: nil, filter: nil)
    }

    public var isEmpty: Bool { separation == .none && denoise == nil && enhance == nil && filter == nil }
}

extension FilterLayer {
    public func isActive(at t: Int64) -> Bool { target.contains(t) }

    /// Colours a layer painted below it.
    public func covers(_ layer: VisualLayer) -> Bool {
        (layer.renderIndex, layer.trackIndex) < (renderIndex, trackIndex)
    }
}

extension LutLayout {
    public var size: Int {
        switch self {
        case let .grid(size, _), let .row(size), let .column(size): Int(size)
        }
    }

    /// The pixel holding entry (r, g, b).
    public func pixel(r: Int, g: Int, b: Int) -> (x: Int, y: Int) {
        switch self {
        case let .grid(n, k): ((b % Int(k)) * Int(n) + r, (b / Int(k)) * Int(n) + g)
        case let .row(n): (b * Int(n) + r, g)
        case let .column(n): (r, b * Int(n) + g)
        }
    }
}

extension RenderReport {
    public var isEmpty: Bool { unsupported.isEmpty && missingFiles.isEmpty }
}

extension ReportItem: Identifiable {
    public var id: String { category + "/" + name }
}

// MARK: Writers

extension OriginalAudio: CaseIterable, Identifiable {
    public static var allCases: [OriginalAudio] { [.keep, .lower, .mute, .replace, .removeVocal] }

    /// The English name, also what the app stores.
    public var rawValue: String { originalAudioRawValue(original: self) }
    public init?(rawValue: String) {
        guard let value = originalAudioFromRawValue(raw: rawValue) else { return nil }
        self = value
    }

    public var id: String { rawValue }
    /// The name in the interface language.
    public var title: String { originalAudioTitle(original: self) }
}

extension WriteResult {
    public var fileURLs: [URL] { files.map(\.fileURL) }
    public var backupURL: URL { backupFolder.fileURL }
}
