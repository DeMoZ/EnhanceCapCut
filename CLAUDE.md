# EnhanceCapCut — conventions

macOS app that renders the CapCut project open in the editor with our own renderer. See README.md (the
landing page), docs/USER-GUIDE.md and docs/CLI.md for usage, docs/DEVELOPMENT.md for building and releases,
docs/PLAN.md for the roadmap, docs/FORMAT.md for what we know about CapCut drafts.

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
- Search for reference footage only among the 10 most recent exports there (`ls -t … | head -10`); never scan or
  decode all of them — with several agents running at once that takes too much memory.

## Interface language

- The UI is in English and Russian. Every user-visible text is written in English and wrapped in `L("…")` (Swift:
  `EnhanceCapCutCore/Localization`; C#: `using static EnhanceCapCut.Draft.Localization;`), with `{0}`, `{1}` for
  values. The key must be a literal (or literals joined with `+`) so the tests can find it.
- Translations live in `Localization/ru.json` (English text → Russian), one file for both apps. A new or changed text
  gets its entry there; `swift test` / `dotnet test` fail on a key without a translation, and the Swift tests on entries
  no code uses any more.
- Until an app picks a language, `L` returns English: the CLIs and `ecc info` output stay English.
- Don't build sentences from pieces (gender words, plurals): write one key per variant.

## Not rendered yet

- `README.md` has a **Not rendered yet** list near the top: every CapCut feature the renderer doesn't reproduce, each
  with its GitHub issue (label `capcut-feature`, board https://github.com/users/DeMoZ/projects/3).
- When a feature is implemented (on both platforms), in the same change remove it from that list, from the FAQ /
  `docs/USER-GUIDE.md` wording and from the examples in the in-app guide (`GuideView.swift`, `GuideWindow.cs`,
  `ru.json`) if it's named there, update `docs/PLAN.md`, and close its issue. A newly found unsupported feature gets
  an issue and a line in the list.

## Code layout and style

- `core/` (Rust, openspec/changes/rust-core): drafts read leniently (unknown, missing or mistyped fields must never
  fail a draft), draft → `Timeline` (visual layers in paint order, audio clips, report of unsupported features) and
  its evaluation over time (`Timeline.frame`), `ecc info`, caption segmentation and the draft writers. New CapCut
  features start by being listed in the builder's report (`core/ecc-core/src/timeline/builder.rs`). Both apps use it:
  macOS through the generated `EccCore` module (`scripts/build-core.sh`; Swift conveniences in
  `EnhanceCapCutCore/Core`), Windows through the generated C# bindings in `EnhanceCapCut.Draft` (namespace
  `EnhanceCapCut.Core`; C# conveniences in `CoreExtensions.cs`); a change to the exported API means regenerating both
  bindings (docs/DEVELOPMENT.md).
- `EnhanceCapCutCore/Support/Lenient.swift`: the same lenient reading for the app's own JSON (`AnyKey` container
  helpers `opt/num/str/bool/list`).
- `EnhanceCapCutCore/Render`: decoding (`FrameSource`), compositing (`FrameCompositor`, Core Image),
  text (`TextRasterizer`, Core Text), audio (`AudioRenderer`).
- `EnhanceCapCutCore/Export`: settings and the `Exporter` pipeline (audio → loudnorm → frames → AVAssetWriter).
- `EnhanceCapCutCore/Donations`: the "Support EnhanceCapCut" window's ways to donate, downloaded from the public
  `DeMoZ/donations` repo (`enhancecapcut.json`, localized texts; the format is in that repo's README). Never hardcode
  donation links in the app. C# twin: `EnhanceCapCut.Subtitles/Donations.cs`.
- Times are `Int64` microseconds like in the draft; convert to `CMTime` at AVFoundation boundaries.
- Swift 6 toolchain in Swift 5 language mode; the app target is `@MainActor`, `EnhanceCapCutCore` is plain classes.
- Keep calibration constants named and documented where they're defined (see `TextRasterizer`).
- `windows/`: the Windows version in C# (.NET 10; Media Foundation, Direct3D/Direct2D, DirectWrite, WinUI 3 — no
  Swift there), on the same Rust core: draft parsing, the timeline builder, segmentation and the writers change in
  `core/` only, and `scripts/golden.sh fixtures` must stay clean. What stays native is mirrored by hand: the renderers
  and the voice processing; the engines differ (Apple speech/Translation/voices on the Mac, Whisper/NLLB in
  `EnhanceCapCut.Subtitles` and Windows voices in `EnhanceCapCut.Windows` on Windows). `dotnet test
  windows/EnhanceCapCut.slnx` runs on macOS too.
- `windows/src/EnhanceCapCut.App` mirrors the macOS windows (`ExportView`/`SubtitlesView` and their models) row for
  row, with the same texts: a UI change on one side gets the same change on the other. The WinUI UI is built in code
  (no XAML markup) from the grouped-form pieces in `Form.cs`; on macOS it only compiles. CI's `windows-ui` artifact
  has screenshots of both windows.

## Git

Don't run git. When a git step is due, show the one-line command in a ```bash block.
