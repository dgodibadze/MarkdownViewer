#!/bin/bash
# Build MarkdownViewer.app from source.
# Requirements: macOS with Xcode command line tools (swiftc), internet on first run.
set -euo pipefail

cd "$(dirname "$0")"
APP="MarkdownViewer.app"
RES="Resources"

echo "==> Fetching render assets (first run only)…"
# Pinned versions. Downloaded once into Resources/, then bundled for offline use.
fetch() {  # fetch <url> <dest>
  local url="$1" dest="$2"
  if [ -s "$RES/$dest" ]; then
    echo "    have $dest"
    return
  fi
  echo "    get  $dest"
  curl -fsSL "$url" -o "$RES/$dest" \
    || { echo "ERROR: could not download $dest from $url"; echo "Download it manually into $RES/ and re-run."; exit 1; }
}

fetch "https://cdn.jsdelivr.net/npm/marked@12.0.2/marked.min.js"                        "marked.min.js"
fetch "https://cdn.jsdelivr.net/gh/highlightjs/cdn-release@11.9.0/build/highlight.min.js" "highlight.min.js"
fetch "https://cdn.jsdelivr.net/npm/github-markdown-css@5.5.1/github-markdown-light.css"  "github-markdown-light.css"
fetch "https://cdn.jsdelivr.net/npm/github-markdown-css@5.5.1/github-markdown-dark.css"   "github-markdown-dark.css"
fetch "https://cdn.jsdelivr.net/gh/highlightjs/cdn-release@11.9.0/build/styles/github.min.css"      "hljs-github-light.css"
fetch "https://cdn.jsdelivr.net/gh/highlightjs/cdn-release@11.9.0/build/styles/github-dark.min.css" "hljs-github-dark.css"

# Mermaid + KaTeX (offline; loaded lazily by the template only when a document
# actually uses them). KaTeX's CSS references fonts/ relative to itself.
fetch "https://cdn.jsdelivr.net/npm/mermaid@10.9.6/dist/mermaid.min.js"                  "mermaid.min.js"
fetch "https://cdn.jsdelivr.net/npm/katex@0.16.10/dist/katex.min.js"                     "katex.min.js"
fetch "https://cdn.jsdelivr.net/npm/katex@0.16.10/dist/katex.min.css"                    "katex.min.css"
fetch "https://cdn.jsdelivr.net/npm/katex@0.16.10/dist/contrib/auto-render.min.js"       "katex-auto-render.min.js"
mkdir -p "$RES/fonts"
for F in KaTeX_AMS-Regular KaTeX_Caligraphic-Bold KaTeX_Caligraphic-Regular \
         KaTeX_Fraktur-Bold KaTeX_Fraktur-Regular KaTeX_Main-Bold KaTeX_Main-BoldItalic \
         KaTeX_Main-Italic KaTeX_Main-Regular KaTeX_Math-BoldItalic KaTeX_Math-Italic \
         KaTeX_SansSerif-Bold KaTeX_SansSerif-Italic KaTeX_SansSerif-Regular \
         KaTeX_Script-Regular KaTeX_Size1-Regular KaTeX_Size2-Regular KaTeX_Size3-Regular \
         KaTeX_Size4-Regular KaTeX_Typewriter-Regular; do
  fetch "https://cdn.jsdelivr.net/npm/katex@0.16.10/dist/fonts/$F.woff2" "fonts/$F.woff2"
done

echo "==> Verifying pinned asset checksums…"
shasum -a 256 -c "$RES/SHA256SUMS"

echo "==> Compiling Swift (universal: arm64 + x86_64)…"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

# Build each slice separately, then lipo into a universal binary so the app
# runs natively on both Apple Silicon and Intel Macs. If one target can't be
# built on this machine (toolchain without that SDK slice), warn and ship the
# other instead of failing the whole build.
BIN="$APP/Contents/MacOS/MarkdownViewer"
SLICES=""
MODULE_CACHE="${TMPDIR:-/tmp}/MarkdownViewer-SwiftModuleCache"
mkdir -p "$MODULE_CACHE"
for ARCH in arm64 x86_64; do
  if swiftc -O -module-cache-path "$MODULE_CACHE" -target "$ARCH-apple-macos11.0" \
       -framework Cocoa -framework WebKit -framework Security -framework CryptoKit \
       -o "$BIN.$ARCH" Sources/main.swift; then
    echo "    built $ARCH"
    SLICES="$SLICES $BIN.$ARCH"
  else
    echo "    WARNING: $ARCH build failed — continuing without it"
  fi
done
set -- $SLICES
case $# in
  0) echo "ERROR: no architecture compiled"; exit 1 ;;
  1) mv "$1" "$BIN" ;;
  *) lipo -create $SLICES -output "$BIN" && rm -f "$BIN".arm64 "$BIN".x86_64 ;;
esac
lipo -info "$BIN" 2>/dev/null | sed 's/^/    /' || true

echo "==> Assembling bundle…"
cp Info.plist "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"
cp "$RES"/template.html \
   "$RES"/marked.min.js "$RES"/highlight.min.js \
   "$RES"/github-markdown-light.css "$RES"/github-markdown-dark.css \
   "$RES"/hljs-github-light.css "$RES"/hljs-github-dark.css \
   "$RES"/mermaid.min.js "$RES"/katex.min.js "$RES"/katex.min.css \
   "$RES"/katex-auto-render.min.js "$RES"/SHA256SUMS \
   CHANGELOG.md README.md "$RES"/ARCHITECTURE.md "$RES"/DESIGN.md \
   "$APP/Contents/Resources/"
cp -R "$RES/fonts" "$APP/Contents/Resources/fonts"
# README's images, so the About window's Read Me renders with screenshots.
mkdir -p "$APP/Contents/Resources/docs/screenshots"
cp docs/icon.png "$APP/Contents/Resources/docs/"
cp docs/screenshots/*.png "$APP/Contents/Resources/docs/screenshots/"
# App icon (optional; ignored if missing)
[ -f Icon/AppIcon.icns ] && cp Icon/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns" || true

echo "==> Ad-hoc code signing…"
codesign --force --deep --sign - "$APP" 2>/dev/null || echo "    (codesign skipped)"

echo "==> Registering with Launch Services…"
LSREG="/System/Library/Frameworks/CoreServices.framework/Versions/A/Frameworks/LaunchServices.framework/Versions/A/Support/lsregister"
[ -x "$LSREG" ] && "$LSREG" -f "$PWD/$APP" || true

echo ""
echo "Done. Built: $PWD/$APP"
echo "Run it:        open '$PWD/$APP'"
echo "Install it:    mv '$APP' /Applications/   (then re-run this script's lsregister line, or just open it once)"
echo "Open a file:   open -a '$PWD/$APP' yourfile.md"
