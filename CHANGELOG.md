# Changelog

All notable changes to MarkdownViewer are documented here.

Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
This project follows [Semantic Versioning](https://semver.org/).

## [2.3] — 2026-07-12

### Added

- **Intel Mac support.** The build now compiles both `arm64` and `x86_64`
  slices (macOS 11+) and merges them with `lipo` into a universal binary, so
  the same `.app`/DMG runs natively on Apple Silicon and Intel. If one slice
  can't be compiled on the build machine, the script warns and ships the other.

## [2.2] — 2026-07-12

### Changed

- **Updated default AI models** to current generations: OpenAI `gpt-5.1`,
  Anthropic `claude-sonnet-5`, Gemini `gemini-2.5-flash` (still user-editable
  per provider in AI ▸ Settings…; existing overrides are untouched).
- The code-block "Copied" green is now a `--success` theme variable (brighter
  in dark mode) instead of a hardcoded color.
- Open panel uses the modern `allowedContentTypes` API instead of the
  deprecated `allowedFileTypes` (removes the last build warning).

### Fixed

- **Find matches by regex on the original text** instead of lowercasing the
  whole document for comparison — offsets could drift (corrupting Replace) on
  characters whose lowercase form changes length, e.g. Turkish `İ`.

## [2.1] — 2026-07-12

### Fixed

- **AI Settings no longer claims "✓ Key stored" when the Keychain write failed.**
  `SecItemAdd` errors were silently discarded; the status line now reports the
  failure and keeps the entered key in the field so it isn't lost.

## [2.0] — 2026-07-12

### Security

- **Rendered documents are now sandboxed by a Content-Security-Policy.**
  Markdown may contain raw HTML, and previously nothing stopped a malicious
  document from loading a remote `<script>` that could drive the native bridge —
  including overwriting the file via the save action — or phone home. The
  template now ships a CSP: only the bundled `file://` assets and the app's own
  inline code may execute, and the page may not open any network connections.
  Local and remote **images** still render as before.
- **Rendered HTML is sanitized.** A CSP can't block inline event handlers (the
  app's own scripts are inline), and those are the one raw-HTML vector that
  `innerHTML` actually executes — `<img onerror="…">` ran arbitrary JS with
  bridge access. The preview now strips all `on*` attributes and
  `javascript:`/`vbscript:` URLs from rendered documents.
- **Explicit `</script>` escaping.** A document containing a literal
  `</script>` only failed to break out of the embedding script because
  `JSONSerialization` happens to escape `/`. The escape is now explicit in both
  the JSON path and the manual fallback (which previously lacked it).

## [1.9] — 2026-07-12

### Security

- **Gemini API key no longer travels in the URL.** It was appended as a
  `?key=` query parameter, which any proxy or server access log would capture.
  It's now sent in the `x-goog-api-key` request header.

## [1.8] — 2026-07-12

### Fixed

- **Multiple windows no longer stack exactly on top of each other.** Every
  window shared one frame-autosave name, so they all restored to the identical
  position and overwrote each other's saved frame. The first window keeps the
  remembered frame; additional windows cascade down-right from it.

## [1.7] — 2026-07-12

### Fixed

- **View ▸ Reload now actually re-renders from disk.** It previously triggered
  `WKWebView.reload`, which just reloaded the stale temp HTML — external file
  changes were never picked up and in-page edits were silently dropped. The menu
  item (retitled "Reload From Disk", still ⌘R) re-renders the document and asks
  for confirmation before discarding unsaved edits.
- **Render-failure no longer causes a once-per-second reload loop.** The error
  path never recorded the file's modification date, so the live-reload watcher
  re-rendered the error page every tick, flickering forever.

### Removed

- Dead code: the unwired `reloadDocument(_:)` action and the empty
  `navigationDidFinish()` stub.

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
