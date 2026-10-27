import Foundation

/// A dubbed copy of a timeline, added to the project as a timeline of its own: the source timeline stays as it was.
///
/// A new timeline is an entry in `Timelines/project.json`, a folder `Timelines/<id>/` with the draft (whose root `id`
/// is the timeline's) and its `template-2.tmp` copy, and a tab in `timeline_layout.json`'s first dock (see
/// docs/FORMAT.md). The source's plain-JSON attachments are copied along; `draft.extra` (binary) is left to CapCut.
public enum DubTimeline {
    /// Files and folders next to a timeline's draft that are copied into the new timeline's folder.
    static let attachments = ["attachment_editing.json", "attachment_pc_common.json", "common_attachment"]

    public enum Failure: LocalizedError {
        case noTimelineList(String)

        public var errorDescription: String? {
            switch self {
            case let .noTimelineList(name):
                return L("{0} is from an older CapCut that keeps a single timeline. Open it in CapCut once, "
                    + "close it, and try again.", name)
            }
        }
    }

    /// `<project>_dub_<languages>_<original audio>_<n>`, with the first `n` from 1 that no timeline has.
    public static func name(project: CapCutProject, languages: [String], original: OriginalAudio) -> String {
        let taken = Set(project.timelines().map(\.name))
        let base = "\(project.name)_dub_\(languages.joined(separator: "-"))_\(original.nameToken)"
        var n = 1
        while taken.contains("\(base)_\(n)") { n += 1 }
        return "\(base)_\(n)"
    }

    /// Adds a timeline named `name` to the project: `source`'s draft run through `transform`. `project.json` and
    /// `timeline_layout.json` are backed up to `DraftSubtitleWriter.backupRoot` first.
    public static func create(project: CapCutProject, source: ProjectTimeline, name: String, cueCount: Int,
                              transform: (Data) throws -> Data) throws -> (ProjectTimeline, DraftSubtitleWriter.Result) {
        if DraftSubtitleWriter.isOpenInCapCut(project) { throw DraftSubtitleError.projectOpen(project.name) }
        let fm = FileManager.default
        let timelines = project.url.appendingPathComponent("Timelines", isDirectory: true)
        let listURL = timelines.appendingPathComponent("project.json")
        guard let list = try? Data(contentsOf: listURL) else { throw Failure.noTimelineList(project.name) }
        let layoutURL = project.url.appendingPathComponent("timeline_layout.json")
        let layout = try? Data(contentsOf: layoutURL)

        let id = UUID().uuidString
        let draft = try withRootID(transform(Data(contentsOf: source.draftURL)), id)
        guard let newList = try addEntry(to: list, id: id, name: name, now: Int64(Date().timeIntervalSince1970 * 1_000_000))
        else { throw Failure.noTimelineList(project.name) }
        let newLayout = try layout.map { try addTab(to: $0, id: id, name: name) }

        let stamp = ISO8601DateFormatter().string(from: Date()).replacingOccurrences(of: ":", with: "-")
        let backup = DraftSubtitleWriter.backupRoot.appendingPathComponent(project.name, isDirectory: true)
            .appendingPathComponent(stamp, isDirectory: true)
        try fm.createDirectory(at: backup.appendingPathComponent("Timelines"), withIntermediateDirectories: true)
        try fm.copyItem(at: listURL, to: backup.appendingPathComponent("Timelines/project.json"))
        if layout != nil { try fm.copyItem(at: layoutURL, to: backup.appendingPathComponent("timeline_layout.json")) }

        let folder = timelines.appendingPathComponent(id, isDirectory: true)
        try fm.createDirectory(at: folder, withIntermediateDirectories: true)
        do {
            let sourceFolder = source.draftURL.deletingLastPathComponent()
            for item in attachments where fm.fileExists(atPath: sourceFolder.appendingPathComponent(item).path) {
                try fm.copyItem(at: sourceFolder.appendingPathComponent(item), to: folder.appendingPathComponent(item))
            }
            let files = [folder.appendingPathComponent(source.draftURL.lastPathComponent),
                         folder.appendingPathComponent("template-2.tmp")]
            for url in files { try draft.write(to: url, options: .atomic) }
            // The list last: until then CapCut doesn't know the folder.
            try newList.write(to: listURL, options: .atomic)
            if let newLayout { try newLayout.write(to: layoutURL, options: .atomic) }
            let timeline = ProjectTimeline(id: id, name: name, draftURL: files[0], isMain: false)
            return (timeline, DraftSubtitleWriter.Result(files: files, backupFolder: backup, cueCount: cueCount))
        } catch {
            try? fm.removeItem(at: folder)
            throw error
        }
    }

    /// The draft with its root `id` (the timeline's id) replaced.
    static func withRootID(_ draft: Data, _ id: String) throws -> Data {
        let scanner = JSONScanner(draft)
        let root = try scanner.root()
        var edits = JSONEdits()
        if let range = try scanner.value("id", in: root) {
            edits.replace(range, with: JSONText.encode(id))
        } else {
            edits.replace(root.lowerBound + 1..<root.lowerBound + 1,
                          with: "\"id\":\(JSONText.encode(id))" + (try scanner.members(root).isEmpty ? "" : ","))
        }
        return edits.applied(to: scanner.bytes)
    }

    /// `project.json` with an entry for the new timeline appended to `timelines`; nil without that list.
    static func addEntry(to list: Data, id: String, name: String, now: Int64) throws -> Data? {
        let scanner = JSONScanner(list)
        let root = try scanner.root()
        let entry = JSONText.encode([
            "create_time": now, "id": id, "is_marked_delete": false, "name": name, "update_time": now,
        ] as [String: Any])
        guard let range = try scanner.value("timelines", in: root), scanner.bytes[range.lowerBound] == UInt8(ascii: "[")
        else { return nil }
        var edits = JSONEdits()
        let close = range.upperBound - 1
        edits.replace(close..<close, with: (try scanner.elements(range).isEmpty ? "" : ",") + entry)
        return edits.applied(to: scanner.bytes)
    }

    /// `timeline_layout.json` with the timeline added as a tab of the first dock; unchanged when it has no dock.
    static func addTab(to layout: Data, id: String, name: String) throws -> Data {
        let scanner = JSONScanner(layout)
        guard let docks = try scanner.value("dockItems", in: try scanner.root()),
              scanner.bytes[docks.lowerBound] == UInt8(ascii: "["),
              let dock = try scanner.elements(docks).first,
              let ids = try scanner.value("timelineIds", in: dock), scanner.bytes[ids.lowerBound] == UInt8(ascii: "["),
              let names = try scanner.value("timelineNames", in: dock), scanner.bytes[names.lowerBound] == UInt8(ascii: "[")
        else { return layout }
        var edits = JSONEdits()
        for (range, value) in [(ids, id), (names, name)] {
            let close = range.upperBound - 1
            edits.replace(close..<close, with: (try scanner.elements(range).isEmpty ? "" : ",") + JSONText.encode(value))
        }
        return edits.applied(to: scanner.bytes)
    }
}
