# MarkdownViewer for Windows

The Windows port of MarkdownViewer — same features, same bundled rendering assets, same
architecture as the macOS app one directory up: **one native source file drives a web view
per document** that renders the shared HTML template.

| macOS | Windows |
|---|---|
| Swift + AppKit (`Sources/main.swift`) | C# + WinForms (`Program.cs`) |
| WKWebView | WebView2 (Edge/Chromium, preinstalled on Windows 11) |
| Native window tabs | `TabControl` tabs in one window |
| UserDefaults | `%APPDATA%\MarkdownViewer\settings.json` |
| `⌘1/⌘2/⌘3`, `⌘S`, `⌘F` | `Ctrl+1/2/3`, `Ctrl+S`, `Ctrl+F` |

Everything else is shared: `marked.min.js`, `highlight.min.js`, the GitHub CSS themes, and
the in-page UI (toolbar, find & replace, theme toggle) come from the same `Resources/`
folder as the Mac app — the csproj links them in at build time. The Windows
`Resources/template.html` is regenerated from the Mac template (only the message bridge,
fonts, and shortcut labels differ). The app is fully offline — it makes no network requests.

## Features

Identical to the macOS app:

- **Preview · Edit · Split** modes (`Ctrl+1/2/3`) with draggable splitter + synced scrolling
- **New documents** (`Ctrl+N`) opening in Split mode; the first save asks where to put the
  file (defaults to `.md`, any typed extension accepted)
- **Edit and save** (`Ctrl+S`) with dirty indicator and a save guard on close/exit
- **Open Recent** — the last 10 files, from the File menu
- **Find & Replace** (`Ctrl+F` / `Ctrl+H`), match count, case toggle
- **Tabs** — multiple files in one window; single-instance, so files opened from
  Explorer land as tabs (middle-click or `Ctrl+W` closes)
- **Live reload** when the file changes on disk (paused while you have unsaved edits)
- **Light / Dark / System** theme, document zoom (`Ctrl +/-/0`), wrap-lines toggle,
  copy buttons on code blocks, working in-document anchor links

## Build

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
(`winget install Microsoft.DotNet.SDK.8`) and the WebView2 Runtime (preinstalled on
Windows 11; [download](https://developer.microsoft.com/microsoft-edge/webview2/) for
older systems). Internet is needed on the first build to restore the WebView2 NuGet
package (cached afterward).

```powershell
cd windows
.\build.ps1            # Release build -> bin\Release\net8.0-windows\MarkdownViewer.exe
.\build.ps1 -Publish   # self-contained single-file publish -> dist\MarkdownViewer.exe
```

To make it the default app for `.md` files: right-click a Markdown file →
**Open with** → **Choose another app** → browse to `MarkdownViewer.exe` → **Always**.

## Shortcuts

| Action | Shortcut |
|---|---|
| Preview / Edit / Split | `Ctrl+1` / `Ctrl+2` / `Ctrl+3` |
| New file | `Ctrl+N` |
| Save | `Ctrl+S` |
| Find / Find & Replace | `Ctrl+F` / `Ctrl+H` |
| Open / Open Path / Close Tab | `Ctrl+O` / `Ctrl+Shift+G` / `Ctrl+W` |
| Reload from disk | `Ctrl+R` or `F5` |
| Zoom in / out / reset | `Ctrl +` / `Ctrl -` / `Ctrl 0` |

## How it works

`Program.cs` substitutes tokens into `Resources/template.html` (regenerated from the Mac
template — the only differences are the message bridge, fonts, and shortcut labels),
writes it to a temp file, and loads it in a WebView2. The page posts `{action: ...}`
messages over `chrome.webview.postMessage` for saves, dirty state, and app shortcuts;
C# calls back with `ExecuteScriptAsync` into the same `window.__*` hooks the Mac app
uses. Rendered documents are sandboxed by a Content-Security-Policy plus an HTML
sanitizer — see `Resources/DESIGN.md` (also in the About window) for the full design.

Licensed under **GPLv3** like the rest of the project — see [`../LICENSE.txt`](../LICENSE.txt)
and [`../NOTICE.md`](../NOTICE.md) for bundled library credits.
