# MarkdownViewer

A small standalone macOS app that opens `.md` files in a real window on
double-click, instead of only the Quick Look (spacebar) preview.

Built as an extension of the QLMarkdown project: QLMarkdown gives you the
Quick Look preview, this gives you a double-click viewer app. They are
independent and can both be installed.

## License & credits

MarkdownViewer is licensed under the **GNU GPL v3.0** (see `LICENSE.txt`). It began as a
companion to **QLMarkdown by sbarex** (https://github.com/sbarex/QLMarkdown), which is also
GPLv3; MarkdownViewer is independent code (its own Swift plus the web libraries below) and
remains under GPLv3 with attribution to that project. Bundled libraries — `marked` (MIT), `highlight.js` (BSD-3-Clause),
and `github-markdown-css` (MIT) — are credited in `NOTICE.md`. If you publish a fork, keep the
GPLv3 license, this attribution, and `NOTICE.md`.

## Features

- Opens `.md` (and `.markdown`, `.mdown`, `.rmd`, `.qmd`, `.mdx`, `.mdc`, …) on double-click.
- Renders with `marked` + `highlight.js`, GitHub-style CSS. Fully offline after build.
- GitHub-style **Preview / Edit / Split** modes (toolbar segmented control, or ⌘1 / ⌘2 / ⌘3).
  Split shows the raw editor and a live-updating preview side by side.
- **Synced scrolling** in Split (vertical + horizontal), with always-visible scrollbars so
  content stays reachable when the window is small.
- **Wrap Lines** toggle (`View ▸ Wrap Lines`): soft-wrap, or no-wrap with horizontal scroll
  that syncs sideways with the preview.
- **Find & Replace** (`Edit ▸ Find` / `Find and Replace`): match count, next/prev, replace
  one, replace all, case-sensitive toggle.
- **AI assistant** (`AI` menu) with multiple providers — Groq, Nous Portal, Anthropic, OpenAI,
  Gemini: improve a selection, chat about the document, generate-and-insert. API keys live in
  the macOS Keychain, entered via a secure Settings dialog.
- Edit and **save back to the file** with ⌘S (or the toolbar Save button). A dot marks
  unsaved changes; closing a window/tab with unsaved edits prompts to Save / Don't Save / Cancel.
- Light / Dark / System theme toggle (circular button in the toolbar), follows the OS in System.
- **About window** with author info and quick links to the Changelog and Architecture doc.
- Live reload: edit the file in any other editor and the window refreshes on save. Scroll
  position is kept. (Live reload is paused while you have unsaved edits, so it never
  clobbers your changes.)
- Multiple files open as native window tabs, each with independent edit state.
- External `http(s)` links open in your default browser; local images and links resolve.

## Build

You need Xcode command line tools (`xcode-select --install`) and internet on the
first build (to fetch the JS/CSS libraries, which are then bundled for offline use).

```bash
cd MarkdownViewer
./build.sh
```

This produces `MarkdownViewer.app` in the same folder.

## Install and set as the double-click default

1. Move the app into Applications:
   ```bash
   mv MarkdownViewer.app /Applications/
   open /Applications/MarkdownViewer.app   # registers it once
   ```
2. In Finder, right-click any `.md` file -> **Get Info**.
3. Under **Open with**, choose **Markdown Viewer**, then click **Change All…**.

Now double-clicking any `.md` file opens it in MarkdownViewer. Quick Look
(spacebar) still works as before.

## How it works

- `Sources/main.swift` - AppKit app. Handles file-open events, one window per
  file (native tabs), a 1-second modification-date poll for live reload, and a
  `WKWebView` per window. A `bridge` script-message handler receives editor
  changes/saves from the page and writes them back to disk; the poll is suspended
  while a window has unsaved edits.
- `Resources/template.html` - the render shell and the editor UI. The app substitutes
  the markdown text and asset paths into it, writes a temp HTML file, and loads it.
  Contains the Preview/Edit/Split editor, live preview, theme system, and the
  `window.webkit.messageHandlers.bridge` calls that talk to Swift.
- `Info.plist` - declares the markdown document types so Launch Services routes
  double-clicks here.
- `build.sh` - downloads the libraries, compiles with `swiftc`, assembles and
  ad-hoc signs the `.app`, and registers it with Launch Services.

## Notes / trade-offs

- The app is ad-hoc signed (no Apple Developer ID). If Gatekeeper complains on
  first launch, right-click the app -> Open, or `xattr -dr com.apple.quarantine MarkdownViewer.app`.
- Rendering uses `marked`, not QLMarkdown's `cmark-gfm` C engine, so output is
  GitHub-flavored but not byte-identical to the Quick Look preview. This was a
  deliberate choice for a small, dependency-light build.
- Mermaid/MathJax are not bundled by default. Add them to `template.html` and
  `build.sh` the same way the other assets are wired if you need them.

## App icon

The icon lives at `Icon/AppIcon.icns` (source art: `Icon/icon_1024.png`).
`build.sh` copies it into the bundle and `Info.plist` references it via
`CFBundleIconFile`. To restyle it, edit the art, regenerate the `.icns`, and
rebuild. If a rebuilt app in `/Applications` still shows the old icon, run
`touch /Applications/MarkdownViewer.app && killall Finder`.

## Installer DMG

`make-dmg.sh` builds a `MarkdownViewer.dmg` with a drag-to-Applications layout
(background art in `dmg/background.png`). Run it on macOS after `build.sh`:

```bash
./build.sh
./make-dmg.sh
open MarkdownViewer.dmg
```

The DMG opens to the app icon and an Applications shortcut with an arrow between
them. Drag the app onto Applications to install, then eject the disk image.
