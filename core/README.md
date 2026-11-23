# core — the shared Rust core

The platform-independent core of EnhanceCapCut, being moved from the duplicated Swift (`Sources/EnhanceCapCutCore`)
and C# (`windows/src/EnhanceCapCut.Draft`) code into one Rust crate. The plan, its stages and the decisions are in
[`openspec/changes/rust-core`](../openspec/changes/rust-core). Until the switch stages, the apps don't use it yet.

- `ecc-core` (library `ecc`): pure Rust, no FFI. Lenient draft models (`draft::model`, read like `Lenient.swift`),
  byte-preserving JSON editing (`draft::json`), projects and timelines on disk (`project`), per-OS folders
  (`env::CoreEnvironment`), and the core's texts (`tr!` over `Localization/ru.json`).
  `timeline`: draft → `Timeline` (`TimelineBuilder`, with the report of what isn't rendered), evaluation over time
  (`TimeMap`, `KeyframeTrack`, animations, masks, transitions, effects) and `Timeline::frame(t)`, everything the
  compositors ask at one moment. `info`: the `ecc info` text. `subtitles`: cues from words, line wrapping (ICU4X word
  breaks for no-space scripts) and SRT. `writers`: captions, voice-over and dubbed timelines written into drafts
  byte-preservingly, with backups and the "open in CapCut" refusal; ids and time are inputs (`IdSource`, `now_us`).
- `ecc-ffi` (library `ecc_core`: `EccCoreFFI.framework` around `libecc_core.dylib` on macOS, `ecc_core.dll` on Windows): the UniFFI surface, the only
  API the apps see. Records and enums of the timeline's static data (`types`, declared with the `record!` /
  `simple_enum!` macros of `convert` next to their conversion from `ecc`), the `Timeline`, `TimeMap` and
  `KeyframeTrack` objects (`timeline`), projects, folders and `format_info` (`project`), writers and captions
  (`writers`), and `CoreError` (panics are caught and arrive as `CoreError.Internal`). `tests/api.rs` calls the
  exported functions on every fixture. Names of the generated bindings are in `ecc-ffi/uniffi.toml`.
- `uniffi-bindgen`: the Swift binding generator (`uniffi` with its `cli` feature), run by the build scripts, never
  linked into the apps.
- `ecc-golden`: the Rust side of the golden-parity runner: `info`, `dump-timeline` and `dump-writers`
  (`[project] [--timeline name|number]`) print what the hidden commands of the Swift and C# CLIs print, and
  `materialize` writes a fixture as a project. `fixtures/` holds the synthetic projects with their expected outputs
  and `known-differences.md`; `scripts/golden.sh fixtures|local` compares the three implementations (see
  docs/DEVELOPMENT.md › Rust core and golden parity).

```bash
cd core
cargo test                               # everything
cargo clippy --all-targets -- -D warnings
cargo fmt
```

The toolchain is pinned in `rust-toolchain.toml` at the repo root; `rustup` installs it on first use.
