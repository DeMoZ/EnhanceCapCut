# core — the shared Rust core

The platform-independent core of EnhanceCapCut, being moved from the duplicated Swift (`Sources/EnhanceCapCutCore`)
and C# (`windows/src/EnhanceCapCut.Draft`) code into one Rust crate. The plan, its stages and the decisions are in
[`openspec/changes/rust-core`](../openspec/changes/rust-core). Until the switch stages, the apps don't use it yet.

- `ecc-core` (library `ecc`): pure Rust, no FFI. Lenient draft models (`draft::model`, read like `Lenient.swift`),
  byte-preserving JSON editing (`draft::json`), projects and timelines on disk (`project`), per-OS folders
  (`env::CoreEnvironment`), and the core's texts (`tr!` over `Localization/ru.json`).
- `ecc-golden`: the Rust side of the golden-parity runner (grows with stage 1).

```bash
cd core
cargo test                               # everything
cargo clippy --all-targets -- -D warnings
cargo fmt
```

The toolchain is pinned in `rust-toolchain.toml` at the repo root; `rustup` installs it on first use.
