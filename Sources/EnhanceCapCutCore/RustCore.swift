// The Rust core's generated bindings (Sources/EccCore, scripts/build-core.sh), re-exported so the app and the CLI
// need no import of their own (design decision 4). Until the Swift code moves to the core (openspec/changes/rust-core,
// stage 3), this module's own types shadow the same-named ones of EccCore.
@_exported import EccCore
