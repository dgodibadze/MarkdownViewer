# MarkdownViewer — Changelog

A standalone markdown viewer/editor for macOS and Windows. This changelog tracks the
MarkdownViewer app.

## 1.0.0

### Added
- **Windows port** (`windows/`): the full app rebuilt natively for Windows with
  C# + WinForms + WebView2, sharing the same rendering assets and in-page UI.
  Tabs in one window, single-instance (files opened from Explorer join as tabs),
  API keys encrypted with Windows DPAPI, `Ctrl`-based shortcuts.
- **One-line installers**: `install.sh` (macOS — downloads the release DMG or
  builds from source) and `install.ps1` (Windows — downloads the self-contained
  build, installs the WebView2 Runtime if missing, adds a Start Menu shortcut).

## Unreleased

### Added
- **Synced scrolling** between the editor and preview in Split mode (vertical and
  horizontal, proportional).
- **Always-visible scrollbars** on both panes (horizontal + vertical) so content stays
  reachable when the window is small.
- **Wrap Lines** toggle (`View ▸ Wrap Lines`) — soft-wrap, or no-wrap with horizontal
  scrolling that syncs sideways with the preview.
- **Find & Replace** (`Edit ▸ Find`, `Edit ▸ Find and Replace`): match count, next/prev,
  replace one, replace all, case-sensitive toggle. Works on the raw markdown.
- **AI assistant** with multiple providers (Groq, Nous Portal, Anthropic, OpenAI, Gemini):
  improve a selection, chat about the document, and generate-and-insert. API keys are stored
  in the macOS Keychain and entered through a secure Settings dialog.
- **About window** with author info and quick links to this Changelog and the
  Architecture/Design document.

## Earlier

### Added
- **Preview / Edit / Split** modes with a GitHub-style toolbar toggle (`⌘1` / `⌘2` / `⌘3`).
- **Editing with save to disk** (`⌘S`), unsaved-change indicator, and a Save / Don't Save /
  Cancel prompt on close.
- **Circular theme switcher** that cycles Light → Dark → System.
- System sans-serif UI font for toolbar controls.

### Initial
- Open `.md` (and related extensions) in a real window; multiple files as native tabs.
- Rendering via `marked` + `highlight.js` with GitHub-style CSS; fully offline after build.
- Light / Dark / System theme; live reload on external file changes (scroll position kept).
- External links open in the default browser; local images resolve relative to the file.
