# MarkdownViewer — Architecture

A small, dependency-light macOS app: one compiled Swift file drives a `WKWebView` per
window that renders a bundled HTML template. There is no Xcode project — `build.sh`
compiles with `swiftc` (universal: arm64 + x86_64, macOS 11+) and assembles the
`.app` bundle. See [DESIGN.md](DESIGN.md) for how each feature works behaviorally.

## Components

```
MarkdownViewer/
├─ Sources/main.swift        # the whole native macOS app (AppKit + WebKit)
├─ CHANGELOG.md              # canonical changelog (copied into both bundles)
├─ README.md                 # project README (bundled; shown by the About window)
├─ Resources/
│  ├─ template.html          # render shell + all in-page UI and logic (incl. CSP)
│  ├─ marked.min.js          # markdown → HTML (MIT)
│  ├─ highlight.min.js       # code syntax highlighting (BSD-3)
│  ├─ mermaid.min.js         # diagrams (MIT) — loaded lazily on first use
│  ├─ katex.min.js|css, katex-auto-render.min.js, fonts/  # math (MIT), lazy
│  ├─ github-markdown-*.css  # GitHub markdown styling (MIT)
│  ├─ hljs-github-*.css      # highlight.js themes
│  ├─ SHA256SUMS             # pinned hashes for every downloaded render asset
│  ├─ ARCHITECTURE.md        # this file, shown by the About window
│  └─ DESIGN.md              # behavioral design doc, shown by the About window
├─ docs/                     # icon + screenshots (bundled so the in-app README renders)
├─ windows/                  # native Windows port (C# + WinForms + WebView2)
│  ├─ Program.cs             # the whole Windows app — mirrors main.swift
│  └─ Resources/template.html# regenerated from ../Resources/template.html
│                            # (bridge + fonts + Ctrl labels delta; see DESIGN.md)
└─ build.sh                  # fetch assets, compile both arches, lipo, bundle, sign
```

## Rendering pipeline

1. `Renderer.render(markdownFile:into:)` reads the markdown and substitutes four tokens
   into `template.html`: `__RES__` (Resources dir), `__BASE__` (the file's folder, for
   relative images), `__TITLE__`, and — always **last**, so document text can't clobber
   other tokens — `__MARKDOWN__` (the text as a JSON-escaped JS string, with `</`
   explicitly escaped).
2. The filled template is written to a temp HTML file. That page can read only
   its temp directory. Bundled assets and document-relative regular files are
   exposed through separate directory-confined URL handlers/virtual hosts with
   a 64 MiB per-resource limit.
3. In the page, `marked.parse()` renders the markdown, heading ids are generated,
   the output is sanitized with a tag/attribute/URL allowlist,
   and `highlight.js` colorizes code. Mermaid diagrams and KaTeX math render from
   bundled libraries that are injected only when a document uses them. A
   Content-Security-Policy blocks remote resources, form submission, and all network
   connections from the page; the native side additionally accepts navigation and
   bridge messages only from the generated top-level page. The editor's `<textarea>` is the source of truth; the preview
   re-renders from it (task checkboxes in the preview write back into it).

## Swift ↔ JavaScript bridge

A single `WKScriptMessageHandler` named `bridge` carries messages **page → Swift**:

| action     | meaning                                                        |
|------------|---------------------------------------------------------------|
| `dirty`    | unsaved-changes state changed (drives reload suspension)      |
| `change`   | latest editor text (fallback cache for saves)                 |
| `save`     | write the editor text to disk                                 |
| `saveAs`   | run Save As (Windows routes Ctrl+Shift+S through the page)    |
| `print`    | open the native print dialog (Windows routes Ctrl+P in-page)  |
| `setWrap`  | wrap on/off (keeps the View ▸ Wrap Lines checkmark in sync)   |

(The Windows port adds a few more — `newFile`, `open`, `openPath`, `closeTab`,
`reload` — because WinForms accelerators can be swallowed by the WebView, so
those shortcuts are handled in-page and bridged out.) Every native receiver
validates the exact source page before dispatching an action.

Swift calls back into the page with `evaluateJavaScript` hooks: `window.__onSaved`,
`window.__getText` (live text pulled before every save), `window.__setMode`,
`window.__toggleWrap`, `window.__find`, `window.__findReplace`,
`window.__toggleTOC`, `window.__zoomIn` / `__zoomOut` / `__zoomReset`
(and `window.__escape`, Windows-only).

**New menu items that act on the document follow this pattern**: logic lives in JS,
exposed as a `window.__something`, called from the native menu.

## Windows, tabs, documents, live reload

`AppDelegate` keeps one `ViewerWindowController` per open document — file-backed
or a new **Untitled** one (`fileURL == nil`, created via File ▸ New; the first
save runs a save panel defaulting to `.md`). Native window tabbing; the first
window remembers its frame, later ones cascade. Each file-backed controller
polls the file's modification date once a second to live-reload external
changes — suspended while the buffer is dirty so edits are never clobbered.
One byte snapshot supplies the rendered text, editor cache, and initial
fingerprint. Saving pulls the live editor text from the page, verifies an
on-disk SHA-256 fingerprint, preserves UTF-8/16/32 BOM and newline format,
writes it, and refreshes the fingerprint so the app's own write doesn't trigger
a reload. Writes replace a flushed temporary file from the same directory
rather than truncating in place.
Both window close and app quit prompt for unsaved changes; quit defers termination
until saves complete. Recently opened files persist in `UserDefaults` and show
under File ▸ Open Recent; the set of open documents persists too, so a plain
launch restores the last session. Markdown files can be dropped onto any
window (a `WKWebView` subclass intercepts file drags), and each window carries
the standard unsaved-dot and title-bar proxy icon.

## Design choices / trade-offs

- **marked.js, not a native C engine**: keeps the app tiny and self-contained; output is
  GitHub-flavored and rendered entirely in the WebView.
- **Everything in one template + one Swift file**: easy to read, build, and audit; no
  package manager, no Xcode project.
- **In-page UI** (toolbar, find bar) lives in HTML/CSS/JS so it themes for free
  via the shared CSS variables and needs no extra native views.
- **Fully offline**: the app makes no network requests (the AI assistant was
  removed in v1.2; see DESIGN.md). Even Mermaid and KaTeX are bundled, pinned
  versions fetched once at build time.
- **Two thin native shells, one shared page** (AppKit + WKWebView on macOS,
  WinForms + WebView2 on Windows). Nearly all feature logic lives in the shared
  template, so the ports stay in lockstep; the Windows template is regenerated
  from the Mac one by `windows/regen-template.py`, never edited by hand.
