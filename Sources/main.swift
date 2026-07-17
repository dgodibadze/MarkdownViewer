import Cocoa
import WebKit
import UniformTypeIdentifiers

// MARK: - Markdown rendering

/// Builds the full HTML document for a markdown file by substituting tokens
/// in the bundled template. Asset CSS/JS are referenced via absolute file URLs
/// into the app's Resources directory; relative images in the markdown resolve
/// against the markdown file's own directory via <base href>.
enum Renderer {
    static func templateURL() -> URL? {
        return Bundle.main.url(forResource: "template", withExtension: "html")
    }

    static func resourcesDirURL() -> URL {
        return Bundle.main.resourceURL ?? Bundle.main.bundleURL
    }

    /// Renders the markdown file (or an empty untitled document when `markdownFile`
    /// is nil) into `tempFile`.
    /// Returns nil on success, or a human-readable error string on failure.
    static func render(markdownFile: URL?, into tempFile: URL) -> String? {
        guard let tplURL = templateURL() else {
            return "Bundled template.html not found in app Resources."
        }
        guard var template = try? String(contentsOf: tplURL, encoding: .utf8) else {
            return "Could not read bundled template at \(tplURL.path)."
        }

        // Read the markdown. Try UTF-8, then fall back to a lossy decode so odd
        // encodings still display instead of failing outright.
        let mdText: String
        if let markdownFile = markdownFile {
            do {
                mdText = try String(contentsOf: markdownFile, encoding: .utf8)
            } catch {
                if let data = try? Data(contentsOf: markdownFile) {
                    mdText = String(decoding: data, as: UTF8.self)
                } else {
                    return "Could not read the markdown file.\n\(error.localizedDescription)"
                }
            }
        } else {
            mdText = ""
        }

        // Resources directory as a file:// URL string (assets live here).
        let resDir = resourcesDirURL().absoluteString.trimmingTrailingSlash()
        // Markdown file's directory for resolving relative images/links.
        // Untitled documents have no directory yet — use home as a placeholder.
        let baseDir = (markdownFile?.deletingLastPathComponent()
            ?? FileManager.default.homeDirectoryForCurrentUser).absoluteString
        let title = markdownFile?.lastPathComponent ?? "Untitled"

        // __MARKDOWN__ must be substituted LAST: it injects arbitrary document
        // text, and any token replaced after it would also match occurrences of
        // that token *inside* the document (e.g. a markdown file that mentions
        // "__TITLE__" literally would get corrupted).
        template = template.replacingOccurrences(of: "__RES__", with: resDir)
        template = template.replacingOccurrences(of: "__BASE__", with: baseDir)
        template = template.replacingOccurrences(of: "__TITLE__", with: htmlEscape(title))
        template = template.replacingOccurrences(of: "__MARKDOWN__", with: jsStringLiteral(mdText))

        // Ensure the temp directory exists, then write. Report the real error.
        do {
            try FileManager.default.createDirectory(
                at: tempFile.deletingLastPathComponent(),
                withIntermediateDirectories: true)
            try template.write(to: tempFile, atomically: true, encoding: .utf8)
            return nil
        } catch {
            return "Could not write the render file at \(tempFile.path).\n\(error.localizedDescription)"
        }
    }

    static func jsStringLiteral(_ s: String) -> String {
        if let data = try? JSONSerialization.data(withJSONObject: [s], options: []),
           let arr = String(data: data, encoding: .utf8) {
            // arr looks like ["...escaped..."]; strip the brackets.
            var t = arr
            t.removeFirst()
            t.removeLast()
            // A literal "</script>" inside the document would terminate the
            // inline <script> that embeds it. JSONSerialization happens to
            // escape "/" already, but don't rely on that implicitly.
            return t.replacingOccurrences(of: "</", with: "<\\/")
        }
        // Fallback: minimal manual escaping.
        let escaped = s
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
            .replacingOccurrences(of: "\n", with: "\\n")
            .replacingOccurrences(of: "\r", with: "\\r")
            .replacingOccurrences(of: "</", with: "<\\/")
        return "\"\(escaped)\""
    }

    static func htmlEscape(_ s: String) -> String {
        return s
            .replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
    }
}

private extension String {
    func trimmingTrailingSlash() -> String {
        var s = self
        while s.hasSuffix("/") { s.removeLast() }
        return s
    }
}

// MARK: - Viewer window

final class ViewerWindowController: NSWindowController, WKNavigationDelegate, WKScriptMessageHandler {
    private static var isFirstWindow = true
    private static var cascadePoint = NSPoint.zero

