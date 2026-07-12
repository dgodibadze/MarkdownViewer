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
- **API keys** live in the macOS Keychain (one item per provider), entered via
  a native secure field; they never touch the web view, and the Gemini key is
  sent as a header, never in the URL.

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

- **Undo**: all programmatic edits (Tab-inserts-spaces, Replace / Replace All,
  AI insert/improve) go through `spliceEditor()`, which uses
  `document.execCommand('insertText')` so WebKit records them as normal
  undoable edits. **Never assign `editor.value` directly** — that wipes the
  native undo stack. Replace All is one whole-document splice = one undo step.
- **Find & Replace** operates on the raw markdown with a case-insensitive
  regex on the *original* string (lowercasing a copy shifts offsets for
  characters whose lowercase form changes length). Focus stays in the find
  field while matches are selected and scrolled into view.
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

## AI assistant

Three modes flow through one `ai` bridge action: **improve** (revise the
selection per an instruction), **generate** (insert markdown at the cursor),
and **chat** (docked panel; each request carries the full document as context
plus the visible history). Providers: OpenAI-compatible (Groq, Nous, OpenAI),
Anthropic, and Gemini — base URL and model are user-editable per provider in
AI ▸ Settings…. Requests run on `URLSession` with a 60s timeout; responses
resolve JS promises via `window.__aiResult` / `window.__aiError` matched by id.

## Versioning policy

- **Every fix or new piece of functionality bumps the version by 0.1**
  (`CFBundleShortVersionString` + `CFBundleVersion` in `Info.plist`).
- Every bump gets a dated section in `CHANGELOG.md` (repo root — it is copied
  into the bundle at build time and shown by the About window).
- One git commit per version, message `v<X.Y>: <summary>`.

## Platform support

- **macOS 11+, universal binary** (arm64 + x86_64) — `build.sh` compiles both
  slices and merges them with `lipo`.
- **Windows is not supported and can't be** with this codebase: the shell is
  AppKit + WKWebView. If a Windows/Linux version is ever wanted, the entire
  in-page UI (`template.html`) ports nearly as-is; only the ~1000-line native
  shell would need rewriting — Tauri (system WebView, small binaries) would be
  the closest match to the current design.
