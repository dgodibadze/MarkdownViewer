#!/bin/bash
# Build a drag-to-Applications installer DMG for Markdown Viewer.
# Run on macOS after build.sh has produced MarkdownViewer.app.
# Usage: ./make-dmg.sh [path-to-MarkdownViewer.app]
set -euo pipefail
cd "$(dirname "$0")"

APP="${1:-MarkdownViewer.app}"
[ -d "$APP" ] || APP="/Applications/MarkdownViewer.app"
[ -d "$APP" ] || { echo "MarkdownViewer.app not found. Run ./build.sh first."; exit 1; }

VOL="Markdown Viewer"
DMG="MarkdownViewer.dmg"
STAGE="$(mktemp -d)"
RW="$(mktemp -u).dmg"

echo "==> Staging…"
cp -R "$APP" "$STAGE/MarkdownViewer.app"
ln -s /Applications "$STAGE/Applications"
mkdir "$STAGE/.background"
cp dmg/background.png "$STAGE/.background/background.png"

echo "==> Creating writable image…"
rm -f "$DMG"
hdiutil create -srcfolder "$STAGE" -volname "$VOL" -fs HFS+ \
  -format UDRW -ov "$RW" >/dev/null

echo "==> Laying out window…"
hdiutil attach "$RW" -mountpoint "/Volumes/$VOL" -nobrowse >/dev/null
osascript <<EOF
tell application "Finder"
  tell disk "$VOL"
    open
    set current view of container window to icon view
    set toolbar visible of container window to false
    set statusbar visible of container window to false
    set the bounds of container window to {200, 120, 800, 520}
    set vo to the icon view options of container window
    set arrangement of vo to not arranged
    set icon size of vo to 96
    set background picture of vo to file ".background:background.png"
    set position of item "MarkdownViewer.app" of container window to {150, 205}
    set position of item "Applications" of container window to {450, 205}
    update without registering applications
    delay 1
    close
  end tell
end tell
EOF
sync
hdiutil detach "/Volumes/$VOL" >/dev/null

echo "==> Compressing…"
hdiutil convert "$RW" -format UDZO -imagekey zlib-level=9 -o "$DMG" >/dev/null
rm -f "$RW"
rm -rf "$STAGE"

echo ""
echo "Done: $PWD/$DMG"
echo "Open it with: open '$PWD/$DMG'"
