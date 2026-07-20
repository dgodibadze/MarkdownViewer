# MarkdownViewer — working notes for Claude

Read this before changing anything. It captures the architecture, the build loop,
and the non-obvious traps that have already cost debugging time.

## What this is

A standalone macOS app that opens `.md` files in a real window on double-click,
instead of only the Quick Look (spacebar) preview. It started as a fork/extension
of [QLMarkdown](https://github.com/sbarex/QLMarkdown) (which is Quick-Look-only and
explicitly "not a standalone viewer"), but the viewer is original code. QLMarkdown
is no longer a dependency — do not expect its sources in this repo.

It has since grown into a light editor: preview / edit / split modes, save,
New/Untitled documents, Open Recent, find & replace, zoom, and copy buttons on
code blocks. It is fully offline — the AI assistant was removed in v1.2.

## Architecture (two files carry almost everything)

- **`Sources/main.swift`** (~700 lines, AppKit, no Xcode project) — app lifecycle,
  document types, window/tab management, the native menu bar, live-reload file
  watching, recent files, save/save-as, and one `WKWebView` per open document.
- **`Resources/template.html`** — the entire UI *inside* the web view: toolbar,
  editor textarea, rendered preview, find bar, theme system, zoom, code-block
  copy buttons. All CSS/JS is inline in this one file.
