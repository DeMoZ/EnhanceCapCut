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

    @Published var projects: [CapCutProject] = []
    @Published var selectedProjectURL: URL? {
        didSet { if oldValue != selectedProjectURL { loadTimeline() } }
    }
    @Published private(set) var timeline: Timeline?
    @Published private(set) var loadError: String?
    @Published var settings = ExportSettings.load() {
        didSet { settings.save() }
    }
    @Published private(set) var state: State = .idle

    private var exporter: Exporter?
    private var autoExported = false

    var selectedProject: CapCutProject? { projects.first { $0.url == selectedProjectURL } }
    var isExporting: Bool { if case .exporting = state { return true } else { return false } }
    var ffmpegAvailable: Bool { LoudnessNormalizer.ffmpegPath != nil }

    var outputSize: CGSize? { timeline.map { settings.outputSize(canvas: $0.canvasSize) } }
    var outputFPS: Double? { timeline.map { settings.fps(project: $0.fps) } }

    var outputURL: URL? {
        guard let project = selectedProject, let size = outputSize, let fps = outputFPS else { return nil }
        return URL(fileURLWithPath: (settings.outputFolder as NSString).expandingTildeInPath, isDirectory: true)
            .appendingPathComponent(settings.fileName(project: project.name, size: size, fps: fps))
    }

    func refreshProjects(selectOpen: Bool) {
        let previous = selectedProjectURL
        projects = ProjectLocator.allProjects()
        if selectOpen, let open = ProjectLocator.openProject() {
            selectedProjectURL = open.url
        } else if let previous, projects.contains(where: { $0.url == previous }) {
            selectedProjectURL = previous
        } else {
            selectedProjectURL = projects.first?.url
        }
        // Reload even when the selection didn't change: the draft may have been edited.
        if selectedProjectURL == previous { loadTimeline() }
        if case .finished = state { state = .idle }
        if case .failed = state { state = .idle }
    }

    func loadTimeline() {
        timeline = nil
        loadError = nil
        guard let project = selectedProject else { return }
        Task.detached(priority: .userInitiated) {
            do {
                let timeline = try TimelineBuilder.build(project: project)
                await MainActor.run {
                    guard self.selectedProjectURL == project.url else { return }
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
