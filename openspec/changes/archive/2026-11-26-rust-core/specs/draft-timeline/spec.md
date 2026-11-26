# Spec Delta

## Purpose

Reads CapCut projects and drafts, resolves them into the timeline both renderers draw, reports what is not
rendered, and prints the `ecc info` report — with one behaviour on macOS and Windows.

## ADDED Requirements

### Requirement: Lenient draft reading
Reading a draft SHALL NOT fail because of a field's content: an unknown, missing, `null` or mistyped field
yields "no value" for that field only, and a malformed element of a list is skipped while the rest of the list
is kept. Only text that is not JSON at all fails the draft, with an error.

#### Scenario: Mistyped field
- **WHEN** a segment's `volume` is the string `"0.5"` and another segment's `volume` is `true`
- **THEN** the first segment's volume reads as 0.5, the second as unset, and the draft loads

#### Scenario: Malformed list element
- **WHEN** one element of `materials.videos` is a number instead of an object
- **THEN** that element is skipped and every other video material is read

#### Scenario: Not JSON
- **WHEN** the draft file is truncated in the middle of an object
- **THEN** loading fails with an error naming the draft, and no timeline is built

### Requirement: Project and timeline lookup
The core SHALL list CapCut projects of the platform's project root (or a root given explicitly), with each
project's timelines, and SHALL find a timeline's draft under both file names CapCut uses (`draft_info.json` on
macOS, `draft_content.json` on Windows).

#### Scenario: Windows draft read on a Mac
- **WHEN** a project folder holds only `draft_content.json`
- **THEN** its main timeline loads from that file on either platform

#### Scenario: Several timelines
- **WHEN** a project has a main timeline and two more in `Timelines/project.json`
- **THEN** all three are listed with their names, the main one marked, and each can be selected by name or
  1-based number

### Requirement: Timeline building
Building a timeline SHALL produce the canvas size, frame rate, duration, visual layers in paint order (bottom to
top, with track and render indices), audio clips and filter layers, with nested drafts of compound clips built
as nested timelines.

#### Scenario: Compound clip
- **WHEN** a video segment refers to a combination material
- **THEN** its layer's content is the nested draft's timeline, and the nested audio is mixed into the outer
  timeline's audio clips

#### Scenario: Same result on both platforms
- **WHEN** the same project is built by the macOS app and by the Windows app
- **THEN** both get timelines with the same layers, clips, values and report

### Requirement: Report of what is not rendered
Every CapCut feature present in a timeline that the renderers do not reproduce SHALL be listed in the report as
(category, name, count), and every referenced media file that does not exist on disk SHALL be listed as missing.
Features that are rendered SHALL NOT be listed.

#### Scenario: Unsupported transition
- **WHEN** two clips are joined by a transition we only approximate
- **THEN** the report lists it under "Transition" as "<name> (shown as a cross-fade)" with its count

#### Scenario: Missing media
- **WHEN** a video material's path points to a file that was deleted
- **THEN** the path is listed under missing media and the layer renders as empty

### Requirement: Report texts in the interface language
The report's categories and names that are our own texts SHALL be shown in the interface language chosen by the
app, using the shared translation table, and SHALL stay English for the command-line tools.

#### Scenario: Russian interface
- **WHEN** the app runs in Russian and a timeline has an unsupported sticker
- **THEN** the report shows the Russian category for "Sticker" and the sticker's own name unchanged

#### Scenario: CLI
- **WHEN** `ecc info` runs on a system whose preferred language is Russian
- **THEN** the report is printed in English

### Requirement: Evaluation over time
For any timeline time the core SHALL give each active layer's transform (keyframes on the clip's source time axis
with their easing curves, clip animations applied), its text animation, mask, transition and effect states, and
each audio clip's source time and volume envelope.

#### Scenario: Eased keyframes
- **WHEN** a layer has two position keyframes with bezier handles and the clip plays at 2× speed
- **THEN** the position at each time follows the bezier curve evaluated at the clip's source time

#### Scenario: Same values on both platforms
- **WHEN** the macOS and Windows renderers evaluate the same layer at the same time
- **THEN** they get the same transform and animation values

### Requirement: Info report
`ecc info` SHALL print the timeline list (when there are several), project name, canvas, frame rate, duration,
one line per visual layer and per audio clip, the unsupported features and the missing media, and its output
SHALL be byte-identical on macOS and Windows for the same project.

#### Scenario: Identical output
- **WHEN** `ecc info <project>` runs on macOS and on Windows over the same project files
- **THEN** both print the same text, byte for byte
