# Changelog

All notable changes to MarkdownViewer are documented here.

Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
This project follows [Semantic Versioning](https://semver.org/).

## [1.6] — 2026-07-12

### Fixed

- **Undo survived nothing.** Tab-inserts-spaces, Replace / Replace All, and the
  AI insert/improve actions all rewrote `editor.value`, which wipes the
  textarea's native undo stack — one Tab press and ⌘Z was dead. All programmatic
  edits now go through a shared `spliceEditor()` helper built on
  `document.execCommand('insertText')`, which WebKit records as a normal
  undoable edit (Replace All is a single undo step). Falls back to the old
  direct splice if `execCommand` is unavailable.

## [1.5] — 2026-07-12

### Fixed

- **In-document anchor links (`[Jump](#section)`) now work.** They were doubly
  broken: marked v12 no longer generates heading ids, and the `<base href>` used
  for relative images made `#section` resolve against the file's *directory*,
  navigating the view away from the document. The preview now assigns
  GitHub-style slug ids to headings (deduped `-1`, `-2`, …) and intercepts
  fragment clicks to smooth-scroll in place.

## [1.4] — 2026-07-12

### Fixed

- **Documents containing the literal text `__TITLE__` rendered corrupted.** The
  template substituted the markdown body *before* the title token, so any
  `__TITLE__` inside the document itself was then replaced with the filename
  (this repo's own CLAUDE.md triggered it). `__MARKDOWN__` is now always the
  last token substituted.

## [1.3] — 2026-07-12

### Fixed

- **Crash on a malformed AI Base URL.** The request builder force-unwrapped
  `URL(string:)` on user-editable Settings input, so a base URL with a space (or
  other invalid characters) crashed the app on the next AI request. It now throws
  a descriptive "endpoint URL is invalid" error that surfaces as an alert.

## [1.2] — 2026-07-12

### Fixed

- **Quitting (⌘Q) discarded unsaved edits without asking.** The Save / Don't
  Save / Cancel prompt only guarded window close (⌘W); quitting closed all
  windows without consulting it. `applicationShouldTerminate` now walks every
  dirty document, fronts its window, shows the same prompt, and defers
  termination (`.terminateLater`) until all chosen saves have finished writing.

## [1.1] — 2026-07-12

### Fixed

- **File ▸ Save could erase or stale-save the document.** The save path wrote a
  cached copy of the editor text that started out *empty* and was only updated
  250ms after typing — so clicking File ▸ Save on a freshly opened, unedited
  document overwrote the file with an empty string, and saving right after an
  external-change reload silently reverted the external edits. The cache is now
  seeded from disk on every load/reload, and Save first asks the page for the
  live editor text (new `window.__getText` hook), falling back to the cache only
  if the page can't answer.

## [1.0] — 2026-07-12

### Added

- **File ▸ Open Path… (⇧⌘G).** Open a document by typing or pasting an absolute
  path (e.g. `/Users/James/USER.md`) instead of navigating the Finder open panel.
  Pre-fills from the clipboard when it already holds a path, expands `~`, accepts
  `file://` URLs, and tolerates quoted or backslash-escaped paths copied from a
  terminal. Shows a "File not found" alert rather than failing silently.

- **Copy button on fenced code blocks.** Each code block in the preview gets a
  hover button in its top-right corner that copies exactly the text between the
  ``` fences. Shows a "Copied" check for 1.5s. Falls back to a hidden textarea and
  `document.execCommand('copy')` because `navigator.clipboard` is not reliably
  permitted in a `file://` WKWebView.

- **Document zoom.** `Cmd +` / `Cmd -` to resize text, `Cmd 0` to reset. Scales
  both the preview and the editor, clamped to 0.6x–2.6x, and persisted across
  reloads and sessions. Deliberately no toolbar buttons — the UI stays minimalist.

- **App icon.** Bundled `Icon/AppIcon.icns` (blue squircle with the markdown M↓
  mark), wired into the build via `CFBundleIconFile`.

- **Installer DMG.** `make-dmg.sh` builds a `MarkdownViewer.dmg` with a
  drag-to-Applications layout and a custom background image.

- **`CLAUDE.md`.** Handoff notes covering architecture, the build loop, and the
  root causes of the bugs below, so future sessions don't rediscover them.

### Changed

- **Zoom now scales the text column, not just the font.** Previously the preview
  column was pinned at 980px, so larger fonts meant fewer characters per line and
  more wrapping. The column's `max-width` and side padding now scale by the same
  `--zoom` factor as the font, keeping characters-per-line constant — the content
  simply gets bigger rather than reflowing.

### Fixed

- **Cut, Paste, Undo and Redo did nothing.** macOS only dispatches a Cmd key
  equivalent to the first responder if some menu item declares that shortcut. The
  Edit menu only carried Copy and Select All, so ⌘X and ⌘V were dead keys. Added
  the full standard Edit menu (Undo, Redo, Cut, Copy, Paste, Delete, Select All)
  targeting the responder chain.

- **Split panes scrolled slowly on their own.** The editor/preview scroll sync used
  an `isSyncing` flag cleared on the next `requestAnimationFrame`. That guard is
  unsound: writing `dst.scrollTop` fires the destination's `scroll` event
  asynchronously, often after the frame has already cleared the flag. The echo then
  synced back with sub-pixel rounding error (the panes have different
  `scrollHeight`), nudging the source and re-firing — the two panes ratcheted each
  other along indefinitely. Replaced the timing guard with a driver-pane model: the
  pane claimed by real input (`wheel`/`mousedown`/`touchstart`/`keydown`/`focusin`)
  is the only one whose scroll propagates, plus a 1px write threshold.

- **Misleading "Could not read \<file\>" error.** `Renderer.render()` returned a bare
  `false` on any failure, so the UI blamed the markdown file even when the real
  failure was writing the temp render file or locating the bundled template. It now
  returns a descriptive error string that the web view displays, and it creates the
  temp directory immediately before writing.

## [0.9] — 2026-06-19 (initial release)

### Added

- Initial standalone macOS viewer: opens `.md` files in a real window on
  double-click instead of only the Quick Look preview.
- Rendering via bundled `marked` + `highlight.js` with GitHub-style CSS, fully
  offline after the first build.
- Light / Dark / System theme toggle, following the OS in System mode.
- Live reload on file change, preserving scroll position.
- Multiple files open as native window tabs.
- Preview / Edit / Split modes, save, find & replace, and an AI chat panel.
- Hand-rolled `build.sh` (no Xcode project): compiles with `swiftc`, assembles and
  ad-hoc signs the `.app`, and registers it with Launch Services.
