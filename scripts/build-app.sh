#!/bin/zsh
# Builds EnhanceCapCut.app (and the `ecc` CLI) into ./build.
#   scripts/build-app.sh            # build only
#   scripts/build-app.sh --install  # also copy to ~/Applications and register the Services entry
set -euo pipefail

root=${0:A:h:h}
cd "$root"

swift build -c release --product EnhanceCapCut
swift build -c release --product ecc
bin=$(swift build -c release --show-bin-path)

app=build/EnhanceCapCut.app
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$bin/EnhanceCapCut" "$app/Contents/MacOS/EnhanceCapCut"
cp "$bin/ecc" "$app/Contents/MacOS/ecc"
cp App/Info.plist "$app/Contents/Info.plist"
codesign --force --sign - --timestamp=none "$app/Contents/MacOS/ecc"
codesign --force --sign - --timestamp=none "$app"
echo "Built $root/$app"

if [[ "${1:-}" == "--install" ]]; then
  dest=~/Applications
  mkdir -p "$dest"
  rm -rf "$dest/EnhanceCapCut.app"
  cp -R "$app" "$dest/"
  # Register the app and refresh the Services menu.
  /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$dest/EnhanceCapCut.app"
  # Only the installed copy should provide the service.
  /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -u "$root/$app" 2>/dev/null || true
  /System/Library/CoreServices/pbs -update
  echo "Installed $dest/EnhanceCapCut.app"
fi
