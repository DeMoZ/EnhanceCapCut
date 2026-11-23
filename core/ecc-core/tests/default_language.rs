//! Its own test binary (a fresh process), so nothing has switched the language yet.

#[test]
fn the_core_is_english_until_a_host_switches_it() {
    assert_eq!(ecc::l10n::core_language(), ecc::l10n::Language::English);
    assert_eq!(ecc::tr!("Cannot read {0}: {1}", "/a", "b"), "Cannot read /a: b");
}
