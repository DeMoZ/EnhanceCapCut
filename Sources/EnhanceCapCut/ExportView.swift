import EnhanceCapCutCore
import SwiftUI

struct ExportView: View {
    @ObservedObject var model: ExportModel

    var body: some View {
        VStack(spacing: 0) {
            Form {
                projectSection
                unsupportedSection
                videoSection
                audioSection
                outputSection
            }
            .formStyle(.grouped)
            .disabled(model.isExporting)

            Divider()
            footer.padding(16)
        }
        .frame(minWidth: 520, minHeight: 600)
    }

    // MARK: Sections

    private var projectSection: some View {
        Section("Project") {
            HStack {
                Picker("CapCut project", selection: $model.selectedProjectURL) {
                    ForEach(model.displayedProjects) { project in
                        Text(project.isOpen ? "\(project.name)  — open in CapCut" : project.name)
                            .tag(Optional(project.url))
                    }
                }
                // The macOS pop-up menu keeps its items' order when ForEach only reorders them; rebuild it.
                .id(model.sortProjectsByName)
                Toggle(isOn: $model.sortProjectsByName) {
                    Image(systemName: "textformat.abc")
                }
                .toggleStyle(.button)
                .help(model.sortProjectsByName ? "Sorted by name. Click for CapCut's order"
                    : "In CapCut's order. Click to sort by name")
                Button {
                    model.refreshProjects(selectOpen: true)
                } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .help("Re-detect the project open in CapCut")
            }
            if model.timelines.count > 1 {
                Picker("Timeline", selection: $model.selectedTimelineID) {
                    ForEach(model.timelines) { timeline in
                        Text(timeline.isMain ? "\(timeline.name)  — main" : timeline.name).tag(Optional(timeline.id))
                    }
                }
            }
            if let timeline = model.timeline {
                LabeledContent("Canvas", value: "\(Int(timeline.canvasSize.width))×\(Int(timeline.canvasSize.height)), "
                    + "\(format(timeline.fps)) fps, \(duration(timeline.duration))")
            } else if let error = model.loadError {
                Text(error).foregroundStyle(.red)
            } else if model.selectedProject != nil {
                ProgressView().controlSize(.small)
            } else {
                Text("No CapCut projects found in ~/Movies/CapCut.").foregroundStyle(.secondary)
            }
            LabeledContent("Subtitles") {
                Button("Subtitles & Voice-over…") { model.openSubtitles?() }
                    .disabled(model.selectedProject == nil || !Self.subtitlesAvailable)
                    .help(Self.subtitlesAvailable ? "Transcribe and translate the speech and add it to the project as captions"
                        : "Needs macOS 26 or later")
            }
        }
    }

    private static var subtitlesAvailable: Bool {
        if #available(macOS 26, *) { return true }
        return false
    }

    private var videoSection: some View {
        Section("Video") {
            Picker("Aspect ratio", selection: $model.settings.aspect) {
                ForEach(ExportSettings.Aspect.allCases) { Text($0.title).tag($0) }
            }
            Picker("Resolution", selection: $model.settings.resolution) {
                ForEach(ExportSettings.Resolution.allCases) { Text($0.title).tag($0) }
            }
            if let size = model.outputSize {
                LabeledContent("Output size", value: "\(Int(size.width))×\(Int(size.height))")
            }
            Picker("Frame rate", selection: $model.settings.frameRate) {
                Text(model.timeline.map { "From project (\(format($0.fps)))" } ?? "From project").tag(0.0)
                ForEach([24.0, 25, 30, 60], id: \.self) { Text(format($0)).tag($0) }
            }
            Picker("Codec", selection: $model.settings.codec) {
                ForEach(ExportSettings.Codec.allCases) { Text($0.title).tag($0) }
            }
            if !model.settings.codec.isProRes {
                Picker("Rate control", selection: $model.settings.rateControl) {
                    Text("Constant quality").tag(ExportSettings.RateControl.quality)
                    Text("Bitrate").tag(ExportSettings.RateControl.bitrate)
                }
                .pickerStyle(.segmented)
                if model.settings.rateControl == .quality {
                    LabeledContent("Quality") {
                        HStack {
                            Slider(value: $model.settings.quality, in: 0...100)
                            Text("\(Int(model.settings.quality))").monospacedDigit().frame(width: 32, alignment: .trailing)
                        }
                    }
                } else {
                    LabeledContent("Bitrate") {
                        HStack {
                            TextField("", value: $model.settings.bitrateMbps, format: .number)
                                .frame(width: 70)
                            Text("Mbit/s")
                        }
                    }
                }
            }
            if model.timeline?.hasSubtitles == true {
                Toggle("Burn in captions (auto-subtitles)", isOn: $model.settings.burnInSubtitles)
            }
        }
    }

