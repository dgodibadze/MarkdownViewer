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

echo "==> Staging…"
cp -R "$APP" "$STAGE/MarkdownViewer.app"
ln -s /Applications "$STAGE/Applications"

# Clean up any stale mount from a previous run.
hdiutil detach "/Volumes/$VOL" >/dev/null 2>&1 || true

echo "==> Building compressed image…"
rm -f "$DMG"
hdiutil create -volname "$VOL" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$STAGE"
shasum -a 256 "$DMG" > "$DMG.sha256"

echo ""
echo "Done: $PWD/$DMG"
echo "Checksum: $PWD/$DMG.sha256"
echo "The disk image contains MarkdownViewer.app and an Applications alias — drag one onto the other to install."
echo "Open it with: open '$PWD/$DMG'"
