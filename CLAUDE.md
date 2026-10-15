# EnhanceCapCut — conventions

macOS app that renders the CapCut project open in the editor with our own renderer. See README.md for
usage, docs/PLAN.md for the roadmap, docs/FORMAT.md for what we know about CapCut drafts.

## Language

- Everything in the repo (code, comments, docs, commit messages, UI strings) is in English.
- The author talks in Russian in chat.

## Hard rules

- Never copy CapCut's shaders, Lua, LUTs, fonts or any other resource files into the repo. Re-implement
  behaviour in our own code. Reading the user's own cached resources at render time is allowed.
- Never commit user media, renders or CapCut project files (`.gitignore` blocks common types).
- Don't patch or inject into CapCut. Integration is the Services menu plus reading project files.

## Build and verify

- `swift build`, `swift test`; `scripts/build-app.sh [--install]` for the app bundle.
- Verify renderer changes on real projects with the CLI and inspect results with ffprobe/frame grabs:
  `swift build -c release --product ecc && .build/release/ecc render <project> --from 10 --duration 5 -o /tmp/x.mp4`.
- The author's CapCut exports are in `/Volumes/NVME_4TB/VideoFromCapCut/` — the reference for visual
  calibration (text size, positions, colours).

## Code layout and style

- `EnhanceCapCutCore/Draft`: `Decodable` models. Read every field leniently (`AnyKey` container helpers
  `opt/num/str/bool/list` in `Lenient.swift`): unknown, missing or mistyped fields must never fail a draft.
- `EnhanceCapCutCore/Timeline`: draft → `Timeline` (visual layers in paint order, audio clips, report of
  unsupported features). New CapCut features start by being listed in `TimelineBuilder.report`.
- `EnhanceCapCutCore/Render`: decoding (`FrameSource`), compositing (`FrameCompositor`, Core Image),
  text (`TextRasterizer`, Core Text), audio (`AudioRenderer`).
- `EnhanceCapCutCore/Export`: settings and the `Exporter` pipeline (audio → loudnorm → frames → AVAssetWriter).
- Times are `Int64` microseconds like in the draft; convert to `CMTime` at AVFoundation boundaries.
- Swift 6 toolchain in Swift 5 language mode; the app target is `@MainActor`, the core is plain classes.
- Keep calibration constants named and documented where they're defined (see `TextRasterizer`).

## Git

Don't run git. When a git step is due, show the one-line command in a ```bash block.
