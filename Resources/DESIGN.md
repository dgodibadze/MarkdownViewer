# MarkdownViewer — Design: How It Works

This is the behavioral companion to [ARCHITECTURE.md](ARCHITECTURE.md) (which
covers structure). It explains how each feature actually works and why it's
built that way, so changes don't quietly break the invariants.

## Rendering pipeline

1. Opening a file creates one `ViewerWindowController` (one window/tab per file).
2. `Renderer.render()` reads `template.html` and substitutes four tokens:
   `__RES__` (bundled assets dir), `__BASE__` (the file's folder, so relative
   images resolve), `__TITLE__`, and `__MARKDOWN__` (the text as a JSON-escaped
   JS string literal). **`__MARKDOWN__` is always substituted last** — it
   injects arbitrary document text, and any token replaced after it would also
   match occurrences of that token *inside* the document.
3. The result is written to a temp HTML file. The page can read only that temp
   directory; bundled assets and document-relative files resolve through two
   narrowly scoped URL mappings (`mdv-resource:` / `mdv-document:` on macOS,
   private virtual HTTPS hosts on Windows).
4. In the page, `marked.parse()` renders GitHub-flavored markdown into the
   preview, `highlight.js` colorizes code, heading ids are generated
   (GitHub-style slugs, deduped `-1`, `-2`, …), the output is sanitized, and
   copy buttons are attached to code blocks.
