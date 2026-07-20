# MarkdownViewer — Complete Feature & Behavior Reference

Exhaustive documentation of what this app actually does, on both platforms,
as of **v2.1**. Unlike `FEATURES.md` (a stripped-down, tech-agnostic spec for
rebuilding a *similar* app elsewhere), this file documents *this* app,
including every shortcut, every menu item, and every known macOS/Windows
difference. Source of truth: `Sources/main.swift`, `windows/Program.cs`,
`Resources/template.html` (shared UI, regenerated into
`windows/Resources/template.html` by `windows/regen-template.py`),
`Resources/DESIGN.md`, `Resources/ARCHITECTURE.md`, `CHANGELOG.md`.

> **Compile status note**: `windows/Program.cs` has been statically reviewed
> but **not compiled** since the v1.9–v2.1 changes (no .NET SDK on the
> machine used to write them; v2.1 fixed a v2.0 compile error found in review). Everything below about Windows behavior is read
> from source, not verified against a running build — build on Windows before
> releasing.

---

## 1. View modes

Three modes, per open document, switchable at any time without losing edits:

| Mode | What it shows |
|---|---|
| **Preview** | Rendered HTML only, read-only |
| **Edit** | Raw markdown source in a plain-text editor only |
| **Split** | Both panes side by side, draggable divider, synced scrolling |

- New/Untitled documents always open in **Split** mode (applied after the
  page finishes its initial navigation — calling the mode-set function before
  that point is a silent no-op).
- Mode does **not** persist per document across app restarts, but the
  in-page toggle (Preview/Edit/Split buttons + shortcuts) is always live.
