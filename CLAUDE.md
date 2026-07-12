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
find & replace, an AI chat panel, zoom, and copy buttons on code blocks.

## Architecture (two files carry almost everything)

- **`Sources/main.swift`** (~1000 lines, AppKit, no Xcode project) — app lifecycle,
  document types, window/tab management, the native menu bar, live-reload file
  watching, AI provider calls + Keychain, and one `WKWebView` per open file.
- **`Resources/template.html`** — the entire UI *inside* the web view: toolbar,
  editor textarea, rendered preview, find bar, chat panel, theme system, zoom,
  code-block copy buttons. All CSS/JS is inline in this one file.

Everything else (Info.plist, build.sh, Icon/, dmg/) is packaging.

### How rendering works

`Renderer.render()` in main.swift reads `template.html`, substitutes four tokens,
writes the result to a temp file, and loads it with
`loadFileURL(_:allowingReadAccessTo: "/")`.

| Token | Replaced with |
|---|---|
| `__RES__` | `file://` URL of the app's Resources dir (for marked/highlight/CSS) |
| `__BASE__` | the markdown file's directory (so relative images resolve) |
| `__MARKDOWN__` | the file's text, JSON-encoded into a JS string literal |
| `__TITLE__` | the filename, HTML-escaped |

Read access is granted to `/` so the bundled assets *and* local images referenced
by the markdown can both load. Assets are referenced by absolute `file://` URL, so
the `<base href>` (pointing at the doc's folder) doesn't break them.

### Swift ↔ JS bridge

JS → Swift: `window.webkit.messageHandlers.bridge.postMessage({action: ...})`.
Handled in `ViewerWindowController.userContentController` — actions: `dirty`,
`change`, `save`, `setWrap`, `ai`.

Swift → JS: `webView.evaluateJavaScript("window.__xxx && window.__xxx()")`.
Globals the native menus call: `__setMode`, `__toggleWrap`, `__find`,
`__findReplace`, `__aiImprove`, `__aiGenerate`, `__toggleChat`, `__onSaved`,
`__aiResult`, `__aiError`.

**If you add a menu item that acts on the document, follow this pattern** — put the
logic in JS, expose a `window.__something`, and have the menu item call it.

## Build / install loop

There is **no Xcode project**. `build.sh` compiles with `swiftc` and assembles the
`.app` by hand. This is deliberate: it keeps the build debuggable and avoids a
fragile `project.pbxproj`.

```bash
cd ~/GitHub/MarkdownViewer
./build.sh                                             # fetch assets (first run), compile, bundle, ad-hoc sign, lsregister
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

## Conventions

- Single-file-per-concern: don't split `main.swift` or `template.html` without a
  strong reason; the build script compiles one Swift file.
- All CSS colors go through the theme variables (`--bg`, `--surface`, `--text`,
  `--muted`, `--border`, `--accent`) defined under `:root[data-theme="light|dark"]`.
  Never hardcode a color.
- Theme is Light/Dark/System, persisted in `localStorage` under `theme`, resolved
  before first paint by an inline script in `<head>`.
- UI stays minimalist. Prefer a keyboard shortcut over another toolbar button.

## State as of this handoff

Working tree has **uncommitted changes** to `Sources/main.swift` (full Edit menu,
Open Path…) and `Resources/template.html` (code-block copy buttons, zoom,
scroll-sync fix). Build and test, then commit.

Recent additions, newest first: split-pane scroll-drift fix; full Edit menu
(Cut/Paste/Undo/Redo); zoom via Cmd +/-/0 with proportional column width;
copy button on code blocks; File ▸ Open Path… (⇧⌘G) to open a pasted absolute path.
