import AppKit
import AVFoundation
import EnhanceCapCutCore
import SwiftUI
import Translation

/// State of the subtitles & voice-over window: chosen languages and voices, and the running job. The
/// project and timeline are the ones selected in the export window (`ExportModel`). Ported from SubDub's panel.
@available(macOS 26, *)
@MainActor
final class SubtitlesModel: ObservableObject {
    enum Phase {
        case idle
        case running(step: String, fraction: Double)
        case done(DraftSubtitleWriter.Result, summary: String, srt: [URL])
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

    // Output
    @Published var makeCaptions: Bool {
        didSet { UserDefaults.standard.set(makeCaptions, forKey: "subtitles.makeCaptions") }
    }
    @Published var makeDub: Bool {
        didSet { UserDefaults.standard.set(makeDub, forKey: "subtitles.makeDub") }
    }
    /// Chosen voice per "language|gender" (missing = the best match).
    @Published var dubVoices: [String: String] {
        didSet { UserDefaults.standard.set(dubVoices, forKey: "subtitles.dubVoices") }
    }
    @Published var voiceGender: VoiceGender {
        didSet { UserDefaults.standard.set(voiceGender.rawValue, forKey: "subtitles.voiceGender") }
    }
    @Published var originalAudio: OriginalAudio {
        didSet { UserDefaults.standard.set(originalAudio.rawValue, forKey: "subtitles.originalAudio") }
    }
    /// Language code of the voice-over track left audible; the other tracks are added muted.
    @Published var audibleVoice: String {
        didSet { UserDefaults.standard.set(audibleVoice, forKey: "subtitles.audibleVoice") }
    }

    /// Siri voices (only with Xcode installed, see `SiriVoices`).
    @Published var siriVoices: [DubVoice] = []
    @Published var siriLoading = false
    /// Language code whose Siri preview is being generated.
    @Published var previewing: String?
    /// Language whose "Get voices…" was opened, to show what to look for in Settings.
    @Published var voiceHelpLanguage: String?
    var siriPossible: Bool { SiriVoices.isAvailable }

    private var voiceCache: [String: [DubVoice]] = [:]
    private let previewSynth = AVSpeechSynthesizer()
    private var voicesObserver: NSObjectProtocol?

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
        makeCaptions = d.object(forKey: "subtitles.makeCaptions") as? Bool ?? true
        makeDub = d.object(forKey: "subtitles.makeDub") as? Bool ?? false
        dubVoices = d.dictionary(forKey: "subtitles.dubVoices") as? [String: String] ?? [:]
        voiceGender = VoiceGender(rawValue: d.string(forKey: "subtitles.voiceGender") ?? "") ?? .female
        originalAudio = OriginalAudio(rawValue: d.string(forKey: "subtitles.originalAudio") ?? "") ?? .lower
        audibleVoice = d.string(forKey: "subtitles.audibleVoice") ?? ""

