# EnhanceCapCut for Windows

The Windows version of EnhanceCapCut, built on Windows' own stack: C# / .NET 10, Media Foundation,
Direct3D 11 / Direct2D, DirectWrite and WinUI 3. It shares no code with the macOS app (Swift); the draft
and timeline logic is a port of it and is checked against it (see "Status").

## Layout

- `src/EnhanceCapCut.Draft` — CapCut draft model (lenient JSON reading), project and timeline lookup
  (`Timelines/project.json`), `TimelineBuilder` (draft → visual layers, audio clips, report of unsupported
  features), `TimeMap` (speed and speed curves), export settings, voice filter presets. Platform-neutral.
- `src/EnhanceCapCut.Render` — platform-neutral rendering logic: CapCut's layout as Direct2D matrices, colours,
  text calibration, frame timing, the audio mix (resampling, WSOLA time-stretch, fades), the peak limiter and
  EBU R128 loudness.
- `src/EnhanceCapCut.Windows` — the Windows renderer through Vortice: Media Foundation reading and writing
  (`MfVideoReader`, `MfAudioDecoder`, `MfVideoWriter`), Direct2D on Direct3D 11 (`Graphics`, `Compositor`),
  DirectWrite text (`TextImageRenderer`), WIC photos (`ImageLoader`), the `Exporter` pipeline, and the voice-over
  voices (`WindowsVoices`, Windows.Media.SpeechSynthesis).
- `src/EnhanceCapCut.Subtitles` — offline subtitles: speech recognition with Whisper (whisper.cpp through
  Whisper.net, CPU or Vulkan GPU), translation with NLLB-200 (ONNX Runtime), the model store, voice-over timing
  (`Dubber`) and the pipeline. The segmentation, the language table and writing captions and voice-over into the
  draft (`DraftSubtitleWriter`, `DraftVoiceOverWriter`) live in `EnhanceCapCut.Draft`, ported from the macOS
  version. Platform-neutral.
- `src/EnhanceCapCut.App` — the app (`EnhanceCapCut.exe`), WinUI 3 (Windows App SDK, unpackaged and
  self-contained): the export window and the Subtitles & Voice-over window, see below.
- `src/ecc` — command-line tool: `ecc list`, `ecc info [project] [--timeline name|number]`, and on Windows
  `ecc render` (same options as the macOS CLI; HEVC 10-bit needs an encoder that supports it), and
  `ecc subtitles` / `ecc voices` (see below).
- `tests/` — unit tests for the draft and render layers (run anywhere) and end-to-end export tests (Windows only;
  elsewhere they return without checking).

## Build

Requires the .NET 10 SDK. Everything here builds on macOS too and, except the app, runs there, which is how it's
checked against the Swift version (on macOS the app is only compiled: the Windows App SDK's packaging tools are
skipped).

```sh
dotnet test windows/EnhanceCapCut.slnx
dotnet run --project windows/src/ecc -f net10.0 -- info "My Project"
```

On Windows:

```sh
dotnet run --project windows/src/ecc -f net10.0-windows10.0.19041.0 -c Release -- render "My Project" --from 10 --duration 5 -o x.mp4
dotnet run --project windows/src/EnhanceCapCut.App -c Release
dotnet publish windows/src/EnhanceCapCut.App -c Release -r win-x64 --self-contained -o build/app
```

## The app

`EnhanceCapCut.exe` shows the same two windows as the macOS app, laid out the same way (the SwiftUI grouped forms
are rebuilt from WinUI controls in `Form.cs`: titled sections as cards, label on the left and control on the
right, the status and main button in a bar at the bottom):

- **Export with EnhanceCapCut** — the CapCut project (the one open in CapCut is selected), timeline, what isn't
  rendered yet, video (aspect ratio, resolution, frame rate, H.264 / HEVC 8-bit / HEVC 10-bit, constant quality
  or bitrate, burned-in captions), audio (loudness normalization — built in, no ffmpeg needed), output folder and
  file name. Settings are shared with `ecc render` (`%APPDATA%\EnhanceCapCut\settings.json`).