- Opening the Find bar from Preview mode auto-switches to Edit mode (you
  can't highlight matches in read-only rendered HTML) without persisting
  that mode change.

## 2. Rendering

- GitHub-flavored markdown via **marked**, syntax highlighting via
  **highlight.js**, GitHub-style CSS (**github-markdown-css**) — all bundled,
  all offline, pinned versions fetched once by `build.sh`.
- **Heading IDs**: GitHub-style slugs, deduplicated on collision
  (`heading`, `heading-1`, `heading-2`, …).
- **Table of Contents sidebar** (⇧⌘T / Ctrl+T): rebuilt from headings on
  every render; click scrolls to the real heading position in Preview, or an
  approximated position (from source character offset) in Edit mode, since
  the hidden preview has no layout geometry there. Open/closed state
  persists in `localStorage("tocOpen")`.
- **Mermaid diagrams** (` ```mermaid ` fences) and **KaTeX math**
  (`$$…$$`, `\(…\)`, `\[…\]`) — bundled, loaded lazily (only injected via the
  scoped asset mapping when a document actually uses the syntax). Diagrams
  re-render on theme switch because Mermaid bakes theme colors into its SVG
  output. KaTeX caveat: marked runs first, so markdown-significant characters
  inside inline math can be transformed before KaTeX sees them.
- **Clickable task checkboxes** in the preview — the Nth rendered checkbox
  maps to the Nth `[ ]`/`[x]` marker in the source (recomputed at click
  time); the toggle writes back into the source document through the
  undo-safe edit path and marks the document dirty. Only marked-generated
  checkbox inputs are live (carry a per-render token); raw-HTML `<input>`
  checkboxes are stripped. If source/render checkbox counts don't line up
  1:1 (e.g. task syntax inside a code fence), checkboxes stay disabled
  rather than guessing.
- **In-document anchor links** work — fragment clicks are intercepted
  in-page and use `scrollIntoView` rather than a real navigation (because a
  `<base href>` is set, letting the browser navigate `#x` natively would
  leave the document).
- **Copy-to-clipboard button** on every fenced code block. Tries
  `navigator.clipboard` first, falls back to a hidden textarea +
  `document.execCommand('copy')` (clipboard API isn't always permitted from
  a `file://`-style origin).
- **Live reload**: each window polls its file's modification time once a
  second (including a transition to "file went missing") and re-renders on
  change — **suspended while the buffer is dirty**, so an external edit
  never clobbers unsaved local edits. Preview scroll position survives a
  reload via `sessionStorage`, restored after the render completes. A failed
  render also records its own mtime so a persistently broken file doesn't
  re-render every tick forever.

## 3. Editing

- Plain-text editor, monospace font, live **word count** and **character
  count**.
- **Wrap Lines** toggle (menu + in-page control).
- **Tab key** inserts spaces (not a literal tab) without breaking undo.
- **Undo/redo**: every programmatic edit (checkbox toggle, Tab-indent,
  Replace/Replace All) goes through `document.execCommand('insertText')`
  rather than reassigning the textarea's value directly — the latter wipes
  the native undo stack. Replace All is a single undo step.
- **Find** (⌘F / Ctrl+F) and **Find & Replace** (⌥⌘F on Mac, **Ctrl+H** on
  Windows — not just a modifier swap, a different key):
  - Live match count, current-match emphasis vs. other matches (soft
    highlight), scroll-to-current-match using the match's real rendered
    position (exact under soft wrap).
  - Case-sensitive toggle and Unicode-aware whole-word toggle (letters,
    combining marks, numbers, underscore count as word characters).
  - Regex match runs case-insensitively against the *original* string
    (lowercasing a copy first would shift offsets for characters whose
    lowercase form changes length).
  - Matches render on a backdrop `<mark>` layer behind the (unfocused,
    hence non-selection-painting) textarea; the backdrop must share the
    editor's exact font, size, padding, wrap mode, border, and
    `overflow: scroll` (not `auto` — scrollbar width shifts wrap points) or
    highlights drift off the text.
  - `▸` toggle expands/collapses the Replace controls.
  - Escape closes the find bar from anywhere on the page (via a
    `window.__escape` hook on Windows, where WinForms otherwise swallows
    Escape before it reaches the page).
- **Zoom** (⌘+/⌘−/⌘0, also View menu): scales preview font size *and* the
  `.page` column max-width/padding together via one `--zoom` factor, so
  characters-per-line stays constant — no re-wrapping on zoom.

## 4. File & document management

- **New** (⌘N / Ctrl+N): blank Untitled document, opens in Split mode. First
  save shows a save panel pre-filled with `Untitled.md`; any typed extension
  is accepted (`allowsOtherFileTypes`). Cancelling the panel reports the save
  as *not done* — a close/quit waiting on it is aborted, not treated as
  "discard".
- **Open** (⌘O / Ctrl+O), **Open Path…** (⇧⌘G / Ctrl+Shift+G, for pasted
  paths), and double-click / file-association launch.
- **Save** (⌘S / Ctrl+S): unconditional on every trigger (toolbar button,
  shortcut, menu item) — never gated on a dirty flag, so re-writing
  unchanged bytes is a harmless no-op rather than a "sometimes does
  nothing" trap. The dirty **dot** on the Save button is purely an unsaved
  indicator, not a disable condition.
- **Save As…** (⇧⌘S / Ctrl+Shift+S): pre-filled with current name/folder,
  pulls live editor text first. The document adopts the new path only after
  the write succeeds *and* only if no other window/tab already owns that
  path; the original file keeps its last-saved content untouched.
- **Open Recent**: last 10 files, persisted (`UserDefaults` on Mac), rebuilt
  against the filesystem every time the menu opens (missing files hidden).
  Deduplication is canonical-path and case-insensitive on **both** platforms
  (resolves symlinks/junctions), so one file reached by two path spellings
  yields one entry. "Clear Menu" empties it. The About window's bundled
  docs open as throwaway temp copies and are never recorded as recent.
- **Session restore**: a plain launch with no file argument reopens
  whatever was open when the app last quit (persisted per change to the
  open-window set, frozen during quit teardown). Launching via a
  double-clicked file opens just that file. If the session was empty, the
  Open panel shows instead.
- **Drag & drop** onto an open window, filtered to markdown-family extensions
  on **both** platforms (md/markdown/mdown/mkd/mkdn/mdwn/markdn/mdtxt/text/
  txt/rmd/qmd/mdx/mdc). Anything else is refused: on macOS it falls through to
  the web view's normal drag handling; on Windows the drop effect is `None`.
  *(Windows was unfiltered before v2.0.)*
- **Print / Save as PDF** (⌘P / Ctrl+P): prints the rendered preview only —
  `@media print` hides toolbar/editor/sidebar and un-clamps the page width.
  Waits for pending preview/Mermaid/KaTeX rendering to finish before
  invoking the native print dialog; "Save as PDF" in that dialog is the
  export path (no separate PDF export feature).
- **Reload From Disk** (⌘R / Ctrl+R, **also F5 on Windows**): manual
  re-render, confirms first if it would discard unsaved edits.

## 5. Data-safety invariants

- **The live editor is the save source of truth.** `save()` asks the page
  for its current text first; a separately-maintained native-side cache is
  only a fallback for when the page can't answer (e.g. an error page is
  showing), and that cache itself is always freshly seeded from disk on
  every load/reload — never left stale from before a reload.