    /// nil = a new, never-saved "Untitled" document; the first save asks where
    /// to put it (and with what name/extension).
    private(set) var fileURL: URL?
    private var webView: WKWebView!
    private let tempFile: URL
    private var watchTimer: Timer?
    private var lastModified: Date?

    /// True when the in-page editor has unsaved changes. While dirty, the
    /// external-change live-reload is suspended so it can't clobber edits.
    private(set) var isDirty = false
    /// Latest editor text pushed from the page, used for menu/close-triggered saves.
    private var lastText: String = ""
    /// True when the file on disk used CRLF line endings. The textarea's
    /// `value` is LF-normalized by the HTML spec, so without re-applying CRLF
    /// on save a one-character edit would silently rewrite every line ending.
    private var usesCRLF = false
    /// Mode to force once the page finishes loading (e.g. "split" for new
    /// documents). Calling __setMode before the page loads is a silent no-op,
    /// so this is applied from didFinish instead.
    var startMode: String?

    var displayName: String { fileURL?.lastPathComponent ?? "Untitled" }

    init(fileURL: URL?) {
        self.fileURL = fileURL?.resolvingSymlinksInPath()
        let tmpDir = URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
            .appendingPathComponent("MarkdownViewer", isDirectory: true)
        try? FileManager.default.createDirectory(at: tmpDir, withIntermediateDirectories: true)
        self.tempFile = tmpDir.appendingPathComponent("view-\(UUID().uuidString).html")

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 900, height: 760),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.tabbingMode = .preferred
        window.tabbingIdentifier = "MarkdownViewer"
        window.title = self.fileURL?.lastPathComponent ?? "Untitled"
        // Only the first window restores/saves the shared frame — giving every
        // window the same autosave name made them fight over it and stack
        // exactly on top of each other. Later windows cascade instead.
        if Self.isFirstWindow {
            Self.isFirstWindow = false
            window.setFrameAutosaveName("MarkdownViewerWindow")
        }
        Self.cascadePoint = window.cascadeTopLeft(from: Self.cascadePoint)
        window.minSize = NSSize(width: 420, height: 320)
        super.init(window: window)

        let config = WKWebViewConfiguration()
        config.preferences.javaScriptCanOpenWindowsAutomatically = false
        let ucc = WKUserContentController()
        config.userContentController = ucc
        webView = WKWebView(frame: window.contentView!.bounds, configuration: config)
        // Registered after init so `self` is fully available.
        ucc.add(self, name: "bridge")
        webView.autoresizingMask = [.width, .height]
        webView.navigationDelegate = self
        window.contentView?.addSubview(webView)

