# EnhanceCapCut roadmap

EnhanceCapCut renders the CapCut project that is open in the editor to a video file with our own
renderer and our own export parameters. CapCut has no plugin API and we don't patch the app; the
entry point is a macOS Services menu item, and rendering reads the project's `draft_info.json`.

Effects that CapCut implements with its own resource packages are re-implemented in our code.
CapCut's shaders, Lua scripts and other resource files are never copied into this repository;
reading the user's own cached resources (e.g. LUT images) at render time is fine.

Usage counts from the author's 69 projects drive the order of the later milestones
(see [FORMAT.md](FORMAT.md)).

## Milestone 1 — own export of the basic edit (done)

- [x] SwiftPM package: `EnhanceCapCutCore` library, `EnhanceCapCut` app, `ecc` CLI, app bundle script.
- [x] Services menu entry "Export with EnhanceCapCut" (no input, any app, bindable to a hotkey).
- [x] Open-project detection (`.locked` file) with a project picker fallback.
- [x] Tolerant draft parser: tracks, segments, clip transforms, keyframes, crop, speeds (incl. curves),
      volumes, fades, canvases, texts/subtitles, compound clips, per-clip loudness normalization.
- [x] Renderer: AVFoundation decoding, Core Image (Metal) compositing, HDR→SDR tone mapping,
      cuts, overlays in CapCut's render order, constant and curve speed, transforms and keyframes,
      canvas colour/blur/image, photos, compound clips (nested drafts), text and captions with
      fonts, fill, outline and background.
- [x] Audio: mix of all audio tracks and clip sound, volume (incl. >100%), fades, speed, optional
      EBU R128 loudness normalization (ffmpeg `loudnorm`, two-pass, linear).
- [x] Export window: aspect, resolution, frame rate, codec (H.264 / HEVC 8/10-bit / ProRes 422/HQ),
      constant quality or bitrate, captions burn-in, loudness target, output folder and file name
      template; remembers the last choices; lists project features that won't be rendered.