    private var audioSection: some View {
        Section("Audio") {
            Toggle("Normalize loudness", isOn: $model.settings.normalizeLoudness)
                .disabled(!model.ffmpegAvailable)
            if model.settings.normalizeLoudness {
                LabeledContent("Target") {
                    HStack {
                        TextField("", value: $model.settings.targetLUFS, format: .number).frame(width: 60)
                        Text("LUFS")
                        Stepper("", value: $model.settings.targetLUFS, in: -40...(-5), step: 1).labelsHidden()
                    }
                }
            }
            if !model.ffmpegAvailable {
                Text("Loudness normalization needs ffmpeg (brew install ffmpeg).")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    private var outputSection: some View {
        Section("Output") {
            LabeledContent("Folder") {
                HStack {
                    Text((model.settings.outputFolder as NSString).abbreviatingWithTildeInPath)
                        .lineLimit(1).truncationMode(.middle)
                    Button("Choose…") { model.chooseOutputFolder() }
                }
            }
            TextField("File name", text: $model.settings.fileNameTemplate)
            Text("Tokens: {project} {date} {time} {res} {codec} {fps}")
                .font(.caption).foregroundStyle(.secondary)
            if let url = model.outputURL {
                LabeledContent("Will save as", value: url.lastPathComponent)
            }
        }
    }

    @ViewBuilder
    private var unsupportedSection: some View {
        if let report = model.timeline?.report, !report.isEmpty {
            Section {
                ForEach(groupedUnsupported(report), id: \.0) { category, items in
                    VStack(alignment: .leading, spacing: 2) {
                        Text(category).font(.headline)
                        Text(items.map { $0.count > 1 ? "\($0.name) ×\($0.count)" : $0.name }.joined(separator: ", "))
                            .font(.callout).foregroundStyle(.secondary)
                    }
                }
                if !report.missingFiles.isEmpty {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Missing media (rendered empty)").font(.headline).foregroundStyle(.red)
                        ForEach(report.missingFiles.prefix(12), id: \.self) { file in
                            Text(file).font(.caption).lineLimit(1).truncationMode(.middle)
                        }
                        if report.missingFiles.count > 12 {
                            Text("…and \(report.missingFiles.count - 12) more").font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
            } header: {
                Label("Not rendered yet", systemImage: "exclamationmark.triangle")
            } footer: {
                Text("These CapCut features are in the project but this version doesn't reproduce them; "
                    + "the export will look different where they're used.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    // MARK: Footer

    @ViewBuilder
    private var footer: some View {
        switch model.state {
        case let .exporting(progress):
            VStack(alignment: .leading, spacing: 6) {
                ProgressView(value: progress?.fraction ?? 0)
                HStack {
                    Text(progressText(progress)).font(.callout).monospacedDigit()
                    Spacer()
                    Button("Cancel") { model.cancel() }
                }
            }
        case let .finished(url, warnings, seconds):
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
                    Text("Exported \(url.lastPathComponent) in \(duration(Int64(seconds * 1_000_000)))")
                        .lineLimit(1).truncationMode(.middle)
                    Spacer()
                    Button("Show in Finder") { model.revealOutput(url) }
                    exportButtons
                }
                ForEach(warnings, id: \.self) { Text($0).font(.caption).foregroundStyle(.orange) }
            }
        case let .failed(message):
            HStack {
                Image(systemName: "xmark.octagon.fill").foregroundStyle(.red)
                Text(message).font(.callout).lineLimit(3)
                Spacer()
                exportButtons
            }
        case .idle:
            HStack {
                footerLinks
                Spacer()
                exportButtons
            }
        }
    }

    /// "How to Use · Version 0.6.0 · Check for Updates…", as in the Windows version (no version outside the app bundle).
    private var footerLinks: some View {
        HStack(spacing: 4) {
            Button("How to Use") { model.openGuide?() }.buttonStyle(.link)
            if let check = model.checkForUpdates {
                Text("· Version \(model.version) ·").foregroundStyle(.secondary)
                Button("Check for Updates…", action: check).buttonStyle(.link)
            }
        }
        .font(.caption)
    }

    private var exportButtons: some View {
        HStack {
            DonateButton { model.openDonate?() }
            exportButton
        }
    }

    private var exportButton: some View {
        Button("Export") { model.startExport() }
            .keyboardShortcut(.defaultAction)
            .disabled(model.timeline == nil || model.outputURL == nil)
    }

    // MARK: Helpers

    private func groupedUnsupported(_ report: RenderReport) -> [(String, [RenderReport.Item])] {
        Dictionary(grouping: report.unsupported, by: \.category)
            .map { ($0.key, $0.value.sorted { $0.count > $1.count }) }
            .sorted { $0.0 < $1.0 }
    }

    private func progressText(_ progress: ExportProgress?) -> String {
        guard let p = progress else { return "Preparing…" }
        var text = p.phase.rawValue
        if p.phase == .video {
            text += " — frame \(p.framesDone) of \(p.framesTotal)"
        }
        if p.fraction > 0.12 {
            let remaining = p.elapsed / p.fraction * (1 - p.fraction)
            text += ", about \(duration(Int64(remaining * 1_000_000))) left"
        }
        return text
    }

    private func format(_ fps: Double) -> String {
        fps == fps.rounded() ? String(Int(fps)) : String(format: "%.2f", fps)
    }

    private func duration(_ us: Int64) -> String {
        let s = Int((Double(us) / 1_000_000).rounded())
        return s >= 3600 ? String(format: "%d:%02d:%02d", s / 3600, s / 60 % 60, s % 60)
            : String(format: "%d:%02d", s / 60, s % 60)
    }
}
