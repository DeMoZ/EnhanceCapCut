# CapCut draft format notes

Observed on CapCut for macOS 9.5 (`draft_info.json` version 360000, new_version 187.0.0) and CapCut for
Windows (`draft_content.json` version 360000, new_version 189.0.0). These notes describe what the renderer
relies on; everything is read leniently because fields change between versions.

## Locations

- Projects on the Mac: `~/Movies/CapCut/User Data/Projects/com.lveditor.draft/<name>/draft_info.json` (plain JSON).
- Projects on Windows: `%LOCALAPPDATA%\CapCut\User Data\Projects\com.lveditor.draft\<name>\draft_content.json`
  (plain JSON, the same format under another name). Everywhere below, "the draft" is whichever of the two files
  the folder has (`DraftFile`).
- A project that is open in CapCut has a `.locked` file in its folder (stale ones can survive a crash), on both
  systems.
- Effect cache: `~/Library/Containers/com.lemon.lvoverseas/Data/Movies/CapCut/User Data/Cache/effect/<id>/<hash>/`
  — "AmazingFeature" packages (GLSL shaders, scene, materials, Lua). Never copy these into the repo.
- Paths inside a draft can start with `##_draftpath_placeholder_<UUID>_##`, meaning the project folder.

## Timelines

A project can hold several timelines. `Timelines/project.json` lists them: `main_timeline_id`,
`timelines[{id, name, is_marked_delete}]`. Each has its own draft in `Timelines/<id>/`
(same format; media paths still resolve against the project folder). The root draft is a copy
of the main timeline. `timeline_layout.json` describes CapCut's dock of open timelines (not used).
Projects without `project.json` have a single timeline: the root draft.

Every timeline draft's root `id` is its timeline id; segment and material ids are not shared between the timelines
of the author's projects. A voice-over run adds a timeline of its own (`DubTimeline`): a new UUID, an entry
`{create_time, id, is_marked_delete: false, name, update_time}` (µs) appended to `project.json`'s `timelines`, a
folder `Timelines/<id>/` with the transformed draft (root `id` replaced) and an identical `template-2.tmp`, plus the
source folder's `attachment_editing.json`, `attachment_pc_common.json` and `common_attachment/` (plain JSON);
`draft.extra` (binary, likely encrypted) is not copied. The id and name are also appended to the first dock's
`timelineIds` / `timelineNames` in `timeline_layout.json`, so it opens as a tab. Not yet checked: whether CapCut
wants `draft.extra` or fresh segment/material ids in a copied timeline.

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
so the whole `source_timerange` is consumed. Speed curves of a compound clip apply to its nested
timeline, so nested audio gets the composed map (parent curve ∘ inner speed).

`segment.is_tone_modify` = the clip's "Pitch" toggle: speed changes pitch (tape-style). Otherwise the
pitch is preserved. Both kinds can play at once, so the mix picks the algorithm per clip.

Reverse: `segment.reverse` = true and the video material's `reverse_path` points to a fully reversed
copy of the whole media that CapCut renders into the project folder (`##_draftpath_placeholder_…##/<md5>.mp4`,
with audio). The segment's `source_timerange` is in that copy's time base (verified against an export).

## Text

`materials.texts[]` (`type` `text` or `subtitle` = auto-captions). `content` is a JSON string:
`{"text": "...", "styles": [{"range": [a, b], "size": 8, "font": {"path": ...},
"fill": {"content": {"solid": {"color": [r, g, b]}}}, "strokes": [{"width": 0.06, ...}]}]}`.
Ranges count Unicode code points, not UTF-16 units: an emoji such as `😳` is one unit, so a style covering
`"руку 😳"` is `[0, 6]` (UTF-16 would be 7). Renderers convert them to UTF-16 offsets before styling.

Calibrated against CapCut exports: font size in px = `size × canvasWidth / 172` for the default
`SystemFont/en.ttf`, `size × canvasWidth / 155` for other fonts; line pitch ≈ one font size
(+ `line_spacing`); wrapping at `line_max_width × canvasWidth`; the text box is centered on
`clip.transform`. Outline width ≈ `0.75 × stroke.width × fontSize`.

CapCut's default font `SystemFont/en.ttf` is Latin-only. Its fallbacks live next to it
(`NotoSans-Regular.ttf`, `NotoSansThai-Regular.ttf`, …). Measured: Cyrillic is drawn at the nominal size;
Thai is scaled so its ascent+descent equals the primary font's (≈0.66×).

