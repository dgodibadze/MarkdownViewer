<div align="center">

<img src="docs/icon.png" width="120" alt="MarkdownViewer icon">

# MarkdownViewer

**A fast, native Markdown viewer & editor — Preview, Edit, and Split with live
rendering, synced scrolling, and find & replace. 100% offline.**

![platform](https://img.shields.io/badge/platform-macOS%2011%2B%20%7C%20Windows%2010%2B-blue)
![license](https://img.shields.io/badge/license-GPLv3-green)
![built with](https://img.shields.io/badge/built%20with-Swift%20%2B%20WebKit%20%C2%B7%20C%23%20%2B%20WebView2-orange)
![offline](https://img.shields.io/badge/runs-100%25%20offline-brightgreen)

</div>

---

## Screenshots

| Split editing (light) | About |
|:---:|:---:|
| ![Split mode](docs/screenshots/split.png) | ![About](docs/screenshots/about.png) |

*Shots are from the Windows build; the macOS app shares the same in-page UI.*

## Features

- 📝 **Preview · Edit · Split** modes (`⌘1/2/3` · `Ctrl+1/2/3`) with a GitHub-style toolbar toggle
- ↔️ **Draggable splitter** + **synced scrolling** between editor and preview (drag the divider; double-click to reset)
- 💾 **Edit and save to disk** (`⌘S` · `Ctrl+S`) with an unsaved-changes dot and a Save / Don't Save / Cancel guard on close
- 🔍 **Find & Replace** (`⌘F` · `Ctrl+F`): match count, next/prev, replace one/all, case toggle
- ↩️ **Wrap Lines** toggle (`View ▸ Wrap Lines`) — soft-wrap or horizontal scroll
- 🆕 **New documents** (`⌘N` · `Ctrl+N`) opening in Split mode; the first save asks where to put the file (defaults to `.md`, any typed extension accepted)
- 🕘 **Open Recent** — the last 10 files, one menu away
- 🌗 **Light / Dark / System** theme (circular toggle), follows the OS
- 🗂️ **Tabs** — multiple files as tabs (native window tabs on macOS; one tabbed window on Windows, where files opened from Explorer join the running instance)
- 🔄 **Live reload** when the file changes on disk (paused while you have unsaved edits)
- 📋 **Copy buttons** on fenced code blocks; working in-document anchor links
- 🔒 **100% offline** (marked + highlight.js + GitHub CSS bundled) — the app makes no network requests, and rendered documents are sandboxed by a CSP + HTML sanitizer

## Install (one line)

**macOS** — paste in Terminal:

```bash
curl -fsSL https://raw.githubusercontent.com/dgodibadze/MarkdownViewer/main/install.sh | bash
```

**Windows** — paste in PowerShell:

```powershell
irm https://raw.githubusercontent.com/dgodibadze/MarkdownViewer/main/install.ps1 | iex
```

Each installer detects everything else automatically: on macOS it grabs the latest DMG
from [Releases](../../releases) (or builds from source if the release has no DMG yet) and
installs to `/Applications`; on Windows it downloads the self-contained build, installs
the WebView2 Runtime if missing, and adds a Start Menu shortcut — no admin rights needed.
The `curl` line even works in **Git Bash on Windows** (it hands off to the PowerShell
installer), so one command covers both platforms if Git Bash is your shell.

## Windows version

A full Windows port lives in [`windows/`](windows) — same features and the same bundled
rendering assets, built with C# + WebView2 instead of Swift + WebKit (tabs, live reload,
find & replace, new/recent files, light/dark/system theme). See
[`windows/README.md`](windows/README.md) for build and usage.

## Manual install (macOS)

### Download

1. Download the latest **`MarkdownViewer.dmg`** from the [Releases](../../releases) page.
2. Open it and drag **MarkdownViewer** onto **Applications**.
3. First launch: the app is ad-hoc signed (no paid Developer ID), so macOS Gatekeeper may
   warn. **Right-click the app → Open**, or run:
   ```bash
   xattr -dr com.apple.quarantine /Applications/MarkdownViewer.app
   ```

To make it the default for `.md` files: right-click a Markdown file → **Get Info** → **Open
with** → **MarkdownViewer** → **Change All…**

### Build from source

Requires the Xcode command line tools (`xcode-select --install`) and internet on the first
build (to fetch the bundled JS/CSS, cached afterward).

```bash
git clone https://github.com/dgodibadze/MarkdownViewer.git
cd MarkdownViewer
./build.sh            # produces MarkdownViewer.app
./make-dmg.sh         # optional: produces MarkdownViewer.dmg
```

## Usage

| Action | macOS | Windows |
|---|---|---|
| Preview / Edit / Split | `⌘1` / `⌘2` / `⌘3` | `Ctrl+1` / `Ctrl+2` / `Ctrl+3` |
| New / Save | `⌘N` / `⌘S` | `Ctrl+N` / `Ctrl+S` |
| Find / Find & Replace | `⌘F` / `⌥⌘F` | `Ctrl+F` / `Ctrl+H` |
| Open / Close / Reload | `⌘O` / `⌘W` / `⌘R` | `Ctrl+O` / `Ctrl+W` / `Ctrl+R` |
| Zoom in / out / reset | `⌘+` / `⌘−` / `⌘0` | `Ctrl+` / `Ctrl−` / `Ctrl 0` |

- **Resize the split** by dragging the divider; **double-click** it for 50/50.

## How it works

One native source file per platform drives a web view that renders a bundled HTML template,
talking to it over a small message bridge: `Sources/main.swift` + `WKWebView` on macOS,
`windows/Program.cs` + WebView2 on Windows. See
[`Resources/ARCHITECTURE.md`](Resources/ARCHITECTURE.md) and
[`Resources/DESIGN.md`](Resources/DESIGN.md) for the full design and
[`CHANGELOG.md`](CHANGELOG.md) for history. Contributions welcome — see
[`CONTRIBUTING.md`](CONTRIBUTING.md).

## License & credits

Licensed under the **GNU GPL v3.0** — see [`LICENSE.txt`](LICENSE.txt). MarkdownViewer began as
a companion to **[QLMarkdown](https://github.com/sbarex/QLMarkdown) by sbarex** (also GPLv3); it
is independent code and remains under GPLv3 with attribution. Bundled libraries — **marked**
(MIT), **highlight.js** (BSD-3-Clause), **github-markdown-css** (MIT) — are credited in
[`NOTICE.md`](NOTICE.md). If you publish a fork, keep the GPLv3 license, this attribution, and
`NOTICE.md`.
