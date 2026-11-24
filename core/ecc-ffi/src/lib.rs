//! The UniFFI surface of the core (design decision 4): the only API the macOS and Windows apps see. A thin mapping of
//! `ecc` (pure Rust, no FFI): records and enums for the static data (`types`), objects for the built timeline and its
//! curves (`timeline`), projects (`project`), writers and captions (`writers`), and one error type.
//!
//! Swift bindings: `scripts/build-core.sh` (module `EccCore`); C#: `uniffi-bindgen-cs` (namespace
//! `EnhanceCapCut.Core`); names in `uniffi.toml`.

use std::panic::{catch_unwind, AssertUnwindSafe};

mod convert;
pub mod looks;
pub mod project;
pub mod timeline;
pub mod types;
pub mod writers;

pub use looks::*;
pub use project::*;
pub use timeline::*;
pub use types::*;
pub use writers::*;

uniffi::setup_scaffolding!();

/// Every failure, flat on the other side: each case carries its localized text as `message` (Swift
/// `CoreError.ProjectOpen(message:)`, C# `CoreException.ProjectOpen` with `.Message`), design decision 4.
#[derive(Debug, PartialEq, Eq, uniffi::Error)]
#[uniffi(flat_error)]
pub enum CoreError {
    /// The project is open in CapCut, which would overwrite our changes when it saves.
    ProjectOpen(String),
    /// The project keeps a single timeline (an older CapCut), so a dubbed timeline can't be added.
    NoTimelineList(String),
    /// No such project or timeline (the `ecc info` arguments).
    NotFound(String),
    /// A draft that can't be read or parsed.
    Unreadable(String),
    /// A draft whose JSON is broken or lacks what a writer needs.
    Malformed(String),
    Io(String),
    /// A Rust panic caught at the boundary, with its message.
    Internal(String),
}

impl std::fmt::Display for CoreError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            CoreError::ProjectOpen(m)
            | CoreError::NoTimelineList(m)
            | CoreError::NotFound(m)
            | CoreError::Unreadable(m)
            | CoreError::Malformed(m)
            | CoreError::Io(m)
            | CoreError::Internal(m) => f.write_str(m),
        }
    }
}

impl std::error::Error for CoreError {}

impl From<ecc::LoadError> for CoreError {
    fn from(e: ecc::LoadError) -> Self {
        CoreError::Unreadable(e.to_string())
    }
}

impl From<ecc::InfoError> for CoreError {
    fn from(e: ecc::InfoError) -> Self {
        match e {
            ecc::InfoError::Load(e) => e.into(),
            other => CoreError::NotFound(other.to_string()),
        }
    }
}

impl From<ecc::writers::WriteError> for CoreError {
    fn from(e: ecc::writers::WriteError) -> Self {
        use ecc::writers::WriteError as W;
        let message = e.to_string();
        match e {
            W::ProjectOpen(_) => CoreError::ProjectOpen(message),
            W::NoTimelineList(_) => CoreError::NoTimelineList(message),
            W::NoTracks | W::Malformed(_) => CoreError::Malformed(message),
            W::Io(_) => CoreError::Io(message),
        }
    }
}

/// Runs `f`, turning a panic into `CoreError::Internal` (the release profile keeps `panic = "unwind"`), so a bug in
/// the core fails one call instead of the app.
pub(crate) fn guarded<T>(f: impl FnOnce() -> Result<T, CoreError>) -> Result<T, CoreError> {
    catch_unwind(AssertUnwindSafe(f)).unwrap_or_else(|payload| {
        let message = payload
            .downcast_ref::<&str>()
            .map(|s| s.to_string())
            .or_else(|| payload.downcast_ref::<String>().cloned())
            .unwrap_or_else(|| "panic".into());
        Err(CoreError::Internal(message))
    })
}

/// Panics, so the tests (and the bindings' smoke tests) can check that a panic arrives as `CoreError.Internal`.
#[uniffi::export]
pub fn debug_panic(message: String) -> Result<(), CoreError> {
    guarded(|| panic!("{message}"))
}
