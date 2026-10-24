import AppKit
import EnhanceCapCutCore
import SwiftUI
import Translation

/// State of the subtitles window: chosen languages and the running job. The project and timeline are
/// the ones selected in the export window (`ExportModel`). Ported from SubDub's panel.
@available(macOS 26, *)
@MainActor
final class SubtitlesModel: ObservableObject {
    enum Phase {
        case idle
        case running(step: String, fraction: Double)
        case done(DraftSubtitleWriter.Result, project: String, languages: [String], srt: [URL])
        case failed(String)
    }

    let export: ExportModel
    @Published var phase: Phase = .idle

    @Published var sourceLocales: [Locale] = []
    @Published var targetLanguages: [SubtitleLanguage] = []

    @Published var sourceLocaleID: String {
        didSet {
            UserDefaults.standard.set(sourceLocaleID, forKey: "subtitles.sourceLocale")
            Task { await refreshDownloads() }
        }
    }
    @Published var selectedTargets: Set<String> {
        didSet {
            UserDefaults.standard.set(Array(selectedTargets), forKey: "subtitles.targets")
            Task { await refreshDownloads() }
        }
    }
    @Published var includeOriginal: Bool {
        didSet { UserDefaults.standard.set(includeOriginal, forKey: "subtitles.includeOriginal") }
    }
    /// Language code of the track left visible; the other tracks are added hidden.
    @Published var visibleLanguage: String {
        didSet { UserDefaults.standard.set(visibleLanguage, forKey: "subtitles.visible") }
    }
    @Published var replacePrevious: Bool {
        didSet { UserDefaults.standard.set(replacePrevious, forKey: "subtitles.replacePrevious") }
    }
    @Published var saveSRT: Bool {
        didSet { UserDefaults.standard.set(saveSRT, forKey: "subtitles.saveSRT") }
    }

    /// Selected translation languages that still need a one-time download.
    @Published var notDownloaded: [SubtitleLanguage] = []
    @Published var downloadingLanguage: String?
    @Published var downloadError: String?
    /// The selected project is open in CapCut, so the draft can't be written now.
    @Published private(set) var projectIsOpen = false

    let translator = TranslationBroker()
    private var task: Task<Void, Never>?
    private var pipeline: SubtitlePipeline?
    private var pollTask: Task<Void, Never>?

    init(export: ExportModel) {
        self.export = export
        let d = UserDefaults.standard
        sourceLocaleID = d.string(forKey: "subtitles.sourceLocale") ?? "ru-RU"
        selectedTargets = Set(d.stringArray(forKey: "subtitles.targets") ?? ["en"])
        includeOriginal = d.object(forKey: "subtitles.includeOriginal") as? Bool ?? true
        visibleLanguage = d.string(forKey: "subtitles.visible") ?? ""
        replacePrevious = d.object(forKey: "subtitles.replacePrevious") as? Bool ?? true
        saveSRT = d.object(forKey: "subtitles.saveSRT") as? Bool ?? true
    }

    var isRunning: Bool {
        if case .running = phase { return true }
        return false
    }

    var sourceLanguage: SubtitleLanguage { SubtitleLanguage(Locale(identifier: sourceLocaleID).language) }

    /// Selected targets in display order, without the source language itself.
    var effectiveTargets: [SubtitleLanguage] {
        targetLanguages.filter { selectedTargets.contains($0.code) && $0.code != sourceLanguage.code }
    }

    /// The tracks that will be created, in order.
    var outputLanguages: [SubtitleLanguage] {
        (includeOriginal ? [sourceLanguage] : []) + effectiveTargets
    }

    /// The visible track: the saved choice when it's among the outputs, else the first one.
    var effectiveVisibleLanguage: String? {
        let codes = outputLanguages.map(\.code)
        return codes.contains(visibleLanguage) ? visibleLanguage : codes.first
    }

    var canStart: Bool {
        export.timeline != nil && !isRunning && downloadingLanguage == nil && !outputLanguages.isEmpty
    }

    /// Re-checks whether the selected project is open in CapCut.
    func refreshProjectState() {
        projectIsOpen = export.selectedProject.map(DraftSubtitleWriter.isOpenInCapCut) ?? false
        if case .done = phase { phase = .idle }
        if case .failed = phase { phase = .idle }
    }

