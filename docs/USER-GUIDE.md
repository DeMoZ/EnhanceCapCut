# EnhanceCapCut — the full guide

Everything the app does, in detail. The short version is in the [README](../README.md) and inside the app
(**Help ▸ EnhanceCapCut Help**, or the **How to Use** link at the bottom of the export window).

## Enable the Services entry and a hotkey (Mac)

1. Launch EnhanceCapCut once (this registers the service).
2. Open **System Settings → Keyboard → Keyboard Shortcuts… → Services → General** and make sure
   **Export with EnhanceCapCut** is checked.
3. To assign a hotkey, double-click the right side of that row and press the shortcut
   (e.g. ⌃⌥⌘E). Pick one CapCut doesn't use.
4. In CapCut, the entry is in **CapCut → Services**. If it doesn't show up, log out and back in, or run
   `/System/Library/CoreServices/pbs -update`.

On Windows, pin EnhanceCapCut to the taskbar: clicking it again while it runs brings the export window back and
finds the project open in CapCut.

## Interface language

English or Russian, the system's by default; the 🌐 menu at the bottom of the export window switches it right away
(both platforms). The `ecc` command line always speaks English.

## Export window

| Setting | Options |
| --- | --- |
| Timeline | Shown when the project has several timelines; the main one is preselected. Other timelines add their name to `{project}` |
| Aspect ratio | From project, 9:16, 1:1, 16:9 (the canvas is re-laid out like CapCut's ratio switch) |
| Resolution | From project, 720p, 1080p, 1440p, 4K (short side) |
| Frame rate | From project, 24, 25, 30, 60 |
| Codec | H.264, HEVC 8-bit, HEVC 10-bit, ProRes 422, ProRes 422 HQ (ProRes on the Mac only) |
| Rate control | Constant quality (0–100, CRF-like) or bitrate in Mbit/s |
| Captions | Burn in CapCut auto-captions or leave them out |
| Loudness | Normalize to a LUFS target (EBU R128; on the Mac two-pass ffmpeg `loudnorm`, built in on Windows) |
| Output | Folder and file name template: `{project} {date} {time} {res} {codec} {fps}` |

Settings are remembered between exports. Before exporting, the window lists CapCut features in the project
that won't be rendered yet and media files that are missing on disk.

The project shown is the one open in CapCut (CapCut keeps a `.locked` file in it); you can pick any other
project from the list.

Changing the aspect ratio re-lays out the canvas the way CapCut's ratio switch does: clips are fitted to the
new canvas and positions stay relative to it, so texts placed near each other can overlap.

For scripting, the Mac app accepts `open ~/Applications/EnhanceCapCut.app --args -project <name> -autoExport YES`
(preselect a project and export immediately with the saved settings); `-subtitles YES` opens the subtitles window
too. On Windows: `EnhanceCapCut.exe [--project <name>] [--auto-export] [--subtitles]`.

**What is rendered:** cuts, overlays, speed (incl. curves), transforms and keyframes, canvas backgrounds, photos,
compound clips, texts and captions, and the audio mix (incl. "Isolate voice" — keep or remove vocal — "Reduce noise",
"Enhance voice" and voice filters such as "Booming"). CapCut effects, filters, transitions, stickers and text
animations are not yet. See [PLAN.md](PLAN.md).

## Subtitles and voice-over

**Subtitles & Voice-over…** in the export window (or **Window ▸ Subtitles & Voice-over**, ⌘U) transcribes the
speech of the selected timeline, translates it and adds the result **to the CapCut project itself** as
caption tracks and/or voice-over audio tracks: open the project in CapCut and they are there, editable like
CapCut's own auto-captions and voiceovers.
Everything runs offline with Apple Translation (macOS 26 or later); the logic is the one of SubDub, the Final
Cut Pro version. **Speech recognition** is either Apple's, built into macOS, or Whisper large-v3-turbo (the same as
on Windows): Whisper catches more words in noisy dialogue, and its model is downloaded once (574 MB, into
`~/Library/Application Support/EnhanceCapCut/Models`). The CLI takes `--engine apple|whisper`.

