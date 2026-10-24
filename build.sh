#!/bin/bash
# Builds build.noindex/AgentWheel.app with the Xcode Command Line Tools (Xcode itself is not needed).
set -euo pipefail
cd "$(dirname "$0")"

APP="build.noindex/AgentWheel.app"
# ".noindex" keeps Spotlight (and so Launchpad / the Apps list) from showing this test copy next to
# the installed app in /Applications.
mkdir -p build.noindex
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

FLAGS=()
# Some Command Line Tools installs keep a stale duplicate of the SwiftBridging module map, which
# makes every swiftc run fail with "redefinition of module 'SwiftBridging'". Hide it with a VFS overlay
# instead of touching the system files.
SWIFT_INC="$(dirname "$(xcrun -f swiftc)")/../include/swift"
if [ -f "$SWIFT_INC/module.modulemap" ] && [ -f "$SWIFT_INC/bridging.modulemap" ]; then
  INC="$(cd "$SWIFT_INC" && pwd)"
  echo "// hides a stale duplicate SwiftBridging module map" > build.noindex/empty.modulemap
  cat > build.noindex/overlay.yaml <<EOF
{ "version": 0, "case-sensitive": "false", "roots": [ { "type": "directory", "name": "$INC",
  "contents": [ { "type": "file", "name": "module.modulemap", "external-contents": "$PWD/build.noindex/empty.modulemap" } ] } ] }
EOF
  FLAGS+=(-vfsoverlay build.noindex/overlay.yaml)
fi

# UNIVERSAL=1 builds for both Apple silicon and Intel (what package.sh ships).
ARCHS="$(uname -m)"
[ "${UNIVERSAL:-0}" = "1" ] && ARCHS="arm64 x86_64"
SLICES=()
for ARCH in $ARCHS; do
  echo "Compiling ($ARCH)…"
  swiftc -O -swift-version 5 -target "$ARCH-apple-macos14.0" ${FLAGS[@]+"${FLAGS[@]}"} \
    -o "build.noindex/AgentWheel-$ARCH" Sources/*.swift
  SLICES+=("build.noindex/AgentWheel-$ARCH")
done
lipo -create "${SLICES[@]}" -output "$APP/Contents/MacOS/AgentWheel"
cp Resources/Info.plist "$APP/Contents/Info.plist"
# Localized display name: Finder, Spotlight and Launchpad show 特戰輪盤 on a Chinese system.
cp -R Resources/*.lproj "$APP/Contents/Resources/"

echo "Drawing the icon…"
ICONSET=build.noindex/AppIcon.iconset
rm -rf "$ICONSET"
mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  "$APP/Contents/MacOS/AgentWheel" --icon "$ICONSET/icon_${s}x${s}.png" --px "$s"
  "$APP/Contents/MacOS/AgentWheel" --icon "$ICONSET/icon_${s}x${s}@2x.png" --px "$((s * 2))"
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

# Files under Desktop/Documents can carry Finder/quarantine attributes that codesign rejects.
xattr -cr "$APP"
codesign --force --sign - "$APP" >/dev/null
echo "Built $APP"
# Also forget any earlier registration of this test copy with Launch Services.
LSREGISTER=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister
[ -x "$LSREGISTER" ] && "$LSREGISTER" -u "$PWD/$APP" >/dev/null 2>&1 || true
