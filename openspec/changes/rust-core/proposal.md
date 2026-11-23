# Proposal: one Rust core for both apps

## Why

The platform-independent core exists twice: in Swift (`Sources/EnhanceCapCutCore/Draft`, `Timeline`, the
draft-writing half of `Subtitles`, `Localization`) and as its C# port (`windows/src/EnhanceCapCut.Draft`):
about 5k lines on each side. Every draft-format or timeline change is written, reviewed
and tested twice (#89, keyframe easing, touched 14 files on both sides), and the two copies already drift:
Thai and other no-space scripts wrap differently because macOS uses `NLTokenizer` while .NET breaks at graphemes.
The parity rule ("keep them in step, `ecc info` identical") is kept by discipline and one-off comparisons only.
Moving the core into one Rust crate that both apps call removes the duplication and makes parity structural.

## What Changes

- New Cargo workspace `core/` with the platform-independent core in Rust:
  - lenient draft model (unknown, missing or mistyped fields never fail a draft), project and timeline lookup;
  - `TimelineBuilder` → `Timeline` (visual layers, audio clips, filter layers, report of unsupported features and
    missing media), and the time-dependent evaluation the renderers ask for (time maps, keyframes with easing,
    clip / text animations, masks, transitions, effect states);
  - subtitle segmentation, line wrapping (one word segmenter for every platform), SRT writing;
  - writing subtitles, voice-over and dubbed timelines into drafts byte-preservingly (`JSONScanner` /
    `JSONEdits`), with backups;
  - the `ecc info` report text;
  - the core's own interface texts through the shared `Localization/ru.json`.
- FFI bindings generated from the Rust crate with UniFFI: Swift (static library in an XCFramework, SwiftPM
  `binaryTarget`) and C# (`uniffi-bindgen-cs`, P/Invoke to `ecc_core.dll` / `libecc_core.dylib`).
- Golden parity tests: synthetic fixture projects plus a local run over the author's projects compare `ecc info`,
  a canonical Timeline JSON dump and writer outputs of Swift, C# and Rust before each switch.
- Staged migration, each stage shippable: crate alongside → golden parity → macOS switched → Windows switched →
  old Swift/C# core code deleted.
- Behaviour change: no-space scripts (Thai, Lao, Khmer, Burmese, Chinese, Japanese) are wrapped by the same
  segmenter on both platforms; Windows line breaks for them change (and may shift slightly on macOS).
- **BREAKING (for contributors only)**: building needs a Rust toolchain; `swift build` / `dotnet build` need the
  core built first (`scripts/build-core.sh`, or the MSBuild target). The "keep Swift and C# in step" rule is
  removed from `CLAUDE.md` and `docs/DEVELOPMENT.md` once both apps use the crate.

Out of scope (stay native and consume the Rust `Timeline`): rendering (Core Image / Direct3D·Direct2D),
decoding and encoding (AVFoundation / Media Foundation), text rasterization (Core Text / DirectWrite), speech
recognition, translation and voices, voice DSP (`VoiceFilter`, `VoiceFilterGraph`), export settings, the MCP
server, the UIs (SwiftUI / WinUI), Donations, and the "is CapCut running" process check.

## Capabilities

### New Capabilities

- `draft-timeline`: reading CapCut projects and drafts leniently, building the `Timeline` with its report of
  unsupported features, evaluating it over time, and the `ecc info` report — one implementation for both apps.
- `subtitle-authoring`: caption segmentation and line wrapping, SRT output, and writing subtitle tracks,
  voice-over tracks and dubbed timelines into CapCut drafts — one implementation for both apps.

### Modified Capabilities

None (there are no specs in `openspec/specs/` yet).

## Impact

- Code: new `core/` (Rust); `Package.swift` gains a `binaryTarget` and a bindings target; consumers in
  `Sources/EnhanceCapCutCore/Render`, `Export`, `Subtitles` (pipeline, Dubber), `Sources/ecc`,
  `Sources/EnhanceCapCut` move to the generated types; `windows/src/EnhanceCapCut.Draft` keeps its name but
  shrinks to the generated bindings plus the parts that stay C# (`ExportSettings`, `VoiceFilterGraph`, `McpServer`),
  and `EnhanceCapCut.Render`, `.Subtitles`, `.Windows`, `.App`, `ecc` move to the generated types. Swift `Draft/`, `Timeline/`, the writers and their C# twins
  are deleted at the end; their unit tests move to Rust (`cargo test`) or to thin binding smoke tests.
- Build and release: Rust toolchain pinned by `rust-toolchain.toml`; `scripts/build-app.sh` builds the
  universal (arm64 + x86_64) static library first; signing and notarization of the `.pkg` are unchanged (the
  library is linked into the signed executables); the Windows publish carries `ecc_core.dll` (win-x64).
- CI: `windows.yml` installs Rust and builds the core before `dotnet`; a new macOS job (`swift build`,
  `swift test`, golden tests) and a Linux `cargo test` job; `release.yml` builds the core on both runners.
- Dependencies: `serde`/`serde_json`, `uniffi`, `uniffi-bindgen-cs` (pinned to a matching UniFFI version),
  `icu_segmenter` (word breaks for no-space scripts).
- Docs: `CLAUDE.md`, `docs/DEVELOPMENT.md`, `windows/README.md`, `docs/PLAN.md` (Windows section).
