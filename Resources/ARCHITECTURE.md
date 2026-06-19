# MarkdownViewer — Architecture & Design

A small, dependency-light macOS app: one compiled Swift file drives a `WKWebView` per
window that renders a bundled HTML template. There is no Xcode project — `build.sh`
compiles with `swiftc` and assembles the `.app` bundle.

## Components

```
MarkdownViewer/
├─ Sources/main.swift        # the whole native app (AppKit + WebKit)
├─ Resources/
│  ├─ template.html          # render shell + all in-page UI and logic
│  ├─ marked.min.js          # markdown → HTML (MIT)
│  ├─ highlight.min.js       # code syntax highlighting (BSD-3)
│  ├─ github-markdown-*.css  # GitHub markdown styling (MIT)
│  ├─ hljs-github-*.css      # highlight.js themes
│  ├─ CHANGELOG.md           # shown by the About window
│  └─ ARCHITECTURE.md        # this file, shown by the About window
└─ build.sh                  # fetch assets, compile, bundle, sign, register
```

## Rendering pipeline

1. `Renderer.render(markdownFile:into:)` reads the markdown and substitutes four tokens
   into `template.html`: `__RES__` (Resources dir), `__BASE__` (the file's folder, for
   relative images), `__MARKDOWN__` (the text as a JS string), `__TITLE__`.
2. The filled template is written to a temp HTML file and loaded with
   `loadFileURL(allowingReadAccessTo:"/")` so bundled assets and local images both resolve.
3. In the page, `marked.parse()` renders the markdown and `highlight.js` colorizes code.
   The editor's `<textarea>` is the source of truth; the preview re-renders from it.

## Swift ↔ JavaScript bridge

A single `WKScriptMessageHandler` named `bridge` carries messages **page → Swift**:

| action     | meaning                                                        |
|------------|---------------------------------------------------------------|
| `dirty`    | unsaved-changes state changed (drives reload suspension)      |
| `change`   | latest editor text (cached for menu/close-triggered saves)    |
| `save`     | write the editor text to disk                                 |
| `setWrap`  | wrap on/off (keeps the View ▸ Wrap Lines checkmark in sync)   |
| `ai`       | run an AI request (mode: improve / chat / generate)           |

Swift calls back into the page with `evaluateJavaScript` hooks: `window.__onSaved`,
`window.__setMode`, `window.__toggleWrap`, `window.__find`, `window.__findReplace`,
`window.__aiResult`, `window.__aiError`.

## Windows, tabs, live reload

`AppDelegate` keeps one `ViewerWindowController` per open file (native window tabbing).
Each controller polls the file's modification date once a second to live-reload external
changes — suspended while the buffer is dirty so edits are never clobbered. Saving writes
the file and refreshes the stored modification date so the app's own write doesn't trigger
a reload.

## AI assistant

`main.swift` holds a small provider model (`AIProvider` + `ProviderKind`:
`openai` / `anthropic` / `gemini`) with user-editable base URL and model per provider.
API keys live in the macOS **Keychain** (one item per provider), entered through a secure
Settings dialog — keys never touch the WebView. Requests run on `URLSession`; the active
provider and model persist in `UserDefaults`.

## Design choices / trade-offs

- **marked.js, not cmark-gfm**: keeps the app tiny and self-contained; output is
  GitHub-flavored but not byte-identical to the parent QLMarkdown Quick Look engine.
- **Everything in one template + one Swift file**: easy to read, build, and audit; no
  package manager, no Xcode project.
- **In-page UI** (toolbar, find bar, AI chat) lives in HTML/CSS/JS so it themes for free
  via the shared CSS variables and needs no extra native views.