    func loadLanguages() async {
        refreshProjectState()
        guard sourceLocales.isEmpty else { return }
        let locales = await Transcriber.supportedLocales()
        let en = Locale(identifier: "en")
        sourceLocales = locales.sorted {
            (en.localizedString(forIdentifier: $0.identifier) ?? "") < (en.localizedString(forIdentifier: $1.identifier) ?? "")
        }
        if !sourceLocales.contains(where: { $0.identifier(.bcp47) == sourceLocaleID }),
           let first = sourceLocales.first(where: { $0.language.languageCode?.identifier == "ru" }) ?? sourceLocales.first {
            sourceLocaleID = first.identifier(.bcp47)
        }
        targetLanguages = await Translator.supportedTargets()
        await refreshDownloads()
    }

    func toggle(_ language: SubtitleLanguage) {
        if selectedTargets.contains(language.code) {
            selectedTargets.remove(language.code)
        } else {
            selectedTargets.insert(language.code)
        }
    }

    func refreshDownloads() async {
        let source = Locale(identifier: sourceLocaleID).language
        var missing: [SubtitleLanguage] = []
        for target in effectiveTargets {
            if await LanguageAvailability().status(from: source, to: target.language) == .supported {
                missing.append(target)
            }
        }
        notDownloaded = missing
        if missing.isEmpty {
            downloadError = nil
            pollTask?.cancel()
            pollTask = nil
        } else if pollTask == nil {
            // Downloads continue in the background after the system dialog closes: keep checking.
            pollTask = Task { [weak self] in
                while !Task.isCancelled {
                    try? await Task.sleep(for: .seconds(3))
                    guard let self, !Task.isCancelled else { return }
                    await self.refreshDownloads()
                }
            }
        }
    }

    /// Downloads every missing language one by one through the system prompt.
    func downloadMissing() async {
        downloadError = nil
        let source = Locale(identifier: sourceLocaleID).language
        for target in notDownloaded {
            downloadingLanguage = target.displayName
            do {
                try await translator.download(from: source, to: target)
            } catch {
                downloadError = error.localizedDescription
                break
            }
        }
        downloadingLanguage = nil
        await refreshDownloads()
        if downloadError == nil, !notDownloaded.isEmpty {
            let names = notDownloaded.map(\.displayName).joined(separator: ", ")
            downloadError = "\(names) not downloaded yet. In the system dialog click Download next to each language and wait until it finishes — or use Open Settings. This window updates automatically."
        }
    }

    func start() {
        guard canStart, let timeline = export.timeline, let project = export.selectedProject,
              let projectTimeline = export.selectedTimeline else { return }
        refreshProjectState()
        if projectIsOpen {
            phase = .failed(DraftSubtitleError.projectOpen(project.name).localizedDescription)
            return
        }
        let options = SubtitlePipeline.Options(sourceLocale: Locale(identifier: sourceLocaleID),
                                               includeOriginal: includeOriginal, targets: effectiveTargets,
                                               visibleLanguage: effectiveVisibleLanguage)
        let pipeline = SubtitlePipeline(translator: translator)
        self.pipeline = pipeline
        let replace = replacePrevious
        let srtFolder = saveSRT ? URL(fileURLWithPath: (export.settings.outputFolder as NSString).expandingTildeInPath) : nil
        let baseName = projectTimeline.isMain ? project.name : "\(project.name) \(projectTimeline.name)"
        phase = .running(step: "Preparing…", fraction: 0)

        task = Task {
            do {
                let tracks = try await pipeline.run(timeline: timeline, options: options) { step, fraction in
                    Task { @MainActor in
                        if case .running = self.phase { self.phase = .running(step: step, fraction: fraction) }
                    }
                }
                try Task.checkCancellation()
                let result = try DraftSubtitleWriter.write(tracks, project: project, timeline: projectTimeline,
                                                           replacePrevious: replace)
                let srt = try srtFolder.map { try SubtitlePipeline.writeSRT(tracks, to: $0, baseName: baseName) } ?? []
                phase = .done(result, project: project.name, languages: tracks.map(\.language.code), srt: srt)
                // The export window shows the new captions too.
                export.loadTimeline()
                NSApp.requestUserAttention(.informationalRequest)
            } catch is CancellationError {
                phase = .idle
            } catch {
                phase = .failed(error.localizedDescription)
            }
            await refreshDownloads()
        }
    }

    func cancel() {
        pipeline?.cancel()
        task?.cancel()
        task = nil
        phase = .idle
    }

    func reveal(_ url: URL) { NSWorkspace.shared.activateFileViewerSelecting([url]) }
}