- [x] Speed polish: reversed clips (CapCut's reversed copy), per-clip pitch mode in one mix, speed
      curves kept in partial exports and in compound-clip audio. Verified frame-exact on 3x segments.
- [x] Verified against real projects and the author's CapCut exports: text size, wrapping, positions and
      fallback fonts; SDR colour (identical), HDR→SDR look (measured curve); loudness and peak limiting.

## Voice processing (done)

- [x] "Isolate voice" (`vocal_separations.choice`: 1 = remove vocal, 2 = keep vocal) and "Reduce noise"
      (`realtime_denoises`), parsed per clip and applied before the mix (`VoiceProcessor`).
- [x] Keep vocal: Apple's on-device voice isolation (`AUSoundIsolation`); remove vocal: source minus the
      voice estimate as a spectral Wiener mask; reduce noise: 85 % voice isolation + 15 % original, which
      matches CapCut's exports (The edge: corr 0.99, same loudness; SaiKeo Beach: corr 0.99).
- [x] CapCut's cached separation results are not used: they are obfuscated (see FORMAT.md).
- [x] "Enhance voice" (`vocal_beautifys`, milestone 7 item): ambience turned down by voice isolation
      (to 0.3 at `ambient_sound_level` 75), then +2.8 dB, a zero-phase low cut at ~70 Hz and a +13 dB
      air boost at 13.7 kHz (`VoiceEnhancer`). Against CapCut's exports: KoSamet camera clip frame-level
      error 2.9 → 0.8 dB, spectrum error 2.9 → 1.3 dB, waveform corr 0.982 → 0.989; EatingSlugs (with
      "Reduce noise") level error −3.2 → −0.9 dB; ATV/CockScreams voiceovers within 0–1.8 dB.
- [x] `ecc voice` auditions the processing on a single file.

## Milestone 2 — LUT filters

- Read filter packages from CapCut's effect cache (`~/Library/Containers/com.lemon.lvoverseas/Data/Movies/CapCut/User Data/Cache/effect/<id>/<hash>/`)
  at render time, locate the LUT image, apply it with `CIColorCube` with the filter's intensity.
- Filter tracks (`effect`/`filter` track types) and per-clip filters; the "Cherry Cola" filter first.
- Also: HSL adjustments (19 uses) and basic colour adjustments where they map to Core Image.

## Milestone 3 — "Flashback" transition and "Clock Time" stickers

- Transitions: model the overlap of adjacent main-track clips (`is_overlap`, `duration`), render
  "Flashback" (18 of 26 transitions) as our own Metal/Core Image kernel; then a generic cross-fade
  fallback for other transitions.
- Stickers: re-implement "Clock Time" (44 of 46 stickers) natively (clock face drawn with Core Graphics
  from the segment's time), positioned with the sticker clip transform.

## Milestone 4 — cached AI results

- [x] Reverse playback via `reverse_path` (CapCut's pre-rendered reversed media).
- `Resources/audioAlg` audio results (vocal separation, voice enhancement) turned out to be obfuscated —
  see FORMAT.md; voice processing is done by us instead (see "Voice processing", milestone 7).
- Matting / background removal masks (`matting.path`, `mask_video_path`) as alpha.

## Milestone 5 — remaining effects by usage frequency

- Video effects (`video_effects`, 11) and body effects (`effects/figure`, 55), most-used first.
- Text effects / text templates ("ART EN 3D …"), text intro/outro animations (`material_animations`).
- Masks (`common_mask`: rectangle, circle, brush…) with feather and keyframes.
- Video clip animations, manual deformation.

## Milestone 6 — beauty / makeup approximations

- Approximate CapCut's beauty and makeup (`makeup_root`, "Sparkly Skin", smoothing) using Apple Vision
  face landmarks and Core Image filters.

## Milestone 7 — audio effects

- [x] Voice filters (`audio_effects`: "Booming", "Live House", "Old Hollywood", 16 uses): the cached
  `mdsp_preset.json` graph runs on our own DSP (`VoiceFilterGraph`), calibrated on ВазелиновыйПень.
  Open: only the processors those three use are implemented (other filters stay in the report);
  strength below 1 follows the preset curves but is unverified (all uses are at 1); filters run on the
  source timeline, before speed changes; Live House is ~5 dB louder than CapCut's export on its only
  1.2 s clip, part of which is the voice isolation differing there.
- [x] "Enhance voice" (`vocal_beautifys`, 195 uses): approximated, see "Voice processing". Open: the
  ambience share for levels other than 75 is extrapolated (no export to check it against); non-speech
  vocal sounds (imitations, laughter) are partly treated as ambience by Apple's isolation, while CapCut
  keeps them (CockScreams −1.8 dB).
- Better "Remove vocal" for songs: a music source-separation model (e.g. Demucs as Core ML) that the
  user downloads at runtime; Apple's voice isolation only partly removes sung vocals.

## Known gaps in milestone 1

- Text: no text effects/animations; vertical typesetting is rendered horizontally; CapCut breaks
  Cyrillic/Thai lines mid-word, we break at word boundaries; Cyrillic fallback weight differs slightly.
- Keyframes: linear interpolation only; no bezier easing.
- Speed curves: video follows the curve exactly; audio follows it in 50 ms steps.
- Reverse: needs CapCut's reversed copy in the project folder; without it the clip plays forward.
- Track bit `attribute & 2` is treated as "hidden"; tracks with other flags (e.g. locked) render normally.
- Voice: "Remove vocal" removes speech well but only part of a sung vocal; processing time is roughly
  1/10 of the processed clips' duration and isn't reflected in the progress bar.
- Audio: identical media playing simultaneously (clip + compound clip containing it) is ~3 dB louder
  than in CapCut. Per-clip "Normalize loudness" is applied as a static gain from CapCut's stored measurement.