        renderAndLoad()
        if self.fileURL != nil { startWatching() }
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) not used") }

    private func renderAndLoad() {
        if let err = Renderer.render(markdownFile: fileURL, into: tempFile) {
            let safe = err
                .replacingOccurrences(of: "&", with: "&amp;")
                .replacingOccurrences(of: "<", with: "&lt;")
            let html = "<html><body style='font-family:-apple-system,sans-serif;padding:2rem;white-space:pre-wrap'>"
                + "<h3>MarkdownViewer could not render</h3>"
                + "<b>File:</b> \(fileURL?.path ?? "Untitled")<br><br>\(safe)</body></html>"
            webView.loadHTMLString(html, baseURL: nil)
            // Mark the current mtime as seen even on failure — otherwise the
            // 1 Hz watcher re-renders the error page every second, forever.
            lastModified = modificationDate()
            return
        }
        // Allow read access to "/" so the bundled assets (in Resources) and any
        // local images referenced by the markdown can both load.
        webView.loadFileURL(tempFile, allowingReadAccessTo: URL(fileURLWithPath: "/"))
        lastModified = modificationDate()
        // Seed the save cache with what's actually on disk. Without this, a
        // menu-triggered Save before any edit would write "" and wipe the file;
        // after an external-change reload it would silently revert the file.
        if let fileURL = fileURL, let data = try? Data(contentsOf: fileURL) {
            lastText = String(decoding: data, as: UTF8.self)
            usesCRLF = lastText.contains("\r\n")
        }
    }

    private func modificationDate() -> Date? {
        guard let fileURL = fileURL else { return nil }
        let attrs = try? FileManager.default.attributesOfItem(atPath: fileURL.path)
        return attrs?[.modificationDate] as? Date
    }

    // Live reload: poll the file's modification date once per second.
    private func startWatching() {
        guard fileURL != nil else { return }
        watchTimer?.invalidate()
        watchTimer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            guard let self = self else { return }
            // Suspend reload while there are unsaved edits so we never clobber them.
            if self.isDirty { return }
            let current = self.modificationDate()
            if let current = current, current != self.lastModified {
                self.lastModified = current
                self.renderAndLoad()
            }
        }
    }

    // MARK: Editor bridge

    /// Receives messages posted from the page's `bridge` message handler.
    func userContentController(_ userContentController: WKUserContentController,
                              didReceive message: WKScriptMessage) {
        guard let body = message.body as? [String: Any],
              let action = body["action"] as? String else { return }
        switch action {
        case "dirty":
            isDirty = (body["dirty"] as? Bool) ?? false
        case "change":
            if let text = body["text"] as? String { lastText = text }
        case "save":
            if let text = body["text"] as? String { lastText = text }
            save()
        case "setWrap":
            let on = (body["wrap"] as? Bool) ?? true
            (NSApp.delegate as? AppDelegate)?.updateWrapState(on)
        default:
            break
        }
    }

    /// Saves the document: pulls the live editor text from the page, then writes it.
    /// Falls back to the cached `lastText` when the page can't answer (e.g. the
    /// error page is showing). Untitled documents run a save panel first (default
    /// name "Untitled.md"; the user may type any other name/extension).
    /// Captures self strongly so a save triggered while the window is closing
    /// still completes. `completion(false)` = the save didn't happen (panel
    /// cancelled or write failed).
    func save(completion: ((Bool) -> Void)? = nil) {
        webView.evaluateJavaScript("window.__getText ? window.__getText() : null") { result, _ in
            if let text = result as? String { self.lastText = text }
            if self.fileURL == nil {
                self.saveAs(completion: completion)
            } else {
                // NOTE: compute the write FIRST, then call the optional
                // completion. `completion?(writeToDisk())` short-circuits the
                // whole expression when completion is nil — so the toolbar Save
                // button and ⌘S (which pass no completion) never wrote at all,
                // while the close/quit dialog (which passes one) did.
                let ok = self.writeToDisk()
                completion?(ok)
            }
        }
    }

    /// First save of an Untitled document: ask where to put it. Defaults to .md
    /// but `allowsOtherFileTypes` lets the user type any extension.
    private func saveAs(completion: ((Bool) -> Void)?) {
        guard let window = window else { completion?(false); return }
        let panel = NSSavePanel()
        panel.nameFieldStringValue = "Untitled.md"
        if let md = UTType(filenameExtension: "md") { panel.allowedContentTypes = [md] }
        panel.allowsOtherFileTypes = true
        panel.canCreateDirectories = true
        panel.beginSheetModal(for: window) { resp in
            guard resp == .OK, let url = panel.url else { completion?(false); return }
            self.fileURL = url.resolvingSymlinksInPath()
            let ok = self.writeToDisk()
            if ok {
                self.window?.title = self.displayName
                self.startWatching()
                // Re-render so <base href> points at the real folder (relative
                // images now resolve) — editor content equals what was saved.
                self.renderAndLoad()
                (NSApp.delegate as? AppDelegate)?.documentDidGetFile(self)
            }
            completion?(ok)
        }
    }

    /// Writes the latest editor text back to the markdown file on disk.
    @discardableResult
    private func writeToDisk() -> Bool {
        guard let fileURL = fileURL else { return false }
        do {
            // Preserve the file's original line endings: the editor always
            // hands back LF, so normalize first, then re-apply CRLF if that's
            // what the file used.
            var text = lastText.replacingOccurrences(of: "\r\n", with: "\n")
            if usesCRLF { text = text.replacingOccurrences(of: "\n", with: "\r\n") }
            try text.write(to: fileURL, atomically: true, encoding: .utf8)
            // Treat our own write as already-seen so the poll doesn't reload it.
            lastModified = modificationDate()
            isDirty = false
            webView.evaluateJavaScript("window.__onSaved && window.__onSaved()")
            return true
        } catch {
            let alert = NSAlert()
            alert.alertStyle = .warning
            alert.messageText = "Could not save the file."
            alert.informativeText = "\(fileURL.path)\n\n\(error.localizedDescription)"
            alert.addButton(withTitle: "OK")
            alert.runModal()
            return false
        }
    }

    /// Drives the page's Preview/Edit/Split toggle from the native View menu.
    /// `persist: false` applies the mode without overwriting the remembered
    /// preference (used for the deferred startMode of new documents).
    func setEditorMode(_ mode: String, persist: Bool = true) {
        let safe = mode.replacingOccurrences(of: "'", with: "")
        webView.evaluateJavaScript("window.__setMode && window.__setMode('\(safe)', \(persist))")
    }

    /// Toggles soft-wrap in the editor from the native View menu.
    func toggleWrapInPage() {
        webView.evaluateJavaScript("window.__toggleWrap && window.__toggleWrap()")
    }

    /// Opens the in-page find / find-and-replace bar from the native Edit menu.
    func findInPage() { webView.evaluateJavaScript("window.__find && window.__find()") }
    func findReplaceInPage() { webView.evaluateJavaScript("window.__findReplace && window.__findReplace()") }

    /// Re-renders the document from disk, deliberately discarding in-page edits
    /// (the caller confirms with the user first when dirty). No-op for Untitled
    /// documents — there is no disk copy to reload.
    func reloadFromDisk() {
        guard fileURL != nil else { NSSound.beep(); return }
        isDirty = false
        renderAndLoad()
    }

    func stop() {
        watchTimer?.invalidate()
        watchTimer = nil
        // Break the retain cycle created by `ucc.add(self, name:)`.
        webView.configuration.userContentController.removeScriptMessageHandler(forName: "bridge")
        try? FileManager.default.removeItem(at: tempFile)
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        if let mode = startMode {
            startMode = nil
            setEditorMode(mode, persist: false)
        }
    }

    // Only local content may load in the web view. A real link click on
    // http/https/mailto opens in the default browser; every other non-file
    // navigation (scripted, <meta http-equiv="refresh">, form submission from
    // raw HTML in a document) is cancelled outright — a malicious markdown
    // file must not be able to steer the viewer to a remote page.
    func webView(_ webView: WKWebView,
                 decidePolicyFor navigationAction: WKNavigationAction,
                 decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        guard let url = navigationAction.request.url,
              let scheme = url.scheme?.lowercased() else {
            decisionHandler(.allow)
            return
        }
        if scheme == "file" || scheme == "about" {
            decisionHandler(.allow)
            return
        }
        if navigationAction.navigationType == .linkActivated,
           scheme == "http" || scheme == "https" || scheme == "mailto" {
            NSWorkspace.shared.open(url)
        }
        decisionHandler(.cancel)
    }
}

