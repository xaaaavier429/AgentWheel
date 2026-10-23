#!/bin/bash
# Builds a universal AgentWheel.app (Apple silicon + Intel) and packs it for sharing:
#   dist/AgentWheel.dmg  — open, drag into Applications
#   dist/AgentWheel.zip  — the bare app
set -euo pipefail
cd "$(dirname "$0")"

UNIVERSAL=1 ./build.sh
VERSION=$(/usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" Resources/Info.plist)

rm -rf dist
mkdir -p dist/stage
cp -R build/AgentWheel.app dist/stage/
ln -s /Applications dist/stage/Applications
cp "Resources/打不開？看這裡.txt" dist/stage/

echo "Packing DMG…"
hdiutil create -volname "特戰輪盤 $VERSION" -srcfolder dist/stage -ov -format UDZO dist/AgentWheel.dmg >/dev/null
ditto -c -k --sequesterRsrc --keepParent build/AgentWheel.app dist/AgentWheel.zip
rm -rf dist/stage
ls -lh dist
