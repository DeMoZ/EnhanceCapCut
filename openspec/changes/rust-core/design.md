# Design: one Rust core for both apps

## Context

See proposal.md for the motivation. What the code looks like today:

- **Swift core** (`Sources/EnhanceCapCutCore`): `Draft/` (lenient `Decodable` models on `AnyKey` helpers,
  `JSONScanner`/`JSONEdits`/`JSONText` for byte-preserving edits, `Project.swift` with `ProjectLocator`,
  `DraftLoader`), `Timeline/` (`TimelineBuilder` → `Timeline` of `VisualLayer` classes, `AudioClip`,
  `FilterLayer`, `RenderReport`; `TimeMap`, `KeyframeTrack` + `KeyframeBezier`, `ClipAnimation`,
  `TextAnimation`, `ClipMask`, `Transition`, `VideoEffect`, `ColorFilter`, `BlendMode`, `ChromaKey`,
  `ClockSticker`, `TextEffect`), and in `Subtitles/`: `Segmenter` (+ `LineWrapper` on `NLTokenizer`,
  `SRTWriter`), `DraftSubtitleWriter`, `DraftVoiceOverWriter`, `DubTimeline`. `Localization.swift` holds `L()`.
- **C# port** (`windows/src/EnhanceCapCut.Draft`, net10.0, no platform APIs): the same files one-to-one, plus
  things that stay C# here: `ExportSettings`, `VoiceFilterGraph`, `McpServer`.
- **Mirrored render-side code** outside the core: since #93 the voice processing stages and constants exist in
  both languages (Swift `VoiceProcessor` / `SpectralProcessing` / `VoiceFilter` on Accelerate; C#
  `VoiceProcessor`, `VoiceFilterProcessing`, `SpectralProcessing`, `Fft` in `EnhanceCapCut.Render`), and since #90
  vertical text placement (`VerticalTextLayout`, measured with Core Text / DirectWrite).