// MARK: - App delegate

final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, NSMenuDelegate {
    /// All open documents, both file-backed and Untitled.
    var controllers: [ViewerWindowController] = []
    private var wrapMenuItem: NSMenuItem?
    private var wrapOn = true
    private var aboutWindow: NSWindow?
    private let openRecentMenu = NSMenu(title: "Open Recent")

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSWindow.allowsAutomaticWindowTabbing = true
        buildMenu()
        if controllers.isEmpty {
            // Launched with no document: prompt to open one.
            DispatchQueue.main.async { [weak self] in self?.openPanel() }
        }
    }

    func application(_ application: NSApplication, open urls: [URL]) {
        for url in urls { openFile(url) }
    }

    func application(_ sender: NSApplication, openFile filename: String) -> Bool {
        openFile(URL(fileURLWithPath: filename))
        return true
    }

    func applicationShouldOpenUntitledFile(_ sender: NSApplication) -> Bool { false }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    /// The on-disk canonical path (fixes letter case on case-insensitive
    /// volumes) so "README.md" and "readme.md" dedupe to one window.
    private func canonicalPath(_ url: URL) -> String {
        return (try? url.resourceValues(forKeys: [.canonicalPathKey]))?.canonicalPath ?? url.path
    }

    func openFile(_ url: URL, noteAsRecent: Bool = true) {
        let resolved = url.resolvingSymlinksInPath()
        let target = canonicalPath(resolved)
        if let existing = controllers.first(where: { c in
            guard let f = c.fileURL else { return false }
            return canonicalPath(f) == target
        }) {
            existing.window?.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }
        let controller = ViewerWindowController(fileURL: resolved)
        controller.window?.delegate = self
        controllers.append(controller)
        controller.showWindow(nil)
        NSApp.activate(ignoringOtherApps: true)
        if noteAsRecent { noteRecent(resolved) }
    }

    /// File ▸ New: a blank Untitled document opening in Split mode; the first
    /// save asks for a location (default .md, any typed extension accepted).
    @objc func newDocument(_ sender: Any?) {
        let controller = ViewerWindowController(fileURL: nil)
        controller.startMode = "split"
        controller.window?.delegate = self
        controllers.append(controller)
        controller.showWindow(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    /// Called by a controller after an Untitled document is saved for the first time.
    func documentDidGetFile(_ controller: ViewerWindowController) {
        if let url = controller.fileURL { noteRecent(url) }
    }

    // MARK: Recent files (persisted in UserDefaults, shown under File ▸ Open Recent)

    private let recentsKey = "recentFiles"
    private let recentsMax = 10

    private func recentPaths() -> [String] {
        return UserDefaults.standard.stringArray(forKey: recentsKey) ?? []
    }

    private func noteRecent(_ url: URL) {
        var paths = recentPaths().filter { $0 != url.path }
        paths.insert(url.path, at: 0)
        if paths.count > recentsMax { paths = Array(paths.prefix(recentsMax)) }
        UserDefaults.standard.set(paths, forKey: recentsKey)
        // Also tell the system, so the Dock icon's right-click menu stays in sync.
        NSDocumentController.shared.noteNewRecentDocumentURL(url)
    }

    /// Rebuilds File ▸ Open Recent every time it's about to show.
    func menuNeedsUpdate(_ menu: NSMenu) {
        guard menu === openRecentMenu else { return }
        menu.removeAllItems()
        let existing = recentPaths().filter { FileManager.default.fileExists(atPath: $0) }
        for path in existing {
            let item = NSMenuItem(title: (path as NSString).lastPathComponent,
                                  action: #selector(openRecent(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = path
            item.toolTip = path
            menu.addItem(item)
        }
        if existing.isEmpty {
            let none = NSMenuItem(title: "No Recent Files", action: nil, keyEquivalent: "")
            none.isEnabled = false
            menu.addItem(none)
        }
        menu.addItem(NSMenuItem.separator())
        let clear = NSMenuItem(title: "Clear Menu", action: #selector(clearRecents(_:)), keyEquivalent: "")
        clear.target = self
        clear.isEnabled = !existing.isEmpty
        menu.addItem(clear)
    }

    @objc private func openRecent(_ sender: NSMenuItem) {
        guard let path = sender.representedObject as? String else { return }
        openFromString(path)
    }

    @objc private func clearRecents(_ sender: Any?) {
        UserDefaults.standard.removeObject(forKey: recentsKey)
        NSDocumentController.shared.clearRecentDocuments(nil)
    }

    private enum UnsavedChoice { case save, discard, cancel }

    /// The standard Save / Don't Save / Cancel dialog for a dirty document.
    private func unsavedChangesChoice(for controller: ViewerWindowController) -> UnsavedChoice {
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "Do you want to save the changes you made to “\(controller.displayName)”?"
        alert.informativeText = "Your changes will be lost if you don't save them."
        alert.addButton(withTitle: "Save")
        alert.addButton(withTitle: "Don't Save")
        alert.addButton(withTitle: "Cancel")
        switch alert.runModal() {
        case .alertFirstButtonReturn:  return .save
        case .alertSecondButtonReturn: return .discard
        default:                       return .cancel
        }
    }

    /// Warn before closing a window/tab that has unsaved edits.
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        guard let controller = controllers.first(where: { $0.window === sender }),
              controller.isDirty else { return true }
        switch unsavedChangesChoice(for: controller) {
        case .save:
            // Saves are asynchronous (they pull the live text from the page,
            // and Untitled documents run a save panel first) — keep the window
            // open and only close it once the write actually succeeded.
            // Closing immediately would tear the page down mid-save and fall
            // back to a cached copy that can be ~250ms stale.
            controller.save { ok in if ok { sender.close() } }
            return false
        case .discard: return true
        case .cancel:  return false
        }
    }

    /// Quit must honor unsaved edits too — closing windows during termination
    /// does NOT go through windowShouldClose. Saves are async (they pull the
    /// live text from the page), so termination is deferred until all chosen
    /// saves have hit the disk.
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        var toSave: [ViewerWindowController] = []
        for controller in controllers where controller.isDirty {
            controller.window?.makeKeyAndOrderFront(nil)
            switch unsavedChangesChoice(for: controller) {
            case .save:    toSave.append(controller)
            case .discard: break
            case .cancel:  return .terminateCancel
            }
        }
        guard !toSave.isEmpty else { return .terminateNow }
        var remaining = toSave.count
        var aborted = false
        for controller in toSave {
            controller.save { ok in
                if aborted { return }
                // A cancelled save panel or failed write aborts the quit —
                // terminating anyway would throw the unsaved document away.
                if !ok { aborted = true; NSApp.reply(toApplicationShouldTerminate: false); return }
                remaining -= 1
                if remaining == 0 { NSApp.reply(toApplicationShouldTerminate: true) }
            }
        }
        return .terminateLater
    }

    func windowWillClose(_ notification: Notification) {
        guard let window = notification.object as? NSWindow else { return }
        for controller in controllers where controller.window === window {
            controller.stop()
        }
        controllers.removeAll { $0.window === window }
    }

    /// The controller backing the current key window — falling back to the main
    /// window's controller when a non-document window (e.g. About) is key, so
    /// File ▸ Save and friends never silently no-op.
    private func keyController() -> ViewerWindowController? {
        if let c = controllers.first(where: { $0.window?.isKeyWindow == true }) { return c }
        return controllers.first(where: { $0.window?.isMainWindow == true })
    }

    @objc func saveDocument(_ sender: Any?) { keyController()?.save() }

    @objc func setPreviewMode(_ sender: Any?) { setMode("preview") }
    @objc func setEditMode(_ sender: Any?) { setMode("edit") }
    @objc func setSplitMode(_ sender: Any?) { setMode("split") }

    private func setMode(_ mode: String) {
        keyController()?.setEditorMode(mode)
    }

    @objc func toggleWrap(_ sender: Any?) { keyController()?.toggleWrapInPage() }

    @objc func performFind(_ sender: Any?) { keyController()?.findInPage() }
    @objc func performFindReplace(_ sender: Any?) { keyController()?.findReplaceInPage() }

    /// Called from the page (via the `setWrap` bridge action) to keep the menu checkmark synced.
    func updateWrapState(_ on: Bool) {
        wrapOn = on
        wrapMenuItem?.state = on ? .on : .off
    }

    @objc func openDocument(_ sender: Any?) { openPanel() }

    func openPanel() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = false
        panel.canChooseFiles = true
        panel.allowedContentTypes = ["md", "markdown", "mdown", "mkd", "mkdn", "mdwn", "markdn", "mdtxt", "text", "rmd", "qmd", "mdx", "mdc"]
            .compactMap { UTType(filenameExtension: $0) } + [.plainText]
        if panel.runModal() == .OK {
            for url in panel.urls { openFile(url) }
        }
    }

    /// Open by typing or pasting an absolute path, e.g. /Users/James/USER.md
    @objc func openPath(_ sender: Any?) {
        let alert = NSAlert()
        alert.messageText = "Open Path"
        alert.informativeText = "Paste the full path to a Markdown file.\nExample: /Users/James/USER.md"
        alert.addButton(withTitle: "Open")
        alert.addButton(withTitle: "Cancel")

        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 440, height: 24))
        field.placeholderString = "/Users/James/USER.md"
        // Pre-fill from the clipboard when it already looks like a path.
        if let clip = NSPasteboard.general.string(forType: .string) {
            let t = clip.trimmingCharacters(in: .whitespacesAndNewlines)
            if t.hasPrefix("/") || t.hasPrefix("~") || t.hasPrefix("file://") {
                field.stringValue = t
            }
        }
        alert.accessoryView = field
        alert.window.initialFirstResponder = field

        if alert.runModal() == .alertFirstButtonReturn {
            openFromString(field.stringValue)
        }
    }

    /// Normalize a pasted path string and open it if it points to a real file.
    func openFromString(_ raw: String) {
        var s = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !s.isEmpty else { return }
        // Strip surrounding quotes (paths copied from a terminal are often quoted).
        if (s.hasPrefix("\"") && s.hasSuffix("\"")) || (s.hasPrefix("'") && s.hasSuffix("'")) {
            s = String(s.dropFirst().dropLast())
        }
        // Unescape shell-style "\ " spaces.
        s = s.replacingOccurrences(of: "\\ ", with: " ")

        let url: URL
        if s.hasPrefix("file://") {
            url = URL(string: s) ?? URL(fileURLWithPath: (s as NSString).expandingTildeInPath)
        } else {
            url = URL(fileURLWithPath: (s as NSString).expandingTildeInPath)
        }

        var isDir: ObjCBool = false
        if FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir), !isDir.boolValue {
            openFile(url)
        } else {
            let err = NSAlert()
            err.messageText = "File not found"
            err.informativeText = url.path
            err.addButton(withTitle: "OK")
            err.runModal()
        }
    }

    /// View ▸ Reload: re-render the key document from disk. Unlike the old
    /// WKWebView.reload (which reloaded the stale temp HTML), this picks up
    /// external file changes — and asks before discarding unsaved edits.
    @objc func reloadFromDisk(_ sender: Any?) {
        guard let controller = keyController() else { return }
        if controller.isDirty {
            let alert = NSAlert()
            alert.alertStyle = .warning
            alert.messageText = "Reload “\(controller.displayName)” from disk?"
            alert.informativeText = "Your unsaved changes will be lost."
            alert.addButton(withTitle: "Reload")
            alert.addButton(withTitle: "Cancel")
            guard alert.runModal() == .alertFirstButtonReturn else { return }
        }
        controller.reloadFromDisk()
    }

    // MARK: About + bundled docs

    @objc func showAbout(_ sender: Any?) {
        if aboutWindow == nil { aboutWindow = makeAboutWindow() }
        aboutWindow?.center()
        aboutWindow?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    private func makeAboutWindow() -> NSWindow {
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 380, height: 320),
                         styleMask: [.titled, .closable], backing: .buffered, defer: false)
        w.title = "About MarkdownViewer"
        w.isReleasedWhenClosed = false

        let version = (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "1.0"

        let icon = NSImageView()
        icon.image = NSApp.applicationIconImage
        icon.imageScaling = .scaleProportionallyUpOrDown
        icon.translatesAutoresizingMaskIntoConstraints = false
        icon.widthAnchor.constraint(equalToConstant: 84).isActive = true
        icon.heightAnchor.constraint(equalToConstant: 84).isActive = true

        let name = NSTextField(labelWithString: "MarkdownViewer")
        name.font = .systemFont(ofSize: 18, weight: .semibold)
        name.alignment = .center

        let ver = NSTextField(labelWithString: "Version \(version)")
        ver.textColor = .secondaryLabelColor
        ver.alignment = .center

        let author = NSTextField(labelWithString: "by David Godibadze")
        author.alignment = .center

        let changelogBtn = NSButton(title: "Changelog", target: self, action: #selector(openChangelog(_:)))
        changelogBtn.bezelStyle = .rounded
        let archBtn = NSButton(title: "Architecture", target: self, action: #selector(openArchitecture(_:)))
        archBtn.bezelStyle = .rounded
        let designBtn = NSButton(title: "Design", target: self, action: #selector(openDesign(_:)))
        designBtn.bezelStyle = .rounded

        let buttons = NSStackView(views: [changelogBtn, archBtn, designBtn])
        buttons.orientation = .horizontal
        buttons.spacing = 10

        let stack = NSStackView(views: [icon, name, ver, author, buttons])
        stack.orientation = .vertical
        stack.alignment = .centerX
        stack.spacing = 10
        stack.setCustomSpacing(18, after: author)
        stack.translatesAutoresizingMaskIntoConstraints = false

        let content = w.contentView!
        content.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.centerXAnchor.constraint(equalTo: content.centerXAnchor),
            stack.centerYAnchor.constraint(equalTo: content.centerYAnchor),
        ])
        return w
    }

    @objc func openChangelog(_ sender: Any?) { openBundledDoc("CHANGELOG.md") }
    @objc func openArchitecture(_ sender: Any?) { openBundledDoc("ARCHITECTURE.md") }
    @objc func openDesign(_ sender: Any?) { openBundledDoc("DESIGN.md") }

    /// Opens a bundled markdown doc by copying it to a throwaway temp file and viewing that
    /// (so any edits never touch the file inside the app bundle).
    private func openBundledDoc(_ name: String) {
        let base = (name as NSString).deletingPathExtension
        guard let src = Bundle.main.url(forResource: base, withExtension: "md") else {
            NSSound.beep(); return
        }
        let tmpDir = URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
            .appendingPathComponent("MarkdownViewer", isDirectory: true)
        try? FileManager.default.createDirectory(at: tmpDir, withIntermediateDirectories: true)
        let dst = tmpDir.appendingPathComponent(name)
        try? FileManager.default.removeItem(at: dst)
        do { try FileManager.default.copyItem(at: src, to: dst) } catch { NSSound.beep(); return }
        // Throwaway temp copies don't belong in Open Recent.
        openFile(dst, noteAsRecent: false)
    }

    // MARK: Menu

    func buildMenu() {
        let mainMenu = NSMenu()

        // App menu
        let appMenuItem = NSMenuItem()
        mainMenu.addItem(appMenuItem)
        let appMenu = NSMenu()
        let appName = ProcessInfo.processInfo.processName
        appMenu.addItem(withTitle: "About \(appName)", action: #selector(showAbout(_:)), keyEquivalent: "")
        appMenu.addItem(NSMenuItem.separator())
        appMenu.addItem(withTitle: "Hide \(appName)", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(withTitle: "Quit \(appName)", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appMenuItem.submenu = appMenu

        // File menu
        let fileMenuItem = NSMenuItem()
        mainMenu.addItem(fileMenuItem)
        let fileMenu = NSMenu(title: "File")
        fileMenu.addItem(withTitle: "New", action: #selector(newDocument(_:)), keyEquivalent: "n")
        fileMenu.addItem(withTitle: "Open…", action: #selector(openDocument(_:)), keyEquivalent: "o")
        let openPathItem = fileMenu.addItem(withTitle: "Open Path…", action: #selector(openPath(_:)), keyEquivalent: "g")
        openPathItem.keyEquivalentModifierMask = [.command, .shift]
        let openRecentItem = fileMenu.addItem(withTitle: "Open Recent", action: nil, keyEquivalent: "")
        openRecentMenu.delegate = self   // rebuilt from UserDefaults on every open
        openRecentItem.submenu = openRecentMenu
        fileMenu.addItem(NSMenuItem.separator())
        fileMenu.addItem(withTitle: "Save", action: #selector(saveDocument(_:)), keyEquivalent: "s")
        let closeItem = fileMenu.addItem(withTitle: "Close", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        closeItem.target = nil
        fileMenuItem.submenu = fileMenu

        // Edit menu. These items exist so their Cmd key equivalents reach the
        // first responder (the web view / its textarea). Without a menu item
        // declaring the shortcut, macOS never dispatches it — which is why
        // Cut and Paste silently did nothing before.
        let editMenuItem = NSMenuItem()
        mainMenu.addItem(editMenuItem)
        let editMenu = NSMenu(title: "Edit")

        editMenu.addItem(withTitle: "Undo", action: NSSelectorFromString("undo:"), keyEquivalent: "z")
        let redoItem = editMenu.addItem(withTitle: "Redo", action: NSSelectorFromString("redo:"), keyEquivalent: "z")
        redoItem.keyEquivalentModifierMask = [.command, .shift]
        editMenu.addItem(NSMenuItem.separator())

        editMenu.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        editMenu.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        editMenu.addItem(withTitle: "Delete", action: #selector(NSText.delete(_:)), keyEquivalent: "")
        editMenu.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editMenu.addItem(NSMenuItem.separator())
        editMenu.addItem(withTitle: "Find…", action: #selector(performFind(_:)), keyEquivalent: "f")
        let findReplaceItem = editMenu.addItem(withTitle: "Find and Replace…", action: #selector(performFindReplace(_:)), keyEquivalent: "f")
        findReplaceItem.keyEquivalentModifierMask = [.command, .option]
        editMenuItem.submenu = editMenu

        // View menu
        let viewMenuItem = NSMenuItem()
        mainMenu.addItem(viewMenuItem)
        let viewMenu = NSMenu(title: "View")
        viewMenu.addItem(withTitle: "Preview", action: #selector(setPreviewMode(_:)), keyEquivalent: "1")
        viewMenu.addItem(withTitle: "Edit", action: #selector(setEditMode(_:)), keyEquivalent: "2")
        viewMenu.addItem(withTitle: "Split", action: #selector(setSplitMode(_:)), keyEquivalent: "3")
        viewMenu.addItem(NSMenuItem.separator())
        let wrapItem = viewMenu.addItem(withTitle: "Wrap Lines", action: #selector(toggleWrap(_:)), keyEquivalent: "")
        wrapItem.state = wrapOn ? .on : .off
        wrapMenuItem = wrapItem
        viewMenu.addItem(NSMenuItem.separator())
        viewMenu.addItem(withTitle: "Reload From Disk", action: #selector(reloadFromDisk(_:)), keyEquivalent: "r")
        viewMenuItem.submenu = viewMenu

        // Window menu (gives native tabbing items)
        let windowMenuItem = NSMenuItem()
        mainMenu.addItem(windowMenuItem)
        let windowMenu = NSMenu(title: "Window")
        windowMenu.addItem(withTitle: "Minimize", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        windowMenu.addItem(withTitle: "Zoom", action: #selector(NSWindow.performZoom(_:)), keyEquivalent: "")
        windowMenuItem.submenu = windowMenu
        NSApp.windowsMenu = windowMenu

        NSApp.mainMenu = mainMenu
    }
}

// MARK: - Entry point

let app = NSApplication.shared
app.setActivationPolicy(.regular)
let delegate = AppDelegate()
app.delegate = delegate
app.run()