- **Format preservation**: UTF-8 / UTF-8-BOM / UTF-16 LE/BE / UTF-32 LE/BE
  encoding and LF/CRLF/CR line endings are detected on load and restored on
  save, even though the in-editor textarea normalizes to LF internally.
  Invalid or mixed-format input requires explicit confirmation before
  converting to UTF-8/LF; an ordinary clean save does not silently rewrite
  bytes into a different format.
- **Atomic writes**: same-directory temp-file-then-replace on both
  platforms (`Data.write(options: .atomic)` on Mac, manual
  stage-then-`File.Replace`/`File.Move` on Windows) — a crash mid-write
  can't leave a truncated file.
- **External-change conflict detection**: each load records a
  size/mtime/SHA-256 fingerprint; every save compares current disk state
  against it and requires an explicit "Save Anyway" if the file changed
  underneath you (external edit, deletion, or read failure). Rendering,
  editor seeding, and the fingerprint all derive from one byte snapshot, so
  a concurrent replacement can't make displayed text appear older than the
  accepted save baseline. An initial read failure is never treated as an
  empty document.
- **Exit guards**: closing a window (⌘W / Ctrl+W) and quitting the app both
  prompt Save / Don't Save / Cancel for dirty documents. Because saves are
  asynchronous, the window/app stays alive until the chosen save(s) have
  actually completed (`.terminateLater` on quit).
- After a successful write: the dirty flag clears, and the app refreshes its
  own stored modification time so its own write doesn't trigger the
  live-reload watcher as if it were an external change.

## 6. Security / trust model

Rendered markdown is treated as untrusted input, since it can contain raw
HTML:

- **Content-Security-Policy**: only bundled scripts/styles/fonts, the app's
  inline bootstrap, and document-local/data images are permitted;
  `connect-src 'none'`, `form-action 'none'`; remote loads, embeds, and
  unrelated base URLs are blocked.
- **Scoped local-resource hosts**: `mdv-resource:` (bundled assets) and
  `mdv-document:` (the specific open document's folder, for relative image
  paths) on macOS; private virtual HTTPS hosts on Windows. Document
  resources capped at 64 MiB; path traversal, symlink/junction escapes,
  pipes, devices, and unbounded reads are rejected.
- **Navigation + bridge lockdown**: only the app-generated top-level temp
  page may navigate or send native bridge messages. User-clicked web/mail
  links open in the default external app; document-local links resolve
  inside the mapped folder, with safe document/image/media types handed to
  the native shell and executable-capable local targets revealed in
  Finder/Explorer instead of opened. Every other scheme and all subframes
  are rejected.
- **HTML sanitizer**: allowlists normal Markdown-produced tags/attributes;
  strips `on*` event-handler attributes, `style`, embedded pages, scripts,
  arbitrary form controls, and active URL schemes. Mermaid/KaTeX output gets
  a second executable-attribute scrub after rendering.
- **`</script>` escaping**: markdown text is embedded inside an inline
  script as a JSON-encoded string literal; `</` sequences are explicitly
  escaped so a literal `</script>` inside a document can't terminate the
  embedding script early.
- **Zero network requests** from the app itself, of any kind.
- On Windows specifically: WebView2 devtools, autofill, password-save
  prompts, and the default status bar are explicitly disabled.

## 7. Theming

Light / Dark / System, persisted in `localStorage("theme")`, resolved
before first paint by an inline `<head>` script (no flash of wrong theme).
The toolbar theme button cycles the three choices; System mode live-follows
the OS via `matchMedia('(prefers-color-scheme: dark)')`. All UI colors go
through theme variables (`--bg --surface --text --muted --border --accent
--success`) — never hardcoded per-element.

## 8. Tabs / windows model — **the biggest platform difference**

