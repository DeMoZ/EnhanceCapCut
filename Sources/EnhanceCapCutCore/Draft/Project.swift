import Foundation

/// A CapCut project folder on disk.
public struct CapCutProject: Identifiable, Hashable {
    public var url: URL
    public var name: String { url.lastPathComponent }
    public var id: URL { url }
    public var modified: Date
    /// CapCut keeps a `.locked` file in the folder of every project that is open in the editor.
    public var isOpen: Bool

    public var draftURL: URL { url.appendingPathComponent("draft_info.json") }

    public init(url: URL) {
        self.url = url
        let fm = FileManager.default
        let draft = url.appendingPathComponent("draft_info.json")
        let attrs = (try? fm.attributesOfItem(atPath: draft.path)) ?? (try? fm.attributesOfItem(atPath: url.path))
        modified = attrs?[.modificationDate] as? Date ?? .distantPast
        isOpen = fm.fileExists(atPath: url.appendingPathComponent(".locked").path)
    }

    public func loadDraft() throws -> Draft {
        try DraftLoader.load(projectURL: url)
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
            .filter { fm.fileExists(atPath: $0.appendingPathComponent("draft_info.json").path) }
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
        let fm = FileManager.default
        var url = URL(fileURLWithPath: (nameOrPath as NSString).expandingTildeInPath)
        if url.lastPathComponent == "draft_info.json" { url.deleteLastPathComponent() }
        if fm.fileExists(atPath: url.appendingPathComponent("draft_info.json").path) {
            return CapCutProject(url: url)
        }
        let named = root.appendingPathComponent(nameOrPath, isDirectory: true)
        if fm.fileExists(atPath: named.appendingPathComponent("draft_info.json").path) {
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
        let url = projectURL.appendingPathComponent("draft_info.json")
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
