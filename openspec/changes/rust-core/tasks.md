# Tasks

Stages follow design.md → Migration Plan. Each group ends in a state where both apps build, test and ship.

## 1. Stage 0 — spike (throwaway branch)

- [x] 1.1 Create a minimal `core/` workspace (`ecc-core` with lenient reading of a two-layer draft and
  `format_info`, `ecc-ffi` with UniFFI exports) and verify `cargo test` passes on macOS
- [x] 1.2 Generate Swift bindings, package a universal static XCFramework, link it from a scratch SwiftPM target
  with `swift build -c release --arch arm64 --arch x86_64` (Swift 5 language mode) and verify `lipo -info` shows
  both arches and the call returns the expected text
- [x] 1.3 Generate C# bindings with the newest `uniffi-bindgen-cs` compatible with the chosen UniFFI, call them
  from a net10.0 xUnit test, and verify it passes with `dotnet test` on macOS and on `windows-latest` (win-x64)
- [x] 1.4 Express the hard API shapes (object holding `Arc<Timeline>` inside an enum-with-data record, `Vec` of
  records, error enum, `Option<String>`) in both bindings and verify they round-trip in the Swift and C# tests
- [x] 1.5 Build the ICU4X word segmenter with Thai/Lao/Khmer/Burmese and CJK data into the library and record
  the size of the universal `.a`, the win-x64 `.dll` and a stripped linked `ecc` in design.md
- [x] 1.6 Record the outcome in design.md (pinned UniFFI / `uniffi-bindgen-cs` pair; keep decision 3 or switch C#
  to the fallback; ICU data choice) and verify the design's decisions 3 and 7 name the chosen options

## 2. Stage 1 — workspace and lenient draft model

- [x] 2.1 Add `rust-toolchain.toml`, `core/Cargo.toml` (workspace, release profile from design decision 2),
  crates `ecc-core` and `ecc-golden`, `/core/target/` in `.gitignore`, and verify `cargo build` and
  `cargo clippy -D warnings` pass
- [x] 2.2 Port `Lenient.swift` as the `Lenient` accessor trait (`opt`, `num`, `int64`, `str`, `bool`, `list`)
  and verify unit tests for numeric strings, 0/1 booleans, `null`, wrong types and lossy lists
- [x] 2.3 Port the draft models (`DraftModel`, `MaskMaterial`, `AnimationMaterial`, `TextContent` with UTF-16
  style ranges) and verify the ported `testParsesLeniently` and `testStyleRangesCountCodePoints` cases pass
- [x] 2.4 Port `Project.swift` (`CapCutProject`, `ProjectTimeline`, `DraftFile`, `ProjectLocator`,
  `DraftLoader`) with `CoreEnvironment` roots and per-OS defaults, and verify tests for `draft_info.json` vs
  `draft_content.json`, several timelines, and selection by name and 1-based number
- [x] 2.5 Port `JSONScanner`, `JSONEdits` and `JSONText` with a Swift-`Double.description`-compatible float
  formatter, and verify byte-exact tests including `1.0`, `0.1`, `1e-05`, large integers and escaped strings

## 3. Stage 1 — timeline, report and evaluation

- [x] 3.1 Port `TimeMap`, `KeyframeTrack` and `KeyframeBezier` (easing from #89, keyframes on the source time
  axis) and verify the ported `KeyframeEasingTests` and `CoreTests` time-map cases pass in Rust
- [x] 3.2 Port `ClipAnimation`, `TextAnimation`, `ClipMask`, `Transition`, `VideoEffect`, `BlendMode`,
  `ChromaKey`, `ClockSticker`, `TextEffect`, `ColorFilter` (cache lookup through `CoreEnvironment`) and verify
  their ported test files pass
- [x] 3.3 Port `TimelineBuilder` and `Timeline` (layers, audio clips, filters, compound clips, cutouts, voice
  effects, "Enhance quality" from #88, text `typesetting` passed through for vertical text from #90,
  `without_subtitles`) and verify the ported builder tests pass, including
  C#-only cases from `DraftTests.cs`
- [x] 3.4 Port the report (`note`, `reportRefs`, `report(key:item:)`, missing files) and verify one test per
  report category and that rendered features are not listed
- [x] 3.5 Add `Timeline::frame(t) -> FrameState` and vectorized `TimeMap` / `KeyframeTrack` sampling, and verify
  tests that `frame(t)` equals per-layer evaluation for every layer at clip boundaries and keyframe times
- [x] 3.6 Add the `tr!` macro over the embedded `Localization/ru.json` and `set_core_language`, and verify a
  `cargo test` that every `tr!` key has a Russian entry and that the default language is English
- [x] 3.7 Port `ecc info` formatting as `format_info` and verify its output on the seeded fixtures equals the
  Swift CLI's byte for byte

