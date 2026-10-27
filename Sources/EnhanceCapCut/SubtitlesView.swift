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
        .alert(L("Clone voices only with consent"), isPresented: $model.askConsent) {
            Button(L("Cancel"), role: .cancel) {}
            Button(L("Create")) { model.start(consented: true) }
        } message: {
            Text(SubtitlesView.consentText)
        }
    }

    /// The warning before cloning, the same as the Windows version's.
    static var consentText: String {
        L("The voice-over will copy the voices of the people speaking in this video. Use it only "
            + "for your own voice or for people who agreed to have their voice cloned. Click Create only if you have "
            + "their consent.")
    }

    // MARK: Sections

    private var projectSection: some View {
        Section(L("Project")) {
            HStack {
                Picker(L("CapCut project"), selection: $export.selectedProjectURL) {
                    ForEach(export.displayedProjects) { project in
                        Text(project.isOpen ? L("{0}  — open in CapCut", project.name) : project.name).tag(Optional(project.url))
                    }
                }
                .id(export.sortProjectsByName)
                Button {
                    export.refreshProjects(selectOpen: false)
                    model.refreshProjectState()
                } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .help(L("Re-read the project list"))
            }
            if export.timelines.count > 1 {
                Picker(L("Timeline"), selection: $export.selectedTimelineID) {
                    ForEach(export.timelines) { timeline in
                        Text(timeline.isMain ? L("{0}  — main", timeline.name) : timeline.name).tag(Optional(timeline.id))
                    }
                }
            }
            if model.projectIsOpen {
                Label(L("This project is open in CapCut. Close it (back to CapCut's home screen) before creating "
                    + "subtitles or voice-over, and open it again afterwards — otherwise CapCut overwrites them when it saves."),
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
                OutputToggle(title: L("Subtitles"), systemImage: "captions.bubble", isOn: $model.makeCaptions)
                OutputToggle(title: L("Voice-over"), systemImage: "waveform", isOn: $model.makeDub)
            }
            Picker(L("Spoken language"), selection: $model.sourceLocaleID) {
                ForEach(model.sourceLocales, id: \.self) { locale in
                    Text(Self.localeName(locale)).tag(locale.identifier(.bcp47))
                }
            }
            Picker(L("Speech recognition"), selection: $model.engine) {
                Text(L("Apple (built into macOS)")).tag(SpeechEngine.apple)
                Text(L("Whisper large-v3-turbo")).tag(SpeechEngine.whisper)
            }
            .help(L("Whisper catches more words in noisy dialogue; its model is downloaded once (574 MB)."))
        } header: {
            Text(L("Create"))
        } footer: {
            Text(L("Speech recognition, translation and voices run on this Mac (Apple's on-device models or Whisper). "
                + "Voice-over speaks every translation."))
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    private var languagesSection: some View {
        Section(L("Languages")) {
            if model.makeCaptions {
                Toggle(L("Original subtitles ({0})", model.sourceLanguage.displayName), isOn: $model.includeOriginal)
            }
            VStack(alignment: .leading, spacing: 6) {
                Text(L("Translate to:")).font(.callout).foregroundStyle(.secondary)
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 110), spacing: 6)], alignment: .leading, spacing: 6) {
                    ForEach(model.targetLanguages.filter { $0.code != model.sourceLanguage.code }) { lang in
                        LanguageChip(language: lang, isOn: model.selectedTargets.contains(lang.code)) {
                            model.toggle(lang)
                        }
                    }
                }
            }
            if model.outputLanguages.count > 1 {
                Picker(L("Shown on screen"), selection: Binding(get: { model.effectiveVisibleLanguage ?? "" },
                                                            set: { model.visibleLanguage = $0 })) {
                    ForEach(model.outputLanguages) { Text($0.displayName).tag($0.code) }
                }
                .help(L("Each language becomes its own caption track. The others are added hidden: "
                    + "show them with the track's eye button in CapCut."))
            }
        }
    }

    private var voiceSection: some View {
        Section {
            Toggle(isOn: $model.cloneVoice) {
                Text(L("Clone the original voice"))
                Text(L("Keeps each speaker's voice, intonation and emotion. Runs on this Mac; slower than system voices."))
            }
            if model.cloneVoice {
                Label(L("Only for your own voice or people who agreed to have their voice cloned."),
                      systemImage: "exclamationmark.triangle.fill")
                    .font(.caption).foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
                if !model.clonedLanguages.isEmpty {
                    LabeledContent(L("Expressiveness")) {
                        Slider(value: $model.expressiveness, in: 0.25...1, step: 0.05)
                            .frame(maxWidth: 200)
                    }
                    .help(L("0.5 sounds like the original; higher is more dramatic (and a little faster)."))
                }
            }
            if model.dubLanguages.contains(where: { !model.isCloned($0) }) {
                Picker(L("Voice"), selection: $model.voiceGender) {
                    ForEach(VoiceGender.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
            }
            ForEach(model.dubLanguages) { lang in
                if model.isCloned(lang) {
                    LabeledContent(lang.displayName) { Text(L("Original voice, cloned")).foregroundStyle(.secondary) }
                } else {
                    voiceRow(lang)
                    if model.cloneVoice {
                        Text(L("Voice cloning doesn't speak {0}: a system voice will speak it.", lang.displayName))
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
            if model.dubLanguages.count > 1 {
                Picker(L("Heard"), selection: Binding(get: { model.effectiveAudibleVoice ?? "" },
                                                   set: { model.audibleVoice = $0 })) {
                    ForEach(model.dubLanguages) { Text($0.displayName).tag($0.code) }
                }
                .help(L("Each language becomes its own audio track. The others are added muted: "
                    + "unmute them with the track's speaker button in CapCut."))
            }
            Picker(L("Original audio"), selection: $model.originalAudio) {
                ForEach(OriginalAudio.allCases) { Text($0.title).tag($0) }
            }
            .pickerStyle(.segmented)
            .help(L("While the voice-over speaks: leave the project's own sound as it is, lower it by 15 dB, silence all of it, "
                + "replace only the voice — the sound is silenced and a track with it minus the voice plays instead — or "
                + "turn on CapCut's Remove vocal on those clips, which CapCut then works out itself. The others are done with "
                + "volume keyframes on the clips, which you can adjust in CapCut."))
            if let name = model.dubTimelineName {
                Text(L("Goes to a new timeline, {0}. {1} stays as it is.", name, export.selectedTimeline?.name ?? L("The timeline")))
                    .font(.caption).foregroundStyle(.secondary)
            }
        } header: {
            HStack {
                Text(L("Voice-over"))
                if model.siriLoading {
                    ProgressView().controlSize(.mini)
                    Text(L("Loading Siri voices…")).font(.caption).foregroundStyle(.secondary)
                }
            }
        } footer: {
            HStack(spacing: 4) {
                Text(model.siriPossible ? L("Better voices: Enhanced, Premium and Siri voices in Read & Speak settings.")
                    : L("Better voices: Enhanced and Premium in Read & Speak settings. Siri voices appear here when Xcode is installed."))
                    .font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Button(L("Open")) { SubtitlesModel.openVoiceSettings() }.buttonStyle(.link).font(.caption)
            }
        }
    }

    @ViewBuilder
    private func voiceRow(_ lang: SubtitleLanguage) -> some View {
        let voices = model.pickerVoices(for: lang)
        HStack(spacing: 6) {
            if voices.isEmpty {
                LabeledContent(lang.displayName) {
                    Text(L("No voice installed")).foregroundStyle(.orange)
                }
                Button(L("Get Voices…")) { model.getVoices(for: lang) }.controlSize(.small)
            } else {
                Picker(lang.displayName, selection: Binding(get: { model.voiceID(for: lang) },
                                                            set: { model.setVoice($0, for: lang) })) {
                    ForEach(voices) { v in
                        // When the chosen gender isn't available, say which gender this voice is.
                        Text(model.lacksGender(lang) ? "\(v.label) · \(v.gender?.title.lowercased() ?? L("voice"))" : v.label)
                            .tag(v.id)
                    }
                }
                if model.previewing == lang.code {
                    ProgressView().controlSize(.small).help(L("Preparing Siri preview…"))
                } else {
                    Button { model.preview(lang) } label: { Image(systemName: "speaker.wave.2.fill") }
                        .buttonStyle(.borderless)
                        .help(L("Preview voice"))
                }
            }
        }
        if model.lacksGender(lang) {
            VStack(alignment: .leading, spacing: 2) {
                Text(model.voiceGender == .female
                    ? L("No female voice is installed for {0} — another voice will speak.", lang.displayName)
                    : L("No male voice is installed for {0} — another voice will speak.", lang.displayName))
                    .font(.caption).foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
                if model.voiceHelpLanguage == lang.code {
                    Text(model.voiceGender == .female
                        ? L("Check System Settings ▸ Accessibility ▸ Read & Speak: set System speech language to {0} "
                            + "and look in the System voice menu for a female name. Many languages have none.", lang.displayName)
                        : L("Check System Settings ▸ Accessibility ▸ Read & Speak: set System speech language to {0} "
                            + "and look in the System voice menu for a male name. Many languages have none.", lang.displayName))
                        .font(.caption).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                } else {
                    Button(L("Look for more voices…")) { model.getVoices(for: lang) }
                        .buttonStyle(.link).font(.caption)
                }
            }
        }
    }

    @ViewBuilder
    private var downloadSection: some View {
        if !model.missingModels.isEmpty || model.modelProgress != nil {
            Section {
                Label(L("One-time download needed: ") + model.missingModelsText,
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
                        Button(L("Cancel")) { model.cancelModelDownload() }
                    } else {
                        Button(L("Download")) { model.downloadModel() }
                        Spacer()
                    }
                    Button(L("Show Folder")) {
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
                    Label(L("One-time download needed for offline translation: ")
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
                        Text(L("Downloading {0}…", lang)).font(.caption)
                    } else if !model.notDownloaded.isEmpty {
                        Button(L("Download")) { Task { await model.downloadMissing() } }
                    }
                    Spacer()
                    Button(L("Open Settings")) { TranslationBroker.openTranslationSettings() }
                        .help(L("System Settings ▸ General ▸ Language & Region ▸ Translation Languages"))
                    Button {
                        Task { await model.refreshDownloads() }
                    } label: { Image(systemName: "arrow.clockwise") }
                        .help(L("Check again"))
                }
                .controlSize(.small)
            }
        }
    }

    private var optionsSection: some View {
        Section {
            Toggle(L("Replace what was added here before"), isOn: $model.replacePrevious)
                .help(L("Removes the caption and voice-over tracks an earlier run added to this timeline (only the kinds "
                    + "created now), and its volume changes. Your own tracks stay."))
            if model.makeCaptions {
                Toggle(L("Also save .srt files to the export folder"), isOn: $model.saveSRT)
            }
        } header: {
            Text(L("Options"))
        } footer: {
            Text(L("The project file is backed up before it's changed (~/Library/Application Support/EnhanceCapCut/Backups)."))
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
                    Button(L("Cancel")) { model.cancel() }
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
                Text(L("Open the project in CapCut to see the result.")).font(.caption).foregroundStyle(.secondary)
                HStack {
                    Button(L("Show Backup")) { model.reveal(result.backupFolder) }
                    if let first = srt.first { Button(L("Show .srt Files")) { model.reveal(first) } }
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
        if !model.makeCaptions && !model.makeDub { return L("Choose Subtitles or Voice-over") }
        let codes = (model.outputLanguages + model.dubLanguages).map(\.code)
            .reduce(into: [String]()) { if !$0.contains($1) { $0.append($1) } }
        if codes.isEmpty || (model.makeDub && model.dubLanguages.isEmpty && !model.makeCaptions) { return L("Choose a Language") }
        let list = codes.joined(separator: ", ")
        if !model.makeDub { return L("Create Subtitles ({0})", list) }
        return model.makeCaptions ? L("Create Subtitles & Voice-over ({0})", list) : L("Create Voice-over ({0})", list)
    }

    static func localeName(_ locale: Locale) -> String {
        let name = Locale(identifier: Localization.language.rawValue).localizedString(forIdentifier: locale.identifier)
            ?? locale.identifier
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
