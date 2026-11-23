# Known differences

What the golden-parity runner (`scripts/golden.sh`, design decision 11 of `openspec/changes/rust-core`) accepts
between the Rust core and the two native implementations. Where Swift and C# disagree, Swift is the reference; a C#
difference stays listed here until the Windows switch replaces that code with the core.

Lines starting with `accepted:` are read by `golden.sh fixtures`: `accepted: <cli> <fixture> <command>` counts that
difference as known instead of failing.

## Everywhere (normalized by `golden.sh local`)

- **Unicode normalization of file names.** Swift's `URL.lastPathComponent` and `URL.path` print decomposed names
  (NFD, `и` + U+0306); the Rust core and C# print them as the draft stores them (usually NFC). After the macOS switch
  `ecc info` prints the draft's form. `golden.sh local` compares after NFC normalization.
- **Symlinked project folders.** Swift's `FileManager.contentsOfDirectory(at:)` returns real paths, so the cutout
  mask folder of a project reached through a symlink prints with its real path; the Rust core and C# keep the path as
  given. Only the dump differs; it is the same folder. `golden.sh local` maps both forms to `<project>`.

## Not in the dumps

- **Line wrapping of scripts without spaces** (Thai, Lao, Khmer, Burmese, Chinese, Japanese): the core uses ICU4X
  (design decision 7) instead of `NLTokenizer` (macOS) and grapheme breaks (Windows). Latin and Cyrillic wrap the same
  as Swift. The writers in the dumps get ready-made cues, so wrapping isn't compared.
- **Arabic caption language** (C#): Swift and the core write `ar-EG` (Foundation's likely region), C# `ar-SA`.

## C# writers (until the Windows switch)

- **A copied caption segment with `"clip": null`**: Swift and the core copy the `null` (the template's clip, as it
  is); C# keeps the default clip, treating `null` as absent.
- **The size of a copied caption style**: Swift and the core write it as a double (`"size":8.0`), C# as the draft had
  it (`"size":8`).

accepted: csharp captions dump-writers
accepted: csharp odd-values dump-writers