5. **Mermaid & KaTeX load lazily**: ` ```mermaid ` fences become theme-aware
   diagrams and `$$…$$` / `\(…\)` / `\[…\]` become math, but the bundled
   libraries are only injected through the scoped asset mapping when a
   document actually uses them. Theme switches re-render diagrams (the theme
   is baked into Mermaid's SVG). KaTeX caveat: marked runs first, so
   markdown-significant characters inside inline math can be transformed
   before KaTeX sees them.
6. **Task checkboxes are live**: the Nth preview checkbox maps to the Nth
   `[ ]`/`[x]` marker in the source (positions recomputed at click time);
   clicking writes the toggle back through `spliceEditor()` (undoable, marks
   dirty). Only Marked-generated inputs carry a random per-render token; raw
   HTML inputs are removed. If source/render counts do not match 1:1 (for
   example task syntax in a code fence), checkboxes stay disabled rather than
   guess.

## Security model

Markdown may contain raw HTML, so rendered documents are treated as untrusted:

- **CSP** permits only scoped bundled scripts/styles/fonts, document-local or
  data images, and the app's inline bootstrap. Remote images, connections,
  forms, objects, and unrelated base URLs are blocked.
- **Scoped local resources** expose only bundled assets and regular files under
  the document directory. Document resources are capped at 64 MiB; traversal,
  symlink/junction escapes, pipes, devices, and unbounded reads are rejected.
- **Navigation + bridge lockdown**: only the generated top-level temp page may
  navigate or send native bridge messages. User-clicked web/mail links open in
  the default app; document-local links are resolved inside the mapped folder
  and safe document/image/media types are handed to the native shell.
  Executable-capable local targets are revealed in Finder/Explorer instead;
  every other scheme and subframe is rejected.
- **Allowlist sanitizer** keeps only normal Markdown tags and required
  attributes/URL forms. Styles, embedded pages, scripts, arbitrary controls,
  event handlers, and active URL schemes are removed. Mermaid and KaTeX output
  receives a second executable-attribute scrub after rendering.
- **`</script>` escaping**: the markdown is embedded inside an inline script,
  so `jsStringLiteral()` explicitly escapes `</` — a document containing a
  literal `</script>` must not terminate the embedding script.
- **No networking**: the app itself makes no network requests of any kind.

## File management

- **File ▸ New (⌘N)** opens a blank **Untitled** document (`fileURL == nil`,
  starts in Split mode — applied after page load via the deferred `startMode`,
  and *without* persisting, so it never changes how other documents open).
  The first save runs an `NSSavePanel` sheet with
  `Untitled.md` pre-filled; `allowsOtherFileTypes` lets the user type any other
  extension. After a successful first save the window retitles, live-reload
  watching starts, and the page re-renders so `<base href>` points at the real
  folder. Cancelling the panel reports the save as *not done* — a close or quit
  waiting on it is aborted rather than discarding the document.
- **File ▸ Save As… (⇧⌘S / Ctrl+Shift+S)** runs the same panel pre-filled with
  the current name/folder, pulling the live editor text first. The document
  adopts the new path only after the write succeeds and only when no other
  window/tab already owns it; the original keeps its last-saved content. The
  in-page ⌘S/Ctrl+S handler ignores Shift so the
  shortcut reaches the native menu (macOS) / in-page shortcut block (Windows).
- **File ▸ Open Recent** lists the last 10 opened files, persisted in
  `UserDefaults` (`recentFiles`) and rebuilt from disk every time the menu
  opens (missing files are hidden; Clear Menu empties it). Opens also feed
  `NSDocumentController` so the Dock icon's right-click menu matches. The
  About window's bundled docs open as throwaway temp copies and are *not*
  recorded. Deduplication of already-open files is case-insensitive (canonical
  path), matching case-insensitive volumes.

## Save / dirty pipeline (data-loss invariants)

The `<textarea>` editor is the source of truth. Three rules keep saves safe:

1. **The Swift-side text cache (`lastText`) is seeded from disk** on every
   load and live reload — never empty, never stale from before a reload.
2. **Save pulls the live text first**: `save()` asks the page for
   `window.__getText()` and writes what it returns, falling back to the cache
   only if the page can't answer (e.g. the error page is showing). The page
   also pushes `change` messages (debounced 250ms) as a belt-and-braces cache.
3. **Every exit path is guarded**: closing a window (⌘W) *and* quitting (⌘Q)
   both show Save / Don't Save / Cancel for dirty documents. Because saves are
   asynchronous, the window stays open until its save has actually hit disk,
   and quit uses `.terminateLater` until all chosen saves have completed.

**File format is preserved**: the native side remembers UTF-8/UTF-16/UTF-32
BOM state and LF/CRLF/CR line endings, then restores them after the textarea's
mandatory LF normalization. Invalid or mixed-format input requires explicit
confirmation before conversion to UTF-8/LF; a clean Save does not rewrite
bytes. Writes use an atomic same-directory replacement so a crash cannot leave
a truncated file.

**External edits cannot be overwritten silently**: each load stores a
size/mtime/SHA-256 fingerprint. Every save compares the current disk state with
that fingerprint and requires an explicit Save Anyway decision after an
external edit, deletion, or read failure. Rendering, editor seeding, and the
fingerprint all come from one byte snapshot, so a concurrent replacement cannot
make displayed text older than the accepted save baseline. An initial read
failure is never treated as an empty document.

After a successful write, Swift calls `window.__onSaved()` (clears the dirty
flag) and refreshes its stored modification date so its own write doesn't
trigger the live-reload watcher.

**Every save trigger behaves identically** — the toolbar Save button, `⌘S` /
`Ctrl+S`, and the File ▸ Save menu all save unconditionally, even when the
document is clean (re-writing the same bytes is harmless; for an Untitled
document it opens the save panel). The button is never disabled: gating it on
the dirty flag made it the one trigger that could refuse while the menu path
saved, which read as "the Save button doesn't work". The dirty **dot** on the
button is the unsaved-changes indicator.

## Live reload

Each controller polls the file's modification date once a second, including a
transition to a missing file. A change
re-renders the document — **suspended while the buffer is dirty** so edits are
never clobbered. Preview scroll position survives reloads via
`sessionStorage` (restored *after* the initial render — before it, the empty
preview clamps any `scrollTop` write to 0). The error page also records the
mtime; otherwise a failed
render would re-render every tick forever. View ▸ Reload From Disk (⌘R)
triggers the same re-render manually, confirming first if edits would be lost.

## Editor behaviors

- **Undo**: all programmatic edits (Tab-inserts-spaces, Replace / Replace All)
  go through `spliceEditor()`, which uses
  `document.execCommand('insertText')` so WebKit records them as normal
  undoable edits. **Never assign `editor.value` directly** — that wipes the
  native undo stack. Replace All is one whole-document splice = one undo step.
- **Find & Replace** operates on the raw markdown with a case-insensitive
  regex on the *original* string (lowercasing a copy shifts offsets for
  characters whose lowercase form changes length). Focus stays in the find
  field while you type — and since an unfocused `<textarea>` never paints its
  selection, matches are shown on a **backdrop layer** behind the editor: a
  div carrying the same text with `<mark>`s (all hits soft, current hit
  strong). The backdrop must share the editor's *exact* metrics — font, size,
  padding, wrap mode, border, `overflow: scroll` with equal scrollbar width —
  or highlights drift; both live in one CSS rule. Scroll-to-match uses the
  mark's real rendered position, so it's exact under soft wrap. The `▸`
  toggle in the bar expands the Replace controls (also via ⌥⌘F / Ctrl+H).
  Escape closes the bar from anywhere in the page, not just the find fields.
  Opening Find from Preview mode switches to Edit without persisting the mode.
- **Edit-menu commands** (Undo/Redo/Cut/Copy/Paste/Delete/Select All): macOS
  gets these from the AppKit responder chain, so its menu items target the web
  view directly. WinForms has no responder chain, so the Windows Edit menu
  calls `window.__editCmd` / `__editSelection` / `__editInsert` instead. Those
  items show shortcut *labels* but deliberately register **no** WinForms
  accelerator — WebView2's browser accelerators already handle Ctrl+Z/X/C/V/A
  inside the textarea, and a registered accelerator would intercept the key
  before the page saw it. Clipboard payloads travel through the native side
  because Chromium refuses `execCommand('cut'/'copy'/'paste')` without a user
  gesture and an injected script isn't one; text changes go through
  `spliceEditor()` so undo survives. In Preview mode the mutating commands
  no-op and Copy/Select All act on the rendered text, matching what the
  responder chain does on macOS when the textarea isn't first responder.
- **Scroll sync (Split mode)** uses a driver-pane model: only the pane claimed
  by real input (`wheel`/`mousedown`/`touchstart`/`keydown`/`focusin`)
  propagates its scroll, plus a 1px write threshold. Timer/flag guards are
  unsound here — writing `scrollTop` fires the destination's scroll event
  asynchronously and the echo ratchets both panes along.
- **Zoom** (⌘+/⌘−/⌘0, also View-menu items) scales the preview font *and* the
  column width/padding by the same `--zoom` factor, so characters-per-line
  stays constant.
- **Table of Contents** (View menu, ⇧⌘T / Ctrl+T): a sidebar rebuilt from the
  headings on every render; clicking scrolls the preview to the real heading
  position (in Edit mode: approximated from the heading's character position
  in the source, since the hidden preview has no geometry). Open/closed state
  persists in `localStorage("tocOpen")`.
- **Find** also has a Unicode-aware whole-word toggle (letters, combining
  marks, numbers, and underscore count as word characters).
- **Print (⌘P / Ctrl+P)** prints the rendered preview only — `@media print`
  hides the toolbar/editor/sidebar and un-clamps the page. Pending preview,
  Mermaid, and KaTeX work finishes before the native
  print dialog (WKWebView `printOperation` / WebView2 `ShowPrintUI`), whose
  "Save as PDF" is the PDF export path.
- **Anchor links**: heading ids are generated at render time and fragment
  clicks are intercepted in-page (`scrollIntoView`) — with a `<base href>` set,
  letting the browser navigate `#x` would leave the document.

