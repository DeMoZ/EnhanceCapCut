using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using static EnhanceCapCut.Draft.Localization;

namespace EnhanceCapCut.App;

/// <summary>
/// "How to Use EnhanceCapCut": the user guide, shown once at the first launch and from the link at the bottom of the
/// export window. The counterpart of the macOS version's <c>GuideView</c>: same sections, same texts except where the
/// platforms differ (how to open the app, the speech and voice engines).
/// </summary>
sealed class GuideWindow : Window
{
    /// <summary>A guide section: steps to follow in order (numbered) or separate tips. Steps mark the names of buttons and
    /// menus with <c>**</c>.</summary>
    sealed record Section(string Title, string Glyph, bool Numbered, string[] Steps);

    static Section[] Sections =>
    [
        new(L("What EnhanceCapCut does"), "", false,
        [
            L("EnhanceCapCut exports the project you're editing in CapCut with **your own settings**: resolution up to 4K, "
                + "frame rate, H.264 or HEVC, quality or bitrate, loudness normalization. No CapCut export needed."),
            L("It also **transcribes, translates and dubs** your video offline and puts the captions and the voice-over "
                + "**back into the CapCut project**, as tracks you can edit there."),
        ]),
        new(L("Quick start"), "", true,
        [
            L("Open your project in CapCut."),
            L("Start EnhanceCapCut, or click it again if it's already running. The export window opens with that project "
                + "selected."),
            L("Check the settings and click **Export**. When it's done, **Show in Explorer** opens the video."),
        ]),
        new(L("Keep it at hand"), "", true,
        [
            L("Right-click EnhanceCapCut in the taskbar and choose **Pin to taskbar**."),
            L("While editing, click the pinned icon: EnhanceCapCut finds the project open in CapCut every time."),
            L("Keep the app's folder outside Program Files, so updates can replace its files."),
        ]),
        new(L("The export window"), "", false,
        [
            L("**Project** — the one open in CapCut is selected; you can pick any other. The ↻ button finds the open "
                + "project again. Projects with several timelines show a **Timeline** list."),
            L("**Not rendered yet** — CapCut features in this project that EnhanceCapCut doesn't reproduce yet "
                + "(effects, filters, transitions, stickers, text animations) and media files missing on disk."),
            L("**Video** — aspect ratio (CapCut's ratio switch), resolution, frame rate, codec, and constant quality "
                + "or bitrate. **Burn in captions** shows CapCut's auto-captions in the video."),
            L("**Audio** — **Normalize loudness** brings the whole video to one level, −14 LUFS for most platforms."),
            L("**Output** — the folder and the file name. Tokens like {project} {date} {res} are filled in for each "
                + "export. All settings are remembered."),
        ]),
        new(L("Subtitles and voice-over"), Theme.Captions, true,
        [
            L("Close the project in CapCut (go back to its home screen): CapCut overwrites a project that is open "
                + "when it saves."),
            L("Click **Subtitles & Voice-over…** in the export window."),
            L("Choose **Subtitles**, **Voice-over** or both, the spoken language and the languages to translate into."),
            L("For subtitles, pick the language **Shown on screen**; the other caption tracks are added hidden."),
            L("For voice-over, pick a voice for each language (▶ plays a sample), the language that is **Heard**, and "
                + "what happens to the **Original audio**: keep, lower, mute all or remove vocal."),
            L("Click **Create**, then open the project in CapCut. Voice-over goes to a new timeline, a copy of the "
                + "selected one; subtitles alone go into the selected timeline."),
        ]),
        new(L("Dub in the speaker's own voice"), "", false,
        [
            L("Turn on **Clone the original voice**: each translated line is spoken in the voice of the person "
                + "speaking, with their intonation. **Expressiveness** makes the delivery calmer or more dramatic."),
            L("Use it **only for your own voice or people who agreed** to have their voice cloned. The app asks you to "
                + "confirm their consent every time."),
            L("Works best with **Remove vocal**, so the original speech doesn't play underneath."),
        ]),
        new(L("Good to know"), "", false,
        [
            L("Everything runs on this PC: your video is never uploaded. Speech, translation and voice models are "
                + "downloaded once, the first time they're needed."),
            L("Before changing a project, EnhanceCapCut backs it up. Running subtitles again replaces what it added "
                + "before; your own captions stay."),
            L("More voices: add them in **Settings ▸ Time & language ▸ Speech** (the **Get Voices…** button)."),
            L("Updates install from **Check for Updates…** at the bottom of the export window; the app also checks once a "
                + "day."),
            L("The 🌐 menu at the bottom of the export window switches the interface language."),
        ]),
    ];

    public GuideWindow()
    {
        Title = L("How to Use EnhanceCapCut");
        var form = new FormView();
        foreach (var section in Sections)
        {
            var card = form.Section();
            card.Header = Text.Label(section.Glyph, Text.Headline(section.Title));
            for (var i = 0; i < section.Steps.Length; i++)
            {
                var step = Styled(section.Steps[i]);
                if (!section.Numbered)
                {
                    card.Add(step);
                    continue;
                }
                var number = Text.Body($"{i + 1}.");
                number.Foreground = Theme.Secondary;
                number.VerticalAlignment = VerticalAlignment.Top;
                card.Add(Text.IconAndText(number, step, 8));
            }
        }
        form.Refresh();
        Content = new Grid { Background = Theme.Resource("SolidBackgroundFillColorBaseBrush"), Children = { form } };
        Platform.SetUpFrame(this, "window.guide", 520, 640, 460, 420);
    }

    /// <summary><paramref name="step"/> with its <c>**</c> parts in bold.</summary>
    static TextBlock Styled(string step)
    {
        var text = Text.Body();
        text.VerticalAlignment = VerticalAlignment.Top;
        var parts = step.Split("**");
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            var run = new Run { Text = parts[i] };
            if (i % 2 == 1) run.FontWeight = FontWeights.SemiBold;
            text.Inlines.Add(run);
        }
        return text;
    }
}
