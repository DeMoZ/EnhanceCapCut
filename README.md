# EnhanceCapCut

Export the CapCut project you're editing with **your own export settings**, without CapCut's exporter.

EnhanceCapCut is a small macOS app that appears in the system **Services** menu. While editing in
CapCut, choose **CapCut → Services → Export with EnhanceCapCut** (or press your hotkey): a window opens
with the currently open project, you pick the parameters, press **Export**, and the finished video lands
in your folder. Rendering is done by EnhanceCapCut itself from the project file (`draft_info.json`, or
`draft_content.json` in CapCut for Windows) using
AVFoundation, Core Image/Metal and VideoToolbox hardware encoders.

> Status: milestone 1. Cuts, overlays, speed (incl. curves), transforms and keyframes, canvas
> backgrounds, photos, compound clips, texts and captions, and the audio mix (incl. "Isolate voice" —
> keep or remove vocal — "Reduce noise", "Enhance voice" and voice filters such as "Booming") are rendered. CapCut effects,
> filters, transitions, stickers and text animations are not yet — the export window lists which of
> them your project uses. See [docs/PLAN.md](docs/PLAN.md).

## Install

1. Download `EnhanceCapCut-x.y.z.pkg` from [Releases](../../releases) and open it. The app goes to
   `/Applications`.
2. Follow *Enable the Services entry and a hotkey* below.

> If macOS says the installer is from an unidentified developer, open
> **System Settings ▸ Privacy & Security** and click **Open Anyway**.