## Theme system

Light / Dark / System, persisted in `localStorage("theme")`, resolved before
first paint by an inline script in `<head>` (no flash). The toolbar button
cycles the three choices; System mode live-follows the OS via
`matchMedia('(prefers-color-scheme: dark)')`. All UI colors go through the
theme variables `--bg --surface --text --muted --border --accent --success` —
never hardcode a color.

## Windows and frames

One window per file, native tabbing preferred. Only the **first** window uses
the frame-autosave name (multiple windows sharing one name fight over it and
stack exactly); later windows cascade down-right. The close button shows the
standard unsaved-changes dot (`isDocumentEdited`), the title bar carries a
proxy icon for the open file, and markdown files can be dropped onto any
window (a `WKWebView` subclass intercepts file drags for markdown extensions;
everything else falls through to normal web-view drag handling).

## Session restore

A plain launch (no document) reopens the files that were open when the app
last quit (persisted per change to the open-window set; frozen while quitting
so teardown doesn't empty it one window at a time). If the session was empty,
the Open panel shows as before. Launching via a double-clicked file opens
just that file.

## AI assistant (removed in 1.2)

The app previously shipped a multi-provider AI assistant (chat panel, improve
selection, generate & insert, Keychain-stored keys). It was removed in v1.2 at
the owner's request — the app is now fully offline. Keys previously stored
remain in the macOS Keychain under `com.dave.markdownviewer.ai` until deleted
via Keychain Access. If it ever comes back, the old implementation lives in
git history before the v1.2 commit.

## Versioning policy

- **Versions bump by 0.1 per release batch** — a coherent set of fixes/features
  shipped together gets ONE version and ONE combined changelog section (not one
  bump per individual fix). Update `CFBundleShortVersionString` +
  `CFBundleVersion` in `Info.plist`.
- Every release gets a dated section in `CHANGELOG.md` (repo root — it is
  copied into the bundle at build time and shown by the About window).

## Platform support

- **macOS 11+, universal binary** (arm64 + x86_64) — `build.sh` compiles both
  slices and merges them with `lipo`. Shell: Swift + AppKit + WKWebView
  (`Sources/main.swift`).
- **Windows 10+** — a native port in `windows/`: C# + WinForms + WebView2
  (`Program.cs`), tabs in one window, single-instance via a named pipe.
  It shares the rendering assets and implements the same design described in
  this document (same bridge actions, same save/dirty invariants, same
  security model).
- **Known intentional difference**: macOS supports multiple independent
  windows (each optionally tabbed); Windows is one window with tabs. Every
  other capability gap found in the v2.0 audit was closed — see
  `REFERENCE.md` §11 for the exhaustive difference list.
- **Keeping the two in sync**: `windows/Resources/template.html` is
  *regenerated* from the Mac `Resources/template.html` by
  **`windows/regen-template.py`** (delta: message bridge chrome.webview-first,
  Windows font stacks, Ctrl shortcut labels, an in-page app-shortcut block,
  and a `window.__escape` hook). Never hand-edit the Windows template —
  change the Mac one and re-run the script (it fails loudly if an anchor
  drifted). Behavior changes in `main.swift` must be mirrored in
  `windows/Program.cs` and vice versa.
