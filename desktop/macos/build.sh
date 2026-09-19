#!/bin/bash
set -euo pipefail

source_dir="$(cd "$(dirname "$0")" && pwd)"
output="$1"
bundle="$output/GitHub Team App.app"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
cp "$source_dir/Info.plist" "$bundle/Contents/Info.plist"
xcrun swiftc "$source_dir/main.swift" -o "$bundle/Contents/MacOS/GitHubTeamApp" \
    -O -framework Cocoa -framework WebKit -target "$(uname -m)-apple-macosx13.0"
iconset="$output/TeamApp.iconset"
mkdir -p "$iconset"
"$bundle/Contents/MacOS/GitHubTeamApp" --write-icons "$iconset"
iconutil -c icns "$iconset" -o "$bundle/Contents/Resources/AppIcon.icns"
for name in icon_16x16.png icon_16x16@2x.png icon_32x32.png icon_32x32@2x.png icon_128x128.png icon_128x128@2x.png icon_256x256.png icon_256x256@2x.png icon_512x512.png icon_512x512@2x.png; do
    rm "$iconset/$name"
done
rmdir "$iconset"
codesign --force --sign - "$bundle"
