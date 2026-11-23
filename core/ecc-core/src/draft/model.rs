//! Typed view of CapCut's draft (`draft_info.json` on the Mac, `draft_content.json` on Windows; draft version
//! 360000 / new_version 187.0.0 and later), the port of `DraftModel.swift`, `MaskMaterial.swift` and
//! `AnimationMaterial.swift`.
//!
//! Only the fields the renderer and the export report need are modelled; everything else is ignored. All times are
//! in microseconds, as in the draft. Every type is built with `from_value`, which returns `None` only when the value
//! is not a JSON object (or, for a few materials, has no `id`): a field that is missing, `null` or of the wrong type
//! takes its default, exactly as in the Swift models.

use std::collections::BTreeMap;

use serde_json::Value;

use crate::lenient::Lenient;

fn object(v: &Value) -> Option<&Value> {
    v.is_object().then_some(v)
}

fn new_uuid() -> String {
    uuid::Uuid::new_v4().to_string().to_uppercase()
}

#[derive(Debug, Clone, PartialEq)]
pub struct Draft {
    pub id: String,
    pub name: String,
    pub duration: i64,
    pub fps: f64,
    pub canvas: CanvasConfig,
    pub tracks: Vec<Track>,
    pub materials: Materials,
    pub config: DraftConfig,
}

impl Draft {
    pub fn from_value(v: &Value) -> Option<Draft> {
        let c = object(v)?;
        Some(Draft {
            id: c.string("id"),
            name: c.string("name"),
            duration: c.int64("duration").unwrap_or(0),
            fps: c.num("fps").unwrap_or(30.0),
            canvas: c.opt("canvas_config").and_then(CanvasConfig::from_value).unwrap_or_default(),
            tracks: c.list("tracks", Track::from_value),
            materials: c.opt("materials").and_then(Materials::from_value).unwrap_or_default(),
            config: c.opt("config").and_then(DraftConfig::from_value).unwrap_or_default(),
        })
    }

