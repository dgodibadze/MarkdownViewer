# NOTICE — MarkdownViewer

MarkdownViewer is licensed under the **GNU General Public License v3.0 (GPLv3)**, the same
license as the **QLMarkdown** project it began as a companion to. The full license text is
in `LICENSE.txt`.

## Original project

This app was built within and as a companion to **QLMarkdown** by **sbarex**:
https://github.com/sbarex/QLMarkdown

MarkdownViewer is an independent macOS app (its own Swift source plus the bundled web
libraries below); it does not reuse QLMarkdown's C/Swift rendering engine (cmark-gfm et al.).
Per GPLv3, this derivative remains under GPLv3, retains the original attribution, and notes
that it has been modified/extended.

## Bundled third-party libraries

These are downloaded by `build.sh` and bundled into the app for offline use. Their licenses
require their copyright/permission notices to be retained:

- **marked** — MIT License. https://github.com/markedjs/marked
- **highlight.js** — BSD 3-Clause License. © Ivan Sagalaev and contributors.
  https://github.com/highlightjs/highlight.js
- **github-markdown-css** — MIT License. © Sindre Sorhus.
  https://github.com/sindresorhus/github-markdown-css

(The MIT and BSD-3-Clause license texts are short and available at the links above; include
copies alongside this NOTICE when redistributing binaries.)
