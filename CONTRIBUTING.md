# Contributing

Thanks for your interest! MarkdownViewer is intentionally small and dependency-light:
one native source file per platform driving a web view that renders a shared HTML
template. Please keep changes in that spirit.

## Layout

| Path | What it is |
|---|---|
| `Sources/main.swift` | The entire macOS app (AppKit + WebKit) |
| `windows/Program.cs` | The entire Windows app (WinForms + WebView2) |
| `Resources/template.html` | Shared in-page UI + logic (macOS build) |
| `windows/Resources/template.html` | Windows copy — differs only in the message bridge and shortcut labels |
| `Resources/ARCHITECTURE.md` | Design document — read this first |

## Building

- **macOS:** `./build.sh` (needs the Xcode command line tools)
- **Windows:** `cd windows && .\build.ps1` (needs the .NET 8 SDK)

## Guidelines

- If you change the in-page UI or JS, apply the change to **both** template files —
  they are kept line-for-line parallel where possible.
- No new runtime dependencies or package managers; bundled JS/CSS stays offline.
- Update `Resources/CHANGELOG.md` (it is shown in the app's About window).
- License is GPLv3 — contributions are accepted under the same license, and
  attribution/`NOTICE.md` must stay intact.

## Bugs & ideas

Open a GitHub issue with your OS and version, steps to reproduce, and (for rendering
issues) a snippet of the markdown that misbehaves.