- **Subtitles & Voice-over** — the same choices as on the Mac: subtitles and/or voice-over, the spoken language,
  translations, the track shown on screen, per-language Windows voices (▶ previews), the audible voice-over track
  and what happens to the original sound. **Clone the original voice** dubs in the speaker's own voice with
  Chatterbox (`VoiceCloner`, ONNX Runtime, the same model and code as on the Mac) after a consent confirmation;
  Windows has no voice isolation yet, so the reference is the mix itself and "Replace voice" isn't offered. When the
  Whisper, NLLB or voice cloning model isn't there yet, the window offers the one-time download.

There is one instance: starting it again brings the export window back and re-detects the project open in CapCut
(pin it to the taskbar for the macOS Services entry's role). Closing a window hides it while the other one is
open; closing the last one quits, after asking if an export or a subtitles run is still going. Window frames and
the subtitle choices are kept in `%APPDATA%\EnhanceCapCut\app.json`. For testing:
`EnhanceCapCut.exe [--project <name>] [--auto-export] [--subtitles]`.

**Updates:** the link at the bottom of the export window ("Version 0.6.0 · Check for Updates…") asks GitHub for the
latest release, and the app checks once a day at launch (a newer version then shows as "Update to x.y.z…"). Updating
downloads the release zip, checks it against the SHA-256 GitHub publishes, quits, copies the new files over the app's
folder with a small PowerShell script and starts it again (`Updater.cs`, `AppUpdates.cs`). The folder must be
writable — keep the app outside Program Files. A local build (version 0.0.0) doesn't show the link. CI updates a
published 0.0.1 to 0.0.2 served from localhost (`scripts/update-smoke.ps1`, with `ECC_UPDATES_URL` and the app's
`--auto-update`, which installs without asking): once with a file held open, which must fail and restart the old
version with `--update-failed`, and once for real.

CI starts the published app on a demo project and keeps screenshots of both windows (the `windows-ui` artifact
of the Windows workflow).

## Subtitles

`ecc subtitles` does what the macOS app's Subtitles window does — transcribes the timeline, translates it and adds
the result to the CapCut project as caption tracks (one per language, one visible) — with the same options:

```sh
ecc subtitles "My Project" --language ru --translate en,th --show en --srt subs
```

Close the project in CapCut first. Everything runs offline, but the models are downloaded once, on first use, into
`%LOCALAPPDATA%\EnhanceCapCut\Models` (`ECC_MODELS` overrides it): Whisper large-v3-turbo, 5-bit (~570 MB), and,
for translation, NLLB-200 distilled 600M, 8-bit ONNX (~0.9 GB). NLLB's licence (CC BY-NC 4.0) is non-commercial,
which is why it isn't shipped. Apple's Translation on the Mac is noticeably better than NLLB-600M, especially
between two non-English languages (Russian → Thai). Languages: `SubtitleLanguage.All` (24, all handled by both
models). On other systems `ecc subtitles` needs the mix as a WAV file (`--audio mix.wav`), which is how it's
checked on macOS.

Voice-over, as on the Mac: `--voice en,th` (or `en=<voice id>`) speaks those translations with Windows voices
(Settings ▸ Time & language ▸ Speech ▸ Add voices; `ecc voices <language>` lists the installed ones), `--hear th`
picks the audible track, `--original keep|lower|mute` what happens to the project's own sound, `--no-captions`
makes only the voice-over. Each line is sped up just enough to fit its slot (at most 1.35×), saved inside the
project folder and added as a clip of its own.

## Status

Stages 1–3 of the roadmap in [docs/PLAN.md](../docs/PLAN.md#windows-version): the draft/timeline layer, a first
version of the renderer (`ecc render`), checked by CI's end-to-end test but not yet on real projects on Windows,
subtitles and voice-over (`ecc subtitles`, checked on macOS with a WAV mix apart from the Windows voices), and the
app's two windows. Voice effects aren't applied on Windows yet (the export warns).

CapCut for Windows writes the same draft format as the Mac app (seen with new_version 189.0.0), in a file called
`draft_content.json` instead of `draft_info.json`, and keeps projects in
`%LOCALAPPDATA%\CapCut\User Data\Projects\com.lveditor.draft`. A project open in CapCut has a `.locked` file there too.
`ecc list` and the export window find projects by either file name.
