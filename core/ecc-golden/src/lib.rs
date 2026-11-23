//! The Rust side of the golden-parity runner (design decision 11): the canonical dumps (`dump`) and the synthetic
//! fixture projects (`fixtures`). The `ecc-golden` binary prints them; `tests/golden.rs` checks them against the
//! committed expected outputs.

pub mod dump;
pub mod fixtures;
