# EnhanceCapCut

Export the CapCut project you're editing with **your own export settings**, without CapCut's exporter.

EnhanceCapCut is a small macOS app that appears in the system **Services** menu. While editing in
CapCut, choose **CapCut → Services → Export with EnhanceCapCut** (or press your hotkey): a window opens
with the currently open project, you pick the parameters, press **Export**, and the finished video lands
in your folder. Rendering is done by EnhanceCapCut itself from the project file (`draft_info.json`) using
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
into `/Applications` and refreshes the Services menu. Bump `CFBundleShortVersionString` and
`CFBundleVersion` in `App/Info.plist` first, then attach the `.pkg` to a GitHub release:

```sh
gh release create v0.1.0 build/EnhanceCapCut-0.1.0.pkg --title "EnhanceCapCut 0.1.0" --notes-file notes.md
```

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

## Command line

The `ecc` tool (built next to the app, also inside the bundle at `Contents/MacOS/ecc`) uses the same renderer:

```sh
ecc list                                   # projects, * = open in CapCut
ecc info ChiangMaiMarkets                   # what will be rendered, unsupported features, missing media
ecc render 1014 -o ~/Movies/1014.mp4 --codec hevc10 --quality 70
ecc render 1014 --from 10 --duration 5 --codec prores422 -o /tmp/test.mov
ecc voice clip.mov -o voice.wav --keep-voice # audition the voice processing on one file
```

Options: `--codec h264|hevc|hevc10|prores422|prores422HQ`, `--quality 0-100`, `--bitrate Mbps`,
`--fps N`, `--aspect project|9:16|1:1|16:9`, `--height 720|1080|1440|2160`, `--lufs -14`,
`--no-subtitles`, `--from s`, `--duration s`. Without a project argument the open project is used;
unspecified options fall back to the app's saved settings. `ecc voice` takes `--keep-voice`,
`--remove-voice`, `--denoise 0.85`, `--enhance 75` (the ambient sound level) and/or
`--filter <id>` with `--strength 1` (a voice filter by its CapCut resource id or cached effect folder).
Voice filters are read from CapCut's own effect cache, so a filter renders only after CapCut has
downloaded it (i.e. it has been used or previewed in CapCut on this Mac).

## Development

```sh
swift build                 # debug build of everything
swift test                  # unit tests
swift run ecc info 1014     # try the renderer
```

Layout: `Sources/EnhanceCapCutCore` (draft model, timeline, renderer, exporter), `Sources/EnhanceCapCut`
(app, Services provider, SwiftUI window), `Sources/ecc` (CLI), `App/Info.plist` (bundle + `NSServices`),
`docs/` (roadmap and format notes). See [CLAUDE.md](CLAUDE.md) for conventions.

No CapCut resources (shaders, Lua, LUTs, fonts) and no user media or project files are part of this repo.