## 4. Stage 1 — subtitles and draft writers

- [x] 4.1 Port `Segmenter` and `SRTWriter` and verify the ported `SubtitleTests` segmentation and SRT cases pass
- [x] 4.2 Port `LineWrapper` on the ICU4X word segmenter with LSTM data (design decision 7: skipped characters
  attached to the previous unit, numbers kept with the next word, single-cluster units merged into a neighbour,
  spaces preferred, Chinese/Japanese by grapheme) and verify Thai, Lao, Chinese and Japanese tests never break
  inside a word or number (Thai checked against `NLTokenizer` breaks on real caption text), and that
  Latin/Cyrillic results equal today's Swift output (decided: option (b), native word breaks through
  `set_word_breaker`, design decision 7)
- [x] 4.3 Port `DraftSubtitleWriter` (apply, replace previous, Thai size factor, backups, `.locked` +
  `capcut_running` refusal) with `IdSource` and `now` injection, and verify byte-exact tests on fixture drafts
- [x] 4.4 Port `DraftVoiceOverWriter` (all `OriginalAudio` modes, ducking fades, `withoutVoiceOver`) and verify
  the ported `VoiceOverTests` pass byte-exactly
- [x] 4.5 Port `DubTimeline` (unique names, project list entry, layout tab, root id) and verify the ported
  `DubTimelineTests` pass on a temp project

## 5. Stage 1 — golden parity harness

- [x] 5.1 Create `core/fixtures/` (`draft.fixture.json`, `timelines.fixture.json`, `media.txt`) seeded from the
  inline drafts of the Swift and C# tests plus the fixtures listed in design decision 11, and verify the harness
  materializes each as a project under both draft file names
- [x] 5.2 Add hidden `ecc dump-timeline` and `ecc dump-writers` to the Swift CLI, and the identical commands to
  the C# CLI, not listed in help; document them in `docs/DEVELOPMENT.md`; verify both print the canonical JSON
  for one fixture and that `swift test` / `dotnet test` still pass
- [x] 5.3 Add the same commands plus `info` to `ecc-golden`, and verify they run on Linux
- [x] 5.4 Write `scripts/golden.sh fixtures|local` (UUID normalization, one timeline at a time, diff report) and
  `core/fixtures/known-differences.md`, and verify `golden.sh fixtures` shows Rust = Swift on every fixture and
  Rust = C# except listed known differences
- [x] 5.5 Run `golden.sh local` over the author's CapCut library and verify `info` and `timeline.json` are
  identical for every timeline (count of projects/timelines and any known differences in the PR description)
- [x] 5.6 Commit expected outputs under `core/fixtures/<name>/expected/` with an `UPDATE_GOLDEN=1` rewrite path,
  and verify `cargo test` fails on a deliberately changed expected file
- [x] 5.7 Add `.github/workflows/core.yml` (Linux: fmt, clippy, `cargo test`; macOS: `swift build`, `swift test`,
  `golden.sh fixtures`) and verify it passes on the stage-1 PR

## 6. Stage 2 — bindings and build plumbing

- [x] 6.1 Add `ecc-ffi` with the API from design decision 4 (objects, records, `frame(t)`, `format_info`,
  writers, `CoreError`, panic → error) and verify Rust tests call it through the exported functions
- [x] 6.2 Write `scripts/build-core.sh [--universal]` (host or arm64 + x86_64, `MACOSX_DEPLOYMENT_TARGET=15.0`,
  `lipo`, `xcodebuild -create-xcframework`, Swift binding generation) and verify it produces
  `build/core/EccCoreFFI.xcframework` with a `macos-arm64_x86_64` slice
- [x] 6.3 Add `EccCoreFFI` (`binaryTarget`) and `EccCore` (committed generated Swift) to `Package.swift`,
  `@_exported import EccCore` in `EnhanceCapCutCore`, and Swift smoke tests (build a fixture timeline, read
  layers, `frame(t)`, write captions); verify `swift build` and `swift test` pass after `build-core.sh`
- [ ] 6.4 Add the generated C# bindings to `EnhanceCapCut.Draft` (`Generated/`, `AllowUnsafeBlocks`), the
  `BuildRustCore` MSBuild target with RID → Rust triple mapping and `SkipRustBuild`, and C# smoke tests; verify
  `dotnet test windows/EnhanceCapCut.slnx` passes on macOS and on `windows-latest`
- [x] 6.5 Make `scripts/build-app.sh` run `build-core.sh` (`--universal` with `--pkg`) and verify a `--pkg`
  build is universal (`lipo -info` on `ecc` and `EnhanceCapCut`), passes `codesign --verify --strict`, and is
  accepted by `notarytool` (not checkable: no Developer ID, releases are ad-hoc signed; design decision 8)
