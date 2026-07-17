# Changelog

All notable changes to MarkdownViewer are documented here.

Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions bump by 0.1 per release batch.

## [1.8] — 2026-07-16

### Added

- **Print / Save as PDF (⌘P / Ctrl+P).** Prints only the rendered document —
  print CSS hides the toolbar, editor, and sidebar. The system print dialog's
  "Save as PDF" doubles as PDF export.
- **Table of Contents sidebar (View menu, ⇧⌘T / Ctrl+T).** A toggleable
  heading outline; click to jump. Uses the preview's real positions (in Edit
  mode it approximates from the heading's place in the source). State persists.
- **Clickable task checkboxes.** `- [ ]` items rendered in the preview are now
  live: clicking one writes `[x]`/`[ ]` back into the markdown source as a
  normal undoable edit and marks the document dirty. If the preview's
  checkboxes can't be matched 1:1 to source markers (e.g. task syntax inside a
  code fence, raw-HTML inputs), they stay read-only rather than guess.
- **Mermaid diagrams and KaTeX math, fully offline.** ` ```mermaid ` fences
  render as diagrams (theme-aware); `$$…$$`, `\(…\)`, `\[…\]` render as math.
  Both libraries are bundled (pinned versions, fetched by build.sh) and load
  lazily — documents that don't use them pay nothing. Known limitation:
  markdown formatting is applied before KaTeX sees the text, so underscores
  inside inline math can be eaten — prefer `\(…\)` or display `$$` blocks.
- **Word & character count** in the toolbar, live while editing.
- **Zoom menu items** (View ▸ Zoom In / Zoom Out / Actual Size) — the
  keyboard shortcuts existed but were undiscoverable.
- **Whole-word toggle in the find bar** (next to match-case).
- **Session restore.** Launching the app plain (no file) reopens the documents
  that were open last time; if there were none, the Open panel shows as
  before. Launching by double-clicking a file just opens that file.
- **macOS window polish:** the close button now shows the standard unsaved-dot
  (`isDocumentEdited`), the title bar gets a proxy icon for the open file
  (⌘-click it for the path, drag it to copy the file), and markdown files can
  be dropped directly onto a window — not just the Dock icon.
- **Read Me in the About window.** The project README (with its screenshots)
  is bundled and opens from a new About button, alongside Changelog /
  Architecture / Design.
- Docs refreshed to match reality: README carries a version badge and the full
  feature list; ARCHITECTURE.md covers the current asset set, bridge actions,
  and the two-shells-one-template design (it still claimed "macOS-only").

## [1.7] — 2026-07-16

### Added

- **File ▸ Save As… (⇧⌘S / Ctrl+Shift+S)** on both platforms. Saves the live
  editor text to a new location — the panel/dialog pre-fills the current name
  and folder — and the window/tab then follows the new file (title, live
  reload, Open Recent). The original file keeps whatever was last saved to it.
  Plain ⌘S/Ctrl+S is unchanged; the in-page save shortcut now ignores
  Shift so the Save As shortcut reaches its own handler.

## [1.6] — 2026-07-16

### Fixed

- **Preview scroll position now really survives live reloads.** The restore
  ran before the first render, when the preview was still empty, so the
  `scrollTop` write clamped to 0 every time — the feature had never worked.
  It now runs after the initial render.
- **Line endings are preserved on save.** The editor (`<textarea>`) hands back
  LF-normalized text per the HTML spec, so saving a CRLF file silently
  rewrote every line ending — a one-character edit produced a whole-file diff.
  Both apps now remember the file's original line endings and re-apply them.
- **"Save" from the close dialog can no longer lose the last keystrokes**
  (macOS). The window closed immediately while the save was still pulling the
  live text from the page; if the page was torn down first, the write fell
  back to a cache up to 250 ms stale. The window now stays open until the
  write has actually succeeded (and stays open if it fails).
- **File ▸ New no longer changes how future documents open** (both platforms).
  Forcing Split mode for a new document also overwrote the remembered view
  mode, so every file opened afterwards started in Split. Programmatic mode
  switches (New's forced Split, Find leaving Preview) no longer persist.
- **Escape closes the find bar from anywhere** (macOS) — previously only while
  focus was inside the find/replace fields; Windows already behaved.
- **A document can no longer steer the viewer to a remote page.** Raw HTML in
  markdown (`<meta http-equiv="refresh">`, forms, scripted navigation) could
  navigate the web view itself to an external site. Non-local navigation is
  now cancelled outright on both platforms — only a real link click opens the
  default browser — and the CSP gained `form-action 'none'`.
- **The About window's Changelog/Architecture/Design docs no longer pollute
  File ▸ Open Recent** (macOS; they are throwaway temp copies — Windows
  already excluded them).
- **Opening the same file twice with different letter case** (e.g. `README.md`
  vs `readme.md` on a case-insensitive volume) now reuses the existing window
  on macOS instead of opening a second one whose saves could fight the first.
- Small cleanups: `.markdn` files are now associated on macOS (the Open panel
  and Windows already accepted them); the zoom-level read guards `localStorage`
  access like every other accessor.

## [1.5] — 2026-07-12

### Fixed

- **Save actually writes to disk now.** The toolbar Save button and `⌘S` never
  wrote the file — only the Save button in the close/quit dialog did. The cause
  was a Swift optional-chaining trap: `save()` ended with
  `completion?(writeToDisk())`, and when `completion` is `nil` (which it is for
  the toolbar button and `⌘S`) the `?` short-circuits the **whole** expression,
  so `writeToDisk()` was never even called. Only the close/quit path passes a
  completion closure, so that was the one save that worked — which is exactly
  what it looked like. The write is now computed before the optional call.
- **Every save trigger behaves the same.** The toolbar Save button used to be
  disabled whenever the app considered the document clean, while `⌘S` and
  File ▸ Save acted unconditionally. The button is now always enabled and always
  saves (the dot on it is the unsaved-changes indicator); the in-page `⌘S`
  handler dropped the same stale dirty-gate.
- **File ▸ Save works while the About window is focused** — the menu now falls
  back to the main document window instead of silently doing nothing when a
  non-document window is key.
- **The macOS installer quits a running copy before replacing it** (gracefully,
  so unsaved documents still prompt; it aborts rather than force-kills).
  Replacing the bundle under a running app left a "zombie" process whose
  windows kept painting but whose Save/menus were dead — the likely culprit
  behind "save stopped working" after an upgrade. The Windows installer
  already stopped the running copy.

### Changed

- README: the "Why" section no longer makes claims about how the OS handles
  `.md` files; the Install section now names the exact install destinations
  (`/Applications` · `%LOCALAPPDATA%\Programs\MarkdownViewer`) and the manual
  build section explains where the built app lands.

## [1.4] — 2026-07-12

### Fixed

- **Find matches are now actually visible.** The editor is a `<textarea>`,
  which never paints its selection while unfocused — and focus deliberately
  stays in the find field while you type — so "1/3" showed in the bar but
  nothing was highlighted in the document. Matches are now rendered on a
  metric-identical backdrop layer behind the editor: every hit gets a soft
  highlight and the current one a strong one (theme-aware `--find-hit` /
  `--find-hit-current` colors, light and dark).
- **Jumping to a match scrolls precisely**, even with soft-wrapped lines — the
  scroll position comes from the highlight's real rendered position instead of
  a newline-count estimate. Horizontal scroll works with Wrap Lines off too.
- **Highlights stay live while editing** with the find bar open (they
  recompute on every keystroke instead of going stale).

### Added

- **Replace is reachable from the find bar itself.** A `▸` toggle at the left
  of the bar expands the Replace field and buttons — previously the only way
  in was the Edit ▸ Find and Replace menu item, so `⌘F` users never saw it.
- `windows/regen-template.py` — the Windows template is now regenerated from
  the shared Mac template by a checked-in script (it was a one-off before), so
  the two can't silently drift. Both templates carry all of the above.

## [1.3] — 2026-07-12

### Added

- **Windows port brought to full parity.** The C# + WebView2 port (`windows/`,
  contributed via GitHub) was forked from the pre-1.2 app; it has been reviewed
  and updated to match 1.2: all data-loss/save fixes (live-text pull, cache
  seeded from disk, close/exit guards that respect a cancelled save), token
  substitution order, CSP + HTML sanitizer, working anchor links,
  undo-preserving edits, File ▸ New (`Ctrl+N`), Open Recent, Reload From Disk,
  always-visible copy buttons — and the AI assistant is removed there too
  (previously-stored DPAPI keys remain in `%APPDATA%\MarkdownViewer\keys` until
  deleted). Its template is now *regenerated* from the shared Mac template plus
  a small fixed delta (bridge, fonts, Ctrl shortcut labels). Also fixed: the
  Windows build referenced the deleted `Resources/CHANGELOG.md` and would not
  have compiled.
- **New documents open in Split mode** on both platforms, so you can type and
  see the preview immediately.

## [1.2] — 2026-07-12

A full review-and-fix release: two data-loss bugs, a crash, several correctness
and security fixes, Intel support, new file management features — and the AI
assistant was removed.

### Added

- **File ▸ New (⌘N).** Create a blank Untitled document. The first save opens a
  save panel with **`Untitled.md`** pre-filled — keep `.md` or type any other
  name/extension. Closing or quitting an unsaved Untitled document prompts, and
  cancelling its save panel safely aborts the close/quit.
- **File ▸ Open Recent.** The last 10 opened files, rebuilt live from
  `UserDefaults` (missing files are hidden), with a Clear Menu item. Also feeds
  the Dock icon's right-click recents.
- **Intel Mac support.** The build compiles `arm64` + `x86_64` slices
  (macOS 11+) and merges them with `lipo` into a universal binary.
- **In-document anchor links work.** `[Jump](#section)` was doubly broken:
  marked v12 emits no heading ids, and the `<base href>` made fragments
  navigate away from the document. Headings now get GitHub-style slug ids
  (deduped `-1`, `-2`, …) and fragment clicks smooth-scroll in place.
- **Design document** (`Resources/DESIGN.md`) describing how every feature
  works, reachable from a new **Design** button in the About window (next to
  Changelog and Architecture).

### Changed

- **Code-block copy buttons are always visible** (brighten on hover) instead of
  appearing only on hover — they were easy to miss entirely.
- **View ▸ Reload From Disk (⌘R) actually re-renders from disk.** It previously
  reloaded a stale temp file, never picking up external changes; it now
  re-renders and asks before discarding unsaved edits.
- Multiple windows **cascade** instead of stacking exactly on top of each other
  (they all shared one frame-autosave name and fought over it).
- The code-block "Copied" green is a `--success` theme variable (brighter in
  dark mode) instead of a hardcoded color.
- Open panel uses the modern `allowedContentTypes` API (removes the last build
  warning).

### Fixed

- **File ▸ Save could erase or stale-save the document.** The save path wrote a
  cached copy of the editor text that started out *empty* — clicking
  File ▸ Save on a freshly opened, unedited document overwrote the file with an
  empty string, and saving after an external-change reload silently reverted
  the external edits. The cache is now seeded from disk on every load/reload,
  and Save first asks the page for the live editor text.
- **Quitting (⌘Q) discarded unsaved edits without asking.** The prompt only
  guarded ⌘W; quit now walks every dirty document, shows the same prompt, and
  defers termination until all chosen saves have finished writing.
- **Undo survived nothing.** Tab-inserts-spaces and Replace / Replace All
  rewrote `editor.value`, wiping the native undo stack — one Tab press and ⌘Z
  was dead. All programmatic edits now go through undo-preserving
  `execCommand('insertText')` (Replace All is a single undo step).
- **Documents containing the literal text `__TITLE__` rendered corrupted** —
  the template substituted the markdown body before the title token.
  `__MARKDOWN__` is now always substituted last.
- **Render failure no longer causes a once-per-second reload loop** (the error
  page never recorded the file's modification date).
- **Find matches by regex on the original text** instead of lowercasing the
  whole document — offsets could drift (corrupting Replace) on characters whose
  lowercase form changes length, e.g. Turkish `İ`.

### Security

- **Rendered documents are sandboxed.** Markdown may contain raw HTML;
  previously a malicious document could run script (via inline event handlers)
  with access to the native bridge — including overwriting the file — or load
  remote code. Now: a Content-Security-Policy blocks remote scripts and all
  network connections from the page; the preview strips `on*` attributes and
  `javascript:`/`vbscript:` URLs after every render; and `</script>` inside a
  document is explicitly escaped instead of relying on implicit
  `JSONSerialization` behavior. Local and remote images still render.

### Removed

- **The entire AI assistant** (chat panel, Improve Selection,
  Generate & Insert, provider settings, Keychain key storage, all networking
  code). The app makes no network requests at all now. Any API keys previously
  stored remain in the macOS Keychain under `com.dave.markdownviewer.ai` —
  delete them with Keychain Access if you no longer want them.

## [1.1] — 2026-07-12

### Added

- **File ▸ Open Path… (⇧⌘G).** Open a document by typing or pasting an absolute
  path (e.g. `/Users/James/USER.md`) instead of navigating the Finder open panel.
  Pre-fills from the clipboard when it already holds a path, expands `~`, accepts
  `file://` URLs, and tolerates quoted or backslash-escaped paths copied from a
  terminal. Shows a "File not found" alert rather than failing silently.

- **Copy button on fenced code blocks.** Each code block in the preview gets a
  button in its top-right corner that copies exactly the text between the
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

## [1.0] — 2026-06-19 (initial release)

### Added

- Initial standalone macOS viewer: opens `.md` files in a real window on
  double-click instead of only the Quick Look preview.
- Rendering via bundled `marked` + `highlight.js` with GitHub-style CSS, fully
  offline after the first build.
- Light / Dark / System theme toggle, following the OS in System mode.
- Live reload on file change, preserving scroll position.
- Multiple files open as native window tabs.
- Preview / Edit / Split modes, save, and find & replace.
- Hand-rolled `build.sh` (no Xcode project): compiles with `swiftc`, assembles and
  ad-hoc signs the `.app`, and registers it with Launch Services.
