import EnhanceCapCutCore
import SwiftUI

/// The "Subtitles & Voice-over" window: transcribes the timeline, translates it and adds the result
/// to the CapCut project as caption tracks and as voice-over audio tracks spoken by system voices.
@available(macOS 26, *)
struct SubtitlesView: View {
    @ObservedObject var model: SubtitlesModel
    @ObservedObject var export: ExportModel

    init(model: SubtitlesModel) {
        self.model = model
        export = model.export
    }

    var body: some View {
        VStack(spacing: 0) {
            Form {
                projectSection
                createSection
                languagesSection
                if !model.dubLanguages.isEmpty { voiceSection }
                downloadSection
                optionsSection
            }
            .formStyle(.grouped)
            .disabled(model.isRunning)

            Divider()
            footer.padding(16)
        }
        .frame(minWidth: 480, minHeight: 560)
        .task { await model.loadLanguages() }
        .onChange(of: export.selectedProjectURL) { model.refreshProjectState() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            model.refreshProjectState()
        }
        .modifier(TranslationHost(broker: model.translator))
        .alert("Clone voices only with consent", isPresented: $model.askConsent) {
            Button("Cancel", role: .cancel) {}
            Button("Create") { model.start(consented: true) }
        } message: {
            Text(SubtitlesView.consentText)
        }
    }

    /// The warning before cloning, the same as the Windows version's.
    static let consentText = "The voice-over will copy the voices of the people speaking in this video. Use it only "
        + "for your own voice or for people who agreed to have their voice cloned. Click Create only if you have "
        + "their consent."

    // MARK: Sections

    private var projectSection: some View {
        Section("Project") {
            HStack {
                Picker("CapCut project", selection: $export.selectedProjectURL) {
                    ForEach(export.displayedProjects) { project in
                        Text(project.isOpen ? "\(project.name)  — open in CapCut" : project.name).tag(Optional(project.url))
                    }
                }
                .id(export.sortProjectsByName)
                Button {
                    export.refreshProjects(selectOpen: false)
                    model.refreshProjectState()
                } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .help("Re-read the project list")
            }
            if export.timelines.count > 1 {
                Picker("Timeline", selection: $export.selectedTimelineID) {
                    ForEach(export.timelines) { timeline in
                        Text(timeline.isMain ? "\(timeline.name)  — main" : timeline.name).tag(Optional(timeline.id))
                    }
                }
            }
            if model.projectIsOpen {
                Label("This project is open in CapCut. Close it (back to CapCut's home screen) before creating "
                    + "subtitles or voice-over, and open it again afterwards — otherwise CapCut overwrites them when it saves.",
                      systemImage: "exclamationmark.triangle.fill")
                    .font(.callout).foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            } else if let error = export.loadError {
                Text(error).foregroundStyle(.red)
            } else if export.timeline == nil, export.selectedProject != nil {
                ProgressView().controlSize(.small)
            }
        }
    }

