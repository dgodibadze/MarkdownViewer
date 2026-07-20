# MarkdownViewer — Functionality Specification

A tech-agnostic feature and behavior spec, extracted from this project, for
rebuilding a similar app on a different stack. It describes *what* the app
does and *why*, not *how this particular codebase implements it* — no Swift,
AppKit, or WebView2 specifics below. See `README.md` and
`Resources/DESIGN.md` in this repo if you want the actual macOS/Windows
implementation details.

## Elevator pitch

A native, offline desktop app that gives `.md` files a real home: double-click
a file and get a rendered document in a window, with just enough editing to
fix a typo, tick a checkbox, and save. Not a full IDE, not Electron, no
accounts, no network calls of any kind.

## Core interaction model

Three view modes, switchable via keyboard shortcut and toolbar:

- **Preview** — rendered markdown only, read-only.
- **Edit** — raw markdown in a plain-text editor only.
- **Split** — both panes side by side with a draggable divider; scrolling one
  pane scrolls the other proportionally.

New/blank documents open in Split mode by default.

## Rendering

- GitHub-flavored markdown → HTML (tables, task lists, fenced code, etc.).
- Syntax highlighting in fenced code blocks.
- GitHub-style CSS theme for the rendered output, in both light and dark.
- Heading IDs auto-generated (GitHub-style slugs, deduped on collision:
  `heading`, `heading-1`, `heading-2`, …) so anchor links and a
  table-of-contents sidebar both work. Clicking a TOC entry scrolls to the
  heading; TOC open/closed state persists across sessions.
- **Diagrams and math, loaded lazily**: fenced ` ```mermaid ` blocks render as
  diagrams, `$$…$$` / `\(…\)` math renders via a math typesetting library.
  Only pull in these (potentially heavy) libraries when a document actually
  uses the syntax — don't pay the load cost otherwise. Diagrams should
  re-render on theme change if they bake theme colors into their output.
- **Clickable task checkboxes directly in the preview** — ticking `- [ ]` /
  `- [x]` in the rendered view writes the change back into the source
  document (undo-safe, marks the document dirty). Map the Nth rendered
  checkbox to the Nth checkbox marker in the source; if the count doesn't
  line up 1:1 (e.g. task-list syntax appearing inside a code fence), leave
  checkboxes disabled rather than guessing which one was clicked.
- In-document anchor links (`#heading-id`) work in the preview.
- A copy-to-clipboard button on every fenced code block.

## Editing

- Plain-text editor for the raw markdown, with a monospace font.
- **Undo/redo must survive every programmatic edit** — checkbox toggles, Tab
  key indentation, Find & Replace — not just manual typing. If your text
  widget's "set full contents" API resets the undo history (many do), route
  programmatic edits through an insert/delete API instead so the native undo
  stack stays intact. This is one of the easiest things to get wrong and the
  most annoying for a user to hit. Watch the *hidden-editor* case in
  particular: if the undo-preserving edit path needs the editor focused or
  visible (browser `execCommand` does), an edit triggered while the editor is
  hidden — e.g. ticking a checkbox from the rendered preview — silently takes
  the undo-destroying fallback unless you temporarily reveal the editor
  off-screen for the edit.
- Tab key inserts spaces (not a literal tab character) without breaking undo.
- Word count and character count, live-updated.
- Wrap Lines toggle.
- **Find & Replace**: match count, case-sensitive toggle, whole-word toggle,
  live match highlighting as you type, current-match emphasis distinct from
  other matches, scroll-to-current-match. Replace and Replace All are
  undo-safe (ideally Replace All is a *single* undo step, not one per match).
  After a Replace, advance the cursor past the *end of the inserted
  replacement*, not merely to "at/after the match start" — otherwise a
  replacement that contains the search term (find `foo`, replace `foobar`)
  gets re-selected forever and repeated Replace clicks corrupt the text.
  Escape closes the find bar from anywhere on the page, not just when a find
  field has focus.
- **Zoom**: scales both the font size *and* the content column width/padding
  together, so characters-per-line stays constant as you zoom — zooming
  should not cause re-wrapping of already-visible text.

## File management

- **Open** via double-click / file association, an Open dialog, and a
  paste-a-path dialog.
- **New** creates a blank untitled document; first save prompts for a
  filename/location and defaults to a sensible extension, but accepts any
  extension the user types.
- **Save** writes the current editor contents to disk. **Save As** writes to
  a new path and, only on success, makes that new path the document's
  identity going forward — the original file on disk is left untouched with
  its last-saved content.
- **Save must be unconditional**, not gated on a "has anything changed" flag.
  If the Save button/shortcut only acts when the app *thinks* something
  changed, any bug in that dirty-tracking makes Save silently do nothing —
  which reads to a user as "the app is broken," not as a tracking bug. Just
  re-write the bytes; it's cheap and always correct.
- **Open Recent** — last N opened files, persisted across launches, rebuilt
  against the filesystem each time the menu opens so missing files don't show
  stale entries.
- **Session restore** — a plain launch with no file argument reopens whatever
  was open when the app last quit.