- Platform reach inside the core is small and known: file existence checks and directory listings
  (`TimelineBuilder` missing media, cutout mask folders, CapCut's effect cache for voice filters and LUTs),
  home-relative roots (projects, CapCut cache, our backup folder), `NSWorkspace` / `Process` for "is CapCut
  running", `NLTokenizer` for no-space scripts, `UUID()` and `Date()` in the writers.
- Report texts are localized at build time (`note(L("Transition"), …)`), so the core needs the translation table.
- Text style ranges in drafts count code points; renderers need UTF-16 ranges (`TextContent.utf16Range`).
- The renderers evaluate the timeline per frame: `VisualLayer.transform(at:)`, `TimeMap.sourceTime`,
  `TextAnimations.state`, `VideoEffect.state`, `ClipMask.geometry(at:)`, transition `progress(at:)`; the audio
  renderer samples `TimeMap` every 50 ms and the volume envelope at ramp breaks.
- Build: SwiftPM (`swift-tools-version:6.0`, `swiftLanguageModes: [.v5]`, macOS 15), `whisper` already comes as
  a dynamic XCFramework `binaryTarget`; `scripts/build-app.sh --pkg` builds `--arch arm64 --arch x86_64`, signs
  with Developer ID (hardened runtime), `pkgbuild`/`productbuild`, notarizes. Windows: .NET 10,
  `dotnet publish -r win-x64 --self-contained`; CI `windows.yml` on `windows-latest` (PRs and main),
  `release.yml` on `macos-26` + `windows-latest` for tags. There is no macOS CI job on pull requests today.
- `.gitignore` blocks `draft_info.json` / `draft_content.json` everywhere, so committed fixture drafts need other
  file names.
- Parity today: one manual comparison of `ecc info` on 102 timelines of 69 projects, identical up to Unicode
  normalization of file names (docs/PLAN.md, Windows stage 1). No committed fixtures; drafts are inline JSON in
  tests (148 Swift tests, 109 C# `[Fact]`s).

## Goals / Non-Goals

**Goals:**

- One implementation of everything in the two capability specs, called from Swift and C# through generated
  bindings; no hand-written wrapper layer per language beyond small convenience extensions.
- Both apps keep building, testing and shipping after every stage.
- Parity proven mechanically (golden outputs) before each switch, and kept by snapshots afterwards.
- No change to signing, notarization, installer layout or the Windows publish shape beyond one extra native
  library on Windows.

**Non-Goals:**

- Rewriting renderers, encoders, text layout, speech, translation, voices, voice DSP, UI or the MCP server.
- Linux or other new platforms (Linux is only a cheap CI runner for `cargo test`).
- Windows arm64 or any new Windows release artifact (the crate builds for `aarch64-pc-windows-msvc`, but
  nothing publishes it until a release asks for it).
- Performance work beyond "not slower than today".
- The mirrored render-side code (voice DSP from #93, `VerticalTextLayout` from #90). It is duplicated too, but it
  is tied to native audio buffers and font measurement; it is a candidate for a follow-up change once this one
  has proved the bindings, build and golden setup.

## Decisions

### 1. What crosses into Rust, and the seams that stay native

Rust gets the modules listed in the proposal. The platform seams become plain inputs:

- **Roots**: `CoreEnvironment { projects_root, capcut_cache_roots, backup_root }` with per-OS defaults in Rust
  (`cfg(target_os)`: `~/Movies/CapCut/...` and the sandboxed container on macOS, `%LOCALAPPDATA%\CapCut\...` on
  Windows, `~/Library/Application Support/EnhanceCapCut/Backups` / `%LOCALAPPDATA%\EnhanceCapCut\Backups`).
  Native code and tests may override any of them; golden tests always do.
- **File system**: Rust uses `std::fs` directly (existence, listing, reading, atomic writes). Both OSes expose
  the same semantics we rely on; APFS lookups stay normalization-insensitive, so no path normalization is added.
- **"Is CapCut running"** stays native (`NSWorkspace` / `Process.GetProcessesByName`). Write functions take a
  `capcut_running: bool`; Rust checks the `.locked` file and refuses when both are true.
- **Ids and clock**: writer functions take an `IdSource` (random UUIDs by default, a counter in tests) and a
  `now` timestamp, so outputs are deterministic under test.

Alternative considered: moving the "is running" check and roots into Rust behind `cfg` only. Rejected for the
process check (needs `sysinfo`-style dependencies and differs from what the apps already show the user); roots
keep Rust defaults because the Rust golden CLI needs them without a native host.

### 2. Crate layout

```
core/
  Cargo.toml            workspace; rust-toolchain.toml at repo root pins the toolchain
  ecc-core/             pure Rust: draft, project, timeline, eval, subtitles, writers, info, l10n — no FFI
  ecc-ffi/              #[uniffi::export] surface; crate-type = ["staticlib", "cdylib"]; lib name ecc_core
  ecc-golden/           binary: info, dump-timeline, apply-captions, apply-voice-over (golden runner, CI on Linux)
  fixtures/             synthetic projects + expected outputs (see 7)
```

`ecc-core` has no knowledge of bindings, so it is tested with plain `cargo test` and can change shape without
regenerating anything. `ecc-ffi` is a thin mapping layer (records/objects/errors) and is the only API both apps
see. Release profile: `lto = "thin"`, `codegen-units = 1`, `panic = "unwind"` (UniFFI turns panics into errors;
`abort` would kill the app), `debug = "line-tables-only"`.

Spike findings (stage 0): the pure crate's *library* is named `ecc` (`[lib] name = "ecc"`), because `ecc-ffi`'s
library is `ecc_core` and two crates with the same library name collide in `target/`. `ecc-ffi` has
`crate-type = ["lib", "staticlib", "cdylib"]` (`lib` so Rust tests call the exported functions). The workspace
also has a tiny `uniffi-bindgen` binary crate (`uniffi` with the `cli` feature, same pinned version) that
generates the Swift bindings; it is never linked into the apps.

### 3. Binding generator: UniFFI for both languages

| Option | Swift side | C# side | Cost |
|---|---|---|---|
| **A. UniFFI + uniffi-bindgen-cs** (chosen) | official UniFFI Swift bindings | NordSecurity `uniffi-bindgen-cs` | one annotated Rust API; records, enums with data, objects (`Arc`), errors generated for both; third-party C# generator pinned to a UniFFI version |
| B. cbindgen + C module / csbindgen | hand-written Swift wrapper over a C header | csbindgen `DllImport`s + hand-written C# wrapper | raw `extern "C"` API; two hand-written wrapper layers (ownership, strings, nested enums) — the duplication we remove, now in unsafe code |
| C. UniFFI (Swift) + csbindgen (C#) | generated | hand-written C# over a separate C ABI | two ABI surfaces in Rust |
| D. JSON over a few `extern "C"` calls | `Codable` mirror of the Timeline | `System.Text.Json` mirror | model types duplicated again; per-frame evaluation either duplicated or JSON per call |

A wins because the thing we are removing is per-language duplication; B and C bring it back as wrappers, D
brings back the models. UniFFI's record transfer is a serialization into a byte buffer, which is acceptable
because the Timeline crosses once per build and per-frame calls are batched (decision 4).

Pinning: `uniffi` and `uniffi-bindgen-cs` versions are pinned together in `core/Cargo.toml` and installed with
`cargo install --locked` in CI (the C# generator lags UniFFI by one or two minors; we upgrade only when both
support the same version). Generated bindings are committed (`Sources/EccCore/ecc_core.swift` plus header and
modulemap in the XCFramework; `windows/src/EnhanceCapCut.Draft/Generated/ecc_core.cs`), and CI regenerates them
and fails on a diff, so a forgotten regeneration can't ship.

Fallback if the stage-0 spike shows `uniffi-bindgen-cs` cannot express our API (or breaks on .NET 10): C# uses
option D for the static Timeline data plus csbindgen for a handful of batched evaluation calls, keeping Swift
on UniFFI. The spike decides this before any Swift switch work starts, so tasks do not change after it.

**Stage-0 outcome: option A is kept; the fallback is not needed.** Pinned pair:

- `uniffi = "=0.32.0"` (exact: `uniffi-bindgen-cs` is built against `uniffi_bindgen` 0.32.0; crates.io already has
  0.32.2, which we don't take until the C# generator moves);
- `uniffi-bindgen-cs` **v0.12.0+v0.32.0** (released 2026-11-09, the newest tag). It is not on crates.io:
  `cargo install uniffi-bindgen-cs --git https://github.com/NordSecurity/uniffi-bindgen-cs --tag v0.12.0+v0.32.0 --locked`
  (about 1 min on an M-series Mac). Requires Rust ≥ 1.88; we build with 1.100.

Both generators run in library mode on the built library (`libecc_core.dylib` / `ecc_core.dll`) with
`--no-format`, so the output doesn't depend on whether swiftformat or CSharpier happens to be installed. The C#
bindings regenerated from `ecc_core.dll` on `windows-latest` are byte-identical to the ones generated from the
dylib on macOS, and the Swift ones on the macOS runner to the local ones, so the "bindings are current" check
works on either OS. Names come from `ecc-ffi/uniffi.toml`: Swift `module_name = "EccCore"`,
`ffi_module_name = "EccCoreFFI"`; C# `namespace = "EnhanceCapCut.Core"`, `cdylib_name = "ecc_core"`,
`access_modifier = "public"`. The generated C# (net10.0, `Nullable` on, `LangVersion latest`, `AllowUnsafeBlocks`;
it uses `LibraryImport`) and Swift (Swift 5 and Swift 6 language modes) compile without warnings.

### 4. API shape across the boundary

- `Timeline` is a UniFFI **object** (`Arc<Timeline>`, immutable, `Send + Sync`): `info()`, `layers()`,
  `audio_clips()`, `filters()`, `report()`, `has_subtitles()`, `without_subtitles()`. Compound content holds an
  `Arc<Timeline>`, which gives recursion without recursive records.
- Static per-layer data are **records** fetched once (`VisualLayer`, `AudioClip`, `TextLayerSpec`, …), keeping
  today's Swift type and field names so consumer edits stay mechanical (UniFFI maps `snake_case` to Swift
  `camelCase` and C# `PascalCase`).
- Evaluation is batched: `timeline.frame(t) -> FrameState` returns every active layer's transform, text
  animation, effect, mask and transition state in one call per video frame. `TimeMap` and `KeyframeTrack` are
  objects with vectorized methods (`source_times(offsets: Vec<i64>)`, `values(at: Vec<i64>)`) for the audio
  renderer.
- Types: times `i64` µs; paths `String` (Swift extension `.url`, C# as is); canvas `Size { width, height }`
  (Swift extension → `CGSize`); modification dates as Unix ms; text style ranges exposed in UTF-16 units.
- Errors: one `CoreError` enum with localized messages (`ProjectOpen`, `NoTimelineList`, `Malformed`, `Io`; stage 2
  added `NotFound`, `Unreadable` and `Internal`, see below).
- `format_info(project_path, timeline: Option<String>) -> String` produces the whole `ecc info` text; both CLIs
  print it, so their outputs are identical by construction.
- Swift: a target `EccCore` (generated Swift) on the `binaryTarget`; `EnhanceCapCutCore` does
  `@_exported import EccCore` so the app and CLI need no new imports. C#: generated code lives in
  `EnhanceCapCut.Draft`, which keeps its name (no reference churn) and shrinks to the bindings plus the C# that
  stays (`ExportSettings`, `VoiceFilterGraph`, `McpServer`, UI `Localization`, `IsOpenInCapCut`).

What the spike showed about these shapes (an `Arc<Timeline>` object inside an enum-with-data inside a record,
`Vec` of records, an error enum, `Option<String>`; all round-trip in Swift and C# tests):

- **Equality.** Swift gets no `Equatable`/`Hashable` for a record or enum that holds an object (UniFFI can't
  derive it). Fix: `#[uniffi::export(Eq, Hash)]` on the object (identity: same `Arc`) and on the enums that hold
  it. In C#, an exported `Eq` on a record turns `Equals` into an FFI call and `GetHashCode` returns 0 unless
  `Hash` is exported too, so export the traits only where a consumer compares values, not on every record.
  Records without objects get plain `Equatable, Hashable` (Swift) and value `record` equality (C#) for free.
- **Errors.** `CoreError` is `#[uniffi(flat_error)]`: each case carries its localized `Display` text as
  `message` — Swift `CoreError.ProjectOpen(message:)` (case names keep Rust's PascalCase), C#
  `CoreException.ProjectOpen` with `.Message`. With a rich (field-carrying) error, Swift's `errorDescription` is
  only `String(reflecting:)` and `#[uniffi::export(Display)]` is ignored for errors, so the localized text would
  be lost. Panics are caught in `ecc-ffi` (`catch_unwind`) and arrive as `CoreError.Internal(message)` in both
  languages; uncaught ones would surface as Swift `rustPanic` / C# `PanicException`.
- **C# shapes.** `Vec<T>` is `T[]` (not `List<T>`); `Option<String>` is `string?`; enums with data are nested
  `record`s (`LayerContent.Compound(Timeline Timeline)`); objects are `IDisposable` classes with a finalizer. A
  variant field named like its variant is renamed (`Text { text }` → `LayerContent.Text.TextValue`, to avoid
  CS0542), so the real API names such fields differently (`Text { spec }`).
- **Swift shapes.** Enum cases are lowerCamel (`.compound(timeline:)`), objects are `open class … @unchecked
  Sendable`, records and enums are `Sendable`.
- **Mapping cost.** Keeping `ecc-core` free of UniFFI means `ecc-ffi` re-declares every exported type and maps it
  (`From` impls). That is mechanical but proportional to the API; if it grows past what a reviewer can check,
  `#[cfg_attr(feature = "ffi", derive(uniffi::Record))]` on the `ecc-core` types is the cheaper alternative
  (decided in stage 2, it doesn't change the generated API).

**Stage-2 outcome (`ecc-ffi`, task 6.1).** The mapping stays in `ecc-ffi`, and `ecc-core` stays free of UniFFI.
The `cfg_attr` derive would have pushed FFI shapes into the core:
- `usize` indices would become `u64`;
- `BTreeMap` would become `HashMap`;
- `TimeMap` / `KeyframeTrack` would have to be held as `Arc<Object>`;
- `PathBuf` would need custom types.

Instead, two declarative macros (`ecc-ffi/src/convert.rs`) declare each exported type and its conversion in one
place, one line per field:
- `record! { VisualLayer = core::VisualLayer { … } }`;
- `simple_enum! { … }`.

A single `Ffi` trait converts the field types:
- paths become `String`;
- indices become `u64`;
- maps become `HashMap`;
- `Box` is unwrapped;
- `TimeMap` / `KeyframeTrack` become objects;
- `Arc<Timeline>` becomes the `Timeline` object.

Only the enums with data are written by hand, about ten conversions. Other choices the API needed:
- **Writers take data, not closures.** The native `write(…, transform:)` / `DubTimeline.create(…, transform:)` pass
  a closure, which can't cross the boundary well. They take a `DraftEdits` record instead: caption tracks, then
  voice-over tracks, with the original-audio mode and `replace_previous`. This is the core's
  `writers::DraftEdits::apply`, the shared logic of both `SubtitlePipeline.apply`.
  - `write_timeline` and `create_dub_timeline` apply it inside the backup-and-write step.
  - `apply_edits` applies it to bytes.
  - `without_voice_over` plus `Timeline::from_draft` give the speech timeline.
- **Ids.** An `IdSource` enum: `Random`, or `Counting { after }` for deterministic tests. A dubbed timeline takes
  its id from the same sequence as the edits.
- **Records have no methods** (the C# generator's support for them is untested). A project is a `CapCutProject`
  record with `name` filled in, plus free functions: `project_timelines`, `project_timeline`, `resolve_project`,
  `all_projects`, `open_project`. Writers take the project's path and read the lock file fresh.
- **Errors.** `CoreError` cases:
  - `ProjectOpen`, `NoTimelineList`, `Malformed`, `Io`;
  - `NotFound`: the `ecc info` arguments;
  - `Unreadable`: a draft that can't be read or parsed;
  - `Internal`: a caught panic.

  Every call that does work returns `Result` and runs under `catch_unwind`: building, `frame` / `frames`, writers,
  segmentation, `format_info`. That includes `frame`, so a core bug fails one frame instead of crashing the app;
  Swift would `fatalError` on a panic in a non-throwing call. Getters of an already built timeline only copy data
  and don't throw. `debug_panic` exists so the binding smoke tests can check the panic path.
- **Swift error text.** UniFFI 0.32's Swift `errorDescription` is `String(reflecting: self)`, for flat errors too.
  The stage-0 note above held only for C#: there `CoreException.Message` is the text. A hand-written
  `Sources/EccCore/CoreError+Message.swift`, in the same module as the generated file, makes `CoreError`
  `CustomDebugStringConvertible` with the message. `String(reflecting:)` uses that conformance, so
  `localizedDescription` is the localized text, and the generated file stays untouched.
- **Re-export without clashes.** `@_exported import EccCore` in `EnhanceCapCutCore` builds while the old Swift types
  still exist. A module's own declarations shadow those of a module it re-exports, so the app and the CLI keep
  getting `EnhanceCapCutCore.Timeline` until stage 3 deletes it. The smoke tests import `EccCore` alone.
- **Names.** `r#type` and `r#loop` come out as `type` / `loop` in Swift and `Type` / `Loop` in C#, so call sites
  keep today's names. Clock hands become a `ClockHands { minute, hour }` record, and voice-over lines a
  `LineRange { start, end }` record. The generated Swift compiles in Swift 5 and Swift 6 modes, and the C#
  (`Nullable`, `LangVersion latest`) with no warnings.

### 5. Lenient reading and byte-exact writing in Rust

- Drafts are parsed once into `serde_json::Value`; models are built by hand through a `Lenient` accessor trait
  mirroring `Lenient.swift` (`opt`, `num` accepting numeric strings, `int64` rounding doubles, `bool` accepting
  0/1, `list` skipping bad elements), with a small macro for the boilerplate. Strict `serde` derive was
  rejected: a mistyped field fails the whole struct unless every field gets a custom deserializer, which is the
  same code with less control.
- Writers port `JSONScanner` / `JSONEdits` byte-for-byte and `JSONText` with a float formatter that reproduces
  Swift's `Double.description` (`1.0`, `1e-05`, shortest round-trip) — Rust's `Display` differs (`1`,
  `0.00001`). The same formatter serves `ecc info` (`"\(fps)"`); `%.2f`-style output uses explicit rounding
  helpers. Golden byte comparisons pin both.

### 6. Localization in the core

`ecc-core` has a `tr!("English {0}", arg)` macro with the same `{n}` substitution as `L()`, over
`Localization/ru.json` embedded with `include_str!` (one file, still shared). Native apps call
`set_core_language("ru")` when they call `Localization.use`; until then the core is English, so CLIs stay
English. UI texts keep the native `L()`. The key-coverage tests are extended: the Swift test that fails on unused
`ru.json` entries also scans `core/**/*.rs` for `tr!` keys, and a `cargo test` checks every `tr!` key has a
Russian entry. Alternative rejected: returning untranslated keys plus args for native translation — the report
mixes our texts with CapCut names, and every consumer would re-implement the assembly.

### 7. Word segmentation for no-space scripts

`icu_segmenter` (ICU4X) word segmenter with compiled data: dictionary for Chinese/Japanese, LSTM models for Thai,
Lao, Khmer, Burmese. The wrapper keeps today's rules on top (attach skipped characters to the previous unit, keep
numbers with the next word). This replaces `NLTokenizer` on macOS and the space/grapheme split on Windows, so
both platforms get the same breaks (a deliberate behaviour change, see spec `subtitle-authoring`). Size budget:
measured in the spike; if ICU data pushes the library over budget (risk below), use LSTM-only data for the four
Southeast Asian scripts and grapheme breaks for Chinese/Japanese (typographically allowed there).

**Stage-0 measurements** (`icu_segmenter` 2.4, compiled data, release profile of decision 2; spike core = lenient
draft + timeline + `format_info` + word units; "linked" = a stripped universal Swift executable calling the core,
minus the same executable without it):

| ICU data (cargo feature) | linked, universal, stripped | gzip -9 | win-x64 `ecc_core.dll` (gzip -9) |
|---|---|---|---|
| none (rule-based only) | +1.25 MB | 0.61 MB | 0.53 MB (0.22 MB) |
| `icu-lstm` (Thai/Lao/Khmer/Burmese LSTM) | +1.94 MB | 1.23 MB | 0.87 MB (0.53 MB) |
| `icu-auto` (LSTM + Chinese/Japanese dictionary) | +5.97 MB | 4.36 MB | 2.88 MB (2.08 MB) |
| `icu-dictionary` (dictionaries for all) | +7.37 MB | 5.05 MB | 3.59 MB (2.42 MB) |

The universal `libecc_core.a` itself is 84 MB (14 MB after `strip -S`): it carries all of `std` and the
dependencies as object code plus line tables; only what is referenced reaches the executables, so the
executable numbers are the ones that ship. Deployment target `minos 15.0` checked with `otool`. The executable
numbers are from Xcode 27.1; the same `icu-auto` spike executable stripped on the `macos-15` runner (Xcode 16.4)
is 10.7 MB instead of 6.1 MB, so the linker matters — task 6.7 re-measures on the release toolchain. On Windows
the one `ecc_core.dll` is shared by `EnhanceCapCut.exe` and `ecc.exe`; even `icu-auto` fits the +4 MB budget there.

The `.pkg` carries the core twice (statically in `ecc` and in `EnhanceCapCut`, decision 8), so `icu-auto` costs
about 2 × 4.4 ≈ 8.7 MB compressed — over the +6 MB budget before the full core is even ported. Segmentation
quality from the same runs:

- Chinese/Japanese: `auto` gives the same words as `NLTokenizer` with the language set (`我们|今天|去|北京`,
  `今日|は|良い|天気|です`); `lstm` leaves a CJK run as one unit.
- Thai: both ICU4X modes sometimes break *inside* a word where `NLTokenizer` doesn't — LSTM `สวัส|ดี` and
  `ดี|มา|ก` (for `ดีมาก`), dictionary `ดีม|า|ก`; finer but valid splits (`ขอบ|คุณ`, `วัน|นี้`) are harmless.
  Lao, Khmer and Burmese split plausibly.

**Choice: `icu-lstm`** on both platforms (this decision's own fallback): LSTM data for Thai/Lao/Khmer/Burmese, grapheme breaks for
Chinese/Japanese in the wrapper (with the usual no-break-before/after punctuation rules), ≈ 2 × 1.2 MB in the
`.pkg`. Because the LSTM model can split inside a Thai word, the wrapper does not take a lone break as
final: a unit of a single grapheme cluster is merged into its neighbour, spaces are preferred over ICU breaks,
and task 4.2 checks Thai breaks against `NLTokenizer` on real caption text. Alternative kept open: `icu-auto` in
one shared dynamic library in `Contents/Frameworks` (like `whisper`, ≈ 4.4 MB once) if Chinese/Japanese word
breaks turn out to matter; that changes decision 8 and is not taken now.

**Stage-1 measurement (task 4.2).** The wrapper is ported (`subtitles::segmenter`): LSTM units, skipped characters
kept with the unit before, single letters joined to a neighbour, numbers kept with the next word, a break at a space
preferred within 10 characters of balance, Chinese/Japanese by grapheme with no break before closing or after opening
punctuation. Russian and English output equals today's Swift `LineWrapper` on all 1,053 caption texts of the author's
library. On the library's 784 Thai caption texts, 431 need wrapping. Their 527 breaks were checked against
`NLTokenizer` (Thai), and 71 (13 %) fall inside one of its words. Today's Swift output has none, and Windows today breaks at
any grapheme. Almost all of them split compounds that `NLTokenizer` keeps whole (`ออกกำลัง|กาย`, `ความ|ผิด`,
`พวก|เขา`, `ไมโคร|โฟน`). ICU4X's dictionary data does no better (82 of 528), and stronger space preference helps
little (66 with a 20-character margin) at the cost of balance. Options, to be chosen before the macOS switch:
(a) accept it: one implementation, better than Windows today, a visible step back from macOS today for Thai;
(b) a native word-break callback for no-space scripts (`NLTokenizer` on macOS, ICU4X on Windows): today's macOS
quality, at the price of identical Thai/CJK breaks on both platforms; (c) a compound-aware join on top of ICU4X
(e.g. a small list of common Thai compounds), cheap but never complete. Until the author decides, the port uses (a).

### 8. macOS build integration

- `scripts/build-core.sh [--universal]`: `cargo build --release -p ecc-ffi` for the host target, or for
  `aarch64-apple-darwin` and `x86_64-apple-darwin` with `MACOSX_DEPLOYMENT_TARGET=15.0`; `lipo` into one fat
  `libecc_core.a`; `xcodebuild -create-xcframework -library … -headers …` →
  `build/core/EccCoreFFI.xcframework` (under the already ignored `/build/`).
- `Package.swift`: `.binaryTarget(name: "EccCoreFFI", path: "build/core/EccCoreFFI.xcframework")`,
  `.target(name: "EccCore", dependencies: ["EccCoreFFI"])`; `EnhanceCapCutCore` depends on `EccCore`.
- `scripts/build-app.sh` runs `build-core.sh` first (`--universal` with `--pkg`). Static linking puts the core
  inside the `ecc` and `EnhanceCapCut` executables: nothing new to copy into `Contents/Frameworks`, sign or
  notarize separately; the existing codesign/notarytool steps are unchanged. (Stage 2 replaced this with a
  dynamic `EccCoreFFI.framework`, because Xcode 26 can't link the static one in a universal build; see the
  stage-2 change below.)
- Swift 6 toolchain in Swift 5 mode: generated code builds in both modes; the spike checks it with the current
  Xcode command line tools, without `-warnings-as-errors`.
- Spike-verified (Xcode 27.1, Swift 6.4 locally; `macos-15` runner in CI): `cargo build` for both Apple triples →
  `lipo` → `xcodebuild -create-xcframework` gives one `macos-arm64_x86_64` slice; the generated
  `EccCoreFFI.modulemap` must be copied into the headers folder as `module.modulemap`; a `binaryTarget` path must
  lie inside the package (fine for `build/core/` at the repo root). `swift build -c release --arch arm64 --arch
  x86_64` links it, `lipo -info` shows both arches, and the x86_64 slice runs under Rosetta. Xcode 27 prints
  "x86_64 architecture is deprecated" for universal builds — a warning only, but a sign the x86_64 slice has a
  limited future.

**Stage-2 change: a dynamic framework, not a static library.** The static library builds and links with
Xcode 27.1 (the author's Mac), in both the single- and the multi-architecture build. Xcode 26.6 is the newest on
the `macos-26` runner image. In the release's `swift build --arch arm64 --arch x86_64` (XCBuild), it prelinks
`EccCore_Module.o` with `-lecc_core`, but without the XCFramework's folder: `ld: library not found`. Naming the
library after the XCFramework didn't help.

`whisper.framework`, a dynamic framework from an XCFramework, links in that same build, so the core takes the same
shape:
- `build-core.sh` `lipo`s the two `libecc_core.dylib` (the cdylib) into `EccCoreFFI.framework/Versions/A/EccCoreFFI`,
  sets its install name to `@rpath/EccCoreFFI.framework/Versions/A/EccCoreFFI`, and adds the header, a
  `framework module` map and an `Info.plist`. It then wraps it with `xcodebuild -create-xcframework -framework`.
- `build-app.sh` copies it into `Contents/Frameworks` and signs it, like whisper. The executables already have
  `@executable_path/../Frameworks` in their rpath.

This is also smaller: one copy of the core (2.3 MB per architecture, from the dylib's dead-code stripping and the
release profile) instead of one inside each executable.

Measured on `scripts/build-app.sh --pkg`:
- `lipo -info` shows `x86_64 arm64` for the framework, `ecc` and `EnhanceCapCut`.
- `codesign --verify --deep --strict` passes.
- A copy of the app outside the checkout runs `ecc` natively and under Rosetta.
- The `.pkg` is 27.1 MB, against 23.8 MB without the core (+3.3 MB of the +6 MB budget). Each executable also
  grows by about 2 MB of compiled generated Swift, and nothing calls the core yet.

Notarization isn't checked: the project has no Developer ID, and releases are ad-hoc signed (`DEV_ID_APP` unset in
`release.yml`). The new framework is signed the same way as `whisper.framework`. Check `notarytool` the first time
a Developer ID is set up.

Alternatives rejected: a SwiftPM build-tool plugin running `cargo` (sandboxed, no network for the registry, no
writes outside the plugin work dir); committing prebuilt `.a` files (repo bloat, unclear provenance); a remote
`binaryTarget(url:checksum:)` (every core change would need a published artifact before `swift build`).

### 9. Windows / .NET build integration

- `EnhanceCapCut.Draft.csproj` gets `AllowUnsafeBlocks` and an MSBuild target `BuildRustCore` before `Build`:
  maps `$(RuntimeIdentifier)` (or the host when empty) to a Rust triple (`win-x64` → `x86_64-pc-windows-msvc`,
  `osx-arm64` → `aarch64-apple-darwin`, …), runs `cargo build --release -p ecc-ffi --target …`, and adds the
  `cdylib` (`ecc_core.dll` / `libecc_core.dylib`) as `None` with `CopyToOutputDirectory` and
  `CopyToPublishDirectory`. `-p:SkipRustBuild=true` skips it when CI has built the core already.
- `dotnet test windows/EnhanceCapCut.slnx` keeps working on macOS (host dylib copied next to the test assembly).
- Spike-verified with a miniature of this target (`RustTarget` from the RID, `SkipRustBuild`, one `None` item
  for the library): `dotnet test` passes on macOS (osx-arm64) and on `windows-latest` (win-x64). The library item
  is computed per platform (`ecc_core.dll` / `libecc_core.dylib` / `libecc_core.so`) as one property; a
  `Condition="Exists('%(FullPath)')"` on a list of candidates batches over *every* `None` item and copied the
  dylib over unrelated files in the output folder.
- `dotnet publish -r win-x64 --self-contained` puts `ecc_core.dll` next to `EnhanceCapCut.exe` / `ecc.exe`;
  the `runtimes/` pruning in `release.yml` does not touch it.

### 10. CI

- `windows.yml`: `rustup` (preinstalled on `windows-latest`) honours `rust-toolchain.toml`; `Swatinem/rust-cache`;
  install the pinned `uniffi-bindgen-cs`; build the core; then the existing `dotnet` steps unchanged.
- New `core.yml`: `ubuntu-latest` job with `cargo fmt --check`, `cargo clippy -D warnings`, `cargo test`
  (includes golden snapshots) and the bindings-are-current check; `macos-26` job with `build-core.sh`,
  `swift build`, `swift test`, and the Swift-vs-Rust golden comparison while both exist.
- `release.yml`: the macOS job gets the core via `build-app.sh --pkg`; the Windows job builds the core for
  `x86_64-pc-windows-msvc` before `dotnet publish`. The local release fallback (stuck `macos-26` runner) needs
  only `rustup` on the release Mac, since `build-app.sh` builds the core itself.

### 11. Golden parity

- **Fixtures** (`core/fixtures/<name>/`): `draft.fixture.json` (+ optional `timelines.fixture.json`,
  `media.txt` listing placeholder files to create). The harness materializes each into a temp project with
  `draft_info.json` *and*, in a second pass, `draft_content.json`. Seeded from the inline drafts in the Swift and
  C# tests, then one fixture per report category, speed curves, compound clips, masks, transitions, keyframes
  with easing (#89), "Enhance quality" (#88), vertical typesetting (#90, now rendered, so no longer reported),
  captions in en/ru/th/zh, voice-over with every original-audio mode.
- **Outputs**: `info.txt`; `timeline.json` — a canonical dump (static data, plus `frame(t)` and audio samples
  at 0, every clip boundary ±1 µs, every keyframe time ±1 µs and 16 even steps; numbers printed with 6 decimals);
  writer outputs for fixed tracks, with UUIDs replaced by `<id1>`, `<id2>`… in order of appearance and a fixed
  `now`.
- **Producers**: a hidden `ecc dump-timeline` / `ecc dump-writers` in the Swift and C# CLIs (stage 1, not in
  help, documented in `docs/DEVELOPMENT.md`) and the same commands in `ecc-golden`.
- **Runner** `scripts/golden.sh fixtures|local`: `fixtures` runs in CI; `local` walks every project and timeline
  of the author's CapCut library with the three CLIs and diffs `info` and `timeline.json` (memory-light: one
  timeline at a time, no media decoding). Known, accepted differences are listed with reasons in
  `core/fixtures/known-differences.md` (file-name Unicode normalization; no-space script wrapping).
- **Reference**: where Swift and C# disagree, Swift is the reference (the macOS renderer is calibrated against
  CapCut exports); the C# difference is noted in `known-differences.md` until the Windows switch removes it.
- **After the switch**: expected outputs are committed under `core/fixtures/<name>/expected/` and checked by
  `cargo test` (`UPDATE_GOLDEN=1` rewrites them); they become the only parity reference once the old code is gone.

### 12. Tests after the move

Logic tests move to Rust (`ecc-core` unit tests, ported from the Swift tests first since they are the reference,
adding C#-only cases). Swift and C# keep thin binding smoke tests (build a fixture timeline, read a record,
evaluate a frame, write captions) and all tests of the code that stays native. The localization key tests stay
on both sides and learn about `core/`.

## Risks / Trade-offs

- [`uniffi-bindgen-cs` lags or stalls] → pinned pair of versions; spike in stage 0 before committing; fallback in
  decision 3 is chosen up front, so the plan does not change if it is needed. Stage 0: the current pair
  (UniFFI 0.32.0 / v0.12.0+v0.32.0) works; the generator trails UniFFI by one minor (0.32.0 vs 0.32.2) and had
  three releases in 2026.
- [Binary size: Rust std + serde_json + ICU segmenter data in a universal binary] → budget +6 MB for the `.pkg`
  and +4 MB for the Windows publish, measured in the spike and on every release build (`size` printed in CI);
  thin LTO, `opt-level = 3` but stripped symbols in the app; LSTM-only ICU data if over budget. The spike measured
  `icu-auto` over the `.pkg` budget, so LSTM-only is chosen (decision 7); the full core adds code on top of the
  spike's ≈ 0.6 MB compressed per executable, re-measured in task 6.7.
- [ICU4X LSTM splits some Thai words (`ดีมาก` → `ดี|มา|ก`)] → the wrapper merges single-cluster units and prefers
  spaces (decision 7); task 4.2 compares Thai breaks with `NLTokenizer` on real captions; if that is not good
  enough, Thai can switch to dictionary data (dictionary mode misses too, see decision 7) or keep `NLTokenizer`
  on macOS behind a native callback — the last one gives up identical breaks on both platforms.
- [Build time: cold universal cargo build adds minutes to `build-app.sh` and CI] → `rust-cache` in CI; dev builds
  host-only; `cargo` incremental for the core; the core is a separate step, so `swift build` after a Swift-only
  change does not rebuild Rust.
- [Debugging through FFI: a panic or bad value surfaces as a generic error] → panics are caught and carry the
  message and location into `CoreError`; `ecc-golden` reproduces any timeline problem without the app;
  line tables kept so LLDB / Visual Studio show Rust frames; `RUST_BACKTRACE=1` documented for the CLIs.
- [Timeline transfer cost: records are serialized across the boundary] → one transfer per build and one
  `frame(t)` call per frame; acceptance: building + transferring the largest local timeline costs ≤ 50 ms more
  than today, `frame(t)` with 50 active layers ≤ 0.1 ms, export time of a reference project within 2 % of today.
- [Keyframes and easing (#89) and "Enhance quality" (#88) are the newest ported code; Swift and C# may already
  differ] → dedicated fixtures sampled at and around every keyframe; Swift wins on disagreement; the switch PR
  of each platform includes a render of a keyframe-heavy project compared against a recent CapCut export.
- [Float formatting differences (Swift `description`, `%.2f`, C# invariant formatting)] → one formatter in Rust,
  byte-level golden tests over `info.txt` and writer outputs.
- [Thai/CJK wrapping changes for users] → fixtures with known-good breaks; check against the Thai exports among
  the 10 newest CapCut exports; release notes mention it.
- [Triple maintenance while migrating (Swift, C#, Rust)] → any core change between stage 1 and stage 4 lands in
  Rust too, with a fixture; `capcut-feature` work that touches `TimelineBuilder` is preferably scheduled after the
  macOS switch; stages 3–4 kept short.
- [Agents and worktrees: each worktree gets its own multi-GB `target/`] → documented optional shared
  `CARGO_TARGET_DIR` (cargo locks it, concurrent builds wait instead of clobbering).
- [Contributors need Rust] → `rust-toolchain.toml` makes `rustup` install the right version automatically; one
  line in `docs/DEVELOPMENT.md` requirements.

## Migration Plan

Each stage is its own PR (or a few), leaves both apps working, and has an exit criterion.

0. **Spike** (throwaway branch): a minimal `ecc-core` (lenient draft + project lookup + `format_info` for a
   two-layer draft) through UniFFI into Swift and through `uniffi-bindgen-cs` into C#; universal static
   XCFramework linked by `swift build --arch arm64 --arch x86_64`; .NET 10 test on macOS and `windows-latest`;
   ICU segmenter size. *Exit*: both bindings build and run in CI; size and generator choice recorded in this
   design (updating decision 3 or 7 only if the fallback is taken). *Done*: throwaway branch
   `DeMoZ/rust-core-spike`; results in decisions 2, 3, 4, 7, 8, 9 ("Stage-0 outcome", "spike findings"):
   option A kept, ICU fallback (LSTM-only) taken.
1. **Crate alongside**: `core/` with the full core and Rust tests; `ecc-golden`; hidden dump commands in the
   Swift and C# CLIs; fixtures and `scripts/golden.sh`; `core.yml`. Apps unchanged. *Exit*: `cargo test` green;
   golden `fixtures` identical Rust = Swift (and = C# except listed known differences); `golden.sh local`
   identical on all the author's timelines (the previous check covered 102 timelines of 69 projects), with
   the count in the PR description.
2. **Bindings and build plumbing**: `ecc-ffi`, generated Swift/C# bindings, `build-core.sh`, `Package.swift`,
   MSBuild target, CI steps, `build-app.sh`; nothing calls the core yet except smoke tests. *Exit*: `.pkg` built
   with `--pkg` is universal (`lipo -info` on both executables), signed and notarized; Windows publish contains
   `ecc_core.dll`; all existing tests green; size within budget.
3. **macOS switch**: `Sources/EnhanceCapCutCore` consumers (`Render`, `Export`, `Subtitles` pipeline and
   `Dubber`, `Automation`, `ecc`, app) use the Rust types; Swift `Draft/`, `Timeline/` and the writers stay in
   the tree, unused, for rollback. *Exit*: `swift test` green; `golden.sh local` identical; renders of three
   recent projects (incl. keyframes, compound, captions) match the previous build frame-for-frame within
   tolerance and look right next to their CapCut exports; caption + voice-over write round-trips in CapCut;
   export time within 2 %; shipped in a release.
4. **Windows switch**: `EnhanceCapCut.Render`, `.Subtitles`, `.Windows`, `.App`, `ecc` use the generated types;
   old C# core files stay for rollback. *Exit*: `dotnet test` green on macOS and Windows CI; `ui-smoke` and
   `update-smoke` pass; end-to-end export test passes; `ecc info` of the Windows CLI equals the macOS CLI on the
   fixtures (now by construction); shipped in a release.
5. **Removal**: after one release with both switches and no core regression reports, delete Swift `Draft/`,
   `Timeline/`, `Segmenter`/`DraftSubtitleWriter`/`DraftVoiceOverWriter`/`DubTimeline` and their C# twins, the
   hidden dump commands of the native CLIs, and the moved tests; update `CLAUDE.md` (drop the "keep in step"
   rules, describe `core/`), `docs/DEVELOPMENT.md`, `windows/README.md`, `docs/PLAN.md`, `openspec/config.yaml`
   context. *Exit*: no duplicated core logic left (`rg` for the old type names finds only bindings); docs
   describe one core.

**Rollback**: stages 0–2 add code only. Stages 3 and 4 are single switch PRs that can be reverted while the old
code is still in the tree; stage 5 happens only after a release proved the switch.

## Open Questions

- ~~Exact UniFFI / `uniffi-bindgen-cs` versions~~ — resolved in stage 0: `uniffi =0.32.0` with
  `uniffi-bindgen-cs` v0.12.0+v0.32.0 (decision 3).
- Whether Chinese/Japanese word breaks are worth `icu-auto` in a shared dynamic library (decision 7) — decide
  after the Thai/CJK wrapping tests of task 4.2 and the size check of task 6.7.
- Whether the MCP server's draft-only tools (list, info) later call the core directly from both CLIs — possible
  after stage 4 without changing this plan.
