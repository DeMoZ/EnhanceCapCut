#!/bin/bash
# Regenerates the committed C# bindings of the Rust core, windows/src/EnhanceCapCut.Draft/Generated/ecc_core.cs, from
# core/ecc-ffi (design decision 3). Run it after changing the exported API; CI fails when they are out of date.
# Needs the pinned generator, installed once with:
#   cargo install uniffi-bindgen-cs --git https://github.com/NordSecurity/uniffi-bindgen-cs --tag v0.12.0+v0.32.0 --locked
set -euo pipefail
cd "$(dirname "$0")/../core"
cargo build --quiet --release -p ecc-ffi
target=${CARGO_TARGET_DIR:-target}
case "$(uname -s)" in
    Darwin) lib=$target/release/libecc_core.dylib ;;
    MINGW*|MSYS*|CYGWIN*) lib=$target/release/ecc_core.dll ;;
    *) lib=$target/release/libecc_core.so ;;
esac
# Unformatted, so the output doesn't depend on whether CSharpier is installed.
uniffi-bindgen-cs --library "$lib" --config ecc-ffi/uniffi.toml --no-format \
    --out-dir ../windows/src/EnhanceCapCut.Draft/Generated >/dev/null
echo "C# bindings: windows/src/EnhanceCapCut.Draft/Generated/ecc_core.cs"
