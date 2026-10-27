import SwiftUI

/// The "How to Use EnhanceCapCut" window: the user guide, shown once at the first launch and from Help ▸ EnhanceCapCut
/// Help or the link at the bottom of the export window. Its Windows twin is `GuideWindow.cs`: same sections, same
/// texts except where the platforms differ (how to open the app, the speech and voice engines).
struct GuideView: View {
    var body: some View {
        Form {
            ForEach(Guide.sections, id: \.title) { section in
                Section {
                    ForEach(Array(section.steps.enumerated()), id: \.offset) { index, step in
                        HStack(alignment: .firstTextBaseline, spacing: 8) {
                            if section.numbered {
                                Text("\(index + 1).").monospacedDigit().foregroundStyle(.secondary)
                            }
                            Text(Guide.styled(step)).fixedSize(horizontal: false, vertical: true)
                        }
                    }
                } header: {
                    Label(section.title, systemImage: section.symbol)
                }
            }
        }
        .formStyle(.grouped)
        .frame(minWidth: 460, minHeight: 420)
    }
}

/// The guide's text. Steps mark the names of buttons and menus with `**`.
enum Guide {
    struct Section {
        let title: String
        let symbol: String
        /// Steps to follow in order (numbered) or separate tips.
        let numbered: Bool
        let steps: [String]
    }

    static let sections: [Section] = [
        Section(title: "What EnhanceCapCut does", symbol: "sparkles", numbered: false, steps: [
            "EnhanceCapCut exports the project you're editing in CapCut with **your own settings**: resolution up to 4K, "
                + "frame rate, H.264, HEVC or ProRes, quality or bitrate, loudness normalization. No CapCut export needed.",
            "It also **transcribes, translates and dubs** your video offline and puts the captions and the voice-over "
                + "**back into the CapCut project**, as tracks you can edit there.",
        ]),
        Section(title: "Quick start", symbol: "play.circle", numbered: true, steps: [
            "Open your project in CapCut.",
            "Choose **CapCut ▸ Services ▸ Export with EnhanceCapCut**, or press your hotkey. The export window opens "
                + "with that project selected.",
            "Check the settings and click **Export**. When it's done, **Show in Finder** opens the video.",
        ]),
        Section(title: "Set up the Services entry and a hotkey", symbol: "keyboard", numbered: true, steps: [
            "Open **System Settings ▸ Keyboard ▸ Keyboard Shortcuts… ▸ Services ▸ General**.",
            "Make sure **Export with EnhanceCapCut** is checked.",
            "Double-click the right side of that row and press a shortcut CapCut doesn't use, for example ⌃⌥⌘E.",
            "If the entry doesn't show up in CapCut, log out and back in.",
        ]),
        Section(title: "The export window", symbol: "square.and.arrow.up", numbered: false, steps: [
            "**Project** — the one open in CapCut is selected; you can pick any other. The ↻ button finds the open "
                + "project again. Projects with several timelines show a **Timeline** list.",
            "**Not rendered yet** — CapCut features in this project that EnhanceCapCut doesn't reproduce yet "
                + "(effects, filters, transitions, stickers, text animations) and media files missing on disk.",
            "**Video** — aspect ratio (CapCut's ratio switch), resolution, frame rate, codec, and constant quality "
                + "or bitrate. **Burn in captions** shows CapCut's auto-captions in the video.",
            "**Audio** — **Normalize loudness** brings the whole video to one level, −14 LUFS for most platforms. "
                + "It needs ffmpeg (brew install ffmpeg).",
            "**Output** — the folder and the file name. Tokens like {project} {date} {res} are filled in for each "
                + "export. All settings are remembered.",
        ]),
        Section(title: "Subtitles and voice-over", symbol: "captions.bubble", numbered: true, steps: [
            "Close the project in CapCut (go back to its home screen): CapCut overwrites a project that is open "
                + "when it saves.",
            "Click **Subtitles & Voice-over…** in the export window (⌘U).",
            "Choose **Subtitles**, **Voice-over** or both, the spoken language and the languages to translate into.",
            "For subtitles, pick the language **Shown on screen**; the other caption tracks are added hidden.",
            "For voice-over, pick a voice for each language (▶ plays a sample), the language that is **Heard**, and "
                + "what happens to the **Original audio**: keep, lower, mute all, replace voice (music and ambience "
                + "stay) or remove vocal.",
            "Click **Create**, then open the project in CapCut. Voice-over goes to a new timeline, a copy of the "
                + "selected one; subtitles alone go into the selected timeline.",
        ]),
        Section(title: "Dub in the speaker's own voice", symbol: "person.wave.2", numbered: false, steps: [
            "Turn on **Clone the original voice**: each translated line is spoken in the voice of the person "
                + "speaking, with their intonation. **Expressiveness** makes the delivery calmer or more dramatic.",
            "Use it **only for your own voice or people who agreed** to have their voice cloned. The app asks you to "
                + "confirm their consent every time.",
            "Works best with **Replace voice** or **Remove vocal**, so the original speech doesn't play underneath.",
        ]),
        Section(title: "Good to know", symbol: "lightbulb", numbered: false, steps: [
            "Everything runs on this Mac: your video is never uploaded. Speech, translation and voice models are "
                + "downloaded once, the first time they're needed.",
            "Before changing a project, EnhanceCapCut backs it up. Running subtitles again replaces what it added "
                + "before; your own captions stay.",
            "Better voices: download Enhanced or Premium voices in **System Settings ▸ Accessibility ▸ Read & Speak**.",
            "Updates install from **EnhanceCapCut ▸ Check for Updates…**; the app also checks once a day.",
        ]),
    ]

    /// `step` with its `**` parts in bold.
    static func styled(_ step: String) -> AttributedString {
        (try? AttributedString(markdown: step, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)))
            ?? AttributedString(step)
    }
}
