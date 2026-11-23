//! The folders the core reads and writes, as plain inputs (design decision 1). Every field has a per-OS default;
//! the apps, the CLIs and the tests may override any of them (the golden tests always do).

use std::path::PathBuf;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CoreEnvironment {
    /// CapCut's drafts folder: `…/User Data/Projects/com.lveditor.draft`.
    pub projects_root: PathBuf,
    /// Roots of CapCut's effect cache (`…/User Data/Cache/effect`), searched in order.
    pub capcut_cache_roots: Vec<PathBuf>,
    /// Where the draft writers keep backups of the drafts they change.
    pub backup_root: PathBuf,
}

/// The user's home folder (`HOME`, or `USERPROFILE` on Windows).
pub fn home_dir() -> PathBuf {
    let var = if cfg!(windows) { "USERPROFILE" } else { "HOME" };
    std::env::var_os(var).or_else(|| std::env::var_os("HOME")).map(PathBuf::from).unwrap_or_default()
}

/// `%LOCALAPPDATA%` (Windows), falling back to `<home>\AppData\Local`.
#[cfg(windows)]
fn local_app_data() -> PathBuf {
    std::env::var_os("LOCALAPPDATA").map(PathBuf::from).unwrap_or_else(|| home_dir().join("AppData").join("Local"))
}

impl Default for CoreEnvironment {
    fn default() -> Self {
        CoreEnvironment::for_home(&home_dir())
    }
}

impl CoreEnvironment {
    /// The defaults on Windows: everything under `%LOCALAPPDATA%` (CapCut mirrors the Mac layout there).
    #[cfg(windows)]
    pub fn for_home(_home: &std::path::Path) -> CoreEnvironment {
        let local = local_app_data();
        let capcut = local.join("CapCut").join("User Data");
        CoreEnvironment {
            projects_root: capcut.join("Projects").join("com.lveditor.draft"),
            capcut_cache_roots: vec![capcut.join("Cache").join("effect")],
            backup_root: local.join("EnhanceCapCut").join("Backups"),
        }
    }

    /// The defaults on macOS (and other Unix systems, where only the tests run): `~/Movies/CapCut`, the sandboxed
    /// App Store app's container first for the effect cache, and our own Application Support folder.
    #[cfg(not(windows))]
    pub fn for_home(home: &std::path::Path) -> CoreEnvironment {
        CoreEnvironment {
            projects_root: home.join("Movies/CapCut/User Data/Projects/com.lveditor.draft"),
            capcut_cache_roots: vec![
                home.join("Library/Containers/com.lemon.lvoverseas/Data/Movies/CapCut/User Data/Cache/effect"),
                home.join("Movies/CapCut/User Data/Cache/effect"),
            ],
            backup_root: home.join("Library/Application Support/EnhanceCapCut/Backups"),
        }
    }
}

#[cfg(all(test, not(windows)))]
mod tests {
    use super::*;

    #[test]
    fn mac_defaults_are_under_home() {
        let env = CoreEnvironment::for_home(std::path::Path::new("/Users/me"));
        assert_eq!(env.projects_root, PathBuf::from("/Users/me/Movies/CapCut/User Data/Projects/com.lveditor.draft"));
        assert_eq!(env.capcut_cache_roots.len(), 2);
        assert_eq!(env.backup_root, PathBuf::from("/Users/me/Library/Application Support/EnhanceCapCut/Backups"));
    }
}
