//! Helpers for the crate's tests.

use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::{Mutex, MutexGuard};

/// A fresh, empty folder under the system temp folder, removed on drop.
pub struct TempDir(PathBuf);

impl TempDir {
    pub fn new() -> TempDir {
        static N: AtomicU32 = AtomicU32::new(0);
        let n = N.fetch_add(1, Ordering::Relaxed);
        let path = std::env::temp_dir().join(format!("ecc-core-test-{}-{n}", std::process::id()));
        let _ = std::fs::remove_dir_all(&path);
        std::fs::create_dir_all(&path).expect("temp dir");
        TempDir(path)
    }

    pub fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

/// The core's language is process-wide: tests that switch it, or check translated text, hold this lock.
static LANGUAGE_LOCK: Mutex<()> = Mutex::new(());

/// Locks the language for the test and sets it to `code` ("en", "ru").
pub fn language(code: &str) -> MutexGuard<'static, ()> {
    let guard = LANGUAGE_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    crate::l10n::set_core_language(code);
    guard
}

/// A path for a JSON string literal in a test draft: forward slashes (Windows accepts them; backslashes would be
/// JSON escapes).
pub fn slash(path: &Path) -> String {
    path.to_string_lossy().replace('\\', "/")
}