- **`windows/Program.cs`** — the native Windows port (C# + WinForms + WebView2),
  a behavioral mirror of `main.swift`. **Any behavior change in `main.swift`
  must be mirrored there**, and `windows/Resources/template.html` is
  *regenerated* from the Mac template by `windows/regen-template.py` (bridge,
  fonts, Ctrl labels, in-page shortcut block, `__escape` hook — see DESIGN.md ▸
  Platform support). Never hand-edit the Windows template; after changing the
  Mac template, run `python3 windows/regen-template.py`.

Everything else (Info.plist, build.sh, Icon/, dmg/, install.*) is packaging.

### How rendering works

`Renderer.render()` in main.swift reads `template.html`, substitutes four tokens,
writes the result to a temp file, and loads it with
`loadFileURL` with read access limited to that temporary render directory.

| Token | Replaced with |
|---|---|
| `__RES__` | scoped `mdv-resource:` URL (virtual HTTPS host on Windows) |
| `__BASE__` | scoped `mdv-document:` URL rooted at the document directory |
| `__MARKDOWN__` | the file's text, JSON-encoded into a JS string literal |
| `__TITLE__` | the filename, HTML-escaped |

Bundled assets and document-relative files are served through separate scoped
handlers. Traversal is rejected; the page never receives filesystem-wide read
access.

### Swift ↔ JS bridge

JS → Swift: `window.webkit.messageHandlers.bridge.postMessage({action: ...})`.
Handled in `ViewerWindowController.userContentController` — actions: `dirty`,
`change`, `save`, `setWrap`.

Swift → JS: `webView.evaluateJavaScript("window.__xxx && window.__xxx()")`.
Globals the native side calls: `__setMode`, `__toggleWrap`, `__find`,
`__findReplace`, `__onSaved`, `__getText` (live editor text, pulled before
every save).

**If you add a menu item that acts on the document, follow this pattern** — put the
logic in JS, expose a `window.__something`, and have the menu item call it.

## Build / install loop

There is **no Xcode project**. `build.sh` compiles with `swiftc` and assembles the
`.app` by hand. This is deliberate: it keeps the build debuggable and avoids a
fragile `project.pbxproj`.

```bash
cd ~/GitHub/MarkdownViewer
./build.sh                                             # fetch assets (first run), compile, bundle, ad-hoc sign, lsregister
osascript -e 'tell application "MarkdownViewer" to quit' 2>/dev/null; sleep 1   # MUST quit first — see trap 11
rm -rf /Applications/MarkdownViewer.app && cp -R MarkdownViewer.app /Applications/
```

- First run of `build.sh` downloads marked, highlight.js and the GitHub CSS into
  `Resources/` (pinned versions). After that it's fully offline.
- The app is **ad-hoc signed** (no Developer ID). Gatekeeper may complain on a
  fresh install: `xattr -dr com.apple.quarantine /Applications/MarkdownViewer.app`.
- Icon not updating after rebuild? `touch /Applications/MarkdownViewer.app && killall Finder`.
- Installer DMG: `./make-dmg.sh` (needs `hdiutil` + `osascript`, macOS only).

### Verifying changes without a Mac compile

If you can't run `swiftc` (e.g. Linux sandbox), at minimum:
- brace/paren/bracket balance check on `main.swift`
- extract the main `<script>` from `template.html`, replace `__MARKDOWN__` with a
  dummy string, and run `node --check`

Both have caught real errors. They do not replace an actual build.

## Traps already hit (do not re-introduce)

1. **Cmd shortcuts need a menu item.** macOS only dispatches a Cmd key equivalent
   to the first responder if some menu item declares it. Cut/Paste silently did
   nothing because the Edit menu lacked those items. The Edit menu now carries the
   full standard set (Undo/Redo/Cut/Copy/Paste/Delete/Select All). **Any new Cmd
   shortcut that should reach the web view needs a menu item**, *unless* it's
   handled by a JS `keydown` listener (which is how Cmd+S, Cmd+F, and Cmd +/- work).

2. **Scroll sync must not be guarded by a timer.** Writing `dst.scrollTop` fires the
   destination's `scroll` event *asynchronously*, often after a
   `requestAnimationFrame` guard has cleared. The echo then syncs back with
   sub-pixel rounding error and the two split panes ratchet each other along,
   scrolling slowly on their own. Current fix: a **driver pane** claimed by real
   input (`wheel`/`mousedown`/`touchstart`/`keydown`/`focusin`); only the driver's
   scroll propagates, plus a 1px threshold. Don't "simplify" this back to a flag.

3. **Zoom scales the column, not just the font.** `--zoom` drives `.markdown-body`
   font-size *and* `.page` `max-width`/padding, so characters-per-line stays
   constant as text grows. Changing one without the other reintroduces re-wrapping.

4. **Error messages must name the real failure.** An early `render()` returned a
   bare `false` and the UI blamed the markdown file ("Could not read <path>") when
   the actual failure was writing the temp file. `render()` now returns a
   descriptive `String?` and the web view shows it. Keep it that way.

5. **Clipboard on `file://`.** `navigator.clipboard` isn't always permitted in a
   `file://` WKWebView. The code-block copy button tries it, then falls back to a
   hidden textarea + `document.execCommand('copy')`.

6. **`__MARKDOWN__` must be the LAST token substituted** in `Renderer.render()`.
   It injects arbitrary document text; any token replaced after it also matches
   occurrences *inside* the document (a file containing the literal `__TITLE__`
   used to get corrupted). `jsStringLiteral()` also explicitly escapes `</` so a
   literal `</script>` in a document can't terminate the embedding script.

7. **Never assign `editor.value` directly for edits** — it wipes the textarea's
   native undo stack (⌘Z goes dead). All programmatic edits (Tab, Replace)
   go through `spliceEditor()`, which uses
   `document.execCommand('insertText')` so WebKit records a normal undoable edit.

8. **Saves must never trust a possibly-empty cache.** `lastText` is seeded from
   disk on every load/reload, and `save()` pulls the live text via
   `window.__getText()` first, falling back to the cache only if the page can't
   answer. Before this, File ▸ Save on an unedited document wrote "" and wiped
   the file. Quit uses `.terminateLater` so async saves finish before exit.

9. **The find-highlight backdrop must mirror the editor's metrics EXACTLY.**
   An unfocused textarea never paints its selection, so find matches render as
   `<mark>`s on a backdrop div behind the (transparent-background) editor.
   Font, size, padding, wrap, border, tab-size AND `overflow: scroll` (not
   auto — scrollbar width changes the content width and shifts wrap points)
   are shared in one CSS rule for `.editor, .editor-backdrop`. Touch one
   metric without the other and highlights drift off the text.

10. **The template ships a CSP + sanitizer** (rendered markdown is untrusted
   HTML). New in-page features that need network access or remote scripts will
   be blocked by `connect-src 'none'` / `script-src file: 'unsafe-inline'` —
   that's deliberate; route anything network-shaped through the Swift bridge.
   The sanitizer strips `on*` attributes from the preview after every render.

11. **NEVER replace the /Applications bundle while the app is running.** The
   old process keeps its windows painting, and `open -a` re-activates *that*
   zombie instead of launching the new binary — but its bridge and menus
   silently die (this presented as "Save does nothing" and burned a debugging
   session). Quit the app (osascript / killall) before `rm -rf && cp -R`;
   `install.sh` does this automatically.

12. **Never put a side-effecting call inside an optional-chained call's
   arguments.** `completion?(writeToDisk())` does NOT call `writeToDisk()` when
   `completion` is nil — Swift short-circuits the *entire* postfix expression,
   arguments included. This shipped as the real "Save button does nothing" bug:
   the toolbar button and ⌘S call `save()` with no completion, so the write was
   skipped, while the close/quit dialog (which passes a completion) saved fine.
   Compute the side effect into a `let` first, then call the optional. The same
   trap applies to `delegate?.method(sideEffect())` in C#.

## Conventions

- Single-file-per-concern: don't split `main.swift` or `template.html` without a
  strong reason; the build script compiles one Swift file.
- All CSS colors go through the theme variables (`--bg`, `--surface`, `--text`,
  `--muted`, `--border`, `--accent`, `--success`) defined under
  `:root[data-theme="light|dark"]`. Never hardcode a color.
- **Versioning policy**: bump the app version by 0.1 **per release batch** — a
  coherent set of fixes/features shipped together gets ONE version and ONE
  combined, dated section in the root `CHANGELOG.md` (the canonical changelog —
  build.sh copies it into the bundle for the About window). Update
  `CFBundleShortVersionString` **and** `CFBundleVersion` in `Info.plist`.
  Do NOT bump per individual fix (that was tried and rolled back). Also update
  the version badge at the top of `README.md`.
- Docs shown by the About window: `README.md` (root; bundled together with
  `docs/` images so its screenshots render), `CHANGELOG.md` (root),
  `Resources/ARCHITECTURE.md` (structure), `Resources/DESIGN.md`
  (behavior/how-it-works). Update DESIGN.md when changing feature behavior,
  and README's feature list when adding user-visible features.
- Theme is Light/Dark/System, persisted in `localStorage` under `theme`, resolved
  before first paint by an inline script in `<head>`.
- UI stays minimalist. Prefer a keyboard shortcut over another toolbar button.

## State as of this handoff

App is at **v2.1** (2026-07-20) — see `CHANGELOG.md` for release notes. v2.1
was a verified full-codebase bug-hunt batch: fixed Replace looping on a
replacement containing the search term, Preview-mode checkbox toggles wiping
the undo stack (spliceEditor now reveals the hidden editor off-screen —
don't remove that), Edit-mode scrolling clobbering the saved scroll position,
copy-button self-copy on raw `<pre>`, Mac recents canonical dedupe, the
`FF FE 00 00` UTF-16LE/UTF-32LE BOM ambiguity, a quit-hang when quit stacked a
second save panel (saveAs now queues waiters — keep that guard), a **v2.0 C#
compile error** (`MimeType` switch missing `;` — v2.0 Windows never built!),
WebView2 reentrant Dispose on clean-tab close (deferred via BeginInvoke),
WebView2-runtime-missing crash + poisoned cached env task, stale tab title
after Save As, the Windows watch timer ticking inside modal dialogs (WinForms
timers do; NSTimer doesn't — WriteToDisk suspends it), and the second-instance
pipe spin loop. Parity additions: Windows window-frame persistence, shell
recents (SHAddToRecentDocs), About stays open, collect-then-save exit flow.
v2.0
closed the Windows-vs-Mac capability gaps found in a full audit: Windows
drag-drop is now extension-filtered, the Windows Edit menu gained
Undo/Redo/Cut/Copy/Paste/Delete/Select All (routed through new
`window.__editCmd`/`__editSelection`/`__editInsert` hooks in the shared
template — display-only shortcut labels, because WebView2 already handles
those keys natively and a WinForms accelerator would steal them), the Windows
Open dialog lists `.txt`, its About window is non-modal, and its recent-files
dedupe is canonical. **The only remaining intentional platform difference is
multi-window (Mac) vs. one-window-with-tabs (Windows).** Also added
`REFERENCE.md` (exhaustive feature/shortcut/behavior reference) and
`FEATURES.md` (tech-agnostic rebuild spec). v1.9
hardens local-resource isolation, HTML sanitization, bridge trust, navigation,
asset/install verification, external-change saves, format preservation, and
transactional Save As behavior on both platforms. v1.8
added the whole review-suggested feature set: Print/PDF, TOC sidebar,
clickable task checkboxes, offline Mermaid + KaTeX (lazy-loaded; assets
fetched by build.sh), word count, zoom menu items, whole-word find, session
restore, and macOS window polish (dirty dot, proxy icon, window drag-drop).
v1.7 added File ▸ Save As… (⇧⌘S / Ctrl+Shift+S) on both platforms. v1.6
was a bugfix batch from a full review: scroll-restore ordering, CRLF
preservation on save, close-with-save race, mode-persist leak, Escape-closes-
find (Mac), navigation lockdown (+ `form-action 'none'` CSP), About-docs
recents leak, case-insensitive dedupe. v1.2 was the big review release (save/quit data-loss fixes, CSP +
sanitizer, undo-preserving edits, anchor links, File ▸ New, Open Recent,
universal binary, **AI assistant removed** — no networking code remains; don't
reintroduce it without being asked; the old implementation is in git history).
v1.3 merged the owner's **Windows port** (`windows/`, C# + WinForms + WebView2)
and brought it to full parity with v1.2 — all fixes applied, AI removed there
too, template regenerated from the shared one — plus: new documents open in
**Split** mode on both platforms (via a deferred `startMode` applied on
navigation-finish; calling `__setMode` before page load is a silent no-op).

Notes: (1) commits between "v1.0 baseline" and "v1.2" carry interim v1.1–v2.4
numbering from a per-fix versioning experiment that was rolled back — trust
CHANGELOG.md for version mapping. (2) The Windows build (`windows/build.ps1`)
requires a Windows machine with the .NET 8 SDK; v1.9's C# changes were
statically reviewed but **not compiled** (no dotnet on this Mac) — build on
Windows before releasing.

Known future backlog: Dock-reopen polish, file-descriptor watching instead of
1 Hz polling, and Developer ID signing/notarization for release artifacts.