1. Close the project in CapCut (back to the home screen). CapCut overwrites a project that is open when it saves.
2. Choose **Subtitles**, **Voice-over** or both. Pick the spoken language, keep or drop the original-language
   subtitles, choose languages to translate into.
3. Each language becomes its own caption track. Pick which one is **shown on screen**; the others are added
   hidden (show them with the track's eye button in CapCut).
4. For voice-over: pick a female or male voice for each translation (▶ previews it), which language is
   **heard** (the other voice-over tracks are added muted: unmute them with the track's speaker button), and
   what happens to the **original audio** while the voice speaks: keep, lower by 15 dB, **mute all**, **replace
   voice** (the sound is muted and a track with it minus the voice plays instead, so music and ambience stay), or
   **remove vocal** (CapCut's own "Remove vocal" is turned on for the clips under the voice-over; CapCut separates
   them itself. Clips it can't do that on — compound clips, CapCut music, reversed clips, sources over 10 minutes —
   are muted instead).
   Or turn on **Clone the original voice** to dub in the speaker's own voice instead (below).
5. Click **Create**, then open the project in CapCut. With voice-over, the result goes to a **new timeline** — a copy
   of the selected one named `<project>_dub_<languages>_<original audio>_<n>` (e.g. `Trip_dub_en_muteall_1`) — and the
   selected timeline stays as it is; subtitles alone go into the selected timeline. Pick the timeline to dub from in
   the window's **Timeline** list (it follows the main window's choice).

The style and position are copied from captions already in the timeline; otherwise CapCut's default caption
look is used. Running again replaces the tracks added before (your own captions stay). The project file is
backed up first to `~/Library/Application Support/EnhanceCapCut/Backups/<project>/<time>/`, and `.srt`
files can be saved to the export folder too. The first time a language is used, macOS downloads its speech
or translation model once. On Windows the same window uses Whisper, NLLB and Windows voices (see
[windows/README.md](../windows/README.md#the-app)).

Voice-over: each translated line is spoken by an on-device Apple voice and sped up just enough (at most 1.35×)
to fit before the next line. The audio is saved inside the project folder (`EnhanceCapCut/voiceover <time>/`),
each line becomes its own clip, so lines can be moved or trimmed in CapCut. The original sound is lowered with
volume keyframes on its clips, adjustable in CapCut. Apple's default voices are compact; for better quality
download *Enhanced* or *Premium* voices in System Settings ▸ Accessibility ▸ Read & Speak (the window's **Open**
button). **Siri voices** are offered too when Xcode is installed: macOS lets only Apple-signed programs use them,
so the app speaks them through Xcode's Swift interpreter (`Tools/SiriVoice/siri-voice.swift`).

## Clone the original voice

**Clone the original voice** dubs in the voice of the person speaking, with the intonation and emotion of each
line: every translated line is spoken by [Chatterbox Multilingual](https://huggingface.co/ResembleAI/chatterbox)
V3 (Resemble AI, MIT; the ONNX export runs in ONNX Runtime on this Mac) from a reference that is the original speech
at that line, taken from the timeline with Apple's voice isolation. Each line is read back with Whisper and made
again when it came out garbled; lines that run long are sped up (at most 1.35×) without changing the pitch.
**Expressiveness** (0.5 = like the original) makes the delivery calmer or more dramatic. It speaks Arabic, Danish,
Dutch, English, Finnish, French, German, Greek, Hindi, Italian, Korean, Malay, Norwegian, Polish, Portuguese,
Russian, Spanish, Swahili, Swedish and Turkish; other languages fall back to the chosen system voice. The model is
downloaded once (1.55 GB, plus Whisper's 574 MB for the check), and a line takes about twice its length to make on
an M1. **Use it only for your own voice or people who agreed to have their voice cloned**: the window asks you to
confirm their consent before every run. Best with "Replace voice" or "Remove vocal", so the original speech doesn't
play underneath.

## Updates

**EnhanceCapCut ▸ Check for Updates…** (or the link at the bottom of the export window) installs a newer release; the
app also checks by itself, at launch and once a day. Updates come through [Sparkle](https://sparkle-project.org) and
are verified with the project's signing key. On Windows the same link downloads the new zip, checks it and restarts
the app.
