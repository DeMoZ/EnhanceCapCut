# Development

Building, testing and releasing EnhanceCapCut. Conventions are in [CLAUDE.md](../CLAUDE.md); the Windows version
(C#/.NET) has its own [README](../windows/README.md).

## Requirements

- macOS 15 or later, Apple Silicon recommended (hardware HEVC/ProRes encoding).
- Xcode 16+ command line tools (Swift 6 toolchain) to build.
- Rust via [`rustup`](https://rustup.rs): the Swift package links the Rust core (`core/`); `rust-toolchain.toml` pins
  the toolchain and `rustup` installs it on first use.
- Optional: `ffmpeg` (`brew install ffmpeg`) for loudness normalization.
- The .NET 10 SDK for the Windows version (it builds and tests on macOS too).
- Rust (via `rustup`) for the shared core in `core/`; the version is pinned in `rust-toolchain.toml`.

## Build and test

```sh
scripts/build-core.sh       # the Rust core for Swift (again after changing core/)
swift build                 # debug build of everything
swift test                  # unit tests
swift run ecc info 1014     # try the renderer
dotnet test windows/EnhanceCapCut.slnx
```

`scripts/build-core.sh` builds `core/ecc-ffi` into `build/core/EccCoreFFI.xcframework` (static library, C header,
module map; `--universal` for arm64 + x86_64) and writes its generated Swift bindings to `Sources/EccCore/EccCore.swift`,
which is committed; `swift build` fails with a missing `EccCoreFFI.xcframework` until it has run once.
`EnhanceCapCutCore` re-exports the `EccCore` module, whose types its own same-named types shadow until the Swift code
moves to the core.

Layout: `Sources/EnhanceCapCutCore` (draft model, timeline, renderer, exporter, subtitles, localization),
`Sources/EnhanceCapCut` (app, Services provider, SwiftUI windows), `Sources/ecc` (CLI), `App/Info.plist` (bundle +
`NSServices`), `Localization/` (interface translations shared by both apps), `docs/` (roadmap, format notes, guide),
`windows/` (the Windows version in C#/.NET).

No CapCut resources (shaders, Lua, LUTs, fonts) and no user media or project files are part of this repo.

Voice cloning runs in ONNX Runtime, which isn't a build dependency: `OnnxRuntime.swift` loads Microsoft's
`libonnxruntime.dylib` at run time. `scripts/fetch-onnxruntime.sh` puts it into `build/onnxruntime` (from the NuGet
package Microsoft.ML.OnnxRuntime, the version the Windows version uses, checked by SHA-256); `swift run ecc clone`
finds it there, and `ECC_ONNXRUNTIME=<path>` points to another copy. Microsoft builds it for Apple Silicon only, so
on Intel Macs voice cloning is unavailable.

## Rust core and golden parity

`core/` is the shared Rust core the apps are moving to ([openspec/changes/rust-core](../openspec/changes/rust-core)).
Until the switch it sits next to the Swift and C# code; both apps already link it (`scripts/build-core.sh` for
Swift, the `BuildRustCore` target of `EnhanceCapCut.Draft` for .NET) but nothing calls it yet. It needs a Rust
toolchain (`rust-toolchain.toml` pins it; `rustup` installs it on first use).

The apps see it only through `core/ecc-ffi` and its generated bindings, both committed: `Sources/EccCore/EccCore.swift`
(rewritten by `scripts/build-core.sh`) and `windows/src/EnhanceCapCut.Draft/Generated/ecc_core.cs` (rewritten by
`scripts/csharp-bindings.sh`, which needs the pinned C# generator:
`cargo install uniffi-bindgen-cs --git https://github.com/NordSecurity/uniffi-bindgen-cs --tag v0.12.0+v0.32.0 --locked`).
After changing the exported API, run both and commit the bindings; CI regenerates them and fails on a difference.

Every worktree builds the core into its own `core/target` (a minute or two from scratch). To share one build cache,
set `CARGO_TARGET_DIR` (for example `export CARGO_TARGET_DIR=~/.cache/ecc-target`); the scripts and the .NET build
follow it.

```sh
cd core && cargo test                # the core's tests and the committed golden outputs
scripts/golden.sh fixtures           # Swift, C# and Rust on every fixture project
scripts/golden.sh local              # the same on every timeline of your CapCut library
```

While the core is ported, the Swift and C# CLIs have two hidden commands (not in help) that print canonical dumps the
runner compares with the core's `ecc-golden`:

- `ecc dump-timeline [project] [--timeline name|number]` — the built timeline: report, every layer's static data
  (content, timing, time map, crop, clip, keyframes, masks, effects, transitions, animations, text style), audio clips
  (with their time map sampled every 50 ms, at most 200 samples), filter-track filters, and every on-screen layer's
  state (transform, text animation, effects, masks, transition, clock hands) at 0, each clip edge ±1 µs, each keyframe
  ±1 µs, each transition's start, cut and last µs, and 16 even steps. One JSON object per line, fields in a fixed
  order, doubles as `%.6f`, paths in the project as `<project>/…`.
- `ecc dump-writers [project] [--timeline name|number]` — the caption writer (twice) and the voice-over writer (each
  original-audio mode, then `withoutVoiceOver`) run on the draft with fixed tracks; new UUIDs become `<id1>`,
  `<id2>`… in order of appearance.

The three implementations are `Sources/ecc/Golden.swift`, `windows/src/ecc/Golden.cs` and
`core/ecc-golden/src/dump.rs`; a change to the format goes into all three. Fixtures are in `core/fixtures/<name>/`
(`draft.fixture.json`, optional `timelines.fixture.json` and `media.txt`, see `core/ecc-golden/src/fixtures.rs`) with
the core's outputs in `expected/`: `UPDATE_GOLDEN=1 cargo test -p ecc-golden` rewrites them. Differences the runner
accepts are listed, with reasons, in `core/fixtures/known-differences.md`. Set `RUST_BACKTRACE=1` to see where a
core panic came from.

## Build the app

```sh
scripts/build-app.sh --install
```

This builds `build/EnhanceCapCut.app`, copies it to `~/Applications`, registers it with Launch Services
and refreshes the Services menu. Without `--install` it only builds.

The app icon is drawn by `swift scripts/make-icon.swift`, which writes `App/AppIcon.icns` and the Windows
`EnhanceCapCut.ico`; run it again after changing the drawing and commit both files (and `docs/images/icon.png`:
`sips -s format png -Z 256 App/AppIcon.icns --out docs/images/icon.png`).

## README screenshots

The Mac screenshots in `docs/images` are the app's own windows, captured by window id (`screencapture -l<id>`) from a
build started with demo settings in the argument domain, so nothing saved changes:
`.build/debug/EnhanceCapCut -language en -project <name> -subtitles YES -guideShown NO` (and `-language ru`). The
Windows ones come from CI's `windows-ui` artifact (`windows/scripts/ui-smoke.ps1`).

## Release installer

```sh
scripts/build-app.sh --pkg
```

Builds a universal (Apple Silicon + Intel) app and `build/EnhanceCapCut-<version>.pkg`, which installs
into `/Applications` and refreshes the Services menu. Every build ships `libonnxruntime.dylib` (Apple Silicon) in
`Contents/Frameworks` and its licence files in `Contents/Resources/onnxruntime`. The Rust core is built first
(`scripts/build-core.sh`, `--universal` with `--pkg`; `rustup` adds the missing Apple target) and linked statically
into both executables, so nothing else is copied or signed for it.

A dry run of the release workflow (both installers built, nothing signed or published, artifacts kept three days,
sizes in the job summary) runs on pull requests that change the packaging, and by hand with
`gh workflow run release.yml --ref <branch>`.

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