    private var createSection: some View {
        Section {
            HStack(spacing: 8) {
                OutputToggle(title: "Subtitles", systemImage: "captions.bubble", isOn: $model.makeCaptions)
                OutputToggle(title: "Voice-over", systemImage: "waveform", isOn: $model.makeDub)
            }
            Picker("Spoken language", selection: $model.sourceLocaleID) {
                ForEach(model.sourceLocales, id: \.self) { locale in
                    Text(Self.localeName(locale)).tag(locale.identifier(.bcp47))
                }
            }
            Picker("Speech recognition", selection: $model.engine) {
                Text("Apple (built into macOS)").tag(SpeechEngine.apple)
                Text("Whisper large-v3-turbo").tag(SpeechEngine.whisper)
            }
            .help("Whisper catches more words in noisy dialogue; its model is downloaded once (574 MB).")
        } header: {
            Text("Create")
        } footer: {
            Text("Speech recognition, translation and voices run on this Mac (Apple's on-device models or Whisper). "
                + "Voice-over speaks every translation.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    private var languagesSection: some View {
        Section("Languages") {
            if model.makeCaptions {
                Toggle("Original subtitles (\(model.sourceLanguage.displayName))", isOn: $model.includeOriginal)
            }
            VStack(alignment: .leading, spacing: 6) {
                Text("Translate to:").font(.callout).foregroundStyle(.secondary)
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 110), spacing: 6)], alignment: .leading, spacing: 6) {
                    ForEach(model.targetLanguages.filter { $0.code != model.sourceLanguage.code }) { lang in
                        LanguageChip(language: lang, isOn: model.selectedTargets.contains(lang.code)) {
                            model.toggle(lang)
                        }
                    }
                }
            }
            if model.outputLanguages.count > 1 {
                Picker("Shown on screen", selection: Binding(get: { model.effectiveVisibleLanguage ?? "" },
                                                            set: { model.visibleLanguage = $0 })) {
                    ForEach(model.outputLanguages) { Text($0.displayName).tag($0.code) }
                }
                .help("Each language becomes its own caption track. The others are added hidden: "
                    + "show them with the track's eye button in CapCut.")
            }
        }
    }

    private var voiceSection: some View {
        Section {
            Toggle(isOn: $model.cloneVoice) {
                Text("Clone the original voice")
                Text("Keeps each speaker's voice, intonation and emotion. Runs on this Mac; slower than system voices.")
            }
            if model.cloneVoice {
                Label("Only for your own voice or people who agreed to have their voice cloned.",
                      systemImage: "exclamationmark.triangle.fill")
                    .font(.caption).foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
                if !model.clonedLanguages.isEmpty {
                    LabeledContent("Expressiveness") {
                        Slider(value: $model.expressiveness, in: 0.25...1, step: 0.05)
                            .frame(maxWidth: 200)
                    }
                    .help("0.5 sounds like the original; higher is more dramatic (and a little faster).")
                }
            }
            if model.dubLanguages.contains(where: { !model.isCloned($0) }) {
                Picker("Voice", selection: $model.voiceGender) {
                    ForEach(VoiceGender.allCases) { Text($0.rawValue).tag($0) }
                }
                .pickerStyle(.segmented)
            }
            ForEach(model.dubLanguages) { lang in
                if model.isCloned(lang) {
                    LabeledContent(lang.displayName) { Text("Original voice, cloned").foregroundStyle(.secondary) }
                } else {
                    voiceRow(lang)
                    if model.cloneVoice {
                        Text("Voice cloning doesn't speak \(lang.displayName): a system voice will speak it.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            if model.dubLanguages.count > 1 {
                Picker("Heard", selection: Binding(get: { model.effectiveAudibleVoice ?? "" },
                                                   set: { model.audibleVoice = $0 })) {
                    ForEach(model.dubLanguages) { Text($0.displayName).tag($0.code) }
                }
                .help("Each language becomes its own audio track. The others are added muted: "
                    + "unmute them with the track's speaker button in CapCut.")
            }
            Picker("Original audio", selection: $model.originalAudio) {
                ForEach(OriginalAudio.allCases) { Text($0.rawValue).tag($0) }
            }
            .pickerStyle(.segmented)
            .help("While the voice-over speaks: leave the project's own sound as it is, lower it by 15 dB, silence all of it, "
                + "replace only the voice — the sound is silenced and a track with it minus the voice plays instead — or "
                + "turn on CapCut's Remove vocal on those clips, which CapCut then works out itself. The others are done with "
                + "volume keyframes on the clips, which you can adjust in CapCut.")
            if let name = model.dubTimelineName {
                Text("Goes to a new timeline, \(name). \(export.selectedTimeline?.name ?? "The timeline") stays as it is.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        } header: {
            HStack {
                Text("Voice-over")
                if model.siriLoading {
                    ProgressView().controlSize(.mini)
                    Text("Loading Siri voices…").font(.caption).foregroundStyle(.secondary)
                }
            }
        } footer: {
            HStack(spacing: 4) {
                Text(model.siriPossible ? "Better voices: Enhanced, Premium and Siri voices in Read & Speak settings."
                    : "Better voices: Enhanced and Premium in Read & Speak settings. Siri voices appear here when Xcode is installed.")
                    .font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Button("Open") { SubtitlesModel.openVoiceSettings() }.buttonStyle(.link).font(.caption)
            }
        }
    }

    @ViewBuilder
    private func voiceRow(_ lang: SubtitleLanguage) -> some View {
        let voices = model.pickerVoices(for: lang)
        HStack(spacing: 6) {
            if voices.isEmpty {
                LabeledContent(lang.displayName) {
                    Text("No voice installed").foregroundStyle(.orange)
                }
                Button("Get Voices…") { model.getVoices(for: lang) }.controlSize(.small)
            } else {
                Picker(lang.displayName, selection: Binding(get: { model.voiceID(for: lang) },
                                                            set: { model.setVoice($0, for: lang) })) {
                    ForEach(voices) { v in
                        // When the chosen gender isn't available, say which gender this voice is.
                        Text(model.lacksGender(lang) ? "\(v.label) · \(v.gender?.rawValue.lowercased() ?? "voice")" : v.label)
                            .tag(v.id)
                    }
                }
                if model.previewing == lang.code {
                    ProgressView().controlSize(.small).help("Preparing Siri preview…")
                } else {
                    Button { model.preview(lang) } label: { Image(systemName: "speaker.wave.2.fill") }
                        .buttonStyle(.borderless)
                        .help("Preview voice")
                }
            }
        }
        if model.lacksGender(lang) {
            let other = voices.first.flatMap(\.gender)?.rawValue.lowercased() ?? "another"
            VStack(alignment: .leading, spacing: 2) {
                Text("No \(model.voiceGender.rawValue.lowercased()) \(lang.displayName) voice is installed — the \(other) voice will speak.")
                    .font(.caption).foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
                if model.voiceHelpLanguage == lang.code {
                    Text("Check System Settings ▸ Accessibility ▸ Read & Speak: set System speech language to \(lang.displayName) "
                        + "and look in the System voice menu for a \(model.voiceGender.rawValue.lowercased()) name. Many languages have none.")
                        .font(.caption).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                } else {
                    Button("Look for more voices…") { model.getVoices(for: lang) }
                        .buttonStyle(.link).font(.caption)
                }
            }
        }
    }

    @ViewBuilder
    private var downloadSection: some View {
        if !model.missingModels.isEmpty || model.modelProgress != nil {
            Section {
                Label("One-time download needed: " + model.missingModelsText,
                      systemImage: "arrow.down.circle")
                    .fixedSize(horizontal: false, vertical: true)
                if let error = model.modelError {
                    Text(error).font(.caption).foregroundStyle(.red).textSelection(.enabled)
                        .fixedSize(horizontal: false, vertical: true)
                }
                HStack {
                    if let progress = model.modelProgress {
                        ProgressView(value: progress)
                        Text("\(Int(progress * 100))%").font(.caption).monospacedDigit()
                        Button("Cancel") { model.cancelModelDownload() }
                    } else {
                        Button("Download") { model.downloadModel() }
                        Spacer()
                    }
                    Button("Show Folder") {
                        try? FileManager.default.createDirectory(at: ModelStore.root, withIntermediateDirectories: true)
                        NSWorkspace.shared.open(ModelStore.root)
                    }
                    .help(ModelStore.root.path)
                }
                .controlSize(.small)
            }
        }
        if !model.notDownloaded.isEmpty || model.downloadError != nil {
            Section {
                if !model.notDownloaded.isEmpty {
                    Label("One-time download needed for offline translation: "
                        + model.notDownloaded.map(\.displayName).joined(separator: ", "),
                          systemImage: "arrow.down.circle")
                        .fixedSize(horizontal: false, vertical: true)
                }
                if let error = model.downloadError {
                    Text(error).font(.caption).foregroundStyle(.red).textSelection(.enabled)
                        .fixedSize(horizontal: false, vertical: true)
                }
                HStack {
                    if let lang = model.downloadingLanguage {
                        ProgressView().controlSize(.small)
                        Text("Downloading \(lang)…").font(.caption)
                    } else if !model.notDownloaded.isEmpty {
                        Button("Download") { Task { await model.downloadMissing() } }
                    }
                    Spacer()
                    Button("Open Settings") { TranslationBroker.openTranslationSettings() }
                        .help("System Settings ▸ General ▸ Language & Region ▸ Translation Languages")
                    Button {
                        Task { await model.refreshDownloads() }
                    } label: { Image(systemName: "arrow.clockwise") }
                        .help("Check again")
                }
                .controlSize(.small)
            }
        }
    }

    private var optionsSection: some View {
        Section {
            Toggle("Replace what was added here before", isOn: $model.replacePrevious)
                .help("Removes the caption and voice-over tracks an earlier run added to this timeline (only the kinds "
                    + "created now), and its volume changes. Your own tracks stay.")
            if model.makeCaptions {
                Toggle("Also save .srt files to the export folder", isOn: $model.saveSRT)
            }
        } header: {
            Text("Options")
        } footer: {
            Text("The project file is backed up before it's changed (~/Library/Application Support/EnhanceCapCut/Backups).")
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    // MARK: Footer

    @ViewBuilder
    private var footer: some View {
        switch model.phase {
        case let .running(step, fraction):
            VStack(alignment: .leading, spacing: 6) {
                ProgressView(value: fraction)
                HStack {
                    Text(step).font(.callout)
                    Spacer()
                    Button("Cancel") { model.cancel() }
                }
            }
        case let .done(result, summary, srt):
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
                    Text(summary).lineLimit(2)
                    Spacer()
                    createButton
                }
                Text("Open the project in CapCut to see the result.").font(.caption).foregroundStyle(.secondary)
                HStack {
                    Button("Show Backup") { model.reveal(result.backupFolder) }
                    if let first = srt.first { Button("Show .srt Files") { model.reveal(first) } }
                }
                .controlSize(.small)
            }
        case let .failed(message):
            HStack {
                Image(systemName: "xmark.octagon.fill").foregroundStyle(.red)
                Text(message).font(.callout).lineLimit(4).textSelection(.enabled)
                Spacer()
                createButton
            }
        case .idle:
            HStack {
                Spacer()
                createButton
            }
        }
    }

    private var createButton: some View {
        Button(createTitle) { model.start() }
            .keyboardShortcut(.defaultAction)
            .disabled(!model.canStart || model.projectIsOpen)
    }

    private var createTitle: String {
        if !model.makeCaptions && !model.makeDub { return "Choose Subtitles or Voice-over" }
        let what = [model.makeCaptions ? "Subtitles" : nil, model.makeDub ? "Voice-over" : nil].compactMap { $0 }
        let codes = (model.outputLanguages + model.dubLanguages).map(\.code)
            .reduce(into: [String]()) { if !$0.contains($1) { $0.append($1) } }
        if codes.isEmpty || (model.makeDub && model.dubLanguages.isEmpty && !model.makeCaptions) { return "Choose a Language" }
        return "Create \(what.joined(separator: " & ")) (\(codes.joined(separator: ", ")))"
    }

    static func localeName(_ locale: Locale) -> String {
        let name = Locale(identifier: "en").localizedString(forIdentifier: locale.identifier) ?? locale.identifier
        return name.prefix(1).uppercased() + name.dropFirst()
    }
}

private struct LanguageChip: View {
    let language: SubtitleLanguage
    let isOn: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 4) {
                Image(systemName: isOn ? "checkmark.circle.fill" : "circle")
                    .foregroundStyle(isOn ? Color.accentColor : Color.secondary)
                VStack(alignment: .leading, spacing: 0) {
                    Text(language.displayName).font(.callout).lineLimit(1)
                    if language.nativeName.lowercased() != language.displayName.lowercased() {
                        Text(language.nativeName).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 5)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 7).fill(isOn ? Color.accentColor.opacity(0.15) : Color.secondary.opacity(0.08)))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(language.code)
    }
}

private struct OutputToggle: View {
    let title: String
    let systemImage: String
    @Binding var isOn: Bool

    var body: some View {
        Button { isOn.toggle() } label: {
            Label(title, systemImage: isOn ? "checkmark.circle.fill" : systemImage)
                .frame(maxWidth: .infinity)
                .padding(.vertical, 6)
                .background(RoundedRectangle(cornerRadius: 7).fill(isOn ? Color.accentColor.opacity(0.18) : Color.secondary.opacity(0.08)))
                .foregroundStyle(isOn ? Color.accentColor : Color.primary)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}