| | macOS | Windows |
|---|---|---|
| Model | One `NSWindow` per document; native macOS window tabs group them; a `Window` menu lists open windows | One `MainForm`; every document is a `TabControl` tab in that single window |
| Second top-level window | Yes — you can have N independent windows, each optionally tabbed | No — all documents live as tabs in one window |
| Close a tab | ⌘W closes the window/tab | Ctrl+W closes the tab; middle-click on a tab header also closes it (no Mac equivalent — Mac has no middle-click-to-close since ⌘W already targets the frontmost window) |
| Frame position | Only the first window uses the frame-autosave name (to avoid multiple windows fighting over one saved position/stacking exactly); later windows cascade down-right | The single window's frame persists across launches (validated against current monitors) — since v2.1 |
| Second-launch / "Open with" routing | Handled for free via `application(_:open:)` | A named-pipe single-instance server forwards the file path into the already-running process |
| File dropped onto window | Opens only if extension is markdown-family | Same (since v2.0) |

## 9. Full keyboard shortcut reference

| Action | macOS | Windows | Notes |
|---|---|---|---|
| Preview mode | ⌘1 | Ctrl+1 | |
| Edit mode | ⌘2 | Ctrl+2 | |
| Split mode | ⌘3 | Ctrl+3 | |
| New | ⌘N | Ctrl+N | |
| Open… | ⌘O | Ctrl+O | |
| Open Path… | ⇧⌘G | Ctrl+Shift+G | |
| Save | ⌘S | Ctrl+S | Unconditional — see §4 |
| Save As… | ⇧⌘S | Ctrl+Shift+S | |
| Print / Save as PDF | ⌘P | Ctrl+P | |
| Close tab/window | ⌘W | Ctrl+W | |
| Quit / Exit | ⌘Q | Alt+F4 | |
| Undo | ⌘Z | Ctrl+Z | Menu item on both since v2.0 |
| Redo | ⇧⌘Z | Ctrl+Y | |
| Cut | ⌘X | Ctrl+X | |
| Copy | ⌘C | Ctrl+C | In Preview mode copies the rendered selection |
| Paste | ⌘V | Ctrl+V | No-op in Preview mode (nothing editable) |
| Delete | *(menu only, no shortcut)* | *(menu only, no shortcut)* | |
| Select All | ⌘A | Ctrl+A | In Preview mode selects the rendered text |
| Find | ⌘F | Ctrl+F | |
| Find & Replace | ⌥⌘F | **Ctrl+H** | Different key, not just modifier |
| Table of Contents | ⇧⌘T | Ctrl+T | |
| Wrap Lines | *(menu only, no shortcut)* | *(menu only, no shortcut)* | |
| Zoom In | ⌘+ / ⌘= | Ctrl++ / Ctrl+= | |
| Zoom Out | ⌘− | Ctrl+− | |
| Actual Size (zoom reset) | ⌘0 | Ctrl+0 | |
| Reload From Disk | ⌘R | Ctrl+R **or F5** | F5 is Windows-only |
| Minimize | ⌘M | *(standard OS window control)* | |
| Zoom (window) | *(menu only, no shortcut)* | — | macOS-only menu item |
| Hide app | ⌘H | — | macOS-only (app menu) |
| Escape (close find bar) | Escape | Escape | Reaches the page directly on Mac; routed via `window.__escape` on Windows since WinForms treats Escape as a dialog key |
| F3 / F7 / F11 | *(OS default)* | Swallowed (`preventDefault`, no-op) | Prevents WebView2/Chromium default find/caret-browsing/fullscreen behavior from firing unexpectedly |
| Ctrl+J / Ctrl+U | *(N/A)* | Swallowed | Blocks Chromium's default downloads/view-source shortcuts |

## 10. Menu structure

**macOS** — MarkdownViewer, File, Edit, View, Window, Help:
- **MarkdownViewer**: About MarkdownViewer, Hide MarkdownViewer (⌘H), Quit (⌘Q)
- **File**: New (⌘N), Open… (⌘O), Open Path… (⇧⌘G), Open Recent ▸ (last 10 + Clear Menu), Save (⌘S), Save As… (⇧⌘S), Print… (⌘P), Close (⌘W)
- **Edit**: Undo (⌘Z), Redo (⇧⌘Z), Cut (⌘X), Copy (⌘C), Paste (⌘V), Delete, Select All (⌘A), Find… (⌘F), Find and Replace… (⌥⌘F)
- **View**: Preview (⌘1), Edit (⌘2), Split (⌘3), Table of Contents (⇧⌘T), Wrap Lines, Zoom In (⌘+), Zoom Out (⌘−), Actual Size (⌘0), Reload From Disk (⌘R)
- **Window**: Minimize (⌘M), Zoom, *(native window list)*
- **Help**: *(About docs — README/CHANGELOG/ARCHITECTURE/DESIGN)*

