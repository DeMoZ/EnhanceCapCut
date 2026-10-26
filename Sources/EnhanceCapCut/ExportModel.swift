import AppKit
import EnhanceCapCutCore
import SwiftUI

@MainActor
final class ExportModel: ObservableObject {
    enum State {
        case idle
        case exporting(ExportProgress?)
        case finished(URL, warnings: [String], seconds: TimeInterval)
        case failed(String)
    }

    /// Projects in CapCut's order: most recently modified first.
    @Published var projects: [CapCutProject] = []
    /// Show the project list by name instead of CapCut's order. Only affects the list, not the default selection.
    @Published var sortProjectsByName = UserDefaults.standard.bool(forKey: "sortProjectsByName") {
        didSet { UserDefaults.standard.set(sortProjectsByName, forKey: "sortProjectsByName") }
    }
    @Published var selectedProjectURL: URL? {
        didSet { if oldValue != selectedProjectURL { reloadTimelines(keepSelection: false) } }
    }
    /// Timelines of the selected project (CapCut 9 projects can have several).
    @Published private(set) var timelines: [ProjectTimeline] = []
    @Published var selectedTimelineID: String? {
        didSet { if oldValue != selectedTimelineID { loadTimeline() } }
    }
    @Published private(set) var timeline: Timeline?
    @Published private(set) var loadError: String?
    @Published var settings = ExportSettings.load() {
        didSet { settings.save() }
    }
    @Published private(set) var state: State = .idle

    /// Opens the subtitles window (set by the app delegate).
    var openSubtitles: (() -> Void)?
    /// Opens the "Support EnhanceCapCut" window (set by the app delegate).
    var openDonate: (() -> Void)?
    /// Sparkle's "Check for Updates…" (nil outside the app bundle).
    var checkForUpdates: (() -> Void)?
    /// "0.6.0" from the bundle; empty for a `swift run` build.
    let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""

    private var exporter: Exporter?
    private var autoExported = false

    var displayedProjects: [CapCutProject] {
        guard sortProjectsByName else { return projects }
        return projects.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    var selectedProject: CapCutProject? { projects.first { $0.url == selectedProjectURL } }
    var selectedTimeline: ProjectTimeline? { timelines.first { $0.id == selectedTimelineID } }
    var isExporting: Bool { if case .exporting = state { return true } else { return false } }
    var ffmpegAvailable: Bool { LoudnessNormalizer.ffmpegPath != nil }

    var outputSize: CGSize? { timeline.map { settings.outputSize(canvas: $0.canvasSize) } }
    var outputFPS: Double? { timeline.map { settings.fps(project: $0.fps) } }

    var outputURL: URL? {
        guard let project = selectedProject, let size = outputSize, let fps = outputFPS else { return nil }
        return URL(fileURLWithPath: (settings.outputFolder as NSString).expandingTildeInPath, isDirectory: true)
            .appendingPathComponent(settings.fileName(project: outputName(project), size: size, fps: fps))
    }

    func refreshProjects(selectOpen: Bool) {
        let previous = selectedProjectURL
        projects = ProjectLocator.allProjects()
        if selectOpen, let open = ProjectLocator.openProject() {
            selectedProjectURL = open.url
        } else if let previous, projects.contains(where: { $0.url == previous }) {
            selectedProjectURL = previous
        } else {
            // Nothing open in CapCut: the most recently edited project (first in CapCut's order).
            selectedProjectURL = projects.first?.url
        }
        // Reload even when the selection didn't change: the draft or its timelines may have been edited.
        if selectedProjectURL == previous { reloadTimelines(keepSelection: true) }
        if case .finished = state { state = .idle }
        if case .failed = state { state = .idle }
    }

    /// Re-reads the project's timeline list. A newly selected project starts on its main timeline.
    private func reloadTimelines(keepSelection: Bool) {
        timelines = selectedProject?.timelines() ?? []
        let id = keepSelection && selectedTimeline != nil ? selectedTimelineID
            : (timelines.first(where: \.isMain) ?? timelines.first)?.id
        if id == selectedTimelineID { loadTimeline() } else { selectedTimelineID = id }
    }

    /// The `{project}` token: the project name, plus the timeline name for timelines other than the main one.
    private func outputName(_ project: CapCutProject) -> String {
        guard timelines.count > 1, let timeline = selectedTimeline, !timeline.isMain else { return project.name }
        return "\(project.name) \(timeline.name)"
    }

    func loadTimeline() {
        timeline = nil
        loadError = nil
        guard let project = selectedProject, let projectTimeline = selectedTimeline else { return }
        Task.detached(priority: .userInitiated) {
            do {
                let timeline = try TimelineBuilder.build(project: project, timeline: projectTimeline)
                await MainActor.run {
                    guard self.selectedProjectURL == project.url, self.selectedTimelineID == projectTimeline.id else { return }
                    self.timeline = timeline
                    // `--args -autoExport YES` exports right away with the saved settings (for scripting).
                    if UserDefaults.standard.bool(forKey: "autoExport"), !self.autoExported {
                        self.autoExported = true
                        self.startExport()
                    }
                }
            } catch {
                await MainActor.run { self.loadError = error.localizedDescription }
            }
        }
    }

    func chooseOutputFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = "Choose"
        panel.directoryURL = URL(fileURLWithPath: (settings.outputFolder as NSString).expandingTildeInPath)
        if panel.runModal() == .OK, let url = panel.url { settings.outputFolder = url.path }
    }

    func startExport() {
        guard let timeline, let url = outputURL, !isExporting else { return }
        if FileManager.default.fileExists(atPath: url.path) {
            let alert = NSAlert()
            alert.messageText = "\(url.lastPathComponent) already exists"
            alert.informativeText = "Replace it?"
            alert.addButton(withTitle: "Replace")
            alert.addButton(withTitle: "Cancel")
            guard alert.runModal() == .alertFirstButtonReturn else { return }
        }
        let exporter = Exporter(timeline: timeline, settings: settings, outputURL: url)
        self.exporter = exporter
        state = .exporting(nil)
        let started = Date()
        Task.detached(priority: .userInitiated) {
            do {
                try await exporter.run { progress in
                    Task { @MainActor in
                        if case .exporting = self.state { self.state = .exporting(progress) }
                    }
                }
                let warnings = exporter.warnings
                await MainActor.run {
                    self.state = .finished(url, warnings: warnings, seconds: Date().timeIntervalSince(started))
                    NSApp.requestUserAttention(.informationalRequest)
                }
            } catch RenderError.cancelled {
                try? FileManager.default.removeItem(at: url)
                await MainActor.run { self.state = .idle }
            } catch {
                await MainActor.run { self.state = .failed(error.localizedDescription) }
            }
        }
    }

    func cancel() { exporter?.cancel() }

    func revealOutput(_ url: URL) { NSWorkspace.shared.activateFileViewerSelecting([url]) }
}
