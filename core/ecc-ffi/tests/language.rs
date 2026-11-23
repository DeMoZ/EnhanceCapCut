//! The core's language is global, so it is switched in a test binary of its own.

use ecc_core::*;

#[test]
fn texts_follow_the_core_language() {
    assert_eq!(original_audio_title(OriginalAudio::Keep), "Keep", "English until an app picks a language");
    set_core_language("ru".into());
    assert_eq!(original_audio_title(OriginalAudio::Keep), "Оставить");
    assert_eq!(original_audio_raw_value(OriginalAudio::Mute), "Mute all", "raw values stay English");
    set_core_language("en".into());
    assert_eq!(original_audio_title(OriginalAudio::Mute), "Mute all");
}