- [x] 6.6 Update `windows.yml`, `release.yml` and `core.yml` (toolchain, `rust-cache`, pinned
  `uniffi-bindgen-cs`, a bindings-are-current check that regenerates and runs `git diff --exit-code`, library
  size printed) and verify all three workflows pass (release via `workflow_dispatch` dry run or a test tag on a
  fork)
- [x] 6.7 Check sizes against the budget in design.md (universal `.pkg`, win-x64 publish) and verify the numbers
  are recorded in the PR description
- [x] 6.8 Update `docs/DEVELOPMENT.md` (Rust in requirements, `build-core.sh` before `swift build`, optional
  shared `CARGO_TARGET_DIR`, `RUST_BACKTRACE`, golden runner) and `windows/README.md`, and verify the documented
  commands run as written on a clean checkout

## 7. Stage 3 — macOS switch

- [x] 7.1 Add Swift convenience extensions on the generated types (`.url`, `CGSize`, `Identifiable` /
  `Hashable` conformances, `Date`) and verify they compile with no change to call sites' meaning
- [x] 7.2 Move `Render/` (`FrameCompositor`, `MaskRaster`, `VideoEffectRenderer`, `AudioRenderer`, voice and
  stabilization users) to the generated types and `frame(t)` / vectorized sampling, and verify `swift test`
  render tests pass
- [x] 7.3 Move `Export/`, `Subtitles/` (`SubtitlePipeline`, `Dubber`), `Automation/MCPServer`, `Sources/ecc`
  (`info` prints `format_info`) and the app models to the Rust core, call `set_core_language` next to
  `Localization.use`, and verify `swift test` and `golden.sh local` pass
- [x] 7.4 Extend `LocalizationTests` to collect `tr!` keys from `core/**/*.rs` and verify it fails on a
  `ru.json` entry used by no Swift, C# or Rust code
- [x] 7.5 Render three recent projects (keyframes with easing, a compound clip, captions in Thai) with the old and
  new builds via `ecc render`, and verify frame grabs match within tolerance and look right next to the matching
  CapCut exports among the 10 newest in `/Volumes/NVME_4TB/VideoFromCapCut/`
- [ ] 7.6 Write captions and a voice-over into a test project from the app, open it in CapCut, and verify the
  tracks appear and play; verify export time of a reference project is within 2 % of the previous build
- [x] 7.7 Note the Thai/CJK line-break change in the next release notes and verify it is in
  `docs/releases/<tag>.md` (nothing to note on macOS: with option (b) of decision 7 the Mac keeps NLTokenizer's
  breaks; the Windows change goes into stage 4's notes)

## 8. Stage 4 — Windows switch

- [x] 8.1 Move `EnhanceCapCut.Render`, `EnhanceCapCut.Subtitles`, `EnhanceCapCut.Windows`, `EnhanceCapCut.App`
  and `ecc` (`info` prints `format_info`) to the generated types, call `set_core_language` at startup, and verify
  `dotnet test windows/EnhanceCapCut.slnx` passes on macOS
- [x] 8.2 Extend the C# `LocalizationTests` to `core/**/*.rs` keys and verify it passes
- [x] 8.3 Verify `windows.yml` passes: unit tests, end-to-end export test, `ui-smoke.ps1` screenshots and
  `update-smoke.ps1`, with `ecc_core.dll` in the published folder
- [x] 8.4 Run the Windows `ecc info` on the fixtures and verify it equals the macOS CLI byte for byte, then
  remove the Windows-only entries from `known-differences.md`

## 9. Stage 5 — removal of the duplicated core

- [ ] 9.1 After one release shipped with both switches and no core regression reports, delete Swift
  `Draft/`, `Timeline/`, `Segmenter`, `DraftSubtitleWriter`, `DraftVoiceOverWriter`, `DubTimeline` and their tests
  that moved to Rust, and verify `swift build` and `swift test` pass
- [ ] 9.2 Delete the C# twins in `EnhanceCapCut.Draft` and their moved tests, and verify `dotnet test` passes on
  macOS and Windows CI
- [ ] 9.3 Remove the hidden dump commands from both native CLIs and the Swift/C# producers in `golden.sh`
  (snapshots in `core/fixtures/*/expected/` stay the reference), and verify `cargo test` and `core.yml` pass
- [ ] 9.4 Update `CLAUDE.md` (drop the "keep Swift and C# in step" rules, describe `core/` and where core
  changes go), `docs/DEVELOPMENT.md`, `windows/README.md`, `docs/PLAN.md` and the `openspec/config.yaml`
  context, and verify a search for the old type names outside `core/` finds only generated bindings and the docs
  mention one core

## Workflow follow-up

- Archive the change after stage 5 is merged and the docs are updated.
