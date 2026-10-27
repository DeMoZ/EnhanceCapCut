#!/bin/zsh
# Builds EnhanceCapCut.app (and the `ecc` CLI) into ./build.
#   scripts/build-app.sh            # build only (this Mac's architecture)
#   scripts/build-app.sh --install  # also copy to ~/Applications and register the Services entry
#   scripts/build-app.sh --pkg      # universal build + build/EnhanceCapCut-<version>.pkg installer for releases
#
# The version comes from App/Info.plist (CFBundleShortVersionString / CFBundleVersion).
# Optional signing for distribution (otherwise everything is ad-hoc signed):
#   DEV_ID_APP="Developer ID Application: Company (TEAMID)"
#   DEV_ID_INSTALLER="Developer ID Installer: Company (TEAMID)"
#   NOTARY_PROFILE="profile-name"   # created with: xcrun notarytool store-credentials
set -euo pipefail

root=${0:A:h:h}
cd "$root"

mode=${1:-}
DEV_ID_APP=${DEV_ID_APP:-}
DEV_ID_INSTALLER=${DEV_ID_INSTALLER:-}
NOTARY_PROFILE=${NOTARY_PROFILE:-}
version=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" App/Info.plist)
bundle_id=$(/usr/libexec/PlistBuddy -c "Print :CFBundleIdentifier" App/Info.plist)

# Releases run on Apple Silicon and Intel Macs.
arch_flags=()
[[ "$mode" == "--pkg" ]] && arch_flags=(--arch arm64 --arch x86_64)

# The CLI first: building a product prunes frameworks only the other one links (Sparkle is the app's).
swift build -c release "${arch_flags[@]}" --product ecc
swift build -c release "${arch_flags[@]}" --product EnhanceCapCut
bin=$(swift build -c release "${arch_flags[@]}" --show-bin-path)

sign() { # sign <path> [extra codesign options]
  if [[ -n "$DEV_ID_APP" ]]; then
    codesign --force --sign "$DEV_ID_APP" --options runtime --timestamp "${@:2}" "$1"
  else
    codesign --force --sign - --timestamp=none "${@:2}" "$1"
  fi
}

app=build/EnhanceCapCut.app
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$bin/EnhanceCapCut" "$app/Contents/MacOS/EnhanceCapCut"
cp "$bin/ecc" "$app/Contents/MacOS/ecc"
# whisper.cpp (speech recognition) is a dynamic framework: ship it and let both binaries find it.
mkdir -p "$app/Contents/Frameworks"
ditto "$bin/whisper.framework" "$app/Contents/Frameworks/whisper.framework"
# Sparkle (app updates), signed inside out as its documentation lists.
ditto "$bin/Sparkle.framework" "$app/Contents/Frameworks/Sparkle.framework"
for exe in "$app/Contents/MacOS/EnhanceCapCut" "$app/Contents/MacOS/ecc"; do
  install_name_tool -add_rpath @executable_path/../Frameworks "$exe" 2>/dev/null || true
done
cp App/Info.plist "$app/Contents/Info.plist"
cp App/AppIcon.icns "$app/Contents/Resources/" # made by scripts/make-icon.swift
# Siri voice helper, run by the app through Xcode's Swift interpreter.
cp Tools/SiriVoice/siri-voice.swift "$app/Contents/Resources/"
# Interface translations (Localization.swift), shared with the Windows version.
cp -R Localization "$app/Contents/Resources/"
sign "$app/Contents/Frameworks/whisper.framework"
sparkle="$app/Contents/Frameworks/Sparkle.framework/Versions/B"
sign "$sparkle/XPCServices/Installer.xpc"
sign "$sparkle/XPCServices/Downloader.xpc" --preserve-metadata=entitlements
sign "$sparkle/Autoupdate"
sign "$sparkle/Updater.app"
sign "$app/Contents/Frameworks/Sparkle.framework"
sign "$app/Contents/MacOS/ecc"
sign "$app"
echo "Built $root/$app ($version)"

lsregister=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister

if [[ "$mode" == "--install" ]]; then
  dest=~/Applications
  mkdir -p "$dest"
  rm -rf "$dest/EnhanceCapCut.app"
  cp -R "$app" "$dest/"
  # Register the app and refresh the Services menu.
  "$lsregister" -f "$dest/EnhanceCapCut.app"
  /System/Library/CoreServices/pbs -update
  echo "Installed $dest/EnhanceCapCut.app"
fi

if [[ "$mode" == "--pkg" ]]; then
  pkgroot=build/pkgroot work=build/pkgwork pkg=build/EnhanceCapCut-$version.pkg
  rm -rf "$pkgroot" "$work" "$pkg"
  mkdir -p "$pkgroot/Applications" "$work"
  ditto --norsrc --noextattr --noqtn "$app" "$pkgroot/Applications/EnhanceCapCut.app"

  # Always install into /Applications, never "relocate" onto a copy found elsewhere.
  pkgbuild --analyze --root "$pkgroot" "$work/components.plist" >/dev/null
  plutil -replace 0.BundleIsRelocatable -bool NO "$work/components.plist"
  xattr -cr "$pkgroot" 2>/dev/null || true
  COPYFILE_DISABLE=1 pkgbuild --root "$pkgroot" --component-plist "$work/components.plist" \
    --scripts Installer/scripts --identifier "$bundle_id.pkg" --version "$version" \
    --install-location / "$work/EnhanceCapCut-component.pkg" >/dev/null

  sed "s/__VERSION__/$version/g; s/__PKG_ID__/$bundle_id.pkg/g" Installer/distribution.xml > "$work/distribution.xml"
  sign_args=()
  [[ -n "$DEV_ID_INSTALLER" ]] && sign_args=(--sign "$DEV_ID_INSTALLER" --timestamp)
  productbuild --distribution "$work/distribution.xml" --resources Installer/resources \
    --package-path "$work" "${sign_args[@]}" "$pkg" >/dev/null

  if [[ -n "$NOTARY_PROFILE" && -n "$DEV_ID_INSTALLER" ]]; then
    xcrun notarytool submit "$pkg" --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$pkg"
  fi
  rm -rf "$pkgroot" "$work"
  echo "Built $root/$pkg ($(du -h "$pkg" | cut -f1))"
fi

# The build copy must never provide the Services entry: with an installed copy also registered, both
# get launched and the user sees two export windows (one of them possibly an old version).
# Unregistering isn't enough (pbs finds the bundle again), so drop the entry from this copy.
/usr/libexec/PlistBuddy -c "Delete :NSServices" "$app/Contents/Info.plist"
sign "$app"
"$lsregister" -f "$root/$app"
/System/Library/CoreServices/pbs -update
