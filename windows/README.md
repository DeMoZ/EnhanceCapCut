# EnhanceCapCut for Windows

The Windows version of EnhanceCapCut, built on Windows' own stack: C# / .NET 10, Media Foundation,
Direct3D 11 / Direct2D, DirectWrite and WinUI 3. It shares no code with the macOS app (Swift); the draft
and timeline logic is a port of it and is checked against it (see "Status").

## Layout

- `src/EnhanceCapCut.Draft` — CapCut draft model (lenient JSON reading), project and timeline lookup
  (`Timelines/project.json`), `TimelineBuilder` (draft → visual layers, audio clips, report of unsupported
  features), `TimeMap` (speed and speed curves), export settings, voice filter presets. Platform-neutral.
- `src/EnhanceCapCut.Render` — platform-neutral rendering logic: CapCut's layout as Direct2D matrices, colours,
  text calibration, frame timing, the audio mix (resampling, WSOLA time-stretch, fades), the peak limiter and
  EBU R128 loudness.
- `src/EnhanceCapCut.Windows` — the Windows renderer through Vortice: Media Foundation reading and writing
  (`MfVideoReader`, `MfAudioDecoder`, `MfVideoWriter`), Direct2D on Direct3D 11 (`Graphics`, `Compositor`),
  DirectWrite text (`TextImageRenderer`), WIC photos (`ImageLoader`), and the `Exporter` pipeline.
- `src/ecc` — command-line tool: `ecc list`, `ecc info [project] [--timeline name|number]`, and on Windows
  `ecc render` (same options as the macOS CLI; HEVC 10-bit needs an encoder that supports it).
- `tests/` — unit tests for the draft and render layers (run anywhere) and end-to-end export tests (Windows only;
  elsewhere they return without checking).

## Build

Requires the .NET 10 SDK. Everything here builds and runs on macOS too, which is how it's checked against
the Swift version.

```sh
dotnet test windows/EnhanceCapCut.slnx
dotnet run --project windows/src/ecc -f net10.0 -- info "My Project"
```

On Windows:

```sh
dotnet run --project windows/src/ecc -f net10.0-windows -c Release -- render "My Project" --from 10 --duration 5 -o x.mp4
```

## Status

Stages 1–2 of the roadmap in [docs/PLAN.md](../docs/PLAN.md#windows-version): the draft/timeline layer and a
first version of the renderer (`ecc render`), checked by CI's end-to-end test but not yet on real projects on
Windows. The WinUI 3 window comes next. Voice effects aren't applied on Windows yet (the export warns).

CapCut for Windows is assumed to write the same draft format as the Mac app and to keep projects in
`%LOCALAPPDATA%\CapCut\User Data\Projects\com.lveditor.draft`.
