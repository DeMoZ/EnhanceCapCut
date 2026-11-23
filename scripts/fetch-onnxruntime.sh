#!/bin/zsh
# Puts ONNX Runtime for voice cloning into build/onnxruntime: libonnxruntime.dylib (Apple silicon) with its LICENSE and
# ThirdPartyNotices.txt, taken from Microsoft's NuGet package Microsoft.ML.OnnxRuntime, the same version as the
# Windows version (windows/src/EnhanceCapCut.Subtitles). Run by scripts/build-app.sh; `swift run ecc clone` in a
# checkout finds the library there too (OnnxRuntime.swift). Microsoft ships no Intel macOS build since 1.21.
set -euo pipefail

version=1.31.0
sha256=7e35fdb4f5adb62f01e60798e2e5ed05108f5fbbe1cca03fbcc37903cf7c5e3b

root=${0:A:h:h}
dest=$root/build/onnxruntime
[[ -f "$dest/libonnxruntime.dylib" && "$(cat "$dest/VERSION" 2>/dev/null)" == "$version" ]] && exit 0

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
url=https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime/$version/microsoft.ml.onnxruntime.$version.nupkg
echo "Downloading ONNX Runtime $version from NuGet"
curl -fsSL --retry 3 -o "$work/ort.nupkg" "$url"
echo "$sha256  $work/ort.nupkg" | shasum -a 256 -c --status || { echo "ONNX Runtime package checksum mismatch" >&2; exit 1; }
unzip -q -o "$work/ort.nupkg" runtimes/osx-arm64/native/libonnxruntime.dylib LICENSE ThirdPartyNotices.txt -d "$work/x"

rm -rf "$dest"
mkdir -p "$dest"
mv "$work/x/runtimes/osx-arm64/native/libonnxruntime.dylib" "$work/x/LICENSE" "$work/x/ThirdPartyNotices.txt" "$dest/"
echo "$version" > "$dest/VERSION"
echo "ONNX Runtime $version in $dest"