        // Voices downloaded in System Settings appear in the window without a manual refresh.
        voicesObserver = NotificationCenter.default.addObserver(
            forName: AVSpeechSynthesizer.availableVoicesDidChangeNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.reloadVoices() }
        }
    }

    deinit {
        if let voicesObserver { NotificationCenter.default.removeObserver(voicesObserver) }
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

    /// The caption tracks that will be created, in order.
    var outputLanguages: [SubtitleLanguage] {
        guard makeCaptions else { return [] }
        return (includeOriginal ? [sourceLanguage] : []) + effectiveTargets
    }

    /// The voice-over tracks that will be created: every translation.
    var dubLanguages: [SubtitleLanguage] { makeDub ? effectiveTargets : [] }

    /// The audible voice-over track: the saved choice when it's among them, else the first one.
    var effectiveAudibleVoice: String? {
        let codes = dubLanguages.map(\.code)
        return codes.contains(audibleVoice) ? audibleVoice : codes.first
    }

    /// The visible track: the saved choice when it's among the outputs, else the first one.
    var effectiveVisibleLanguage: String? {
        let codes = outputLanguages.map(\.code)
        return codes.contains(visibleLanguage) ? visibleLanguage : codes.first
    }

    var canStart: Bool {
        export.timeline != nil && !isRunning && downloadingLanguage == nil
            && !(outputLanguages.isEmpty && dubLanguages.isEmpty)
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
        await loadSiriVoices()
    }

    // MARK: Voices

    func voices(for language: SubtitleLanguage) -> [DubVoice] {
        if let cached = voiceCache[language.code] { return cached }
        let wanted = Locale.Language(identifier: language.code).languageCode
        let siri = siriVoices.filter { Locale.Language(identifier: $0.language).languageCode == wanted }
        let list = siri + Dubber.voices(for: language.code)
        voiceCache[language.code] = list
        return list
    }

    func loadSiriVoices() async {
        guard SiriVoices.isAvailable, !siriLoading, siriVoices.isEmpty else { return }
        siriLoading = true
        siriVoices = await SiriVoices.list()
        siriLoading = false
        reloadVoices()
    }

    /// Voices offered in the picker: the chosen gender, or all voices when none of that gender is installed.
    func pickerVoices(for language: SubtitleLanguage) -> [DubVoice] {
        let all = voices(for: language)
        let matching = all.filter { $0.gender == voiceGender }
        return matching.isEmpty ? all : matching
    }

    /// True when no voice of the chosen gender is installed for the language.
    func lacksGender(_ language: SubtitleLanguage) -> Bool {
        let all = voices(for: language)
        return !all.isEmpty && !all.contains { $0.gender == voiceGender }
    }

    private func choiceKey(_ language: SubtitleLanguage) -> String { "\(language.code)|\(voiceGender.rawValue)" }

    /// The voice used for a language: the saved choice for the current gender if still installed, else the best match.
    func voiceID(for language: SubtitleLanguage) -> String {
        let list = pickerVoices(for: language)
        if let saved = dubVoices[choiceKey(language)], list.contains(where: { $0.id == saved }) { return saved }
        // A Siri voice of the right gender beats the built-in voices.
        if let siri = list.first(where: { $0.isSiri && $0.gender == voiceGender }) { return siri.id }
        return Dubber.defaultVoice(for: language.code, gender: voiceGender)?.id ?? list.first?.id ?? ""
    }

    func setVoice(_ id: String, for language: SubtitleLanguage) {
        dubVoices[choiceKey(language)] = id
    }

    func preview(_ language: SubtitleLanguage) {
        previewSynth.stopSpeaking(at: .immediate)
        let id = voiceID(for: language)
        if id.hasPrefix(SiriVoices.idPrefix) {
            previewing = language.code
            Task {
                do { try await SiriVoices.preview(Self.sample(for: language), voiceID: id) } catch { downloadError = error.localizedDescription }
                previewing = nil
            }
            return
        }
        let utterance = AVSpeechUtterance(string: Self.sample(for: language))
        utterance.voice = AVSpeechSynthesisVoice(identifier: id)
        previewSynth.speak(utterance)
    }

    func getVoices(for language: SubtitleLanguage) {
        voiceHelpLanguage = language.code
        Self.openVoiceSettings()
    }

    /// Refreshes the voice list, e.g. after the user installs voices in System Settings.
    func reloadVoices() {
        voiceCache = [:]
        // The voice the user went to download has arrived: hide the hint.
        if let code = voiceHelpLanguage, let lang = targetLanguages.first(where: { $0.code == code }),
           voices(for: lang).contains(where: { $0.gender == voiceGender }) {
            voiceHelpLanguage = nil
        }
        objectWillChange.send()
    }

    static func openVoiceSettings() {
        for s in ["x-apple.systempreferences:com.apple.Accessibility-Settings.extension?SpokenContent",
                  "x-apple.systempreferences:com.apple.Accessibility-Settings.extension"] {
            if let url = URL(string: s), NSWorkspace.shared.open(url) { return }
        }
    }

    private static func sample(for language: SubtitleLanguage) -> String {
        let samples = [
            "ar": "هكذا سيبدو التعليق الصوتي.", "de": "So klingt die Synchronisation.",
            "en": "This is how the voice-over will sound.", "es": "Así sonará el doblaje.",
            "fr": "Voici comment sonnera le doublage.", "hi": "वॉइस-ओवर ऐसा सुनाई देगा।",
            "id": "Beginilah suara sulih suaranya.", "it": "Ecco come suonerà il doppiaggio.",
            "ja": "吹き替えはこのように聞こえます。", "ko": "더빙은 이렇게 들립니다.",
            "nl": "Zo klinkt de voice-over.", "pl": "Tak będzie brzmiał lektor.",
            "pt": "É assim que a dublagem vai soar.", "ru": "Так будет звучать озвучка.",
            "th": "นี่คือตัวอย่างเสียงพากย์", "tr": "Seslendirme böyle duyulacak.",
            "uk": "Так звучатиме озвучення.", "vi": "Đây là giọng lồng tiếng.",
            "zh": "这是配音的效果。",
        ]
        if language.code == "zh-Hant" { return "這是配音的效果。" }
        return samples[String(language.code.prefix(2))] ?? language.nativeName
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
        guard canStart, export.timeline != nil, let project = export.selectedProject,
              let projectTimeline = export.selectedTimeline else { return }
        refreshProjectState()
        if projectIsOpen {
            phase = .failed(DraftSubtitleError.projectOpen(project.name).localizedDescription)
            return
        }
        var options = SubtitlePipeline.Options(sourceLocale: Locale(identifier: sourceLocaleID),
                                               includeOriginal: includeOriginal, targets: effectiveTargets,
                                               visibleLanguage: effectiveVisibleLanguage)
        options.captions = makeCaptions
        options.voices = Dictionary(uniqueKeysWithValues: dubLanguages.map { ($0.code, voiceID(for: $0)) })
        options.audibleVoice = effectiveAudibleVoice
        options.voiceOverFolder = SubtitlePipeline.voiceOverFolder(in: project)
        let pipeline = SubtitlePipeline(translator: translator)
        self.pipeline = pipeline
        let replace = replacePrevious
        let original = originalAudio
        let srtFolder = saveSRT && makeCaptions
            ? URL(fileURLWithPath: (export.settings.outputFolder as NSString).expandingTildeInPath) : nil
        let baseName = projectTimeline.isMain ? project.name : "\(project.name) \(projectTimeline.name)"
        phase = .running(step: "Preparing…", fraction: 0)

        task = Task {
            // The voice-over audio goes away with a failed run, unless the draft already points at it.
            var draftWritten = false
            func discardAudio() {
                if !draftWritten { options.voiceOverFolder.map { try? FileManager.default.removeItem(at: $0) } }
            }
            do {
                // Earlier voice-over must not be heard by speech recognition.
                let timeline = try SubtitlePipeline.speechTimeline(project: project, timeline: projectTimeline)
                let output = try await pipeline.run(timeline: timeline, options: options) { step, fraction in
                    Task { @MainActor in
                        if case .running = self.phase { self.phase = .running(step: step, fraction: fraction) }
                    }
                }
                try Task.checkCancellation()
                let tracks = output.captions
                let result = try DraftSubtitleWriter.write(project: project, timeline: projectTimeline,
                                                           cueCount: tracks.reduce(0) { $0 + $1.cues.count }) {
                    try SubtitlePipeline.apply(output, to: $0, original: original, replacePrevious: replace,
                                               projectURL: project.url)
                }
                draftWritten = true
                let srt = try srtFolder.map { try SubtitlePipeline.writeSRT(tracks, to: $0, baseName: baseName) } ?? []
                var parts: [String] = []
                if !tracks.isEmpty {
                    parts.append("\(result.cueCount) captions (\(tracks.map(\.language.code).joined(separator: ", ")))")
                }
                if !output.voiceOver.isEmpty {
                    parts.append("voice-over (\(output.voiceOver.map(\.language.code).joined(separator: ", ")))")
                }
                phase = .done(result, summary: "Added \(parts.joined(separator: " and ")) to \(project.name)", srt: srt)
                // The export window shows the new captions too.
                export.loadTimeline()
                NSApp.requestUserAttention(.informationalRequest)
            } catch is CancellationError {
                phase = .idle
                discardAudio()
            } catch {
                phase = .failed(error.localizedDescription)
                discardAudio()
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
