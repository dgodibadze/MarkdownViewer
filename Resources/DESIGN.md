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
3. The result is written to a temp HTML file and loaded with
   `loadFileURL(allowingReadAccessTo: "/")` so bundled assets and local images
   both resolve.
4. In the page, `marked.parse()` renders GitHub-flavored markdown into the
   preview, `highlight.js` colorizes code, heading ids are generated
   (GitHub-style slugs, deduped `-1`, `-2`, …), the output is sanitized, and
   copy buttons are attached to code blocks.

## Security model

Markdown may contain raw HTML, so rendered documents are treated as untrusted:

- **CSP** (meta tag in the template head): scripts and styles may only come
  from `file:` (the bundled assets) or be inline (the app's own code); images
  may be local or remote; the page may open **no** network connections
  (`connect-src 'none'`). A malicious document cannot load remote code or
  exfiltrate content.
- **Sanitizer**: `innerHTML` never executes `<script>` tags, so the one raw-HTML
  vector that actually runs is inline event handlers. After every render, all
  `on*` attributes and `javascript:`/`vbscript:` URLs are stripped from the
  preview.
- **`</script>` escaping**: the markdown is embedded inside an inline script,
  so `jsStringLiteral()` explicitly escapes `</` — a document containing a
  literal `</script>` must not terminate the embedding script.
- **No networking**: the app itself makes no network requests of any kind.

## File management

- **File ▸ New (⌘N)** opens a blank **Untitled** document (`fileURL == nil`,
  starts in Edit mode). The first save runs an `NSSavePanel` sheet with
  `Untitled.md` pre-filled; `allowsOtherFileTypes` lets the user type any other
  extension. After a successful first save the window retitles, live-reload
  watching starts, and the page re-renders so `<base href>` points at the real
  folder. Cancelling the panel reports the save as *not done* — a close or quit
  waiting on it is aborted rather than discarding the document.
- **File ▸ Open Recent** lists the last 10 opened files, persisted in
  `UserDefaults` (`recentFiles`) and rebuilt from disk every time the menu
  opens (missing files are hidden; Clear Menu empties it). Opens also feed
  `NSDocumentController` so the Dock icon's right-click menu matches.

## Save / dirty pipeline (data-loss invariants)

The `<textarea>` editor is the source of truth. Three rules keep saves safe:

1. **The Swift-side text cache (`lastText`) is seeded from disk** on every
   load and live reload — never empty, never stale from before a reload.
2. **Save pulls the live text first**: `save()` asks the page for
   `window.__getText()` and writes what it returns, falling back to the cache
   only if the page can't answer (e.g. the error page is showing). The page
   also pushes `change` messages (debounced 250ms) as a belt-and-braces cache.
3. **Every exit path is guarded**: closing a window (⌘W) *and* quitting (⌘Q)
   both show Save / Don't Save / Cancel for dirty documents. Quit uses
   `.terminateLater` and only completes after all chosen saves have hit disk,
   because saves are asynchronous.

After a successful write, Swift calls `window.__onSaved()` (clears the dirty
flag) and refreshes its stored modification date so its own write doesn't
trigger the live-reload watcher.

## Live reload

Each controller polls the file's modification date once a second. A change
re-renders the document — **suspended while the buffer is dirty** so edits are
never clobbered. Preview scroll position survives reloads via
`sessionStorage`. The error page also records the mtime; otherwise a failed
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
- **Scroll sync (Split mode)** uses a driver-pane model: only the pane claimed
  by real input (`wheel`/`mousedown`/`touchstart`/`keydown`/`focusin`)
  propagates its scroll, plus a 1px write threshold. Timer/flag guards are
  unsound here — writing `scrollTop` fires the destination's scroll event
  asynchronously and the echo ratchets both panes along.
- **Zoom** (⌘+/⌘−/⌘0) scales the preview font *and* the column width/padding
  by the same `--zoom` factor, so characters-per-line stays constant.
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
stack exactly); later windows cascade down-right.

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
- **Keeping the two in sync**: `windows/Resources/template.html` is
  *regenerated* from the Mac `Resources/template.html` by
  **`windows/regen-template.py`** (delta: message bridge chrome.webview-first,
  Windows font stacks, Ctrl shortcut labels, an in-page app-shortcut block,
  and a `window.__escape` hook). Never hand-edit the Windows template —
  change the Mac one and re-run the script (it fails loudly if an anchor
  drifted). Behavior changes in `main.swift` must be mirrored in
  `windows/Program.cs` and vice versa.
