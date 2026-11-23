//! The platform-independent core of EnhanceCapCut: reading CapCut projects and drafts leniently, and (in later
//! stages) building the timeline, subtitles and the draft writers. Pure Rust without FFI knowledge; the apps reach it
//! through the `ecc-ffi` bindings (openspec/changes/rust-core, design decisions 1–2).

pub mod draft;
pub mod env;
pub mod l10n;
pub mod lenient;
pub mod project;

#[cfg(test)]
pub(crate) mod testing;

pub use env::CoreEnvironment;
pub use l10n::set_core_language;
pub use project::{CapCutProject, DraftFile, DraftLoader, LoadError, ProjectLocator, ProjectTimeline};
