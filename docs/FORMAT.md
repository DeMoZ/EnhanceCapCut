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

## Compound clips

`materials.drafts[]` (`type` `combination`) embed a complete nested draft in `draft`. A video segment
references it via `extra_material_refs`. Nested drafts can reference combinations stored in the outer draft.

## Usage in the author's 69 projects

~655 texts (626 captions), speed objects on all 1564 segments, 381 audios, 195 vocal_beautify,
151 loudness, 135 effects (makeup/figure/"Sparkly Skin"/"ART EN 3D" text effects/"Cherry Cola" filter),
46 stickers (44 "Clock Time"), 26 transitions (18 "Flashback"), 19 hsl, 16 audio_effects,
11 video_effects, 10 masks, 52 compound clips.
