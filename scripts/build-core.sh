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
for t in $targets; do
  cargo build --quiet --release -p ecc-ffi --target "$t"
done

out=$root/build/core
rm -rf "$out"
mkdir -p "$out/lib" "$out/headers" "$out/gen"
libs=(target/${^targets}/release/libecc_core.a)
lipo -create $libs -output "$out/lib/libecc_core.a"

# Library mode reads the UniFFI metadata from the built library (names from ecc-ffi/uniffi.toml); unformatted, so
# the output doesn't depend on whether swiftformat is installed.
cargo run --quiet -p uniffi-bindgen -- generate --no-format --language swift \
  --out-dir "$out/gen" "target/${targets[1]}/release/libecc_core.dylib" >/dev/null
cp "$out/gen/EccCoreFFI.h" "$out/headers/"
cp "$out/gen/EccCoreFFI.modulemap" "$out/headers/module.modulemap"
xcodebuild -create-xcframework -library "$out/lib/libecc_core.a" -headers "$out/headers" \
  -output "$out/EccCoreFFI.xcframework" >/dev/null

swift_out=$root/Sources/EccCore/EccCore.swift
mkdir -p "${swift_out:h}"
cmp -s "$out/gen/EccCore.swift" "$swift_out" || cp "$out/gen/EccCore.swift" "$swift_out"
echo "Rust core: $out/EccCoreFFI.xcframework ($(lipo -archs "$out/lib/libecc_core.a"), $(du -h "$out/lib/libecc_core.a" | cut -f1))"
