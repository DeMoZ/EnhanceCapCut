# CapCut draft format notes

Observed on CapCut for macOS 9.5 (`draft_info.json` version 360000, new_version 187.0.0). These notes
describe what the renderer relies on; everything is read leniently because fields change between versions.

## Locations

- Projects: `~/Movies/CapCut/User Data/Projects/com.lveditor.draft/<name>/draft_info.json` (plain JSON).
- A project that is open in CapCut has a `.locked` file in its folder (stale ones can survive a crash).
- Effect cache: `~/Library/Containers/com.lemon.lvoverseas/Data/Movies/CapCut/User Data/Cache/effect/<id>/<hash>/`
  — "AmazingFeature" packages (GLSL shaders, scene, materials, Lua). Never copy these into the repo.
- Paths inside a draft can start with `##_draftpath_placeholder_<UUID>_##`, meaning the project folder.

## Time

All times are microseconds. A segment has `target_timerange` (timeline) and `source_timerange` (media).
`source.duration ≈ target.duration × speed`.

## Tracks and order

- `tracks[]` with `type` = `video`, `audio`, `text`, `sticker`, `effect`, `filter`.
- The first `video` track is the main track; its clips get the canvas background.
- Paint order is `segment.render_index` (main track 0, overlays ~12–18, texts/stickers 14000+,
  effects 11000+, filters 10000+), then track order.
- `track.attribute` is a bit set: bit 0 = muted. Bit 1 is assumed to be hidden.
- `config.video_mute` mutes the main track.

## Segment

- `material_id` → the main material (`materials.videos`, `audios`, `texts`, …).
- `extra_material_refs` → per-clip materials: `speeds`, `canvases`, `audio_fades`, `loudnesses`,
  `transitions`, `material_animations`, `common_mask`, `effects`, `drafts` (compound clip), ….
- `clip`: `scale {x,y}`, `rotation` (degrees, clockwise on screen), `transform {x,y}` (center offset in
  half-canvas units, +y up), `flip`, `alpha`. `uniform_scale.on` ties X/Y scale.
- `common_keyframes[]`: `property_type` (`KFTypePositionX/Y`, `KFTypeScaleX/Y`, `KFTypeRotation`,
  `KFTypeAlpha`, `KFTypeVolume`, mask properties) with `keyframe_list[{time_offset, values[0]}]`;
  offsets are relative to the segment start, values use the same units as `clip`.
- `volume` is linear gain and can exceed 1. `is_tone_modify` = speed changes pitch.

## Layout

A visual is fitted inside the canvas (aspect fit) after cropping (`materials.videos[].crop`, normalized
corners, origin top-left), then scaled by `clip.scale`, rotated, and moved by `clip.transform`.

## Speed

`materials.speeds[]`: `mode` 0 = constant `speed`; 1 = curve, `curve_speed.speed_points[{x, y}]`
with x = normalized timeline position, y = speed multiplier. We integrate the curve and normalize it
so the whole `source_timerange` is consumed.

## Text

`materials.texts[]` (`type` `text` or `subtitle` = auto-captions). `content` is a JSON string:
`{"text": "...", "styles": [{"range": [a, b], "size": 8, "font": {"path": ...},
"fill": {"content": {"solid": {"color": [r, g, b]}}}, "strokes": [{"width": 0.06, ...}]}]}`.
Ranges are UTF-16 offsets.

Calibrated against CapCut exports: font size in px = `size × canvasWidth / 172` for the default
`SystemFont/en.ttf`, `size × canvasWidth / 155` for other fonts; line pitch ≈ one font size
(+ `line_spacing`); wrapping at `line_max_width × canvasWidth`; the text box is centered on
`clip.transform`. Outline width ≈ `0.75 × stroke.width × fontSize`.

CapCut's default font `SystemFont/en.ttf` is Latin-only. Its fallbacks live next to it
(`NotoSans-Regular.ttf`, `NotoSansThai-Regular.ttf`, …). Measured: Cyrillic is drawn at the nominal size;
Thai is scaled so its ascent+descent equals the primary font's (≈0.66×).

Captions (`type: subtitle`) are burned in only when CapCut's export option is on — the author exports
both variants — hence our "Burn in captions" toggle.

## Colour

Calibrated against the author's CapCut exports (HEVC Main10, BT.709 tagged):

- SDR clips: CapCut keeps code values (only the YCbCr matrix is applied). Core Image reads BT.709-tagged
  buffers with an sRGB-like curve, so we compose and encode in sRGB space (BT.709 primaries); encoding with
  `CGColorSpace.itur_709` (camera OETF) would brighten everything.
- HDR clips (iPhone HLG): CapCut's SDR rendition is flatter than a reference-white tone map — lifted
  shadows, compressed highlights. After `CIToneMapHeadroom` we apply a measured curve (`HDRLook` in
  `FrameSource.swift`); luma quantiles then match within ~1.5/255.
- CapCut ignores `segment.hdr_settings` differences we've seen (`mode` 4, 1000 nits) — not modelled.

## Audio

- The master bus is limited: CapCut exports peak at 0 dBFS even when clips sum far above it. We use a
  look-ahead limiter at −0.2 dBFS. Integrated loudness of our mix matches CapCut's exports (−28.1 vs
  −28.1 LUFS on "Durian1"). Exception: the same media playing in two places at once (a clip and a compound
  clip containing it) sums coherently for us, ~3 dB louder than CapCut.

