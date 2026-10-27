# Development

Building, testing and releasing EnhanceCapCut. Conventions are in [CLAUDE.md](../CLAUDE.md); the Windows version
(C#/.NET) has its own [README](../windows/README.md).

## Requirements

- macOS 15 or later, Apple Silicon recommended (hardware HEVC/ProRes encoding).
- Xcode 16+ command line tools (Swift 6 toolchain) to build.
- Optional: `ffmpeg` (`brew install ffmpeg`) for loudness normalization.
- The .NET 10 SDK for the Windows version (it builds and tests on macOS too).

## Build and test

```sh
swift build                 # debug build of everything
swift test                  # unit tests
swift run ecc info 1014     # try the renderer
dotnet test windows/EnhanceCapCut.slnx
```

Layout: `Sources/EnhanceCapCutCore` (draft model, timeline, renderer, exporter, subtitles, localization),
`Sources/EnhanceCapCut` (app, Services provider, SwiftUI windows), `Sources/ecc` (CLI), `App/Info.plist` (bundle +
`NSServices`), `Localization/` (interface translations shared by both apps), `docs/` (roadmap, format notes, guide),
`windows/` (the Windows version in C#/.NET).

No CapCut resources (shaders, Lua, LUTs, fonts) and no user media or project files are part of this repo.

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
