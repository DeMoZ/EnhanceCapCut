//! The platform-independent core of EnhanceCapCut: reading CapCut projects and drafts leniently, building the
//! timeline and evaluating it over time, the `ecc info` report, and (in later stages) subtitles and the draft writers. Pure Rust without FFI knowledge; the apps reach it
//! through the `ecc-ffi` bindings (openspec/changes/rust-core, design decisions 1–2).

pub mod draft;
pub mod env;
pub mod info;
pub mod l10n;
pub mod lenient;
pub mod project;
pub mod timeline;

#[cfg(test)]
pub(crate) mod testing;

pub use env::CoreEnvironment;
pub use info::{format_info, InfoError};
pub use l10n::set_core_language;
pub use project::{CapCutProject, DraftFile, DraftLoader, LoadError, ProjectLocator, ProjectTimeline};
pub use timeline::{Timeline, TimelineBuilder};
