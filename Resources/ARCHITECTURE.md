# MarkdownViewer — Architecture

A small, dependency-light macOS app: one compiled Swift file drives a `WKWebView` per
window that renders a bundled HTML template. There is no Xcode project — `build.sh`
compiles with `swiftc` (universal: arm64 + x86_64, macOS 11+) and assembles the
`.app` bundle. See [DESIGN.md](DESIGN.md) for how each feature works behaviorally.

## Components

```
MarkdownViewer/
├─ Sources/main.swift        # the whole native app (AppKit + WebKit)
├─ CHANGELOG.md              # canonical changelog (copied into the bundle)
├─ Resources/
│  ├─ template.html          # render shell + all in-page UI and logic (incl. CSP)
│  ├─ marked.min.js          # markdown → HTML (MIT)
│  ├─ highlight.min.js       # code syntax highlighting (BSD-3)
│  ├─ github-markdown-*.css  # GitHub markdown styling (MIT)
│  ├─ hljs-github-*.css      # highlight.js themes
│  ├─ ARCHITECTURE.md        # this file, shown by the About window
│  └─ DESIGN.md              # behavioral design doc, shown by the About window
└─ build.sh                  # fetch assets, compile both arches, lipo, bundle, sign
```

## Rendering pipeline

1. `Renderer.render(markdownFile:into:)` reads the markdown and substitutes four tokens
   into `template.html`: `__RES__` (Resources dir), `__BASE__` (the file's folder, for
   relative images), `__TITLE__`, and — always **last**, so document text can't clobber
   other tokens — `__MARKDOWN__` (the text as a JSON-escaped JS string, with `</`
   explicitly escaped).
2. The filled template is written to a temp HTML file and loaded with
   `loadFileURL(allowingReadAccessTo:"/")` so bundled assets and local images both resolve.
3. In the page, `marked.parse()` renders the markdown, heading ids are generated,
   the output is sanitized (inline event handlers and script-scheme URLs stripped),
   and `highlight.js` colorizes code. A Content-Security-Policy blocks remote
   scripts and all network connections from the page. The editor's `<textarea>` is
   the source of truth; the preview re-renders from it.

## Swift ↔ JavaScript bridge

A single `WKScriptMessageHandler` named `bridge` carries messages **page → Swift**:

| action     | meaning                                                        |
|------------|---------------------------------------------------------------|
| `dirty`    | unsaved-changes state changed (drives reload suspension)      |
| `change`   | latest editor text (fallback cache for saves)                 |
| `save`     | write the editor text to disk                                 |
| `setWrap`  | wrap on/off (keeps the View ▸ Wrap Lines checkmark in sync)   |

Swift calls back into the page with `evaluateJavaScript` hooks: `window.__onSaved`,
`window.__getText` (live text pulled before every save), `window.__setMode`,
`window.__toggleWrap`, `window.__find`, `window.__findReplace`.

**New menu items that act on the document follow this pattern**: logic lives in JS,
exposed as a `window.__something`, called from the native menu.

## Windows, tabs, documents, live reload

`AppDelegate` keeps one `ViewerWindowController` per open document — file-backed
or a new **Untitled** one (`fileURL == nil`, created via File ▸ New; the first
save runs a save panel defaulting to `.md`). Native window tabbing; the first
window remembers its frame, later ones cascade. Each file-backed controller
polls the file's modification date once a second to live-reload external
changes — suspended while the buffer is dirty so edits are never clobbered.
Saving pulls the live editor text from the page, writes it, and refreshes the
stored modification date so the app's own write doesn't trigger a reload. Both
window close and app quit prompt for unsaved changes; quit defers termination
until saves complete. Recently opened files persist in `UserDefaults` and show
under File ▸ Open Recent.

## Design choices / trade-offs

- **marked.js, not a native C engine**: keeps the app tiny and self-contained; output is
  GitHub-flavored and rendered entirely in the WebView.
- **Everything in one template + one Swift file**: easy to read, build, and audit; no
  package manager, no Xcode project.
- **In-page UI** (toolbar, find bar) lives in HTML/CSS/JS so it themes for free
  via the shared CSS variables and needs no extra native views.
- **Fully offline**: the app makes no network requests (the AI assistant was
  removed in v1.2; see DESIGN.md).
- **macOS-only by construction** (AppKit + WKWebView). A Windows port would mean
  rewriting the native shell (Tauri would be the closest fit); the in-page UI
  would port nearly as-is.