**Updates:** **EnhanceCapCut ▸ Check for Updates…** (or the link at the bottom of the export window) installs a newer
release; the app also checks by itself once you allow it. Updates come through [Sparkle](https://sparkle-project.org)
and are verified with the project's signing key. On Windows the same link downloads the new zip, checks it and
restarts the app.

## Requirements

- macOS 15 or later, Apple Silicon recommended (hardware HEVC/ProRes encoding).
- Xcode 16+ command line tools (Swift 6 toolchain) to build.
- Optional: `ffmpeg` (`brew install ffmpeg`) for loudness normalization.

## Build from source

```sh
scripts/build-app.sh --install
```

This builds `build/EnhanceCapCut.app`, copies it to `~/Applications`, registers it with Launch Services
and refreshes the Services menu. Without `--install` it only builds.

### Release installer

```sh
scripts/build-app.sh --pkg
```

Builds a universal (Apple Silicon + Intel) app and `build/EnhanceCapCut-<version>.pkg`, which installs
into `/Applications` and refreshes the Services menu.

Releases are built by GitHub Actions (`.github/workflows/release.yml`) for both platforms: bump
`CFBundleShortVersionString` and `CFBundleVersion` in `App/Info.plist` (the version of the Windows build too),
put the release notes in `docs/releases/v<version>.md`, merge, then push a tag:

```sh
git tag v0.3.0 origin/main && git push origin v0.3.0
```

The workflow builds the macOS `.pkg` and the Windows `EnhanceCapCut-<version>-windows-x64.zip` (self-contained
`EnhanceCapCut.exe` and `ecc.exe`) and publishes the release with both, plus `appcast.xml`: the Mac app's update feed
(`scripts/appcast.sh`), signed with the EdDSA key in the `SPARKLE_PRIVATE_KEY` secret. Its public half is
`SUPublicEDKey` in `App/Info.plist`; a copy of the private key is in the maintainer's login keychain (account
`EnhanceCapCut`, Sparkle's `generate_keys`). The Windows app reads the latest release from the GitHub API and checks
the zip against GitHub's SHA-256. CI builds are unsigned.

Signed and notarized installer (requires an Apple Developer ID):

```sh
xcrun notarytool store-credentials enhancecapcut --apple-id you@example.com --team-id TEAMID
DEV_ID_APP="Developer ID Application: Your Name (TEAMID)" \
DEV_ID_INSTALLER="Developer ID Installer: Your Name (TEAMID)" \
NOTARY_PROFILE=enhancecapcut scripts/build-app.sh --pkg
```

## Enable the Services entry and a hotkey

1. Launch EnhanceCapCut once (this registers the service).
2. Open **System Settings → Keyboard → Keyboard Shortcuts… → Services → General** and make sure
   **Export with EnhanceCapCut** is checked.
3. To assign a hotkey, double-click the right side of that row and press the shortcut
   (e.g. ⌃⌥⌘E). Pick one CapCut doesn't use.
4. In CapCut, the entry is in **CapCut → Services**. If it doesn't show up, log out and back in, or run
   `/System/Library/CoreServices/pbs -update`.

## Export window

| Setting | Options |
| --- | --- |
| Timeline | Shown when the project has several timelines; the main one is preselected. Other timelines add their name to `{project}` |
| Aspect ratio | From project, 9:16, 1:1, 16:9 (the canvas is re-laid out like CapCut's ratio switch) |
| Resolution | From project, 720p, 1080p, 1440p, 4K (short side) |
| Frame rate | From project, 24, 25, 30, 60 |
| Codec | H.264, HEVC 8-bit, HEVC 10-bit, ProRes 422, ProRes 422 HQ |
| Rate control | Constant quality (0–100, CRF-like) or bitrate in Mbit/s |
| Captions | Burn in CapCut auto-captions or leave them out |
| Loudness | Normalize to a LUFS target (EBU R128, two-pass ffmpeg `loudnorm`) |
| Output | Folder and file name template: `{project} {date} {time} {res} {codec} {fps}` |

Settings are remembered between exports. Before exporting, the window lists CapCut features in the project
that won't be rendered yet and media files that are missing on disk.

The project shown is the one open in CapCut (CapCut keeps a `.locked` file in it); you can pick any other
project from the list.

For scripting, the app accepts `open ~/Applications/EnhanceCapCut.app --args -project <name> -autoExport YES`
(preselect a project and export immediately with the saved settings).

Changing the aspect ratio re-lays out the canvas the way CapCut's ratio switch does: clips are fitted to the
new canvas and positions stay relative to it, so texts placed near each other can overlap.

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
   what happens to the **original audio** while the voice speaks: keep, lower by 15 dB, or mute.
5. Click **Create**, then open the project in CapCut.

The style and position are copied from captions already in the timeline; otherwise CapCut's default caption
look is used. Running again replaces the tracks added before (your own captions stay). The project file is
backed up first to `~/Library/Application Support/EnhanceCapCut/Backups/<project>/<time>/`, and `.srt`
files can be saved to the export folder too. The first time a language is used, macOS downloads its speech
or translation model once. On Windows the same window uses Whisper, NLLB and Windows voices (see
[windows/README.md](windows/README.md#the-app)).

Voice-over: each translated line is spoken by an on-device Apple voice and sped up just enough (at most 1.35×)
to fit before the next line. The audio is saved inside the project folder (`EnhanceCapCut/voiceover <time>/`),
each line becomes its own clip, so lines can be moved or trimmed in CapCut. The original sound is lowered with
volume keyframes on its clips, adjustable in CapCut. Apple's default voices are compact; for better quality
download *Enhanced* or *Premium* voices in System Settings ▸ Accessibility ▸ Read & Speak (the window's **Open**
button). **Siri voices** are offered too when Xcode is installed: macOS lets only Apple-signed programs use them,
so the app speaks them through Xcode's Swift interpreter (`Tools/SiriVoice/siri-voice.swift`).

## Command line

The `ecc` tool (built next to the app, also inside the bundle at `Contents/MacOS/ecc`) uses the same renderer:

```sh
ecc list                                   # projects, * = open in CapCut
ecc info ChiangMaiMarkets                   # what will be rendered, unsupported features, missing media
ecc render 1014 -o ~/Movies/1014.mp4 --codec hevc10 --quality 70
ecc render KohChangFisherman --timeline 2 # one of several timelines, by number or name
ecc render 1014 --from 10 --duration 5 --codec prores422 -o /tmp/test.mov
ecc voice clip.mov -o voice.wav --keep-voice # audition the voice processing on one file
ecc subtitles DurianDick --language ru-RU --translate en,th --show en --srt ~/Movies   # add captions to the project
ecc subtitles DurianDick --language ru-RU --voice en,th --hear en --no-captions        # voice-over only
ecc voices th                              # installed voices for a language, with their ids
```

Options: `--codec h264|hevc|hevc10|prores422|prores422HQ`, `--quality 0-100`, `--bitrate Mbps`,
`--fps N`, `--aspect project|9:16|1:1|16:9`, `--height 720|1080|1440|2160`, `--lufs -14`,
`--no-subtitles`, `--from s`, `--duration s`, `--timeline name|number`. Without a project argument the open project is used;
unspecified options fall back to the app's saved settings. `ecc voice` takes `--keep-voice`,
`--remove-voice`, `--denoise 0.85`, `--enhance 75` (the ambient sound level) and/or
`--filter <id>` with `--strength 1` (a voice filter by its CapCut resource id or cached effect folder).
Voice filters are read from CapCut's own effect cache, so a filter renders only after CapCut has
downloaded it (i.e. it has been used or previewed in CapCut on this Mac).
`ecc subtitles` takes `--language` (spoken, e.g. `ru-RU`), `--translate en,th`, `--no-original`, `--show <code>`
(the visible track), `--keep-previous` (don't replace earlier tracks), `--srt <folder>` and `--draft out.json`
(write the changed draft there instead of into the project), `--no-captions`, `--voice en,th` (voice-over
languages; `th=<voice id>` picks a voice from `ecc voices`, Siri voices as `siri:<id>`), `--hear <code>` (the
audible voice-over track) and `--original keep|lower|mute` (default `lower`). Translation languages must already
be downloaded (the app's window can download them).

## Development

```sh
swift build                 # debug build of everything
swift test                  # unit tests
swift run ecc info 1014     # try the renderer
```

Layout: `Sources/EnhanceCapCutCore` (draft model, timeline, renderer, exporter), `Sources/EnhanceCapCut`
(app, Services provider, SwiftUI window), `Sources/ecc` (CLI), `App/Info.plist` (bundle + `NSServices`),
`docs/` (roadmap and format notes), `windows/` (the Windows version in C#/.NET, see its README). See [CLAUDE.md](CLAUDE.md) for conventions.

No CapCut resources (shaders, Lua, LUTs, fonts) and no user media or project files are part of this repo.
