#!/bin/sh
# MarkdownViewer one-line installer.
#   macOS:            curl -fsSL https://raw.githubusercontent.com/dgodibadze/MarkdownViewer/main/install.sh | bash
#   Windows Git Bash: same command (delegates to install.ps1)
#   Windows PowerShell: irm https://raw.githubusercontent.com/dgodibadze/MarkdownViewer/main/install.ps1 | iex
set -e
REPO="dgodibadze/MarkdownViewer"

case "$(uname -s)" in
  Darwin) ;;
  MINGW*|MSYS*|CYGWIN*)
    # Running under Git Bash on Windows — hand off to the PowerShell installer.
    exec powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \
      "irm https://raw.githubusercontent.com/$REPO/main/install.ps1 | iex"
    ;;
  *)
    echo "MarkdownViewer supports macOS and Windows. For Windows, run in PowerShell:"
    echo "  irm https://raw.githubusercontent.com/$REPO/main/install.ps1 | iex"
    exit 1
    ;;
esac

echo "Installing MarkdownViewer for macOS…"

# Replacing the bundle under a RUNNING app leaves a zombie process: the window
# keeps painting but its native bridge (Save, menus) silently dies. Ask a
# running copy to quit first — gracefully, so unsaved documents still prompt.
if pgrep -xq MarkdownViewer; then
  echo "MarkdownViewer is running — asking it to quit…"
  osascript -e 'tell application "MarkdownViewer" to quit' >/dev/null 2>&1 || true
  i=0
  while pgrep -xq MarkdownViewer && [ $i -lt 15 ]; do sleep 1; i=$((i+1)); done
  if pgrep -xq MarkdownViewer; then
    echo "MarkdownViewer is still open (unsaved changes?)."
    echo "Please close it, then re-run this installer."
    exit 1
  fi
fi

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

# Prefer a prebuilt DMG from the latest GitHub release.
DMG_URL=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" 2>/dev/null \
  | grep -o '"browser_download_url": *"[^"]*\.dmg"' | head -1 | cut -d'"' -f4 || true)

if [ -n "$DMG_URL" ]; then
  echo "Downloading $(basename "$DMG_URL")…"
  curl -fL --progress-bar "$DMG_URL" -o "$TMP/MarkdownViewer.dmg"
  MOUNT=$(hdiutil attach -nobrowse -readonly "$TMP/MarkdownViewer.dmg" \
    | awk -F'\t' '/\/Volumes\//{print $NF; exit}')
  rm -rf /Applications/MarkdownViewer.app
  cp -R "$MOUNT/MarkdownViewer.app" /Applications/
  hdiutil detach "$MOUNT" -quiet
else
  echo "No DMG in the latest release — building from source (needs the Xcode command line tools)…"
  git clone --depth 1 "https://github.com/$REPO.git" "$TMP/src"
  (cd "$TMP/src" && ./build.sh)
  rm -rf /Applications/MarkdownViewer.app
  cp -R "$TMP/src/MarkdownViewer.app" /Applications/
fi

# The app is ad-hoc signed; clear Gatekeeper's quarantine flag so it opens.
xattr -dr com.apple.quarantine /Applications/MarkdownViewer.app 2>/dev/null || true

echo "✓ Installed to /Applications/MarkdownViewer.app"
echo "  Open it from Launchpad, or: open -a MarkdownViewer README.md"