- `materials.audios[]` types: `music`, `extract_music`, `record`, `video_original_sound`, `text_to_audio`.
- Video clips carry their own sound unless `videos[].has_sound_separated` (then an `audio` track holds it).
- `audio_fades[]`: `fade_in_duration`, `fade_out_duration`.
- `loudnesses[]`: per-clip "Normalize loudness": `enable`, `target_loudness`, `loudness_param.avg_loudness`.
- Audio detached from a compound clip is an audio segment referencing the `drafts` material.

## Voice processing

Per-clip toggles live in materials referenced from `extra_material_refs` (on the video segment for
camera sound, on the audio segment otherwise; one of each per segment, usually present even when off):

- `vocal_separations[]` — "Isolate voice": `choice` 0 = none, **1 = "Remove vocal"** (keep background),
  **2 = "Keep vocal"** (confirmed by running Apple's sound classifier on CapCut's outputs: choice 1 drops
  speech 0.80 → 0.04, choice 2 drops music 0.50 → 0.11). Also `time_range` (source range processed),
  `production_path` (`Resources/audioAlg/<md5>_<start>_<duration>.wav`), `final_algorithm`
  (`vocal_separate`), `enter_from`, `removed_sounds`. Can't be applied to compound clips, CapCut music,
  reversed clips or clips over 10 minutes. On audio detached from a compound clip it applies to the
  whole nested mix.
- `realtime_denoises[]` — "Reduce noise": `is_denoise`, `denoise_rate` (always 0.85), `denoise_mode` 1,
  `path` → `CapCut.app/Contents/Resources/audiosami/unet_denoise_44k_music_model_v1.0.model`
  (`sami_name` `denoise_v2`). Applied while playing/exporting; nothing is cached. In exports it removes
  wind and water noise almost completely (The edge: wind 0.66 → 0.06), i.e. it behaves like voice
  isolation, not like a stationary-noise gate.
- `vocal_beautifys[]` — "Enhance voice": `enable`, `ambient_sound_level` (75 almost everywhere, 100 on
  two clips), `time_range` (always the whole source file), `production_path`
  (`Resources/audioAlg/<md5>_0_<duration>.wav`, obfuscated), `voice_change_mode` (null),
  `is_from_hd_sounds` (false). Used on voiceovers and on camera audio, often together with
  "Reduce noise".

`Resources/audioAlg/*.wav` (also `_vocal`, `_background`, `_human` and `_upload` variants) are 44.1 kHz
16-bit WAVs, but all except `_upload` are **obfuscated**: windows of the file (header included) are
XORed with a single byte, at file-dependent sizes and periods, with keys that can differ between the
header and the body. We don't decode them; `VoiceProcessor` processes the source audio itself.

Matching CapCut's exports: "Keep vocal" = Apple's voice isolation (`AUSoundIsolation`, high-quality voice)
at 100 %; "Reduce noise" = the same at 85 % wet (`denoise_rate`), 15 % original; "Remove vocal" = source
minus that voice estimate as a spectral Wiener mask. `AUSoundIsolation` reports zero latency but delays by
6360 frames at 48 kHz (4159 at 32 kHz); the delay is measured per clip by cross-correlation.

"Enhance voice", measured on clips where it is the only audible sound (CockScreams voiceover, KoSamet
IMG_2944 camera audio at 128–135 s, EatingSlugsLong 0–4.4 s with "Reduce noise" too), by fitting the
export as `g_v·voice + g_a·ambience` per ⅓-octave band (voice = Apple's isolation, ambience = source − voice):

- Voiced parts come out +2.2…3.0 dB louder; coherence with the source is 0.99 from 100 Hz to 6 kHz, so
  there's no compression or de-essing to speak of, mostly a linear EQ.
- Low cut: −3 dB around 80 Hz, −8 dB at 63 Hz, −11 dB at 50 Hz, without phase shift (a minimum-phase
  high-pass drops the waveform correlation with the export from 0.98 to 0.84; forward–backward doesn't).
- Air boost: +2 dB at 8 kHz, +5 at 10 kHz, +11 at 12.8 kHz, +7 at 16 kHz relative to the speech band.
- Ambience at level 75: 0.26–0.37 of the voice gain (non-speech frames end up 8–15 dB below the
  source while speech is +2.8 dB). With "Reduce noise" on as well the ambience lands at −12.5 dB, close
  to the denoiser's 15 % × the enhancer's gain, i.e. the two don't stack.
- No export with level 100 exists in the author's library; we assume 100 keeps the ambience whole.

## Compound clips

`materials.drafts[]` (`type` `combination`) embed a complete nested draft in `draft`. A video segment
references it via `extra_material_refs`. Nested drafts can reference combinations stored in the outer draft.

## Usage in the author's 69 projects

~655 texts (626 captions), speed objects on all 1564 segments, 381 audios, 195 vocal_beautify,
58 vocal separations (35 keep vocal, 23 remove vocal), 52 "Reduce noise",
151 loudness, 135 effects (makeup/figure/"Sparkly Skin"/"ART EN 3D" text effects/"Cherry Cola" filter),
46 stickers (44 "Clock Time"), 26 transitions (18 "Flashback"), 19 hsl, 16 audio_effects,
11 video_effects, 10 masks, 52 compound clips.
