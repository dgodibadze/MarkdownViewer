#!/bin/sh
# MarkdownViewer one-line installer.
#   macOS:            curl -fsSL https://raw.githubusercontent.com/dgodibadze/MarkdownViewer/main/install.sh | bash
#   macOS dev build:  curl -fsSL https://raw.githubusercontent.com/dgodibadze/MarkdownViewer/dev/install.sh | bash -s -- --branch dev
#   Windows Git Bash: same command (delegates to install.ps1)
#   Windows PowerShell: irm https://raw.githubusercontent.com/dgodibadze/MarkdownViewer/main/install.ps1 | iex
set -e
REPO="dgodibadze/MarkdownViewer"
INSTALL_REF="${MARKDOWNVIEWER_REF:-${MARKDOWNVIEWER_BRANCH:-}}"
INSTALL_FROM_SOURCE=0

while [ $# -gt 0 ]; do
  case "$1" in
    --branch|--ref)
      [ $# -ge 2 ] || { echo "$1 requires a branch or ref name."; exit 2; }
      INSTALL_REF="$2"
      INSTALL_FROM_SOURCE=1
      shift 2
      ;;
    --branch=*|--ref=*)
      INSTALL_REF="${1#*=}"
      INSTALL_FROM_SOURCE=1
      shift
      ;;
    --source)
      INSTALL_FROM_SOURCE=1
      shift
      ;;
    --release)
      INSTALL_FROM_SOURCE=0
      INSTALL_REF=""
      shift
      ;;
    -h|--help)
      echo "Usage: install.sh [--branch REF|--source|--release]"
      echo "  default       Install the latest verified GitHub release."
      echo "  --branch dev  Build and install that branch/ref from source."
      echo "  --source      Build and install the default branch from source."
      exit 0
      ;;
    *)
      echo "Unknown option: $1"
      exit 2
      ;;
  esac
done

if [ -n "$INSTALL_REF" ]; then
  case "$INSTALL_REF" in
    -*|*[^A-Za-z0-9._/-]*|'')
      echo "Unsafe branch/ref name: $INSTALL_REF"
      exit 2
      ;;
  esac
  INSTALL_FROM_SOURCE=1
fi

case "$(uname -s)" in
  Darwin) ;;
  MINGW*|MSYS*|CYGWIN*)
    # Running under Git Bash on Windows — hand off to the PowerShell installer.
    PS_REF="$INSTALL_REF"
    if [ -n "$PS_REF" ]; then PS_REF_CMD="\$env:MARKDOWNVIEWER_REF='$PS_REF'; "; else PS_REF_CMD=""; fi
    if [ -n "$INSTALL_REF" ]; then PS_INSTALL_REF="$INSTALL_REF"; else PS_INSTALL_REF="main"; fi
    exec powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \
      "${PS_REF_CMD}irm https://raw.githubusercontent.com/$REPO/$PS_INSTALL_REF/install.ps1 | iex"
    ;;
  *)
    echo "MarkdownViewer supports macOS and Windows. For Windows, run in PowerShell:"
    echo "  irm https://raw.githubusercontent.com/$REPO/main/install.ps1 | iex"
    exit 1
    ;;
esac

if [ "$INSTALL_FROM_SOURCE" -eq 1 ]; then
  if [ -n "$INSTALL_REF" ]; then
    echo "Installing MarkdownViewer for macOS from source ref '$INSTALL_REF'…"
  else
    echo "Installing MarkdownViewer for macOS from source…"
  fi
else
  echo "Installing MarkdownViewer for macOS…"
fi

ensure_app_stopped() {
  # Replacing the bundle under a running app leaves a zombie process: the
  # window keeps painting but its native bridge (Save, menus) silently dies.
  # Quit gracefully so unsaved documents still prompt.
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
}

ensure_app_stopped

TMP=$(mktemp -d)
TARGET_APP="/Applications/MarkdownViewer.app"
INSTALL_STAGE=$(mktemp -d "/Applications/.MarkdownViewer.install.XXXXXX")
NEW_APP="$INSTALL_STAGE/New.app"
BACKUP_APP="$INSTALL_STAGE/Previous.app"
MOUNT=""

