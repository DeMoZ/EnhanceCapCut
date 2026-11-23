# Spec Delta

## Purpose

Turns timed words into caption cues and writes captions, voice-over and dubbed timelines into CapCut projects,
with one behaviour on macOS and Windows and without disturbing anything else in the draft.

## ADDED Requirements

### Requirement: Caption segmentation
Timed words SHALL be grouped into cues of at most two lines and six seconds, broken at the best pause or sentence
end, with every cue at least one second long where the speech allows it. The same words and language SHALL give
the same cues on both platforms.

#### Scenario: Long sentence
- **WHEN** a 14-second sentence with a 0.9 s pause in the middle is segmented
- **THEN** it is split at that pause into cues no longer than six seconds each

#### Scenario: Same cues on both platforms
- **WHEN** the macOS and Windows apps segment the same recognized words for the same language
- **THEN** they produce the same cues with the same times and texts

### Requirement: Line wrapping for scripts without spaces
Cue text in a no-space script (Thai, Lao, Khmer, Burmese, Chinese, Japanese) SHALL break only between words found
by a dictionary-based word segmenter, never inside a word, a grapheme cluster or a number, with the same breaks on
both platforms.

#### Scenario: Thai caption
- **WHEN** a Thai cue longer than the line limit is wrapped
- **THEN** each line break falls between two Thai words, and the same breaks appear on macOS and Windows

#### Scenario: Number before a word
- **WHEN** a Chinese cue contains "2024年"
- **THEN** "2024" stays on the same line as the word after it

### Requirement: SRT output
Caption tracks SHALL be written as SRT files with 1-based numbering and `HH:MM:SS,mmm` stamps rounded to the
nearest millisecond, one file per language.

#### Scenario: Stamp rounding
- **WHEN** a cue starts at 1.2346 s
- **THEN** its stamp reads `00:00:01,235`

### Requirement: Byte-preserving draft edits
Writing captions or voice-over into a draft SHALL change only the tracks, materials and fields it owns; every
other byte of the draft (key order, number formatting, unknown fields) SHALL stay as CapCut wrote it.

#### Scenario: Unknown fields survive
- **WHEN** captions are written into a draft that has fields we do not model
- **THEN** those fields and their formatting are unchanged in the output

#### Scenario: Same edit on both platforms
- **WHEN** the same tracks are written into the same draft on macOS and on Windows with the same ids and time
- **THEN** the two output drafts are byte-identical

### Requirement: Replacing our own tracks
Writing SHALL recognise the caption and voice-over tracks it wrote before (by their track name prefix and id
prefix) and, when asked to replace them, remove them and their materials before adding the new ones; tracks the
user made SHALL never be removed.

#### Scenario: Second run
- **WHEN** captions are written twice into the same timeline with "replace previous" on
- **THEN** the draft holds only the second run's caption tracks and the user's own text tracks are untouched

### Requirement: Voice-over and original audio
Voice-over tracks SHALL be added as audio tracks, and the original audio SHALL be kept, lowered, muted, replaced
by its background track, or given CapCut's own "Remove vocal" under the voice-over, as chosen, with fades around
each voice-over line when it is lowered.

#### Scenario: Lowered original
- **WHEN** a voice-over is written with the original audio "lower"
- **THEN** the original clips are ducked under each voice-over line with a 250 ms fade down and 400 ms fade up

### Requirement: Dubbed timeline
A dubbed copy SHALL be added to the project as a new timeline, with a unique name built from the project name,
the voice-over languages and the original-audio choice, registered in the project's timeline list and layout.

#### Scenario: Name taken
- **WHEN** a timeline named `Trip_dub_en_lower_1` already exists and the same dub is created again
- **THEN** the new timeline is named `Trip_dub_en_lower_2`

### Requirement: Backups and open projects
Before any project file is rewritten its previous content SHALL be saved to the app's backup folder, and nothing
SHALL be written while the project is open in a running CapCut.

#### Scenario: Project open in CapCut
- **WHEN** captions are written into a project that CapCut has open
- **THEN** the write is refused with an error naming the project, and no file changes

#### Scenario: Backup
- **WHEN** captions are written into a timeline
- **THEN** every rewritten draft file has its previous content in a new, time-stamped backup folder
