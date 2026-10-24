import EnhanceCapCutCore
import SwiftUI

/// The "Subtitles & Voice-over" window: transcribes the timeline, translates it and adds the result
/// to the CapCut project as caption tracks.
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
    }

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
                    + "subtitles, and open it again afterwards — otherwise CapCut overwrites them when it saves.",
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
                OutputToggle(title: "Subtitles", systemImage: "captions.bubble", isOn: .constant(true))
                OutputToggle(title: "Voice-over", systemImage: "waveform", isOn: .constant(false))
                    .disabled(true)
                    .help("Coming soon")
            }
            Picker("Spoken language", selection: $model.sourceLocaleID) {
                ForEach(model.sourceLocales, id: \.self) { locale in
                    Text(Self.localeName(locale)).tag(locale.identifier(.bcp47))
                }
            }
        } header: {
            Text("Create")
        } footer: {
            Text("Speech recognition and translation run on this Mac (Apple's on-device models). "
                + "Voice-over is coming later.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    private var languagesSection: some View {
        Section("Languages") {
            Toggle("Original subtitles (\(model.sourceLanguage.displayName))", isOn: $model.includeOriginal)
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

    @ViewBuilder
    private var downloadSection: some View {
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
            Toggle("Replace subtitles added here before", isOn: $model.replacePrevious)
                .help("Removes the caption tracks an earlier run added to this timeline. Your own captions stay.")
            Toggle("Also save .srt files to the export folder", isOn: $model.saveSRT)
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
        case let .done(result, project, languages, srt):
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
                    Text("Added \(result.cueCount) captions (\(languages.joined(separator: ", "))) to \(project)")
                        .lineLimit(2)
                    Spacer()
                    createButton
                }
                Text("Open the project in CapCut to see them.").font(.caption).foregroundStyle(.secondary)
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
        let codes = model.outputLanguages.map(\.code)
        return codes.isEmpty ? "Choose a Language" : "Create Subtitles (\(codes.joined(separator: ", ")))"
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