### Text animations

A text (or sticker) segment's `extra_material_refs` holds one `materials.material_animations[]` entry (`type`
`sticker_animation` for both) whose `animations[]` has at most one of each `type`: `in`, `out`, `loop`. Each has
`duration` (µs; for a loop, one cycle), `start` (always 0: an outro plays in the last `duration` of the clip),
`name` (in the language CapCut used when it was applied: "Fade In", "渐隐", …), `resource_id` / `id` (the preset in
CapCut's library), `category_id` (`ruchang` in, `chuchang` out, `xunhuan` loop, `…_fav` from Favorites), `path` (the
cached preset) and `anim_adjust_params`. Seen in third-party drafts (the author's projects and CapCut's cache have
none). An intro and outro longer than the clip are squeezed into it.

We recognize presets by name and reproduce fade, typewriter (characters appear in place, the layout stays that of the
whole text), slide, zoom in/out, pop, and blink/pulse loops; the rest are reported by name. The motion is our own,
not yet calibrated against a CapCut export (#68).

Captions (`type: subtitle`) are burned in only when CapCut's export option is on — the author exports
both variants — hence our "Burn in captions" toggle.

### Text effects and bubbles

A text effect ("word art", panel `panel-text-flower`) is a `materials.effects` entry of type `text_effect`
(`name`, `effect_id`, `path` to the package in CapCut's effect cache), referenced (often twice) from the text
segment's `extra_material_refs`; the content style that uses it has `"effectStyle": {"path", "id"}` with the same
id. CapCut applies the effect only while the style has `effectStyle`: after the style is reset the segment keeps the
reference and CapCut exports plain text (22 "ART EN 3D WHITE AND BLUE" texts in the author's "Мокрая прогулка"). A
style can carry `effectStyle` without the material ("word art").

Bubbles are `effects` of type `text_shape` (`category_id` `bubble`, `sub_type` `manual_stretch`); one with an empty
`name` and `path` is CapCut's "none" and draws nothing. The packages (shaders/textures for effects, the bubble
image) are downloaded on use and later evicted: those of the author's projects were no longer in the cache.

What we render: "… Glow" effects as a halo in the effect name's colour (or the text's), blur ≈ 0.12 × font size
(`TextEffectLook`), opaque, drawn three times under the outline. Other effects and named bubbles are reported by name.

### Caption tracks (what EnhanceCapCut writes)

CapCut's auto-captions live on `text` tracks with `flag` 1 (plain text tracks have 0). Each caption is a
segment (`source_timerange` null, `target_timerange` frame-aligned, `render_index` 14000+ and unique,
`track_render_index` = the track's index in `tracks`, `track_attribute` = the track's `attribute`) whose
`extra_material_refs` holds one `material_animations` entry (`type` `sticker_animation`, empty `animations`),
and a `materials.texts` entry with:

- `type` `subtitle`, `language` like `ru-RU`, `group_id` `<language>_<ms timestamp>` shared by one recognition
  run (CapCut also uses `Auto_<ms>`), `recognize_text` = the plain text;
- `words`: `start_time` / `end_time` in ms from the segment start and `text`; recognized Russian has real
  timings with `" "` tokens between words, translations spread the words evenly without spaces;
- recognized captions: `sub_type` 0, `add_type` 1, `recognize_task_id` and `relevance_segment` (ids of the
  source clips); translated ones (bilingual captions): `sub_type` 5, `add_type` 0, both empty;
- `font_size` mirrors `content`'s style `size`; the author's captions are size 8–18, `transform.y` −0.5…−0.85
  (CapCut's preset: 8, near −0.65), `line_max_width` 0.82, `border_width` 0.08 with `border_color` "".

`config.subtitle_taskinfo` lists CapCut's recognition tasks; generated captions don't need an entry.
EnhanceCapCut names its tracks `EnhanceCapCut subtitles · <code>` (`is_default_name` false) so a later run can
replace them, and writes the same bytes to every copy of the timeline's draft (the root draft of
the main timeline and `template-2.tmp` next to each draft, which CapCut keeps identical); the draft's `.bak`
(`draft_info.json.bak`, `draft_content.json.bak`) is CapCut's previous save and is left alone.

### Voice-over tracks (what EnhanceCapCut writes)

A voice-over language is an `audio` track (`flag` 0, `attribute` 0 audible / 1 muted) named
`EnhanceCapCut voice-over · <code>`, with one segment per spoken line, laid out like CapCut's own voiceover
clips: `materials.audios[]` entry of `type` `record` (one per segment, all pointing at the language's single
WAV that is aligned to the timeline, `duration` = the file's length), `source_timerange` = `target_timerange`
(the line's place in the file and on the timeline), `clip` and `uniform_scale` null, `render_index` 0, and
`extra_material_refs` to fresh `speeds`, `placeholder_infos`, `beats`, `sound_channel_mappings` and
`vocal_separations` entries (CapCut's voiceovers also carry `audio_fades` / `vocal_beautifys`, not needed).
The WAVs (48 kHz mono 16-bit) go to `<project>/EnhanceCapCut/voiceover <time>/<code>.wav`, written with the
draft's `##_draftpath_placeholder_…##` prefix when the draft has one, so they move with the project.

"Lower" / "Mute all" original audio: a `KFTypeVolume` group in `common_keyframes` of every other audible
`video`/`audio` segment that overlaps the speech, in the same shape as CapCut's other keyframes
(`keyframe_list[{id, curveType "Line", time_offset, left/right_control, values, string_value, graphID}]`),
with `time_offset` relative to the segment start in timeline time and `values` = linear volume
(`segment.volume` × 0.178 for −15 dB, or 0, while the voice speaks; ramps 0.25 s down, 0.4 s up). The format
of CapCut's own volume keyframes was not observed in the author's projects: this is our assumption, to be
checked in CapCut. Segments that already have volume keyframes are skipped. Our group ids start with
`ECC0D0CC-` so a later run can remove them. The renderer reads volume keyframes as a multiplier of
`segment.volume` over the clip (`AudioClip.volumeEnvelope`), including on compound clips.

"Replace voice" mutes like "Mute all" and adds one more audible track, `EnhanceCapCut voice-over · background`, laid out
like the voice tracks: the timeline's mix with the voice removed (`background.wav`, 48 kHz stereo 16-bit, aligned to
the timeline), with a clip over each ducked stretch from 0.25 s before to 0.4 s after it. Its gain is baked into the
file as the complement of the ducking ramps, so original and background cross-fade. A later run removes it with the
voice tracks (same name prefix).

"Remove vocal" turns on CapCut's own "Remove vocal" on every audible `video`/`audio` segment that overlaps the
speech, for the whole clip: a `vocal_separations` entry with `choice` 1, `time_range` = the segment's
`source_timerange`, `final_algorithm` `vocal_separate` and an empty `production_path` (CapCut's result file is
obfuscated and named by a hash we don't know, so CapCut has to make it). It replaces the segment's own entry in
`extra_material_refs`; the old entry stays in the materials untouched. Our entries' ids start with `ECC0D0CC-`, and
a later run sets their `choice` back to 0. Segments CapCut can't separate (compound video clips, CapCut `music`,
reversed clips, sources over 10 minutes) and ones set to "Keep vocal" are muted like "Mute all"; ones already on
"Remove vocal" are left alone. Whether CapCut makes the result from such an entry on its own is to be checked.

## Colour

Calibrated against the author's CapCut exports (HEVC Main10, BT.709 tagged):

- SDR clips: CapCut keeps code values (only the YCbCr matrix is applied). Core Image reads BT.709-tagged
  buffers with an sRGB-like curve, so we compose and encode in sRGB space (BT.709 primaries); encoding with
  `CGColorSpace.itur_709` (camera OETF) would brighten everything.
- HDR clips (iPhone HLG): CapCut's SDR rendition is flatter than a reference-white tone map — lifted
  shadows, compressed highlights. After `CIToneMapHeadroom` we apply a measured curve (`HDRLook` in
  `FrameSource.swift`); luma quantiles then match within ~1.5/255.
- `segment.hdr_settings` (`mode`, `intensity` 1, `nits` 1000): `mode` 4 on HLG clips = the tone map above (984
  segments); `mode` 1 on SDR and compound clips (no effect); `mode` 0 on HLG clips (Laughing, GymCrowd; 69
  segments) = no tone mapping: CapCut keeps the HLG code values like an SDR clip's — luma quantiles of
  Laughing's export equal the raw source's within 1/255. We do the same (`VisualLayer.toneMapsHDR`).

## Colour adjustments

- `materials.hsl[]` (referenced from a segment's `extra_material_refs`): `hsl_color_type` (1 seen; the effect
  has 8 default colour ranges plus up to 10 picked ones, `custom_color` e.g. `#FFE64444`), `hue`, `saturation`,
  `lightness`, `path` / `lumi_hub_path` (CapCut's shared colour-tools package in the effect cache). All 123 in the
  author's projects are neutral (`hsl_color_type` 1, all three values 0): CapCut adds one when the HSL panel is
  opened. They render as nothing and aren't reported; the value range of a changed slider is unknown.
- Segments carry `enable_adjust`, `enable_hsl`, `enable_color_curves`, `enable_color_wheels`, `enable_lut`,
  `enable_smart_color_adjust`, `enable_color_correct_adjust`, … — toggles, true by default; not values.
  `config.adjust_max_index` and empty `adjust_mask/` folders exist too. No draft of the author's has values from
  the Adjust panel (brightness, contrast, temperature, …), so their format is still unknown.
- "Color correction" / "Smart color adjust": `materials.effects[]` of `type` `color_correct` (OldSOund) or
  `smart_color_adjust` (Laughing), `name` "color-adjustment", `value` 0.8, empty `adjust_params`. The package runs
  a pre-render algorithm (`lens_ii_auto_color`, a CapCut model) and the segment's `color_correct_alg_result` is
  empty, so the result can't be reproduced; it stays reported. In Laughing's export the corrected clip is clearly
  brighter and more neutral than our render (luma ≈ 157 vs 114 at 2 s), but part of that gap is the HLG footage:
  the next clip, with no correction, is also brighter in CapCut's export (≈ 132 vs 118 at 8 s).

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

### Filters (`effects`, type `filter`)

`materials.effects[]` with `type` `filter` ("Faded Mood", "Film", "Yellowstone", "Shimmering B&W", "Butter Cheese" in
the author's projects): `name`, `resource_id` / `effect_id` (the effect id), `value` (the intensity slider, 0…1; all
uses are at 1), `path` (the cached package, `…/Cache/effect/<resource_id>/<md5>`), `category_name`
("Featured"), `adjust_params` (empty in all uses). Two ways to use one:

- on a `filter` track: a segment whose `material_id` is the filter (`render_index` 10000+, no `clip`); it colours
  everything painted below it (videos and overlays, not the texts at 14000+) for its `target_timerange`;
- per clip: the filter's id in a video segment's `extra_material_refs`.

We look for the lookup tables in the package at render time (`ColorFilter.locate`: the draft's `path`, else
`<cache>/<resource_id>/<hash>`). A package is a list of parts (`config.json` → `effect.Link[{path, zorder}]`); from
each part, in `zorder`, we take the table one of its `material/*.material` files names (binary files that contain the
texture paths, e.g. `image/filter_bg.png`; Butter Cheese's names just `image/filter`) and apply them in that order.
Parts often carry tables no material uses, so those don't count. A package without such references falls back to
its first `.cube` file or lookup-shaped PNG, preferring names with "lut". PNG
layouts: a √N×√N grid of N×N tiles (512×512 for N = 64), or N tiles in a row (N²×N) or a column; red grows to the
right and green downwards in a tile, blue picks the tile. The table is applied to sRGB-encoded values, blended with
the original by `value`. A filter whose package or table isn't found stays in the report.

CapCut downloads a package when a project using it is opened. Checked on "Butter Cheese" (OldSOund): the package is
an AmazingFeature (`AmazingFeature/image/filter.png`, 512×512, the 8×8 grid above; `main.scene`, five `xshader`
passes, `lua/SeekModeScript.lua`). The last pass looks up the grid image exactly as we do (blue between two tiles,
red/green inside a tile) and mixes with the input by `uniAlpha` = the intensity. Before it the package sharpens the
frame, independently of the intensity: two Gaussian passes with radius 0 (a no-op), then a 3×3 blur `b` and
`out = in + (in − b) × sharpness` with `sharpness` 1.35 and radius 1 set in the Lua script. The sharpening isn't
reproduced yet. Rendered against the export: the table moves the colours the right way (a wall patch goes from RGB
161/163/131 without the filter to 156/152/111, CapCut 169/159/103); the rest is the clip's "color correct" adjustment
(`effects`, `type` `color_correct`, `value` 0.8), which is still reported (#39). The other four used filters
(Faded Mood, Film, Yellowstone) are single-part packages with the same lookup shader (`AmazingFeature_2998/` or
`AmazingFeature/`, one `filter.png`); Film's `filter.png` is a JPEG despite the name, so tables are recognised by
their header (PNG or JPEG), not the extension. In Мокрая прогулка (filter track) our frames equal the table applied
to the frame's values, but the export's shift is only ≈ 0.55 of that for all three filters (best per-pixel fit 0.55,
0.55, 0.58 at `value` 1.0). CapCut's intensity slider does follow `value` (writing 0.3 into the draft shows 30 in
CapCut), so the export (16:31, before the draft's last save at 17:56) was most likely made at a lower intensity; there
is no export at the saved settings to confirm it.

"Shimmering B&W" (ElefantCafeLong, per clip) has four parts: `AmazingFeature` (8001: animated sparkles from
`kira-*.png` textures, plus an unused colour table `image/filter.png`), `AmazingFeature1` (8002: a glow/highlight
chain whose `final` pass uses an identity table), `AmazingFeature_filter` (8003: the black-and-white table, as
`filter_bg.png` and `filter_skin.png`, the same image, picked by a skin segmentation), `AmazingFeature_light` (8004:
an Orton-style glow, blur ×4 + edge + merge, strength 0.8 × intensity × 0.5). We apply the identity and the B&W table;
against the export the frame is black and white like CapCut's (saturation 0.8 vs 1.0 of 65535) but darker (luma
27 250 vs 30 080): the glow and the sparkles aren't reproduced.

"Cherry Cola" (2 uses) is not a filter: it's `type` `figure` with `value` 0, and its cached package is a makeup
AmazingFeature (`image/blusher`, `eyebrow`, `lip`…), so it's reported as *Beauty / makeup*.

### Voice filters (`audio_effects`)

`materials.audio_effects[]` (`type` `audio_effect`, `category_name` "Voice filters", `sub_type` 1):
`name`, `resource_id` / `third_resource_id` (the effect id), `path` (the cached effect folder,
`…/Cache/effect/<resource_id>/<md5>`, in the sandboxed app's container), `audio_adjust_params` (one
`strength` slider 0…1, default 1; the author's 16 uses are all at 1), `is_vc_clone_tone`, `vc_type`
("none"). Referenced from the segment's `extra_material_refs` like the other voice materials.

The cached folder holds `config.json` (`SAMIAudioEffect`), `extra.json` (the slider), `preset3.yaml`
(`music_dsp_effect` → `mdsp_preset.json`) and sometimes audio (Old Hollywood loops a `noise.wav`).
`mdsp_preset.json` is a processor graph: `nodes` (`processor_name` + `processor_state.parameters`),
`connections` (audio or `parameter_change`), and `ports` mapping the graph's audio input, its
strength parameter input and its output to nodes. All three filters share a dry/wet frame: the input
goes to the effect chain and to a dry gain; two `ParameterChangeTransform` curves map strength to
normalized gains (wet 0 → 0.3715 → 0.4444, dry 0.4444 → 0.3715 → 0 at strength 0 / 0.5 / 1) and a
`Mixer` sums them. The wet gain's stored state at strength 1 is −0.0035 dB, so 0.4444 ≈ unity. Chains:

- Booming: Filter Bell 80 Hz +10 dB "Resonance" 1 → Filter HighPass 40 Hz.
- Old Hollywood: Equaliser (LowPass 4 kHz, HighPass 750 Hz, Q 1) → Distortion "FoldBackSinArctan"
  (amount 0.527, wetness 0.404, post gain −7.8 dB) → Compressor (−3.9 dB, 8.2:1, 6 dB knee, 0.25 ms /
  100 ms, auto makeup) + `noise.wav` looped at −19.1 dB.
- Live House: StudioReverb "Hall" (pre-delay 25 ms, RT60 low 3.0 s / mid 2.3 s / high 1.1 s, crossover
  285 Hz, HF damping 3.5 kHz, wet mix 0.5) → LowPass 11 kHz → HighPass 60 Hz.

We read the preset at render time and run it with our own processors (`VoiceFilterGraph`). Measured on
ВазелиновыйПень (export "Вазелиновый пень.mov"; source = the project's plain `_upload.wav` in
`Resources/audioAlg`, since the original MP4 is gone; every voice clip is Keep vocal + Enhance voice 75
+ a filter + Normalize loudness, music at 0.31 under it), comparing the export with our chain per clip
with the music regressed out, ⅓-octave 125 Hz–6.3 kHz, levels relative to the median clip:

| filter (clips) | shape error, no filter → filter | level error, no filter → filter |
|---|---|---|
| Booming (11) | 2.3 → 2.0 dB | −1.2 → +0.4 dB |
| Old Hollywood (4) | 4.9 → 0.8 dB | +3.5 → −0.7 dB |
| Live House (1) | 5.2 → 5.7 dB | +10.2 → +5.7 dB |

- Filters: RBJ biquads with Q = "Resonance"/"Q"; Old Hollywood's 750 Hz / 4 kHz corners fit Q 1
  (−11 dB an octave below the high-pass, 0 dB at the corners) better than Butterworth.
- Booming lifts 100–160 Hz by +4…+9 dB over the speech band; below 100 Hz the music dominates.
- Old Hollywood's distortion: `sin(2·atan(d·x))` with `d = 5^amount` mixed by wetness; drives 3–30
  at amount 1 all land within 1 dB in level, more drive adds highs above 5 kHz the export lacks.
  Auto makeup = the gain reduction of a 0 dBFS signal (+3.4 dB here).
- Live House: CapCut's tail starts ~12 dB below the speech and decays ~1.3 s (60 dB) from 1 kHz up,
  ~3 s below — the "Hi RT60" band reaches far below the 3.5 kHz damping frequency. Our FDN puts the
  high band at 0.4 × damping and the wet level at 0.12. The clip as a whole is ~6 dB down (the dry half).
- The per-clip `loudnesses` (Normalize loudness, target −23) `avg_loudness` is measured before the
  filter: it tracks our pre-filter chain within a few dB, and the filters' own level changes survive
  in the export.

## Remove background

`materials.videos[].matting`: `flag` 0 = off, 1 = custom cutout (subject picked with the brush), 3 = auto
cutout; `path` = `##_draftpath_placeholder_…_##/matting/<hash>`; `custom_matting_id`; `reverse` ("Invert");
`expansion`, `feather`, `enable_matting_stroke` + `strokes` (outline), `mask_video_path` / `is_clould`
(empty / false in all the author's projects).

The result folder holds one subfolder per cutout run:

- `2/` — the auto cutout (every person in the frame); `<custom_matting_id>/5/` — the custom cutout (only the
  picked subject). Several custom runs can share one `<hash>` folder; the material's `custom_matting_id` picks one.
- Each has `mask/<µs>` — one raw 8-bit frame per source frame (0 = removed, 255 = kept, soft edges), not
  obfuscated, upright (display orientation), file name = source time in µs of the material (for a compound
  clip: of the compound's rendered video); `maskinfo/<µs>` — JSON `boundingBox` (normalized `left/top/right/
  bottom` of the subject and the mask's `width`/`height`); `matting_result.json` — `result_time_range`
  (computed source ranges); `mocf` — the frame times again; `brush_interaction` (custom only).
- Mask sizes seen: 448×256 / 256×448 for 16:9 / 9:16 footage, 256×336 for 3:4, 720×720 for a square photo,
  512×288 for custom cutouts of 16:9. A photo has one frame (`0`).
- A custom cutout (`flag` 1) whose `<custom_matting_id>` folder is missing or empty shows the whole clip in
  CapCut's export (Pullups7: the clip under the cut-out people is such a clip and the scene shows); the auto
  result in `2/` is not used for it.
- A compound clip's material is its rendered video and can be cut out itself; its frames are transparent
  where nothing is drawn, so cut-out clips inside it show the layers below (GymCrowd).

## Masks

`materials.common_mask[]` (`type` `mask`; older drafts may call the list `masks`), referenced from the video
segment's `extra_material_refs`. A clip can have several: CapCut shows what any of them keeps (Pullups7, GymCrowd: a
"Split" plus a rectangle, up to three rectangles and a brush on one clip). Fields: `name` ("Split", "Rectangle",
"Brush", …), `resource_type` (`line`, `mirror`, `circle`, `rectangle`, `custom` = brush; `geometric_shape` is assumed
for "Heart" / "Star", which none of the author's projects uses), `constant_material_id`, `loader_work_space`,
`track_segment` and `config`:

- `centerX`, `centerY`: the mask's center in half-frame units of the clip's own frame (before the clip's scale,
  rotation, flip and position, so the mask moves with the clip), +y up;
- `width`, `height`: fractions of the frame's width and height (can exceed 1); "Split" ignores both, "Mirror" uses
  `height` as the band's thickness;
- `rotation`: degrees, clockwise; "Split" at 0 keeps what is above the line;
- `feather` 0…1 (we fade over `feather × 0.5 ×` the frame's shorter side, centred on the edge, which matched
  Pullups7); `roundCorner` 0…1 of half the shorter side; `invert`; `expansion` (always 0 in the author's projects);
  `aspectRatio` (always 1).

Mask keyframes are in the segment's `common_keyframes` with `property_type` `KFTypeCommonMask` + `PositionX`,
`PositionY`, `SizeWidth`, `SizeHeight`, `Rotation`, `Feather`, `RoundCorner`, `Expansion` (the same units as
`config`). Their `material_id` is the mask's `constant_material_id`, not its `id`, and unlike the clip keyframes their
`time_offset` is on the clip's **source** time axis (a clip with `source_timerange.start` 51.5 s has mask keyframes
from 51 500 000).

"Brush" (`custom`): CapCut tracks the painted strokes over the clip and saves the result in
`loader_work_space` = `##_draftpath_placeholder_…_##/adjust_mask/<id>/`: one raw 8-bit frame per file (0 = hidden,
255 = shown, soft edges), upright, named by its source time in µs (512×288 for 16:9 footage, the size is in
`data.json` `data[].width/height`, with each frame's `pts` and the strokes' normalized bounds), plus `desc.json`
(`startTime`, `endTime`), `interactiveFrame` (the frame as drawn) and `config_*.ini` (binary plists of the brush
settings). A brush that was never tracked has a single frame. The brush's `config` keeps its defaults (0.28 × 0.5,
center 0) and isn't used. Five GymCrowd brushes have an empty `loader_work_space`; we report them.

We draw the masks ourselves (`MaskRaster` on both platforms): each shape as a signed distance field on a raster of at
most 1024 px, the feather as a linear ramp across the edge, brush frames bilinearly scaled, the union of all masks as
the clip's alpha, applied after "Chroma key" and "Remove background" and before the crop. Not reproduced: `expansion`
and a brush's feather (reported).

## Chroma key

`materials.chromas[]` (`type` `chroma`), referenced from the video segment's `extra_material_refs`: `color`
(the picked colour, `#RRGGBB` or `#AARRGGBB`; empty until one is picked), `intensity_value`, `shadow_value`,
`edge_smooth_value`, `spill_value` (the sliders, 0…1), `path`. None of the author's projects uses it; the field names
come from CapCut's `MaterialChroma` (`libvideoeditor.dylib`). In CapCut's own effect "Intensity" 0 leaves the clip as
it is, and "Shadow" keeps dark pixels opaque.

Our keyer (`ChromaKeyFilter` / `ChromaKeyMath`) is our own, not CapCut's: a pixel's opacity comes from its distance
to the picked colour in Y′CbCr (BT.601 on sRGB values; brightness counts little for a saturated key and fully for a
white, grey or black one), with a soft band; "Shadow" keeps pixels below a brightness opaque; "Spill" takes the
key's hue out of what's kept; "Edge feather" blurs the matte. The keying runs on the clip's frames before the
cutout and the crop. Its constants are not calibrated against a CapCut export yet.

## Transitions

`materials.transitions[]`: `{id, type: "transition", name, effect_id / resource_id, path (effect cache), duration (µs),
is_overlap, category_name, is_ai_transition, video_path}`. The transition is referenced from the `extra_material_refs`
of the clip it **leaves** (the outgoing clip); the last clip of a track has none. Target ranges stay back to back —
adding a transition doesn't move or shorten the clips. Seen only on the main track, every one with `is_overlap: true`.

Against the export of ATV (30 fps): the transition is centred on the cut, `[cut − duration/2, cut + duration/2)` —
"Flashback" (0.2 s) blurs from 9.733 s to 9.933 s around the cut at 9.833 s; "Fading Red" (2 s) is fully red at the
cut, the outgoing clip plays up to the cut (it ends at its media's end, so there are no frames beyond it) and the
incoming one plays from the cut at its normal source time. What CapCut shows of the other clip in the opposite half
(edge frame held or media beyond the clip edges) isn't settled: we hold the edge frame.

"Flashback" (`effect_id` 7252586720078598658) looks like a zoom-and-spin blur that peaks at the cut while the picture
dissolves from one clip into the other; we re-implement it (`FlashbackLook`), every other transition is a
cross-fade.

## Stickers

Sticker tracks (`type` `sticker`) hold segments whose `material_id` points into `materials.stickers[]`: `name`,
`resource_id` = `sticker_id`, `category_name`, `path` (CapCut's cache,
`~/Library/Containers/com.lemon.lvoverseas/Data/Movies/CapCut/User Data/Cache/artistEffect/<resource_id>/<hash>` — the
folders were gone on the author's Mac), `global_alpha`, `cycle_setting`, shape/border/shadow fields (defaults in all
the author's projects). The segment has `source_timerange` null, `render_index` 14000 and is placed with the ordinary
`clip` (scale, rotation, transform, alpha).

"Clock Time" (resource `7528282080199904565` "EN White Outline Progress Bar" and `7528280675909209405` "EN Blue
White Minimalist") is drawn natively (`ClockSticker`). Measured on the author's exports (Pullups3Wide, Pullups5Wide):

- The clock is centered on the clip position; its outer diameter is 0.441 (white) / 0.448 (blue) × the canvas height
  (1080, 16:9; the shorter side is assumed for other shapes) × the clip scale.
- White: white ring (0.157 of the outer radius) and white hands, no face. Blue: grey ring `#535353` (0.191), face
  `#F9F9F9`, hands `#0078D2`. Hands are round-capped, 0.13–0.14 wide, 0.67–0.69 (minute) and 0.45–0.47 (hour) long.
- The animation loops every 82 frames (2.733 s) from the segment start: from the rest pose (minute hand at 12, hour
  hand at 3) the minute hand turns twice and the hour hand once, both slowing down, then they rest for ~4 frames.
  Both styles move identically. The angle table is in `ClockSticker`.

## Video effects

`materials.video_effects[]` (`type` `video_effect`, or `face_effect` for body effects such as "Laser Eyes"): `name`,
`effect_id` = `resource_id` (the effect's id, also its cache folder `Cache/effect/<id>/<md5>` in `path`),
`category_name`, `apply_target_type`, `adjust_params[{name, value, default_value}]` (`effects_adjust_speed` default ⅓,
`effects_adjust_size`, `_intensity`, `_luminance`, `_distortion`, `_range`, all 0.5 by default), `value` (1),
`time_range` (`{start: 0, duration: …}` or null; not understood yet), `common_keyframes`, `effect_mask` (empty in
the author's projects).

- `apply_target_type` 0: the effect is in a clip's `extra_material_refs` and changes that clip's picture inside its
  own frame (a picture-in-picture clip stays in its rectangle). 2: a segment on an `effect` track (`material_id`),
  over everything below it.
- The packages of the effects the author used were gone from the cache by the time we looked, so the effects are
  re-implemented from the export ("Мокрая прогулка", 30 fps grabs). Both are one-shot openings at the clip start,
  nothing repeats later in the clip:
  - "Pudding Bounce" (`7534579654615649553`): the first frame is mixed ≈50% towards white (mean luma 146 → 204),
    halving every 2 frames; zoomed in ≈1.45× and blurred, back to 1× by ≈0.25 s with a small bounce; sharp by 0.4 s.
  - "Hazy Rotation" (`7486402432189091127`): ≈0.13 s zoomed in ≈2×, tilted and out of focus (bokeh), then turns and
    zooms back, settled by ≈0.36 s.
- Seen but not rendered: "Stack Twist" (the frame as a grid of mirrored tiles), "BG Insert" (black/white block
  glitch), "Card Slash" (slashes and flying shards at the start), "Flame Frame", and on effect tracks "Live Stream"
  (punch-in towards the face), "Box Magnifier", "Magnify Glass".

## Compound clips

`materials.drafts[]` (`type` `combination`) embed a complete nested draft in `draft`. A video segment
references it via `extra_material_refs`. Nested drafts can reference combinations stored in the outer draft.

## Usage in the author's 69 projects

~655 texts (626 captions), speed objects on all 1564 segments, 381 audios, 195 vocal_beautify,
58 vocal separations (35 keep vocal, 23 remove vocal), 52 "Reduce noise",
151 loudness, 135 effects (makeup/figure/"Sparkly Skin"/"ART EN 3D" text effects/"Cherry Cola" filter),
46 stickers (44 "Clock Time"), 26 transitions (18 "Flashback"), 19 hsl, 16 audio_effects,
11 video_effects, 10 masks (139 uses: brush 83, rectangle 48, split 8; 18 clips with mask keyframes), 52 compound
clips.
