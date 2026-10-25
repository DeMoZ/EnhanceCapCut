import Foundation

/// A CapCut project folder on disk.
public struct CapCutProject: Identifiable, Hashable {
    public var url: URL
    public var name: String { url.lastPathComponent }
    public var id: URL { url }
    public var modified: Date
    /// CapCut keeps a `.locked` file in the folder of every project that is open in the editor.
    public var isOpen: Bool

    public var draftURL: URL { DraftFile.find(in: url) }

    public init(url: URL) {
        self.url = url
        let fm = FileManager.default
        let draft = DraftFile.find(in: url)
        let attrs = (try? fm.attributesOfItem(atPath: draft.path)) ?? (try? fm.attributesOfItem(atPath: url.path))
        modified = attrs?[.modificationDate] as? Date ?? .distantPast
        isOpen = fm.fileExists(atPath: url.appendingPathComponent(".locked").path)
    }

    /// CapCut 9 projects can hold several timelines (`Timelines/project.json`), each with its own draft in
    /// `Timelines/<id>/` (see `DraftFile`); the root draft is a copy of the main one. Projects
    /// without that file have a single timeline: the root draft.
    public func timelines() -> [ProjectTimeline] {
        let root = url.appendingPathComponent("Timelines", isDirectory: true)
        let single = [ProjectTimeline(id: "", name: "Timeline 01", draftURL: draftURL, isMain: true)]
        guard let data = try? Data(contentsOf: root.appendingPathComponent("project.json")),
              let list = try? JSONDecoder().decode(TimelineList.self, from: data) else { return single }
        let fm = FileManager.default
        let found = list.timelines.compactMap { entry -> ProjectTimeline? in
            guard !entry.isMarkedDelete else { return nil }
            let isMain = entry.id == list.mainID
            var draft = DraftFile.find(in: root.appendingPathComponent(entry.id, isDirectory: true))
            if !fm.fileExists(atPath: draft.path) {
                guard isMain else { return nil }
                draft = draftURL
            }
            return ProjectTimeline(id: entry.id, name: entry.name.isEmpty ? entry.id : entry.name, draftURL: draft, isMain: isMain)
        }
        return found.isEmpty ? single : found
    }

    public func loadDraft(timeline: ProjectTimeline? = nil) throws -> Draft {
        try DraftLoader.load(draftURL: timeline?.draftURL ?? draftURL)
    }
}

/// One timeline of a project.
public struct ProjectTimeline: Identifiable, Hashable {
    /// The timeline's UUID; empty for projects without `Timelines/project.json`.
    public var id: String
    public var name: String
    public var draftURL: URL
    public var isMain: Bool
}

/// `Timelines/project.json`.
private struct TimelineList: Decodable {
    struct Entry: Decodable {
        var id: String
        var name: String
        var isMarkedDelete: Bool

        init(from decoder: Decoder) throws {
            let c = try decoder.anyKeyed()
            id = c.str("id") ?? ""
            name = c.str("name") ?? ""
            isMarkedDelete = c.bool("is_marked_delete") ?? false
        }
    }

    var mainID: String
    var timelines: [Entry]

    init(from decoder: Decoder) throws {
        let c = try decoder.anyKeyed()
        mainID = c.str("main_timeline_id") ?? ""
        timelines = c.list("timelines", of: Entry.self).filter { !$0.id.isEmpty }
    }
}

/// The file a CapCut draft is stored in: `draft_info.json` in CapCut for macOS, `draft_content.json` in
/// CapCut for Windows (seen with new_version 189.0.0). Both are plain JSON of the same format, in the same places:
/// the project folder and `Timelines/<id>/`.
public enum DraftFile {
    public static let names = ["draft_info.json", "draft_content.json"]

    public static func isName(_ fileName: String) -> Bool { names.contains(fileName) }

    /// The draft in `folder`. When there is none, the macOS file name's URL, so that callers have something to report.
    public static func find(in folder: URL) -> URL {
        let urls = names.map { folder.appendingPathComponent($0) }
        return urls.first { FileManager.default.fileExists(atPath: $0.path) } ?? urls[0]
    }

    public static func exists(in folder: URL) -> Bool {
        FileManager.default.fileExists(atPath: find(in: folder).path)
    }
}

public enum ProjectLocator {
    public static var defaultProjectsRoot: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Movies/CapCut/User Data/Projects/com.lveditor.draft", isDirectory: true)
    }

    /// All projects that have a draft, most recently modified first.
    public static func allProjects(root: URL = defaultProjectsRoot) -> [CapCutProject] {
        let fm = FileManager.default
        guard let entries = try? fm.contentsOfDirectory(at: root, includingPropertiesForKeys: [.isDirectoryKey],
                                                        options: [.skipsHiddenFiles]) else { return [] }
        return entries
            .filter { DraftFile.exists(in: $0) }
            .map(CapCutProject.init(url:))
            .sorted { $0.modified > $1.modified }
    }

    /// The project currently open in CapCut. A crashed CapCut can leave stale `.locked` files,
    /// so when several projects look open the most recently modified one wins.
    public static func openProject(root: URL = defaultProjectsRoot) -> CapCutProject? {
        allProjects(root: root).first(where: \.isOpen)
    }

    /// Resolves a CLI argument: a path to a project folder or draft file, or a project name.
    public static func resolve(_ nameOrPath: String, root: URL = defaultProjectsRoot) -> CapCutProject? {
        var url = URL(fileURLWithPath: (nameOrPath as NSString).expandingTildeInPath)
        if DraftFile.isName(url.lastPathComponent) { url.deleteLastPathComponent() }
        if DraftFile.exists(in: url) {
            return CapCutProject(url: url)
        }
        let named = root.appendingPathComponent(nameOrPath, isDirectory: true)
        if DraftFile.exists(in: named) {
            return CapCutProject(url: named)
        }
        return nil
    }
}

public enum DraftLoader {
    public enum LoadError: LocalizedError {
        case unreadable(URL, Error)

        public var errorDescription: String? {
            switch self {
            case let .unreadable(url, error): return "Cannot read \(url.path): \(error.localizedDescription)"
            }
        }
    }

    public static func load(projectURL: URL) throws -> Draft {
        try load(draftURL: DraftFile.find(in: projectURL))
    }

    public static func load(draftURL url: URL) throws -> Draft {
        do {
            let data = try Data(contentsOf: url)
            return try JSONDecoder().decode(Draft.self, from: data)
        } catch {
            throw LoadError.unreadable(url, error)
        }
    }

    private static let placeholder = try! NSRegularExpression(pattern: "##_draftpath_placeholder_[^#]*_##")

    /// Paths inside a draft may start with a placeholder for the project folder.
    public static func resolvePath(_ path: String, projectURL: URL) -> URL? {
        guard !path.isEmpty else { return nil }
        let range = NSRange(path.startIndex..., in: path)
        let resolved = placeholder.stringByReplacingMatches(
            in: path, range: range, withTemplate: NSRegularExpression.escapedTemplate(for: projectURL.path))
        return URL(fileURLWithPath: resolved)
    }
}