- Drag-and-drop of `.md` files onto an open window.
- Tabs/multiple documents in one process where the platform supports it.
- **Live reload**: watch the open file for external changes and re-render
  automatically — but **suspend watching while the user has unsaved edits**,
  so an external change never clobbers in-progress work. Preserve scroll
  position across a reload.
- **Print / Export to PDF** of the rendered document only (not the raw
  markdown, not the app chrome).

## Data-safety invariants (the part most worth stealing)

These are the rules that prevent silent data loss — they mattered enough in
this project's history to be worth calling out explicitly:

1. **The editor is the single source of truth for "what to save."** Don't
   save from some separately-maintained text cache unless the live editor is
   genuinely unreachable (e.g. showing an error page) — cache staleness is
   exactly how a Save writes empty or outdated content.
2. **Every exit path (close window, quit app) must block on in-flight saves**
   actually completing, and must offer Save / Don't Save / Cancel for dirty
   documents — a window closing (or the app quitting) while a save is still
   in flight is a data-loss bug waiting to happen.
3. **Detect external changes before overwriting.** Fingerprint the file
   (size + mtime, or a hash) when you load/last-save it; before writing,
   compare against the current on-disk state and require explicit
   confirmation if it changed underneath you (edited elsewhere, deleted,
   unreadable).
4. **Preserve file format on save** — if you support cross-platform line
   endings (LF/CRLF/CR) or BOM-prefixed encodings, remember what the file
   used on load and restore it on write, even though your in-memory text
   widget normalizes line endings internally. Silently converting a Windows
   user's CRLF file to LF on every open+save is a surprising diff generator.
5. **Writes should be atomic** (write to a temp file in the same directory,
   then rename over the original) so a crash or power loss mid-write can't
   leave a truncated file.
6. **Every UI path that can trigger "save"** (toolbar button, keyboard
   shortcut, menu item) must go through the same code path. Divergent save
   implementations are how you end up with "the button doesn't work but the
   menu does" bugs.
7. **Suspend background file-watching while a save-related dialog is open.**
   On toolkits whose timers keep firing inside modal dialog message loops
   (WinForms does; AppKit's default-mode timers don't), a live-reload can fire
   *under* your "file changed on disk — overwrite?" prompt and silently swap
   the buffer before the user's answer is applied.
8. **Only one save dialog per document at a time.** If a second save request
   arrives while a save panel is already up (e.g. window-close asked to save,
   then the user hits Quit), chain the second request onto the in-flight
   dialog's outcome instead of stacking a second dialog — a dialog queued on a
   window that then closes may never resolve, wedging a "quit after all saves
   finish" state machine forever.
9. **BOM sniffing has ambiguous prefixes**: `FF FE 00 00` is both a UTF-32LE
   BOM and a UTF-16LE BOM followed by U+0000. Try the longer interpretation
   first, but fall back to the shorter one before declaring the file
   undecodable — otherwise a save converts a valid file to mojibake.

## Trust / security model (markdown is untrusted input)

Rendered markdown can contain raw HTML — treat it as untrusted content, not
as your own UI:

- Sanitize rendered HTML: strip event-handler attributes (`on*`), scripts,
  embeds, and anything else that isn't a normal content tag before it's
  displayed.
- If rendering happens inside an embedded web view, apply a strict
  Content-Security-Policy: only your bundled scripts/styles/fonts run, no
  arbitrary remote script/style/image loads, no arbitrary network
  connections, no form submission to arbitrary origins.
- Scope filesystem access from the render surface to only what's needed
  (bundled app assets, and files under the *specific document's* directory
  for resolving relative image paths) — not filesystem-wide read access.
  Reject path traversal attempts.
- The app should make **zero network requests** of any kind, so there's
  nothing to leak even if sanitization has a gap.
- If there's a bridge between the rendered content and native app code
  (e.g. for the checkbox-click-writes-to-source feature), make sure only
  your own generated page can call it — not arbitrary web content someone
  might navigate to.

## Theming

Light / Dark / follow-OS-setting, persisted across launches, resolved
*before first paint* so there's no flash of the wrong theme. All UI colors
should go through a small set of theme variables (background, surface, text,
muted text, border, accent, success) rather than being hardcoded per-element,
so adding a theme or tweaking one is a one-place change.

## Explicit non-goals

- No AI/LLM features, no accounts, no telemetry, no network access of any
  kind — this is a deliberate constraint, not a missing feature.
- Not a full IDE — no project/folder tree, no language server, no plugins.
  The editing surface exists to support quick fixes to a document you're
  primarily *reading*, not to write code or long-form prose from scratch.

## Suggested keyboard shortcuts (adapt to your platform's conventions)

| Action | Typical binding |
|---|---|
| Preview / Edit / Split | Mod+1 / Mod+2 / Mod+3 |
| New / Save / Save As | Mod+N / Mod+S / Mod+Shift+S |
| Find / Find & Replace | Mod+F / Mod+Alt+F (or Mod+H) |
| Open / Open Path / Close | Mod+O / Mod+Shift+G / Mod+W |
| Table of Contents | Mod+Shift+T |
| Print / Export PDF | Mod+P |
| Reload from disk | Mod+R |
| Zoom in / out / reset | Mod+Plus / Mod+Minus / Mod+0 |

("Mod" = Cmd on macOS, Ctrl on Windows/Linux.)
