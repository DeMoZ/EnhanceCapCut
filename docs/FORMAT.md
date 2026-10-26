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

"Lower" / "Mute" original audio: a `KFTypeVolume` group in `common_keyframes` of every other audible
`video`/`audio` segment that overlaps the speech, in the same shape as CapCut's other keyframes
(`keyframe_list[{id, curveType "Line", time_offset, left/right_control, values, string_value, graphID}]`),
with `time_offset` relative to the segment start in timeline time and `values` = linear volume
(`segment.volume` × 0.178 for −15 dB, or 0, while the voice speaks; ramps 0.25 s down, 0.4 s up). The format
of CapCut's own volume keyframes was not observed in the author's projects: this is our assumption, to be
checked in CapCut. Segments that already have volume keyframes are skipped. Our group ids start with
`ECC0D0CC-` so a later run can remove them. The renderer reads volume keyframes as a multiplier of
`segment.volume` over the clip (`AudioClip.volumeEnvelope`), including on compound clips.

"Replace voice" mutes like "Mute" and adds one more audible track, `EnhanceCapCut voice-over · background`, laid out
like the voice tracks: the timeline's mix with the voice removed (`background.wav`, 48 kHz stereo 16-bit, aligned to
the timeline), with a clip over each ducked stretch from 0.25 s before to 0.4 s after it. Its gain is baked into the
file as the complement of the ducking ramps, so original and background cross-fade. A later run removes it with the
voice tracks (same name prefix).

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

## Compound clips

`materials.drafts[]` (`type` `combination`) embed a complete nested draft in `draft`. A video segment
references it via `extra_material_refs`. Nested drafts can reference combinations stored in the outer draft.

## Usage in the author's 69 projects

~655 texts (626 captions), speed objects on all 1564 segments, 381 audios, 195 vocal_beautify,
58 vocal separations (35 keep vocal, 23 remove vocal), 52 "Reduce noise",
151 loudness, 135 effects (makeup/figure/"Sparkly Skin"/"ART EN 3D" text effects/"Cherry Cola" filter),
46 stickers (44 "Clock Time"), 26 transitions (18 "Flashback"), 19 hsl, 16 audio_effects,
11 video_effects, 10 masks, 52 compound clips.
