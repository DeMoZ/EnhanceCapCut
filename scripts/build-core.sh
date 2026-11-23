#!/bin/zsh
# Builds the Rust core for the Swift package (design decision 8): core/ecc-ffi as a static library in
# build/core/EccCoreFFI.xcframework (with the C header and module map), and its Swift bindings in
# Sources/EccCore/EccCore.swift (committed; rewritten only when they change). Run it before `swift build`.
#   scripts/build-core.sh              # this Mac's architecture
#   scripts/build-core.sh --universal  # arm64 + x86_64, for the release .pkg
set -euo pipefail

root=${0:A:h:h}
cd "$root/core"

if [[ "${1:-}" == "--universal" ]]; then
  targets=(aarch64-apple-darwin x86_64-apple-darwin)
else
  [[ "$(uname -m)" == "arm64" ]] && targets=(aarch64-apple-darwin) || targets=(x86_64-apple-darwin)
fi

# The toolchain comes from rust-toolchain.toml; the Apple targets it lacks are added once.
installed=$(rustup target list --installed)
for t in $targets; do
  grep -qx "$t" <<<"$installed" || rustup target add "$t"
done

export MACOSX_DEPLOYMENT_TARGET=15.0
# cargo's output folder: core/target, or a shared one (CARGO_TARGET_DIR, docs/DEVELOPMENT.md).
target_dir=${CARGO_TARGET_DIR:-$root/core/target}
for t in $targets; do
  cargo build --quiet --release -p ecc-ffi --target "$t"
done

out=$root/build/core
rm -rf "$out"
mkdir -p "$out/lib" "$out/headers" "$out/gen"
libs=($target_dir/${^targets}/release/libecc_core.a)
# Named after the XCFramework: Xcode 26's multi-architecture build (swift build --arch arm64 --arch x86_64) only
# finds a static library whose name matches it.
lipo -create $libs -output "$out/lib/libEccCoreFFI.a"

# Library mode reads the UniFFI metadata from the built library (names from ecc-ffi/uniffi.toml); unformatted, so
# the output doesn't depend on whether swiftformat is installed.
cargo run --quiet -p uniffi-bindgen -- generate --no-format --language swift \
  --out-dir "$out/gen" "$target_dir/${targets[1]}/release/libecc_core.dylib" >/dev/null
cp "$out/gen/EccCoreFFI.h" "$out/headers/"
cp "$out/gen/EccCoreFFI.modulemap" "$out/headers/module.modulemap"
xcodebuild -create-xcframework -library "$out/lib/libEccCoreFFI.a" -headers "$out/headers" \
  -output "$out/EccCoreFFI.xcframework" >/dev/null

swift_out=$root/Sources/EccCore/EccCore.swift
mkdir -p "${swift_out:h}"
cmp -s "$out/gen/EccCore.swift" "$swift_out" || cp "$out/gen/EccCore.swift" "$swift_out"
echo "Rust core: $out/EccCoreFFI.xcframework ($(lipo -archs "$out/lib/libEccCoreFFI.a"), $(du -h "$out/lib/libEccCoreFFI.a" | cut -f1))"
