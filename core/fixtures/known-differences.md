# Known differences

What the golden-parity runner (`scripts/golden.sh`, design decision 11 of `openspec/changes/rust-core`) accepts
between the Rust core and the native CLIs. Both apps run on the core since stages 3 (macOS) and 4 (Windows), so both
CLIs print the core's `ecc info` and the runner expects them to match it byte for byte; what is left here are the
differences of the replaced Swift code that `golden.sh local` still normalizes, and one that no dump shows.

Lines starting with `accepted: <cli> <fixture> info` are read by `golden.sh fixtures` and count that difference as
known instead of failing. There are none.

## Everywhere (normalized by `golden.sh local`)

- **Unicode normalization of file names.** Swift's `URL.lastPathComponent` and `URL.path` print decomposed names
  (NFD, `и` + U+0306); the Rust core prints them as the draft stores them (usually NFC). Since the macOS switch
  `ecc info` prints the draft's form. `golden.sh local` compares after NFC normalization.
- **Symlinked project folders.** Swift's `FileManager.contentsOfDirectory(at:)` returns real paths, so the cutout
  mask folder of a project reached through a symlink printed with its real path; the Rust core keeps the path as
  given. It is the same folder. `golden.sh local` maps both forms to `<project>`.

## Not in the outputs

- **Line wrapping of scripts without spaces** (Thai, Lao, Khmer, Burmese, Chinese, Japanese): the macOS app gives the
  core `NLTokenizer`'s word breaks (design decision 7, option b); Windows, the core's tests and the golden runner use
  ICU4X. Latin and Cyrillic wrap the same everywhere. The writers in the dumps get ready-made cues, so wrapping isn't
  compared.