**Windows** — File, Edit, View, Help:
- **File**: New (Ctrl+N), Open… (Ctrl+O), Open Path… (Ctrl+Shift+G), Open Recent ▸, Save (Ctrl+S), Save As… (Ctrl+Shift+S), Print… (Ctrl+P), Close Tab (Ctrl+W), Exit (Alt+F4)
- **Edit**: Undo, Redo, Cut, Copy, Paste, Delete, Select All, Find… (Ctrl+F), Find and Replace… (Ctrl+H). The first seven show their shortcut labels but do not register WinForms accelerators — WebView2 handles those keys natively inside the editor, and registering them would intercept the key before the page saw it.
- **View**: Preview (Ctrl+1), Edit (Ctrl+2), Split (Ctrl+3), Table of Contents (Ctrl+T), Wrap Lines, Zoom In (Ctrl++), Zoom Out (Ctrl+−), Actual Size (Ctrl+0), Reload From Disk (Ctrl+R)
- **Help**: About MarkdownViewer

## 11. Known macOS/Windows differences (full list)

As of v2.0 the functional gaps are closed; what remains is architectural or
platform-convention.

1. **Multi-window (Mac) vs. single-window-with-tabs (Windows)** — architectural,
   documented, intentional. This is the only remaining capability difference.
2. Find & Replace shortcut differs by letter: ⌥⌘F (Mac) vs. Ctrl+H (Windows) —
   deliberate, Ctrl+H is the Windows convention for Replace.
3. Windows Edit-menu shortcut labels are display-only (the keys are handled by
   WebView2 natively); Mac's are real AppKit accelerators. Same end behavior.
4. F5 also triggers Reload From Disk on Windows; no F-key equivalent on Mac.
5. Quit is ⌘Q on Mac, Alt+F4 (Exit) on Windows — platform convention.
6. Windows has a named-pipe single-instance server to forward file opens into
   the running process; macOS gets equivalent behavior free via
   `application(_:open:)`.
7. Windows swallows F3/F7/F11/Ctrl+J/Ctrl+U to suppress unwanted Chromium
   defaults; not applicable on WKWebView.
8. Windows adds middle-click-on-tab-header to close a tab; Mac closes with ⌘W
   against the frontmost window.

**Fixed in v2.0** (previously listed here): unfiltered Windows drag-and-drop,
missing Windows Edit-menu commands, `.txt` hidden in the Windows Open dialog,
modal Windows About window, non-canonical Windows recent-files dedupe.

**Fixed in v2.1**: Mac recents dedupe made canonical (matching Windows);
Windows window-frame persistence added; Windows recents mirrored to the shell
(`SHAddToRecentDocs` — the Jump List / Quick Access analog of the Mac Dock
menu); Windows About window stays open after opening a doc; Windows exit flow
now collects all Save/Don't Save/Cancel answers before performing any save,
matching the Mac quit transaction semantics.

## 12. Explicitly removed / non-existent features

- **AI assistant** (chat panel, "improve selection", generate & insert,
  Keychain-stored provider API keys) — shipped pre-1.2, **removed in v1.2**
  at the owner's request. The app is fully offline; no networking code
  remains. Any previously-stored keys remain in the macOS Keychain under
  `com.dave.markdownviewer.ai` until manually deleted via Keychain Access.
  The old implementation exists in git history before the v1.2 commit — not
  to be reintroduced without being explicitly asked.
- Not a project/folder-tree IDE — no file explorer sidebar, no
  multi-file project concept beyond open tabs/windows, no language server,
  no plugin system.
- No PDF export distinct from OS print-dialog "Save as PDF" — there is no
  standalone "Export to PDF" menu action.
- No mobile, web, or Linux build.

## 13. Known backlog (not yet built)

- Dock-reopen polish (macOS).
- File-descriptor-based change watching instead of the current 1 Hz
  polling loop for live reload.
- Developer ID signing / notarization — the app currently ships **ad-hoc
  signed only**, so Gatekeeper warns on first launch unless quarantine is
  stripped or the user right-click-opens.

## 14. Version

Current: **v2.1** (2026-07-20). Versioning policy: bump `0.1` per coherent
release batch (not per individual fix), update
`CFBundleShortVersionString`/`CFBundleVersion` in `Info.plist` and the
matching Windows version, and record one dated section per release in
`CHANGELOG.md` (the canonical version history — see that file for the full
release-by-release feature timeline).
