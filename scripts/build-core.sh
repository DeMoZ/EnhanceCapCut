#!/bin/zsh
# Builds the Rust core for the Swift package (design decision 8): core/ecc-ffi as a dynamic framework in
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
mkdir -p "$out/gen"

# Library mode reads the UniFFI metadata from the built library (names from ecc-ffi/uniffi.toml); unformatted, so
# the output doesn't depend on whether swiftformat is installed.
cargo run --quiet -p uniffi-bindgen -- generate --no-format --language swift \
  --out-dir "$out/gen" "$target_dir/${targets[1]}/release/libecc_core.dylib" >/dev/null

# A dynamic framework around the cdylib, shipped in Contents/Frameworks like whisper.framework. (A static library in
# the XCFramework links on Xcode 27, but Xcode 26's multi-architecture build can't find it: design decision 8.)
fw=$out/EccCoreFFI.framework
version=$fw/Versions/A
mkdir -p "$version/Headers" "$version/Modules" "$version/Resources"
lipo -create $target_dir/${^targets}/release/libecc_core.dylib -output "$version/EccCoreFFI"
install_name_tool -id @rpath/EccCoreFFI.framework/Versions/A/EccCoreFFI "$version/EccCoreFFI"
cp "$out/gen/EccCoreFFI.h" "$version/Headers/"
cat > "$version/Modules/module.modulemap" <<'MAP'
framework module EccCoreFFI {
    header "EccCoreFFI.h"
    export *
}
MAP
cat > "$version/Resources/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key><string>EccCoreFFI</string>
  <key>CFBundleIdentifier</key><string>com.enhancecapcut.EccCoreFFI</string>
  <key>CFBundleName</key><string>EccCoreFFI</string>
  <key>CFBundlePackageType</key><string>FMWK</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>15.0</string>
</dict>
</plist>
PLIST
ln -s A "$fw/Versions/Current"
for item in EccCoreFFI Headers Modules Resources; do ln -s "Versions/Current/$item" "$fw/$item"; done
xcodebuild -create-xcframework -framework "$fw" -output "$out/EccCoreFFI.xcframework" >/dev/null

swift_out=$root/Sources/EccCore/EccCore.swift
mkdir -p "${swift_out:h}"
cmp -s "$out/gen/EccCore.swift" "$swift_out" || cp "$out/gen/EccCore.swift" "$swift_out"
echo "Rust core: $out/EccCoreFFI.xcframework ($(lipo -archs "$version/EccCoreFFI"), $(du -h "$version/EccCoreFFI" | cut -f1))"
