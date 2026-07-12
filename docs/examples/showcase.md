# MarkdownViewer Showcase

A quick tour of everything the viewer renders — headings, tables, task lists,
code with syntax highlighting, quotes, and links. Jump straight to a section:

[Formatting](#formatting-basics) · [Code](#code-highlighting) ·
[Tables](#tables) · [Tasks](#task-lists) · [Quotes & Rules](#quotes--rules)

---

## Formatting basics

Text can be **bold**, *italic*, ***both***, ~~struck through~~, or `inline code`.
Links open in your default browser: [marked](https://marked.js.org),
[highlight.js](https://highlightjs.org), and in-document anchors scroll in place.

Relative images resolve against the file's own folder:

![MarkdownViewer icon](../icon.png)

## Code highlighting

Hover any block for the copy button — it grabs exactly what's between the fences.

```swift
/// One window controller per open document.
final class ViewerWindowController: NSWindowController {
    private(set) var fileURL: URL?

    var displayName: String { fileURL?.lastPathComponent ?? "Untitled" }

    func save(completion: ((Bool) -> Void)? = nil) {
        webView.evaluateJavaScript("window.__getText()") { result, _ in
            // Always write the live editor text — never a stale cache.
        }
    }
}
```

```js
// Driver-pane scroll sync: only the pane the user is actually
// driving propagates its scroll — no feedback loops.
['wheel', 'mousedown', 'touchstart', 'keydown'].forEach(evt => {
  editor.addEventListener(evt, () => { driver = editor; });
  preview.addEventListener(evt, () => { driver = preview; });
});
```

```bash
# Build a universal binary (arm64 + x86_64) with no Xcode project
./build.sh && cp -R MarkdownViewer.app /Applications/
```

## Tables

| Mode | Shortcut | What you see |
|---|---|---|
| Preview | `⌘1` / `Ctrl+1` | Rendered document only |
| Edit | `⌘2` / `Ctrl+2` | Raw markdown only |
| Split | `⌘3` / `Ctrl+3` | Both, with synced scrolling |

## Task lists

- [x] Open `.md` files in a real window
- [x] Live reload when the file changes on disk
- [x] Find & replace on the raw markdown
- [x] New documents starting in Split mode
- [ ] Your next great document

## Quotes & rules

> The best markdown viewer is the one that gets out of your way —
> open, read, edit, save, done.

Numbered lists, nested lists, and everything in between:

1. First point
2. Second point
   - A nested detail
   - Another one
3. Third point

---

*Rendered fully offline with marked + highlight.js + GitHub CSS — no network, ever.*
