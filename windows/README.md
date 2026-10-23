# EnhanceCapCut for Windows

The Windows version of EnhanceCapCut, built on Windows' own stack: C# / .NET 10, Media Foundation,
Direct3D 11 / Direct2D, DirectWrite and WinUI 3. It shares no code with the macOS app (Swift); the draft
and timeline logic is a port of it and is checked against it (see "Status").

## Layout

- `src/EnhanceCapCut.Draft` — CapCut draft model (lenient JSON reading), project and timeline lookup
  (`Timelines/project.json`), `TimelineBuilder` (draft → visual layers, audio clips, report of unsupported
  features), `TimeMap` (speed and speed curves), export settings, voice filter presets. Platform-neutral.
- `src/ecc` — command-line tool: `ecc list`, `ecc info [project] [--timeline name|number]`.
- `tests/EnhanceCapCut.Draft.Tests` — unit tests (ported from the macOS ones).

## Build

Requires the .NET 10 SDK. Everything here builds and runs on macOS too, which is how it's checked against
the Swift version.

```sh
dotnet test windows/EnhanceCapCut.slnx
dotnet run --project windows/src/ecc -- info "My Project"
```

## Status

Stage 1 of the roadmap in [docs/PLAN.md](../docs/PLAN.md#windows-version): the draft/timeline layer.
The renderer (Media Foundation, Direct3D 11, DirectWrite) and the WinUI 3 window come next.

CapCut for Windows is assumed to write the same draft format as the Mac app and to keep projects in
`%LOCALAPPDATA%\CapCut\User Data\Projects\com.lveditor.draft`.