    /// Parses a whole draft; fails on invalid JSON or a root that is not an object.
    pub fn parse(text: &str) -> Result<Draft, String> {
        let v: Value = serde_json::from_str(text).map_err(|e| e.to_string())?;
        Draft::from_value(&v).ok_or_else(|| "The draft is not a JSON object.".to_string())
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct CanvasConfig {
    pub width: i64,
    pub height: i64,
    pub ratio: String,
}

impl Default for CanvasConfig {
    fn default() -> Self {
        CanvasConfig { width: 1920, height: 1080, ratio: "original".into() }
    }
}

impl CanvasConfig {
    pub fn from_value(v: &Value) -> Option<CanvasConfig> {
        let c = object(v)?;
        Some(CanvasConfig {
            width: c.num_int("width", 1920.0),
            height: c.num_int("height", 1080.0),
            ratio: c.str("ratio").unwrap_or("original").into(),
        })
    }
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct DraftConfig {
    /// The speaker toggle on the main track.
    pub video_mute: bool,
}

impl DraftConfig {
    pub fn from_value(v: &Value) -> Option<DraftConfig> {
        let c = object(v)?;
        Some(DraftConfig { video_mute: c.bool("video_mute").unwrap_or(false) })
    }
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Hash)]
pub struct TimeRange {
    pub start: i64,
    pub duration: i64,
}

impl TimeRange {
    pub const fn new(start: i64, duration: i64) -> TimeRange {
        TimeRange { start, duration }
    }

    pub const fn end(&self) -> i64 {
        self.start + self.duration
    }

    pub fn from_value(v: &Value) -> Option<TimeRange> {
        let c = object(v)?;
        Some(TimeRange { start: c.int64("start").unwrap_or(0), duration: c.int64("duration").unwrap_or(0) })
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct Track {
    pub id: String,
    /// "video", "audio", "text", "sticker", "effect", "filter", ...
    pub r#type: String,
    /// Bit flags of the track header toggles. Bit 0 is "mute"; bit 1 is "hide".
    pub attribute: i64,
    pub flag: i64,
    pub segments: Vec<Segment>,
}

impl Track {
    pub fn is_muted(&self) -> bool {
        self.attribute & 1 != 0
    }

    pub fn is_hidden(&self) -> bool {
        self.attribute & 2 != 0
    }

    pub fn from_value(v: &Value) -> Option<Track> {
        let c = object(v)?;
        Some(Track {
            id: c.str("id").map(str::to_string).unwrap_or_else(new_uuid),
            r#type: c.string("type"),
            attribute: c.num_int("attribute", 0.0),
            flag: c.num_int("flag", 0.0),
            segments: c.list("segments", Segment::from_value),
        })
    }
}

#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct Point2 {
    pub x: f64,
    pub y: f64,
}

impl Point2 {
    pub const fn new(x: f64, y: f64) -> Point2 {
        Point2 { x, y }
    }

    pub fn from_value(v: &Value) -> Option<Point2> {
        let c = object(v)?;
        Some(Point2 { x: c.num("x").unwrap_or(0.0), y: c.num("y").unwrap_or(0.0) })
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct Clip {
    pub scale: Point2,
    /// Degrees, clockwise on screen.
    pub rotation: f64,
    /// Offset of the layer center in half-canvas units; +y is up.
    pub transform: Point2,
    pub flip_horizontal: bool,
    pub flip_vertical: bool,
    pub alpha: f64,
}

impl Default for Clip {
    fn default() -> Self {
        Clip {
            scale: Point2::new(1.0, 1.0),
            rotation: 0.0,
            transform: Point2::default(),
            flip_horizontal: false,
            flip_vertical: false,
            alpha: 1.0,
        }
    }
}

impl Clip {
    pub fn from_value(v: &Value) -> Option<Clip> {
        let c = object(v)?;
        let flip = c.obj("flip");
        Some(Clip {
            scale: c.opt("scale").and_then(Point2::from_value).unwrap_or(Point2::new(1.0, 1.0)),
            rotation: c.num("rotation").unwrap_or(0.0),
            transform: c.opt("transform").and_then(Point2::from_value).unwrap_or_default(),
            flip_horizontal: flip.and_then(|f| f.bool("horizontal")).unwrap_or(false),
            flip_vertical: flip.and_then(|f| f.bool("vertical")).unwrap_or(false),
            alpha: c.num("alpha").unwrap_or(1.0),
        })
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct Keyframe {
    pub time_offset: i64,
    pub values: Vec<f64>,
    pub curve_type: String,
    /// Bezier handles relative to the keyframe: x in µs, y in the property's units (see `KeyframeBezier`).
    pub left_control: Point2,
    pub right_control: Point2,
}

impl Keyframe {
    pub fn from_value(v: &Value) -> Option<Keyframe> {
        let c = object(v)?;
        Some(Keyframe {
            time_offset: c.int64("time_offset").unwrap_or(0),
            values: c.f64_array("values").unwrap_or_default(),
            curve_type: c.str("curveType").unwrap_or("Line").into(),
            left_control: c.opt("left_control").and_then(Point2::from_value).unwrap_or_default(),
            right_control: c.opt("right_control").and_then(Point2::from_value).unwrap_or_default(),
        })
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct KeyframeGroup {
    /// e.g. "KFTypePositionX", "KFTypeScaleX", "KFTypeRotation", "KFTypeAlpha", "KFTypeVolume".
    pub property_type: String,
    pub keyframes: Vec<Keyframe>,
    /// For mask keyframes: the mask's `constant_material_id`.
    pub material_id: String,
}

impl KeyframeGroup {
    pub fn from_value(v: &Value) -> Option<KeyframeGroup> {
        let c = object(v)?;
        Some(KeyframeGroup {
            property_type: c.string("property_type"),
            keyframes: c.list("keyframe_list", Keyframe::from_value),
            material_id: c.string("material_id"),
        })
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct Segment {
    pub id: String,
    pub material_id: String,
    pub source_timerange: Option<TimeRange>,
    pub target_timerange: TimeRange,
    pub speed: f64,
    pub volume: f64,
    pub reverse: bool,
    pub visible: bool,
    pub is_tone_modify: bool,
    pub clip: Option<Clip>,
    pub uniform_scale: bool,
    pub extra_material_refs: Vec<String>,
    pub render_index: i64,
    pub track_render_index: i64,
    pub keyframes: Vec<KeyframeGroup>,
    /// "Loop" playback of a clip shorter than its segment (not supported).
    pub is_loop: bool,
    /// `hdr_settings.mode`: 4 = an HDR clip tone-mapped to SDR, 0 = an HDR clip shown as is (no tone mapping),
    /// 1 = SDR and compound clips (see FORMAT.md › Colour).
    pub hdr_mode: Option<i64>,
}

impl Segment {
    pub fn from_value(v: &Value) -> Option<Segment> {
        let c = object(v)?;
        Some(Segment {
            id: c.str("id").map(str::to_string).unwrap_or_else(new_uuid),
            material_id: c.string("material_id"),
            source_timerange: c.opt("source_timerange").and_then(TimeRange::from_value),
            target_timerange: c.opt("target_timerange").and_then(TimeRange::from_value).unwrap_or_default(),
            speed: c.num("speed").unwrap_or(1.0),
            volume: c.num("volume").unwrap_or(1.0),
            reverse: c.bool("reverse").unwrap_or(false),
            visible: c.bool("visible").unwrap_or(true),
            is_tone_modify: c.bool("is_tone_modify").unwrap_or(false),
            clip: c.opt("clip").and_then(Clip::from_value),
            uniform_scale: c.obj("uniform_scale").is_none_or(|u| u.bool("on").unwrap_or(true)),
            extra_material_refs: c.string_array("extra_material_refs").unwrap_or_default(),
            render_index: c.num_int("render_index", 0.0),
            track_render_index: c.num_int("track_render_index", 0.0),
            keyframes: c.list("common_keyframes", KeyframeGroup::from_value),
            is_loop: c.bool("is_loop").unwrap_or(false),
            hdr_mode: c.obj("hdr_settings").and_then(|h| h.num("mode")).map(|m| m as i64),
        })
    }
}

// MARK: - Materials

#[derive(Debug, Clone, Default, PartialEq)]
pub struct Materials {
    pub videos: Vec<VideoMaterial>,
    pub audios: Vec<AudioMaterial>,
    pub texts: Vec<TextMaterial>,
    pub canvases: Vec<CanvasMaterial>,
    pub speeds: Vec<SpeedMaterial>,
    pub audio_fades: Vec<AudioFadeMaterial>,
    pub drafts: Vec<CombinationMaterial>,
    pub loudnesses: Vec<LoudnessMaterial>,
    pub vocal_separations: Vec<VocalSeparationMaterial>,
    pub realtime_denoises: Vec<RealtimeDenoiseMaterial>,
    pub vocal_beautifys: Vec<VocalBeautifyMaterial>,
    pub audio_effects: Vec<AudioEffectMaterial>,
    pub chromas: Vec<ChromaMaterial>,
    pub video_effects: Vec<VideoEffectMaterial>,
    pub material_animations: Vec<AnimationMaterial>,
    /// Clip masks (`common_mask`, older drafts `masks`).
    pub masks: Vec<MaskMaterial>,
    /// Every material of every category, with just the fields used for reporting.
    pub all: BTreeMap<String, Vec<GenericMaterial>>,
}

impl Materials {
    pub fn from_value(v: &Value) -> Option<Materials> {
        let c = object(v)?;
        let mut masks = c.list("common_mask", MaskMaterial::from_value);
        masks.extend(c.list("masks", MaskMaterial::from_value));
        let mut all = BTreeMap::new();
        for key in c.keys() {
            let items = c.list(key, GenericMaterial::from_value);
            if !items.is_empty() {
                all.insert(key.to_string(), items);
            }
        }
        Some(Materials {
            videos: c.list("videos", VideoMaterial::from_value),
            audios: c.list("audios", AudioMaterial::from_value),
            texts: c.list("texts", TextMaterial::from_value),
            canvases: c.list("canvases", CanvasMaterial::from_value),
            speeds: c.list("speeds", SpeedMaterial::from_value),
            audio_fades: c.list("audio_fades", AudioFadeMaterial::from_value),
            drafts: c.list("drafts", CombinationMaterial::from_value),
            loudnesses: c.list("loudnesses", LoudnessMaterial::from_value),
            vocal_separations: c.list("vocal_separations", VocalSeparationMaterial::from_value),
            realtime_denoises: c.list("realtime_denoises", RealtimeDenoiseMaterial::from_value),
            vocal_beautifys: c.list("vocal_beautifys", VocalBeautifyMaterial::from_value),
            audio_effects: c.list("audio_effects", AudioEffectMaterial::from_value),
            chromas: c.list("chromas", ChromaMaterial::from_value),
            video_effects: c.list("video_effects", VideoEffectMaterial::from_value),
            material_animations: c.list("material_animations", AnimationMaterial::from_value),
            masks,
            all,
        })
    }
}

/// The common subset of every material: enough to name it in the "not supported yet" list.
#[derive(Debug, Clone, PartialEq)]
pub struct GenericMaterial {
    pub id: String,
    pub r#type: String,
    /// For `effects`: e.g. "auto_beauty" (skin retouch) or "exclusion_face" (a makeup look) on a "figure".
    pub sub_type: String,
    pub name: String,
    pub path: String,
    /// CapCut's library `effect_id` (also matched for blend modes, see `resource_id`).
    pub effect_id: String,
    /// For `material_animations`: the names of the text/sticker animations it holds.
    pub animation_names: Vec<String>,
    /// Toggles used by audio-processing materials (`enable`, `is_denoise`, `choice`).
    pub enable: Option<bool>,
    pub is_denoise: Option<bool>,
    pub choice: Option<i64>,
    /// For filters (`effects`, type "filter"): the effect id in CapCut's library (the cache folder name) and the
    /// intensity slider, 0...1.
    pub resource_id: String,
    pub value: Option<f64>,
    /// For `transitions`: the length in µs.
    pub duration: i64,
    /// For `hsl`: the hue, saturation and lightness shifts of one colour range (0 = unchanged).
    pub hue: f64,
    pub saturation: f64,
    pub lightness: f64,
}

impl GenericMaterial {
    /// An `hsl` material whose sliders are all at 0: CapCut adds one when the HSL panel is opened.
    pub fn is_neutral_hsl(&self) -> bool {
        self.hue == 0.0 && self.saturation == 0.0 && self.lightness == 0.0
    }

    /// `None` without an `id`.
    pub fn from_value(v: &Value) -> Option<GenericMaterial> {
        let c = object(v)?;
        let id = c.str("id")?.to_string();
        Some(GenericMaterial {
            id,
            r#type: c.string("type"),
            sub_type: c.string("sub_type"),
            name: c
                .str("name")
                .or_else(|| c.str("effect_name"))
                .or_else(|| c.str("material_name"))
                .unwrap_or("")
                .into(),
            path: c.string("path"),
            effect_id: c.string("effect_id"),
            animation_names: c.list("animations", |a| {
                let a = object(a)?;
                Some(a.str("name").or_else(|| a.str("type")).unwrap_or("animation").to_string())
            }),
            enable: c.bool("enable"),
            is_denoise: c.bool("is_denoise"),
            choice: c.num("choice").map(|d| d as i64),
            resource_id: c.str("resource_id").or_else(|| c.str("effect_id")).unwrap_or("").into(),
            value: c.num("value"),
            duration: c.int64("duration").unwrap_or(0),
            hue: c.num("hue").unwrap_or(0.0),
            saturation: c.num("saturation").unwrap_or(0.0),
            lightness: c.num("lightness").unwrap_or(0.0),
        })
    }
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Crop {
    // Normalized corners, origin at the top-left of the source frame.
    pub upper_left: Point2,
    pub lower_right: Point2,
}

impl Default for Crop {
    fn default() -> Self {
        Crop { upper_left: Point2::new(0.0, 0.0), lower_right: Point2::new(1.0, 1.0) }
    }
}

impl Crop {
    pub fn is_identity(&self) -> bool {
        self.upper_left.x <= 0.0001
            && self.upper_left.y <= 0.0001
            && self.lower_right.x >= 0.9999
            && self.lower_right.y >= 0.9999
    }

    pub fn from_value(v: &Value) -> Option<Crop> {
        let c = object(v)?;
        Some(Crop {
            upper_left: Point2::new(c.num("upper_left_x").unwrap_or(0.0), c.num("upper_left_y").unwrap_or(0.0)),
            lower_right: Point2::new(c.num("lower_right_x").unwrap_or(1.0), c.num("lower_right_y").unwrap_or(1.0)),
        })
    }
}

/// A video material's "Remove background" (`matting`). CapCut stores its result as raw mask frames in the project
/// folder (see FORMAT.md).
#[derive(Debug, Clone, Default, PartialEq)]
pub struct Matting {
    /// 0 = off, 1 = custom cutout (subject picked with the brush), 3 = auto cutout.
    pub flag: i64,
    /// The folder with the results, usually `##_draftpath_placeholder_…_##/matting/<hash>`.
    pub path: String,
    /// The custom cutout's result folder inside `path`.
    pub custom_id: String,
    /// "Invert".
    pub reverse: bool,
    pub expansion: f64,
    pub feather: f64,
    /// The outline drawn around the cutout ("Stroke") is on.
    pub stroke: bool,
}

impl Matting {
    pub fn from_value(v: &Value) -> Option<Matting> {
        let c = object(v)?;
        Some(Matting {
            flag: c.num_int("flag", 0.0),
            path: c.string("path"),
            custom_id: c.string("custom_matting_id"),
            reverse: c.bool("reverse").unwrap_or(false),
            expansion: c.num("expansion").unwrap_or(0.0),
            feather: c.num("feather").unwrap_or(0.0),
            stroke: c.bool("enable_matting_stroke").unwrap_or(false),
        })
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct VideoMaterial {
    pub id: String,
    /// "video" or "photo".
    pub r#type: String,
    pub path: String,
    pub name: String,
    pub width: i64,
    pub height: i64,
    pub duration: i64,
    pub has_audio: bool,
    pub has_sound_separated: bool,
    pub reverse_path: String,
    pub crop: Crop,
    /// "Stabilize" level, 0 = off.
    pub stable_level: i64,
    /// "Remove background".
    pub matting: Matting,
    /// AI processing applied to the clip: `video_algorithm.algorithms[].type` plus the names of the
    /// non-null per-feature configs (e.g. "super_resolution", "deflicker").
    pub algorithms: Vec<String>,
    /// "Retouch" (face beauty) is on.
    pub beauty_mode: bool,
}

impl VideoMaterial {
    /// Per-feature configs inside `video_algorithm` that change the picture when set.
    pub const ALGORITHM_CONFIGS: [&'static str; 9] = [
        "super_resolution",
        "deflicker",
        "noise_reduction",
        "motion_blur_config",
        "smart_complement_frame",
        "complement_frame_config",
        "ai_expression_driven",
        "ai_motion_driven",
        "mouth_shape_driver",
    ];

    pub fn from_value(v: &Value) -> Option<VideoMaterial> {
        let c = object(v)?;
        let mut algorithms = Vec::new();
        if let Some(a) = c.obj("video_algorithm") {
            algorithms = a.list("algorithms", |item| Some(object(item)?.str("type").unwrap_or("unknown").to_string()));
            for key in Self::ALGORITHM_CONFIGS {
                if a.opt(key).is_some() {
                    algorithms.push(key.to_string());
                }
            }
        }
        Some(VideoMaterial {
            id: c.string("id"),
            r#type: c.str("type").unwrap_or("video").into(),
            path: c.string("path"),
            name: c.string("material_name"),
            width: c.num_int("width", 0.0),
            height: c.num_int("height", 0.0),
            duration: c.int64("duration").unwrap_or(0),
            has_audio: c.bool("has_audio").unwrap_or(true),
            has_sound_separated: c.bool("has_sound_separated").unwrap_or(false),
            reverse_path: c.string("reverse_path"),
            crop: c.opt("crop").and_then(Crop::from_value).unwrap_or_default(),
            stable_level: c.obj("stable").and_then(|s| s.num("stable_level")).map_or(0, |d| d as i64),
            matting: c.opt("matting").and_then(Matting::from_value).unwrap_or_default(),
            algorithms,
            beauty_mode: c.bool("is_set_beauty_mode").unwrap_or(false),
        })
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct AudioMaterial {
    pub id: String,
    /// "music", "extract_music", "record", "video_original_sound", "text_to_audio", ...
    pub r#type: String,
    pub path: String,
    pub name: String,
    pub duration: i64,
}

impl AudioMaterial {
    pub fn from_value(v: &Value) -> Option<AudioMaterial> {
        let c = object(v)?;
        Some(AudioMaterial {
            id: c.string("id"),
            r#type: c.string("type"),
            path: c.string("path"),
            name: c.string("name"),
            duration: c.int64("duration").unwrap_or(0),
        })
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct CanvasMaterial {
    pub id: String,
    /// "canvas_color", "canvas_blur" or "canvas_image".
    pub r#type: String,
    pub color: String,
    pub blur: f64,
    pub image: String,
}

impl CanvasMaterial {
    pub fn from_value(v: &Value) -> Option<CanvasMaterial> {
        let c = object(v)?;
        Some(CanvasMaterial {
            id: c.string("id"),
            r#type: c.str("type").unwrap_or("canvas_color").into(),
            color: c.string("color"),
            blur: c.num("blur").unwrap_or(0.0),
            image: c.string("image"),
        })
    }
}

/// "Chroma key" (`materials.chromas`), referenced from the segment's `extra_material_refs`. The sliders are 0…1.
#[derive(Debug, Clone, PartialEq)]
pub struct ChromaMaterial {
    pub id: String,
    /// The picked colour, "#RRGGBB" or "#AARRGGBB"; empty until one is picked.
    pub color: String,
    /// "Intensity": how far from the colour is still removed; 0 = off.
    pub intensity: f64,
    /// "Shadow": keeps dark pixels (shadows) opaque.
    pub shadow: f64,
    /// "Edge feather".
    pub edge_smooth: f64,
    /// "Spill": removes the colour's cast from what's kept.
    pub spill: f64,
}

impl ChromaMaterial {
    pub fn from_value(v: &Value) -> Option<ChromaMaterial> {
        let c = object(v)?;
        Some(ChromaMaterial {
            id: c.string("id"),
            color: c.string("color"),
            intensity: c.num("intensity_value").unwrap_or(0.0),
            shadow: c.num("shadow_value").unwrap_or(0.0),
            edge_smooth: c.num("edge_smooth_value").unwrap_or(0.0),
            spill: c.num("spill_value").unwrap_or(0.0),
        })
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct SpeedMaterial {
    pub id: String,
    /// 0 = constant, 1 = curve.
    pub mode: i64,
    pub speed: f64,
    pub curve_name: String,
    /// x: normalized position in the segment's timeline duration, y: speed multiplier.
    pub curve_points: Vec<Point2>,
}

impl SpeedMaterial {
    pub fn from_value(v: &Value) -> Option<SpeedMaterial> {
        let c = object(v)?;
        let curve = c.obj("curve_speed");
        Some(SpeedMaterial {
            id: c.string("id"),
            mode: c.num_int("mode", 0.0),
            speed: c.num("speed").unwrap_or(1.0),
            curve_name: curve.map(|k| k.string("name")).unwrap_or_default(),
            curve_points: curve.map(|k| k.list("speed_points", Point2::from_value)).unwrap_or_default(),
        })
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct AudioFadeMaterial {
    pub id: String,
    pub fade_in: i64,
    pub fade_out: i64,
}

impl AudioFadeMaterial {
    pub fn from_value(v: &Value) -> Option<AudioFadeMaterial> {
        let c = object(v)?;
        Some(AudioFadeMaterial {
            id: c.string("id"),
            fade_in: c.int64("fade_in_duration").unwrap_or(0),
            fade_out: c.int64("fade_out_duration").unwrap_or(0),
        })
    }
}

/// Per-clip "Normalize loudness": CapCut stores the measured loudness and the target.
#[derive(Debug, Clone, PartialEq)]
pub struct LoudnessMaterial {
    pub id: String,
    pub enable: bool,
    pub target_loudness: f64,
    pub average_loudness: Option<f64>,
}

impl LoudnessMaterial {
    /// Gain in dB that brings the clip to its target, or 0 when disabled.
    pub fn gain_db(&self) -> f64 {
        match self.average_loudness {
            Some(avg) if self.enable && avg > -70.0 => self.target_loudness - avg,
            _ => 0.0,
        }
    }

    pub fn from_value(v: &Value) -> Option<LoudnessMaterial> {
        let c = object(v)?;
        Some(LoudnessMaterial {
            id: c.string("id"),
            enable: c.bool("enable").unwrap_or(false),
            target_loudness: c.num("target_loudness").unwrap_or(-23.0),
            average_loudness: c.obj("loudness_param").and_then(|p| p.num("avg_loudness")),
        })
    }
}

/// Per-clip "Isolate voice": `choice` 0 = off, 1 = "Remove vocal" (keep the background),
/// 2 = "Keep vocal". CapCut renders the result into `production_path` (obfuscated, not read by us).
#[derive(Debug, Clone, PartialEq)]
pub struct VocalSeparationMaterial {
    pub id: String,
    pub choice: i64,
}

impl VocalSeparationMaterial {
    pub fn from_value(v: &Value) -> Option<VocalSeparationMaterial> {
        let c = object(v)?;
        Some(VocalSeparationMaterial { id: c.string("id"), choice: c.num("choice").map_or(0, |d| d as i64) })
    }
}

/// Per-clip "Reduce noise". CapCut applies its model while playing/exporting; nothing is cached.
#[derive(Debug, Clone, PartialEq)]
pub struct RealtimeDenoiseMaterial {
    pub id: String,
    pub is_denoise: bool,
    /// Strength 0...1 (always 0.85 in the author's projects; not adjustable in the UI).
    pub rate: f64,
}

impl RealtimeDenoiseMaterial {
    pub fn from_value(v: &Value) -> Option<RealtimeDenoiseMaterial> {
        let c = object(v)?;
        Some(RealtimeDenoiseMaterial {
            id: c.string("id"),
            is_denoise: c.bool("is_denoise").unwrap_or(false),
            rate: c.num("denoise_rate").unwrap_or(0.85),
        })
    }
}

/// Per-clip "Enhance voice". CapCut caches its result in `production_path` (obfuscated, not read by us).
#[derive(Debug, Clone, PartialEq)]
pub struct VocalBeautifyMaterial {
    pub id: String,
    pub enable: bool,
    /// How much of the non-voice sound is kept, 0...100 (75 by default; 100 on a few clips).
    pub ambient_sound_level: f64,
}

impl VocalBeautifyMaterial {
    pub fn from_value(v: &Value) -> Option<VocalBeautifyMaterial> {
        let c = object(v)?;
        Some(VocalBeautifyMaterial {
            id: c.string("id"),
            enable: c.bool("enable").unwrap_or(false),
            ambient_sound_level: c.num("ambient_sound_level").unwrap_or(75.0),
        })
    }
}

/// Per-clip voice filter ("Voice filters" in CapCut: "Booming", "Old Hollywood", "Live House"…).
/// The effect itself is a processor graph in CapCut's effect cache (see `VoiceFilterGraph`).
#[derive(Debug, Clone, PartialEq)]
pub struct AudioEffectMaterial {
    pub id: String,
    pub name: String,
    /// The cached effect directory (absolute, may be stale).
    pub path: String,
    /// The effect's id in CapCut's library; also the cache folder name (`Cache/effect/<id>/<hash>`).
    pub resource_id: String,
    pub category_name: String,
    /// The "strength" slider, 0...1 (default 1; the only parameter the voice filters expose).
    pub strength: f64,
}

impl AudioEffectMaterial {
    pub fn from_value(v: &Value) -> Option<AudioEffectMaterial> {
        let c = object(v)?;
        // (name, value) of `audio_adjust_params`; `value` falls back to `default_value`.
        let params: Vec<(String, Option<f64>)> = c.list("audio_adjust_params", |p| {
            let p = object(p)?;
            Some((p.string("name"), p.num("value").or_else(|| p.num("default_value"))))
        });
        let strength = params
            .iter()
            .find(|p| p.0 == "strength")
            .and_then(|p| p.1)
            .or_else(|| params.first().and_then(|p| p.1))
            .unwrap_or(1.0);
        Some(AudioEffectMaterial {
            id: c.string("id"),
            name: c.string("name"),
            path: c.string("path"),
            resource_id: c
                .str("resource_id")
                .or_else(|| c.str("third_resource_id"))
                .or_else(|| c.str("effect_id"))
                .unwrap_or("")
                .into(),
            category_name: c.string("category_name"),
            strength,
        })
    }
}

/// A compound clip: a nested draft embedded in the parent draft.
#[derive(Debug, Clone, PartialEq)]
pub struct CombinationMaterial {
    pub id: String,
    pub draft: Option<Box<Draft>>,
}

impl CombinationMaterial {
    pub fn from_value(v: &Value) -> Option<CombinationMaterial> {
        let c = object(v)?;
        Some(CombinationMaterial {
            id: c.string("id"),
            draft: c.opt("draft").and_then(Draft::from_value).map(Box::new),
        })
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct TextMaterial {
    pub id: String,
    /// "text" or "subtitle".
    pub r#type: String,
    /// JSON string: `{"text": "...", "styles": [...]}`.
    pub content: String,
    pub font_path: String,
    pub font_size: f64,
    pub text_color: String,
    pub alignment: i64,
    pub line_spacing: f64,
    pub letter_spacing: f64,
    pub line_max_width: f64,
    pub border_color: String,
    pub border_width: f64,
    pub background_color: String,
    pub background_alpha: f64,
    pub background_round_radius: f64,
    pub background_width: f64,
    pub background_height: f64,
    pub has_shadow: bool,
    pub shadow_color: String,
    pub shadow_alpha: f64,
    pub shadow_smoothing: f64,
    pub shadow_distance: f64,
    pub shadow_angle: f64,
    pub global_alpha: f64,
    pub bold_width: f64,
    pub italic_degree: f64,
    pub underline: bool,
    /// `1` = vertical typesetting (characters stacked in columns, see `VerticalTextLayout`).
    pub typesetting: i64,
    /// "Curve" text (bent along an arc), not supported.
    pub curve_enabled: bool,
}

impl TextMaterial {
    pub fn from_value(v: &Value) -> Option<TextMaterial> {
        let c = object(v)?;
        Some(TextMaterial {
            id: c.string("id"),
            r#type: c.str("type").unwrap_or("text").into(),
            content: c.string("content"),
            font_path: c.string("font_path"),
            font_size: c.num("font_size").unwrap_or(15.0),
            text_color: c.str("text_color").unwrap_or("#FFFFFF").into(),
            alignment: c.num_int("alignment", 1.0),
            line_spacing: c.num("line_spacing").unwrap_or(0.02),
            letter_spacing: c.num("letter_spacing").unwrap_or(0.0),
            line_max_width: c.num("line_max_width").unwrap_or(0.82),
            border_color: c.string("border_color"),
            border_width: c.num("border_width").unwrap_or(0.08),
            background_color: c.string("background_color"),
            background_alpha: c.num("background_alpha").unwrap_or(1.0),
            background_round_radius: c.num("background_round_radius").unwrap_or(0.0),
            background_width: c.num("background_width").unwrap_or(0.14),
            background_height: c.num("background_height").unwrap_or(0.14),
            has_shadow: c.bool("has_shadow").unwrap_or(false),
            shadow_color: c.string("shadow_color"),
            shadow_alpha: c.num("shadow_alpha").unwrap_or(0.9),
            shadow_smoothing: c.num("shadow_smoothing").unwrap_or(0.45),
            shadow_distance: c.num("shadow_distance").unwrap_or(5.0),
            shadow_angle: c.num("shadow_angle").unwrap_or(-45.0),
            global_alpha: c.num("global_alpha").unwrap_or(1.0),
            bold_width: c.num("bold_width").unwrap_or(0.0),
            italic_degree: c.num("italic_degree").unwrap_or(0.0),
            underline: c.bool("underline").unwrap_or(false),
            typesetting: c.num_int("typesetting", 0.0),
            curve_enabled: c.obj("text_curve").and_then(|t| t.bool("enable")).unwrap_or(false),
        })
    }
}

/// The rich-text payload stored as a JSON string in `TextMaterial::content`.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct TextContent {
    pub text: String,
    pub styles: Vec<TextStyle>,
}

impl TextContent {
    pub fn from_value(v: &Value) -> Option<TextContent> {
        let c = object(v)?;
        Some(TextContent { text: c.string("text"), styles: c.list("styles", TextStyle::from_value) })
    }

    pub fn parse(json: &str) -> Option<TextContent> {
        serde_json::from_str::<Value>(json).ok().as_ref().and_then(TextContent::from_value)
    }

    /// The UTF-16 offsets `[start, end)` of a style's run in `text`, clamped to the text; the whole text when the
    /// style has no range. Draft ranges count Unicode code points, so an emoji is one unit.
    pub fn utf16_range(&self, style: Option<&TextStyle>) -> std::ops::Range<usize> {
        let length = self.text.encode_utf16().count();
        let Some(r) = style.map(|s| &s.range).filter(|r| r.len() == 2) else {
            return 0..length;
        };
        // UTF-16 offset of each code point, then the end.
        let mut offsets = Vec::with_capacity(self.text.chars().count() + 1);
        offsets.push(0usize);
        for ch in self.text.chars() {
            offsets.push(offsets[offsets.len() - 1] + ch.len_utf16());
        }
        let clamp = |i: i64| i.clamp(0, offsets.len() as i64 - 1) as usize;
        let lo = offsets[clamp(r[0])];
        let hi = offsets[clamp(r[1])];
        lo..lo.max(hi)
    }
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct TextStyle {
    /// Code-point offsets [start, end) into the text (not UTF-16: an emoji counts as one); see
    /// `TextContent::utf16_range`.
    pub range: Vec<i64>,
    pub size: Option<f64>,
    pub font_path: Option<String>,
    /// RGB in 0...1.
    pub fill_color: Option<Vec<f64>>,
    pub fill_alpha: Option<f64>,
    pub strokes: Vec<TextStroke>,
    pub bold: bool,
    pub italic: bool,
    pub underline: bool,
    /// Set when the text uses a CapCut text-effect package (not supported yet).
    pub effect_style_path: Option<String>,
}

impl TextStyle {
    pub fn from_value(v: &Value) -> Option<TextStyle> {
        let c = object(v)?;
        let mut fill_color = None;
        let mut fill_alpha = None;
        if let Some(fill) = c.obj("fill") {
            fill_alpha = fill.num("alpha");
            if let Some(solid) = fill.obj("content").and_then(|content| content.obj("solid")) {
                fill_color = solid.f64_array("color");
                if let Some(a) = solid.num("alpha") {
                    fill_alpha = Some(fill_alpha.unwrap_or(1.0) * a);
                }
            }
        }
        Some(TextStyle {
            range: c.int_array("range").unwrap_or_default(),
            size: c.num("size"),
            font_path: c.obj("font").and_then(|f| f.str("path")).map(str::to_string),
            fill_color,
            fill_alpha,
            strokes: c.list("strokes", TextStroke::from_value),
            bold: c.bool("bold").unwrap_or(false),
            italic: c.bool("italic").unwrap_or(false),
            underline: c.bool("underline").unwrap_or(false),
            effect_style_path: c.obj("effectStyle").and_then(|e| e.str("path")).map(str::to_string),
        })
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct TextStroke {
    /// Relative to the font size.
    pub width: f64,
    pub color: Vec<f64>,
    pub alpha: f64,
}

impl TextStroke {
    pub fn from_value(v: &Value) -> Option<TextStroke> {
        let c = object(v)?;
        let mut color = vec![0.0, 0.0, 0.0];
        let mut alpha = c.num("alpha").unwrap_or(1.0);
        if let Some(solid) = c.obj("content").and_then(|content| content.obj("solid")) {
            color = solid.f64_array("color").unwrap_or(color);
            if let Some(a) = solid.num("alpha") {
                alpha *= a;
            }
        }
        Some(TextStroke { width: c.num("width").unwrap_or(0.06), color, alpha })
    }
}

/// A clip mask (`materials.common_mask[]`), see FORMAT.md › Masks.
#[derive(Debug, Clone, PartialEq)]
pub struct MaskMaterial {
    pub id: String,
    /// Mask keyframes point at this id (`common_keyframes[].material_id`).
    pub constant_id: String,
    /// "Split", "Mirror", "Circle", "Rectangle", "Heart", "Star", "Brush".
    pub name: String,
    /// `line`, `mirror`, `circle`, `rectangle`, `geometric_shape`, `custom` (brush).
    pub resource_type: String,
    /// The brush strokes' folder, usually `##_draftpath_placeholder_…_##/adjust_mask/<id>`.
    pub loader_work_space: String,
    /// Center in half-material units (+y up), size as fractions of the material's width and height.
    pub center_x: f64,
    pub center_y: f64,
    pub width: f64,
    pub height: f64,
    /// Degrees, clockwise.
    pub rotation: f64,
    /// 0...1.
    pub feather: f64,
    pub expansion: f64,
    /// 0...1 of the shorter side's half.
    pub round_corner: f64,
    pub invert: bool,
}

impl MaskMaterial {
    /// `None` without an `id`.
    pub fn from_value(v: &Value) -> Option<MaskMaterial> {
        let c = object(v)?;
        let id = c.str("id")?.to_string();
        let config = c.obj("config");
        let num = |key: &str, default: f64| config.and_then(|k| k.num(key)).unwrap_or(default);
        Some(MaskMaterial {
            id,
            constant_id: c.string("constant_material_id"),
            name: c.string("name"),
            resource_type: c.string("resource_type"),
            loader_work_space: c.string("loader_work_space"),
            center_x: num("centerX", 0.0),
            center_y: num("centerY", 0.0),
            width: num("width", 0.5),
            height: num("height", 0.5),
            rotation: num("rotation", 0.0),
            feather: num("feather", 0.0),
            expansion: num("expansion", 0.0),
            round_corner: num("roundCorner", 0.0),
            invert: config.and_then(|k| k.bool("invert")).unwrap_or(false),
        })
    }
}

/// `materials.material_animations[]`: the intro / outro / loop animations of a text or sticker clip
/// (`type` "sticker_animation" for both), linked from the segment's `extra_material_refs`.
#[derive(Debug, Clone, PartialEq)]
pub struct AnimationMaterial {
    pub id: String,
    pub animations: Vec<AnimationItem>,
}

impl AnimationMaterial {
    pub fn from_value(v: &Value) -> Option<AnimationMaterial> {
        let c = object(v)?;
        Some(AnimationMaterial { id: c.string("id"), animations: c.list("animations", AnimationItem::from_value) })
    }
}

/// One animation of a `material_animations` entry.
#[derive(Debug, Clone, PartialEq)]
pub struct AnimationItem {
    /// "in", "out" or "loop".
    pub r#type: String,
    /// Display name in the language CapCut used when it was applied ("Fade In", "渐显", …).
    pub name: String,
    /// The preset's id in CapCut's library.
    pub resource_id: String,
    /// µs. For a loop: the length of one cycle.
    pub duration: i64,
}

impl AnimationItem {
    pub fn from_value(v: &Value) -> Option<AnimationItem> {
        let c = object(v)?;
        Some(AnimationItem {
            r#type: c.string("type"),
            name: c.string("name"),
            resource_id: c
                .str("resource_id")
                .or_else(|| c.str("third_resource_id"))
                .or_else(|| c.str("id"))
                .unwrap_or("")
                .into(),
            duration: c.num("duration").unwrap_or(0.0) as i64,
        })
    }
}

/// `materials.video_effects[]` (the port of `VideoEffectMaterial` in `VideoEffect.swift`).
#[derive(Debug, Clone, PartialEq)]
pub struct VideoEffectMaterial {
    pub id: String,
    pub name: String,
    /// The effect's id in CapCut's library (`effect_id` / `resource_id`).
    pub resource_id: String,
    /// 0 = applied to the clip it's attached to (`extra_material_refs`), 2 = an effect-track segment over the frame.
    pub apply_target_type: i64,
    /// The sliders (`adjust_params`): `effects_adjust_speed`, `effects_adjust_size`, … by name.
    pub params: BTreeMap<String, f64>,
}

impl VideoEffectMaterial {
    pub fn from_value(v: &Value) -> Option<VideoEffectMaterial> {
        let c = object(v)?;
        let mut params = BTreeMap::new();
        for (name, value) in c.list("adjust_params", |p| {
            let p = object(p)?;
            Some((p.string("name"), p.num("value").or_else(|| p.num("default_value"))))
        }) {
            if name.is_empty() {
                continue;
            }
            // Like assigning an optional into a Swift dictionary: `nil` removes the entry.
            match value {
                Some(v) => params.insert(name, v),
                None => params.remove(&name),
            };
        }
        Some(VideoEffectMaterial {
            id: c.string("id"),
            name: c.string("name"),
            resource_id: c.str("effect_id").or_else(|| c.str("resource_id")).unwrap_or("").into(),
            apply_target_type: c.num_int("apply_target_type", 0.0),
            params,
        })
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use serde_json::json;

    /// `DraftParsingTests.json` from the Swift tests: a minimal synthetic draft with odd values (nulls, wrong types,
    /// unknown keys) that must not break parsing.
    pub const ODD_DRAFT: &str = r#"
    {
      "id": "D", "name": "Test", "duration": 3000000, "fps": 30.0, "unknown_field": {"x": [1, 2]},
      "canvas_config": {"width": 1080, "height": 1920, "ratio": "9:16", "background": null},
      "config": {"video_mute": false},
      "tracks": [
        {"id": "T1", "type": "video", "attribute": 0, "flag": 0, "segments": [
          {"id": "S1", "material_id": "V1", "source_timerange": {"start": 0, "duration": 6000000},
           "target_timerange": {"start": 0, "duration": 3000000}, "speed": 2.0, "volume": 1.5,
           "clip": {"scale": {"x": 1.2, "y": 1.2}, "rotation": 90, "transform": {"x": 0.1, "y": -0.2},
                    "flip": {"horizontal": true, "vertical": false}, "alpha": 0.5},
           "extra_material_refs": ["SP1", "C1", "TR1"], "render_index": 0,
           "common_keyframes": [{"property_type": "KFTypePositionX", "keyframe_list": [
             {"time_offset": 0, "values": [0.0]}, {"time_offset": 1000000, "values": [0.5]}]}]}
        ]},
        {"id": "T2", "type": "audio", "attribute": 1, "segments": []},
        {"id": "T3", "type": "text", "attribute": 0, "segments": [
          {"id": "S2", "material_id": "X1", "target_timerange": {"start": 0, "duration": 1000000}, "clip": null}
        ]}
      ],
      "materials": {
        "videos": [{"id": "V1", "type": "video", "path": "/nonexistent/clip.mov", "width": 1920, "height": 1080,
                    "crop": {"upper_left_x": 0.1, "upper_left_y": 0.0, "lower_right_x": 0.9, "lower_right_y": 1.0}}],
        "speeds": [{"id": "SP1", "type": "speed", "mode": 0, "speed": 2.0, "curve_speed": null}],
        "canvases": [{"id": "C1", "type": "canvas_blur", "blur": 0.5, "color": ""}],
        "transitions": [{"id": "TR1", "type": "transition", "name": "Sand Spread"}],
        "texts": [{"id": "X1", "type": "subtitle", "font_size": "not a number",
                   "content": "{\"text\":\"Hello\",\"styles\":[{\"range\":[0,5],\"size\":8,\"fill\":{\"content\":{\"solid\":{\"color\":[1,0,0]}}}}]}"}],
        "audios": null
      }
    }
    "#;

    /// Port of `DraftParsingTests.testParsesLeniently`.
    #[test]
    fn parses_leniently() {
        let draft = Draft::parse(ODD_DRAFT).unwrap();
        assert_eq!(draft.canvas.width, 1080);
        assert_eq!(draft.tracks.len(), 3);
        assert!(draft.tracks[1].is_muted());
        let segment = &draft.tracks[0].segments[0];
        assert_eq!(segment.clip.as_ref().unwrap().scale.x, 1.2);
        assert!(segment.clip.as_ref().unwrap().flip_horizontal);
        assert_eq!(segment.keyframes[0].keyframes.len(), 2);
        assert_eq!(draft.materials.videos[0].crop.upper_left.x, 0.1);
        assert_eq!(draft.materials.texts[0].font_size, 15.0, "invalid number falls back to the default");
        let content = TextContent::parse(&draft.materials.texts[0].content).unwrap();
        assert_eq!(content.text, "Hello");
        assert_eq!(content.styles[0].fill_color, Some(vec![1.0, 0.0, 0.0]));
        // More of the same draft.
        assert_eq!(draft.fps, 30.0);
        assert_eq!(draft.canvas.ratio, "9:16");
        assert!(draft.tracks[2].segments[0].clip.is_none());
        assert_eq!(segment.source_timerange, Some(TimeRange::new(0, 6_000_000)));
        assert_eq!(segment.extra_material_refs, ["SP1", "C1", "TR1"]);
        assert!(segment.visible && segment.uniform_scale);
        assert_eq!(draft.materials.speeds[0].curve_points, []);
        assert!(draft.materials.audios.is_empty());
        assert_eq!(draft.materials.all["transitions"][0].name, "Sand Spread");
        assert_eq!(draft.materials.all.len(), 5);
    }

    /// Port of `DraftParsingTests.testStyleRangesCountCodePoints`.
    #[test]
    fn style_ranges_count_code_points() {
        // "руку 😳" is 6 code points but 7 UTF-16 units; the second style covers the emoji only.
        let json = r#"{"text":"руку 😳🦑","styles":[{"range":[0,5]},{"range":[5,7]},{"range":[6,99]}]}"#;
        let content = TextContent::parse(json).unwrap();
        assert_eq!(content.utf16_range(Some(&content.styles[0])), 0..5);
        assert_eq!(content.utf16_range(Some(&content.styles[1])), 5..9);
        assert_eq!(content.utf16_range(Some(&content.styles[2])), 7..9, "clamped to the text");
        assert_eq!(content.utf16_range(None), 0..9);
        assert_eq!(content.utf16_range(Some(&TextStyle { range: vec![3, 1], ..TextStyle::default() })), 3..3);
        assert_eq!(content.utf16_range(Some(&TextStyle { range: vec![-4, 2], ..TextStyle::default() })), 0..2);
    }

    #[test]
    fn root_must_be_an_object() {
        assert!(Draft::parse("[]").is_err());
        assert!(Draft::parse("{").is_err());
        let empty = Draft::parse("{}").unwrap();
        assert_eq!((empty.fps, empty.canvas.width, empty.canvas.height), (30.0, 1920, 1080));
        assert!(empty.tracks.is_empty());
        assert!(TextContent::parse("not json").is_none());
        assert!(TextContent::parse("[1]").is_none());
    }

    #[test]
    fn materials_without_id_are_dropped_where_swift_requires_one() {
        let m = Materials::from_value(&json!({
            "common_mask": [{"id": "M1", "config": {"width": "0.3", "invert": 1}}, {"name": "no id"}],
            "masks": [{"id": "M2"}],
            "effects": [{"type": "filter"}, {"id": "F1", "effect_name": "Warm", "effect_id": "123"}],
            "material_animations": [{"id": "A1", "animations": [{"type": "in", "name": "Fade In", "duration": 2.9}, 5]}]
        }))
        .unwrap();
        assert_eq!(m.masks.len(), 2);
        assert_eq!((m.masks[0].width, m.masks[0].height, m.masks[0].invert), (0.3, 0.5, true));
        assert_eq!(m.all["effects"].len(), 1);
        let f = &m.all["effects"][0];
        assert_eq!((f.name.as_str(), f.resource_id.as_str(), f.effect_id.as_str()), ("Warm", "123", "123"));
        assert_eq!(m.material_animations[0].animations.len(), 1);
        assert_eq!(m.material_animations[0].animations[0].duration, 2, "truncated like Int64(Double)");
        assert_eq!(m.all["material_animations"][0].animation_names, ["Fade In"]);
    }

    #[test]
    fn video_materials_list_their_algorithms() {
        let v = VideoMaterial::from_value(&json!({
            "id": "V", "type": "photo", "has_audio": 0, "stable": {"stable_level": 2.7},
            "video_algorithm": {"algorithms": [{"type": "remove_flicker"}, {}, 3], "super_resolution": {"x": 1},
                                "deflicker": null, "noise_reduction": false},
            "matting": {"flag": 3, "path": "p", "enable_matting_stroke": true}
        }))
        .unwrap();
        assert_eq!(v.algorithms, ["remove_flicker", "unknown", "super_resolution", "noise_reduction"]);
        assert_eq!((v.stable_level, v.has_audio, v.matting.flag, v.matting.stroke), (2, false, 3, true));
        assert!(v.crop.is_identity());
    }

    #[test]
    fn audio_effect_strength_and_video_effect_params() {
        let strength =
            |params: Value| AudioEffectMaterial::from_value(&json!({"audio_adjust_params": params})).unwrap().strength;
        assert_eq!(strength(json!([{"name": "a", "value": 0.2}, {"name": "strength", "value": 0.7}])), 0.7);
        assert_eq!(strength(json!([{"name": "a", "default_value": 0.2}, {"name": "strength"}])), 0.2);
        assert_eq!(strength(json!([])), 1.0);
        let effect = VideoEffectMaterial::from_value(&json!({
            "resource_id": "R", "adjust_params": [{"name": "speed", "value": 0.5}, {"name": "", "value": 1},
                                                  {"name": "size", "value": 0.3}, {"name": "size"}]
        }))
        .unwrap();
        assert_eq!(effect.resource_id, "R");
        assert_eq!(effect.params.len(), 1, "a later param without a value removes the name");
        assert_eq!(effect.params["speed"], 0.5);
    }

    #[test]
    fn compound_clips_hold_a_nested_draft() {
        let m = Materials::from_value(&json!({"drafts": [
            {"id": "C1", "draft": {"duration": 5, "tracks": [{"type": "video"}]}}, {"id": "C2", "draft": "x"}
        ]}))
        .unwrap();
        assert_eq!(m.drafts[0].draft.as_ref().unwrap().tracks.len(), 1);
        assert!(m.drafts[1].draft.is_none());
    }

    #[test]
    fn text_styles_and_strokes() {
        let s = TextStyle::from_value(&json!({
            "range": [0, 2.0], "font": {"path": "/f.ttf"}, "bold": 1,
            "fill": {"alpha": 0.5, "content": {"solid": {"color": [0.1, 0.2, 0.3], "alpha": 0.5}}},
            "strokes": [{"width": 0.1, "alpha": 0.5, "content": {"solid": {"color": [1, 1, 1], "alpha": 0.5}}}, {}],
            "effectStyle": {"path": "/e"}
        }))
        .unwrap();
        assert_eq!(s.range, [0, 2]);
        assert_eq!(s.font_path.as_deref(), Some("/f.ttf"));
        assert_eq!(s.fill_alpha, Some(0.25));
        assert!(s.bold && !s.italic);
        assert_eq!(s.strokes.len(), 2);
        assert_eq!(
            (s.strokes[0].width, s.strokes[0].alpha, s.strokes[0].color.clone()),
            (0.1, 0.25, vec![1.0, 1.0, 1.0])
        );
        assert_eq!((s.strokes[1].width, s.strokes[1].color.clone()), (0.06, vec![0.0, 0.0, 0.0]));
        assert_eq!(s.effect_style_path.as_deref(), Some("/e"));
        let bad_range = TextStyle::from_value(&json!({"range": [0, 1.5]})).unwrap();
        assert!(bad_range.range.is_empty(), "a non-integral range fails like Swift's [Int] decoding");
    }

    #[test]
    fn loudness_gain() {
        let l = |v: Value| LoudnessMaterial::from_value(&v).unwrap().gain_db();
        assert_eq!(l(json!({"enable": true, "target_loudness": -14, "loudness_param": {"avg_loudness": -20}})), 6.0);
        assert_eq!(l(json!({"enable": false, "loudness_param": {"avg_loudness": -20}})), 0.0);
        assert_eq!(l(json!({"enable": true, "loudness_param": {"avg_loudness": -80}})), 0.0);
        assert_eq!(l(json!({"enable": true})), 0.0);
    }
}