cleanup() {
  if [ -n "$MOUNT" ]; then hdiutil detach "$MOUNT" -quiet >/dev/null 2>&1 || true; fi
  if [ -e "$BACKUP_APP" ] || [ -L "$BACKUP_APP" ]; then
    if [ ! -e "$TARGET_APP" ] && [ ! -L "$TARGET_APP" ]; then
      mv "$BACKUP_APP" "$TARGET_APP" 2>/dev/null || true
    fi
  fi
  rm -rf "$INSTALL_STAGE"
  rm -rf "$TMP"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

install_app() {
  source_app="$1"
  [ -d "$source_app" ] || { echo "Release does not contain MarkdownViewer.app; keeping the existing install."; return 1; }
  [ -x "$source_app/Contents/MacOS/MarkdownViewer" ] || { echo "Release app has no executable; keeping the existing install."; return 1; }
  [ ! -e "$NEW_APP" ] && [ ! -L "$NEW_APP" ] &&
    [ ! -e "$BACKUP_APP" ] && [ ! -L "$BACKUP_APP" ] ||
    { echo "Installer staging path is occupied; refusing to continue."; return 1; }

  # Copy and validate the replacement before moving the working app aside.
  cp -R "$source_app" "$NEW_APP"
  [ -x "$NEW_APP/Contents/MacOS/MarkdownViewer" ] || { echo "Staged app validation failed; keeping the existing install."; return 1; }
  plutil -lint "$NEW_APP/Contents/Info.plist" >/dev/null
  codesign --verify --deep --strict "$NEW_APP"

  # Downloads and validation can take long enough for the app to be reopened.
  # Re-check immediately before swapping the installed bundle.
  ensure_app_stopped
  if [ -e "$TARGET_APP" ] || [ -L "$TARGET_APP" ]; then mv "$TARGET_APP" "$BACKUP_APP"; fi
  if mv "$NEW_APP" "$TARGET_APP"; then
    if [ -e "$BACKUP_APP" ]; then rm -rf "$BACKUP_APP"; fi
  else
    if [ -e "$BACKUP_APP" ]; then mv "$BACKUP_APP" "$TARGET_APP"; fi
    echo "Could not activate the staged app; the previous install was restored."
    return 1
  fi
}

if [ "$INSTALL_FROM_SOURCE" -eq 0 ]; then
  # Prefer a prebuilt DMG from the latest GitHub release.
  RELEASE_JSON=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" 2>/dev/null || true)
  DMG_URL=$(printf '%s' "$RELEASE_JSON" \
    | grep -o '"browser_download_url": *"[^"]*\.dmg"' | head -1 | cut -d'"' -f4 || true)
  SUM_URL=$(printf '%s' "$RELEASE_JSON" \
    | grep -o '"browser_download_url": *"[^"]*\.dmg\.sha256"' | head -1 | cut -d'"' -f4 || true)
else
  DMG_URL=""
  SUM_URL=""
fi

if [ -n "$DMG_URL" ]; then
  [ -n "$SUM_URL" ] || { echo "Release DMG has no .sha256 companion; refusing an unverified install."; exit 1; }
  echo "Downloading $(basename "$DMG_URL")…"
  curl -fL --progress-bar "$DMG_URL" -o "$TMP/MarkdownViewer.dmg"
  curl -fsSL "$SUM_URL" -o "$TMP/MarkdownViewer.dmg.sha256"
  EXPECTED=$(awk '{print $1; exit}' "$TMP/MarkdownViewer.dmg.sha256")
  case "$EXPECTED" in
    *[!0-9A-Fa-f]*|'') echo "Release checksum is malformed; refusing installation."; exit 1 ;;
  esac
  [ "${#EXPECTED}" -eq 64 ] || { echo "Release checksum is malformed; refusing installation."; exit 1; }
  printf '%s  %s\n' "$EXPECTED" "$TMP/MarkdownViewer.dmg" | shasum -a 256 -c -
  MOUNT=$(hdiutil attach -nobrowse -readonly "$TMP/MarkdownViewer.dmg" \
    | awk -F'\t' '/\/Volumes\//{print $NF; exit}')
  [ -n "$MOUNT" ] || { echo "Could not locate the mounted release volume; keeping the existing install."; exit 1; }
  install_app "$MOUNT/MarkdownViewer.app"
  hdiutil detach "$MOUNT" -quiet
  MOUNT=""
else
  if [ "$INSTALL_FROM_SOURCE" -eq 0 ]; then
    echo "No DMG in the latest release — building from source (needs the Xcode command line tools)…"
  else
    echo "Building from source (needs the Xcode command line tools)…"
  fi
  if [ -n "$INSTALL_REF" ]; then
    git clone --depth 1 --branch "$INSTALL_REF" "https://github.com/$REPO.git" "$TMP/src"
  else
    git clone --depth 1 "https://github.com/$REPO.git" "$TMP/src"
  fi
  (cd "$TMP/src" && ./build.sh)
  install_app "$TMP/src/MarkdownViewer.app"
fi

echo "✓ Installed to /Applications/MarkdownViewer.app"
echo "  Open it from Launchpad, or: open -a MarkdownViewer README.md"
