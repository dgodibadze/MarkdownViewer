import Cocoa
import WebKit
import Security

// MARK: - AI providers, Keychain, networking

enum ProviderKind: String { case openai, anthropic, gemini }

struct AIProvider {
    let id: String
    let name: String
    let kind: ProviderKind
    let defaultBaseURL: String
    let defaultModel: String
}

/// Built-in providers. Base URL + model are user-editable in Settings, so unusual or new
/// endpoints (and the exact model ids each account has access to) can be corrected by the user.
let AI_PROVIDERS: [AIProvider] = [
    AIProvider(id: "groq",      name: "Groq",        kind: .openai,
               defaultBaseURL: "https://api.groq.com/openai/v1",          defaultModel: "llama-3.3-70b-versatile"),
    AIProvider(id: "nous",      name: "Nous Portal", kind: .openai,
               defaultBaseURL: "https://inference-api.nousresearch.com/v1", defaultModel: "Hermes-3-Llama-3.1-70B"),
    AIProvider(id: "openai",    name: "OpenAI",      kind: .openai,
               defaultBaseURL: "https://api.openai.com/v1",               defaultModel: "gpt-4o"),
    AIProvider(id: "anthropic", name: "Anthropic",   kind: .anthropic,
               defaultBaseURL: "https://api.anthropic.com",               defaultModel: "claude-3-5-sonnet-latest"),
    AIProvider(id: "gemini",    name: "Gemini",      kind: .gemini,
               defaultBaseURL: "https://generativelanguage.googleapis.com/v1beta", defaultModel: "gemini-1.5-flash"),
]

struct ChatMessage { let role: String; let content: String }   // role: "user" | "assistant"
struct AIPrompt { let system: String; let messages: [ChatMessage] }

enum AIError: LocalizedError {
    case noKey(String), badResponse, http(Int, String), badURL(String)
    var errorDescription: String? {
        switch self {
        case .noKey(let name): return "No API key set for \(name). Open AI ▸ Settings… to add one."
        case .badResponse:     return "The AI service returned an unexpected response."
        case .http(let code, let msg): return "AI request failed (HTTP \(code)). \(msg)"
        case .badURL(let url): return "The AI endpoint URL is invalid: \(url)\nCheck the Base URL in AI ▸ Settings…."
        }
    }
}

/// Minimal Keychain wrapper: one generic-password item per provider id.
enum Keychain {
    static let service = "com.dave.markdownviewer.ai"
    static func set(_ value: String, account: String) {
        let base: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                    kSecAttrService as String: service,
                                    kSecAttrAccount as String: account]
        SecItemDelete(base as CFDictionary)
        guard let data = value.data(using: .utf8) else { return }
        var add = base; add[kSecValueData as String] = data
        SecItemAdd(add as CFDictionary, nil)
    }
    static func get(_ account: String) -> String? {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                kSecAttrService as String: service,
                                kSecAttrAccount as String: account,
                                kSecReturnData as String: true,
                                kSecMatchLimit as String: kSecMatchLimitOne]
        var out: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess,
              let data = out as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }
}

/// Builds and sends provider requests. Reads the active provider + overrides from UserDefaults.
final class AIService {
    static let shared = AIService()
    private let d = UserDefaults.standard

    func activeProvider() -> AIProvider {
        let id = d.string(forKey: "ai.activeProvider") ?? "groq"
        return AI_PROVIDERS.first(where: { $0.id == id }) ?? AI_PROVIDERS[0]
    }
    func baseURL(for p: AIProvider) -> String {
        let v = d.string(forKey: "ai.baseURL.\(p.id)") ?? ""
        return v.isEmpty ? p.defaultBaseURL : v
    }
    func model(for p: AIProvider) -> String {
        let v = d.string(forKey: "ai.model.\(p.id)") ?? ""
        return v.isEmpty ? p.defaultModel : v
    }
    func setActive(_ id: String) { d.set(id, forKey: "ai.activeProvider") }
    func setBaseURL(_ v: String, for id: String) { d.set(v, forKey: "ai.baseURL.\(id)") }
    func setModel(_ v: String, for id: String) { d.set(v, forKey: "ai.model.\(id)") }

    func complete(_ prompt: AIPrompt, completion: @escaping (Result<String, Error>) -> Void) {
        let p = activeProvider()
        guard let key = Keychain.get(p.id), !key.isEmpty else {
            return completion(.failure(AIError.noKey(p.name)))
        }
        let req: URLRequest
        do { req = try buildRequest(p, key: key, prompt: prompt) }
        catch { return completion(.failure(error)) }

        URLSession.shared.dataTask(with: req) { data, resp, err in
            let finish: (Result<String, Error>) -> Void = { r in DispatchQueue.main.async { completion(r) } }
            if let err = err { return finish(.failure(err)) }
            guard let http = resp as? HTTPURLResponse, let data = data else { return finish(.failure(AIError.badResponse)) }
            if http.statusCode >= 400 {
                let msg = String(data: data, encoding: .utf8).map { String($0.prefix(300)) } ?? ""
                return finish(.failure(AIError.http(http.statusCode, msg)))
            }
            do { finish(.success(try self.parse(p.kind, data))) }
            catch { finish(.failure(error)) }
        }.resume()
    }

    private func buildRequest(_ p: AIProvider, key: String, prompt: AIPrompt) throws -> URLRequest {
        let base = baseURL(for: p).hasSuffix("/") ? String(baseURL(for: p).dropLast()) : baseURL(for: p)
        let mdl = model(for: p)
        var req: URLRequest
        var json: [String: Any]

        // Base URL is user-editable in Settings — a malformed value must produce
        // a descriptive error, never a force-unwrap crash.
        func endpoint(_ path: String) throws -> URL {
            guard let url = URL(string: base + path) else { throw AIError.badURL(base + path) }
            return url
        }

        switch p.kind {
        case .openai:
            req = URLRequest(url: try endpoint("/chat/completions"))
            req.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            var msgs: [[String: String]] = [["role": "system", "content": prompt.system]]
            msgs += prompt.messages.map { ["role": $0.role, "content": $0.content] }
            json = ["model": mdl, "messages": msgs]
        case .anthropic:
            req = URLRequest(url: try endpoint("/v1/messages"))
            req.setValue(key, forHTTPHeaderField: "x-api-key")
            req.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")
            let msgs = prompt.messages.map { ["role": $0.role, "content": $0.content] }
            json = ["model": mdl, "max_tokens": 2048, "system": prompt.system, "messages": msgs]
        case .gemini:
            req = URLRequest(url: try endpoint("/models/\(mdl):generateContent?key=\(key)"))
            let contents = prompt.messages.map { m -> [String: Any] in
                ["role": m.role == "assistant" ? "model" : "user", "parts": [["text": m.content]]]
            }
            json = ["systemInstruction": ["parts": [["text": prompt.system]]], "contents": contents]
        }
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = try JSONSerialization.data(withJSONObject: json)
        req.timeoutInterval = 60
        return req
    }

    private func parse(_ kind: ProviderKind, _ data: Data) throws -> String {
        let obj = try JSONSerialization.jsonObject(with: data)
        guard let root = obj as? [String: Any] else { throw AIError.badResponse }
        switch kind {
        case .openai:
            if let choices = root["choices"] as? [[String: Any]],
               let msg = choices.first?["message"] as? [String: Any],
               let text = msg["content"] as? String { return text }
        case .anthropic:
            if let content = root["content"] as? [[String: Any]],
               let text = content.first(where: { ($0["type"] as? String) == "text" })?["text"] as? String { return text }
        case .gemini:
            if let cands = root["candidates"] as? [[String: Any]],
               let content = cands.first?["content"] as? [String: Any],
               let parts = content["parts"] as? [[String: Any]],
               let text = parts.first?["text"] as? String { return text }
        }
        throw AIError.badResponse
    }
}

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

    /// Returns the file URL of a freshly written temp HTML file, or nil on failure.
    /// Renders the markdown file into `tempFile`.
    /// Returns nil on success, or a human-readable error string on failure.
    static func render(markdownFile: URL, into tempFile: URL) -> String? {
        guard let tplURL = templateURL() else {
            return "Bundled template.html not found in app Resources."
        }
        guard var template = try? String(contentsOf: tplURL, encoding: .utf8) else {
            return "Could not read bundled template at \(tplURL.path)."
        }

        // Read the markdown. Try UTF-8, then fall back to a lossy decode so odd
        // encodings still display instead of failing outright.
        let mdText: String
        do {
            mdText = try String(contentsOf: markdownFile, encoding: .utf8)
        } catch {
            if let data = try? Data(contentsOf: markdownFile) {
                mdText = String(decoding: data, as: UTF8.self)
            } else {
                return "Could not read the markdown file.\n\(error.localizedDescription)"
            }
        }

        // Resources directory as a file:// URL string (assets live here).
        let resDir = resourcesDirURL().absoluteString.trimmingTrailingSlash()
        // Markdown file's directory for resolving relative images/links.
        let baseDir = markdownFile.deletingLastPathComponent().absoluteString

        // __MARKDOWN__ must be substituted LAST: it injects arbitrary document
        // text, and any token replaced after it would also match occurrences of
        // that token *inside* the document (e.g. a markdown file that mentions
        // "__TITLE__" literally would get corrupted).
        template = template.replacingOccurrences(of: "__RES__", with: resDir)
        template = template.replacingOccurrences(of: "__BASE__", with: baseDir)
        template = template.replacingOccurrences(of: "__TITLE__", with: htmlEscape(markdownFile.lastPathComponent))
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
            return t
        }
        // Fallback: minimal manual escaping.
        let escaped = s
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
            .replacingOccurrences(of: "\n", with: "\\n")
            .replacingOccurrences(of: "\r", with: "\\r")
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

final class ViewerWindowController: NSWindowController, WKNavigationDelegate, WKScriptMessageHandler, WKUIDelegate {
    private static var isFirstWindow = true
    private static var cascadePoint = NSPoint.zero

    let fileURL: URL
    private var webView: WKWebView!
    private let tempFile: URL
    private var watchTimer: Timer?
    private var lastModified: Date?

    /// True when the in-page editor has unsaved changes. While dirty, the
    /// external-change live-reload is suspended so it can't clobber edits.
    private(set) var isDirty = false
    /// Latest editor text pushed from the page, used for menu/close-triggered saves.
    private var lastText: String = ""

    init(fileURL: URL) {
        self.fileURL = fileURL.resolvingSymlinksInPath()
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
        window.title = self.fileURL.lastPathComponent
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
        webView.uiDelegate = self
        window.contentView?.addSubview(webView)

        renderAndLoad()
        startWatching()
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) not used") }

    private func renderAndLoad() {
        if let err = Renderer.render(markdownFile: fileURL, into: tempFile) {
            let safe = err
                .replacingOccurrences(of: "&", with: "&amp;")
                .replacingOccurrences(of: "<", with: "&lt;")
            let html = "<html><body style='font-family:-apple-system,sans-serif;padding:2rem;white-space:pre-wrap'>"
                + "<h3>MarkdownViewer could not render</h3>"
                + "<b>File:</b> \(fileURL.path)<br><br>\(safe)</body></html>"
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
        if let data = try? Data(contentsOf: fileURL) {
            lastText = String(decoding: data, as: UTF8.self)
        }
    }

    private func modificationDate() -> Date? {
        let attrs = try? FileManager.default.attributesOfItem(atPath: fileURL.path)
        return attrs?[.modificationDate] as? Date
    }

    // Live reload: poll the file's modification date once per second.
    private func startWatching() {
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
        case "ai":
            handleAI(body)
        default:
            break
        }
    }

    /// Builds an AI prompt from a page request and streams the result back via `__aiResult`.
    private func handleAI(_ body: [String: Any]) {
        guard let id = body["id"] as? Int, let mode = body["mode"] as? String else { return }
        let userPrompt = body["prompt"] as? String ?? ""
        let selection = body["selection"] as? String ?? ""
        let context = body["context"] as? String ?? ""

        let prompt: AIPrompt
        switch mode {
        case "improve":
            prompt = AIPrompt(
                system: "You are a markdown editing assistant. Revise the user's selected text per their instruction. Return ONLY the revised markdown — no preamble, no code fences, no commentary.",
                messages: [ChatMessage(role: "user", content: "Instruction: \(userPrompt)\n\nText to revise:\n\(selection)")])
        case "generate":
            prompt = AIPrompt(
                system: "You write Markdown. Return ONLY the requested markdown content — no commentary, no surrounding code fences.",
                messages: [ChatMessage(role: "user", content: userPrompt)])
        case "chat":
            var msgs: [ChatMessage] = []
            if let history = body["history"] as? [[String: Any]] {
                for h in history {
                    if let r = h["role"] as? String, let c = h["content"] as? String {
                        msgs.append(ChatMessage(role: r == "assistant" ? "assistant" : "user", content: c))
                    }
                }
            }
            msgs.append(ChatMessage(role: "user", content: userPrompt))
            prompt = AIPrompt(
                system: "You are a helpful assistant answering questions about the user's markdown document. Be concise. The current document is:\n\n\(context)",
                messages: msgs)
        default:
            return
        }

        AIService.shared.complete(prompt) { [weak self] result in
            guard let self = self else { return }
            switch result {
            case .success(let text):
                self.webView.evaluateJavaScript("window.__aiResult && window.__aiResult(\(id), \(Renderer.jsStringLiteral(text)))")
            case .failure(let err):
                let msg = err.localizedDescription
                self.webView.evaluateJavaScript("window.__aiError && window.__aiError(\(id), \(Renderer.jsStringLiteral(msg)))")
            }
        }
    }

    /// Runs arbitrary JS in the page (used by the AI menu items).
    func runJS(_ js: String) { webView.evaluateJavaScript(js) }

    // MARK: WKUIDelegate — lets the page use window.alert/confirm/prompt for AI dialogs.

    func webView(_ webView: WKWebView, runJavaScriptAlertPanelWithMessage message: String,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping () -> Void) {
        let alert = NSAlert(); alert.messageText = message; alert.addButton(withTitle: "OK")
        alert.runModal(); completionHandler()
    }
    func webView(_ webView: WKWebView, runJavaScriptConfirmPanelWithMessage message: String,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping (Bool) -> Void) {
        let alert = NSAlert(); alert.messageText = message
        alert.addButton(withTitle: "OK"); alert.addButton(withTitle: "Cancel")
        completionHandler(alert.runModal() == .alertFirstButtonReturn)
    }
    func webView(_ webView: WKWebView, runJavaScriptTextInputPanelWithPrompt prompt: String,
                 defaultText: String?, initiatedByFrame frame: WKFrameInfo,
                 completionHandler: @escaping (String?) -> Void) {
        let alert = NSAlert(); alert.messageText = prompt
        alert.addButton(withTitle: "OK"); alert.addButton(withTitle: "Cancel")
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 260, height: 24))
        field.stringValue = defaultText ?? ""
        alert.accessoryView = field
        alert.window.initialFirstResponder = field
        completionHandler(alert.runModal() == .alertFirstButtonReturn ? field.stringValue : nil)
    }

    /// Saves the document: pulls the live editor text from the page, then writes it.
    /// Falls back to the cached `lastText` when the page can't answer (e.g. the
    /// error page is showing). Captures self strongly so a save triggered while
    /// the window is closing still completes.
    func save(completion: (() -> Void)? = nil) {
        webView.evaluateJavaScript("window.__getText ? window.__getText() : null") { result, _ in
            if let text = result as? String { self.lastText = text }
            self.writeToDisk()
            completion?()
        }
    }

    /// Writes the latest editor text back to the markdown file on disk.
    private func writeToDisk() {
        do {
            try lastText.write(to: fileURL, atomically: true, encoding: .utf8)
            // Treat our own write as already-seen so the poll doesn't reload it.
            lastModified = modificationDate()
            isDirty = false
            webView.evaluateJavaScript("window.__onSaved && window.__onSaved()")
        } catch {
            let alert = NSAlert()
            alert.alertStyle = .warning
            alert.messageText = "Could not save the file."
            alert.informativeText = "\(fileURL.path)\n\n\(error.localizedDescription)"
            alert.addButton(withTitle: "OK")
            alert.runModal()
        }
    }

    /// Drives the page's Preview/Edit/Split toggle from the native View menu.
    func setEditorMode(_ mode: String) {
        let safe = mode.replacingOccurrences(of: "'", with: "")
        webView.evaluateJavaScript("window.__setMode && window.__setMode('\(safe)')")
    }

    /// Toggles soft-wrap in the editor from the native View menu.
    func toggleWrapInPage() {
        webView.evaluateJavaScript("window.__toggleWrap && window.__toggleWrap()")
    }

    /// Opens the in-page find / find-and-replace bar from the native Edit menu.
    func findInPage() { webView.evaluateJavaScript("window.__find && window.__find()") }
    func findReplaceInPage() { webView.evaluateJavaScript("window.__findReplace && window.__findReplace()") }

    /// Re-renders the document from disk, deliberately discarding in-page edits
    /// (the caller confirms with the user first when dirty).
    func reloadFromDisk() {
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

    // Open http/https links in the default browser; allow local navigation.
    func webView(_ webView: WKWebView,
                 decidePolicyFor navigationAction: WKNavigationAction,
                 decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        if let url = navigationAction.request.url,
           let scheme = url.scheme?.lowercased(),
           scheme == "http" || scheme == "https" || scheme == "mailto" {
            if navigationAction.navigationType == .linkActivated {
                NSWorkspace.shared.open(url)
                decisionHandler(.cancel)
                return
            }
        }
        decisionHandler(.allow)
    }
}

// MARK: - App delegate

final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    var controllers: [URL: ViewerWindowController] = [:]
    private var wrapMenuItem: NSMenuItem?
    private var wrapOn = true
    private var aboutWindow: NSWindow?
    private var aiSettings: AISettingsWindowController?

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

    func openFile(_ url: URL) {
        let resolved = url.resolvingSymlinksInPath()
        if let existing = controllers[resolved] {
            existing.window?.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }
        let controller = ViewerWindowController(fileURL: resolved)
        controller.window?.delegate = self
        controllers[resolved] = controller
        controller.showWindow(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    private enum UnsavedChoice { case save, discard, cancel }

    /// The standard Save / Don't Save / Cancel dialog for a dirty document.
    private func unsavedChangesChoice(for controller: ViewerWindowController) -> UnsavedChoice {
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "Do you want to save the changes you made to “\(controller.fileURL.lastPathComponent)”?"
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
        guard let controller = controllers.values.first(where: { $0.window === sender }),
              controller.isDirty else { return true }
        switch unsavedChangesChoice(for: controller) {
        case .save:    controller.save(); return true
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
        for controller in controllers.values where controller.isDirty {
            controller.window?.makeKeyAndOrderFront(nil)
            switch unsavedChangesChoice(for: controller) {
            case .save:    toSave.append(controller)
            case .discard: break
            case .cancel:  return .terminateCancel
            }
        }
        guard !toSave.isEmpty else { return .terminateNow }
        var remaining = toSave.count
        for controller in toSave {
            controller.save {
                remaining -= 1
                if remaining == 0 { NSApp.reply(toApplicationShouldTerminate: true) }
            }
        }
        return .terminateLater
    }

    func windowWillClose(_ notification: Notification) {
        guard let window = notification.object as? NSWindow else { return }
        for (key, controller) in controllers where controller.window === window {
            controller.stop()
            controllers.removeValue(forKey: key)
        }
    }

    /// The controller backing the current key window, if any.
    private func keyController() -> ViewerWindowController? {
        return controllers.values.first(where: { $0.window?.isKeyWindow == true })
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

    @objc func aiImprove(_ sender: Any?) { keyController()?.runJS("window.__aiImprove && window.__aiImprove()") }
    @objc func aiGenerate(_ sender: Any?) { keyController()?.runJS("window.__aiGenerate && window.__aiGenerate()") }
    @objc func aiChat(_ sender: Any?) { keyController()?.runJS("window.__toggleChat && window.__toggleChat()") }
    @objc func showAISettings(_ sender: Any?) {
        if aiSettings == nil { aiSettings = AISettingsWindowController() }
        aiSettings?.show()
    }

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
        panel.allowedFileTypes = ["md", "markdown", "mdown", "mkd", "mkdn", "mdwn", "markdn", "mdtxt", "text", "rmd", "qmd", "mdx", "mdc"]
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
            alert.messageText = "Reload “\(controller.fileURL.lastPathComponent)” from disk?"
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
        let archBtn = NSButton(title: "Architecture / Design", target: self, action: #selector(openArchitecture(_:)))
        archBtn.bezelStyle = .rounded

        let buttons = NSStackView(views: [changelogBtn, archBtn])
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
        openFile(dst)
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
        fileMenu.addItem(withTitle: "Open…", action: #selector(openDocument(_:)), keyEquivalent: "o")
        let openPathItem = fileMenu.addItem(withTitle: "Open Path…", action: #selector(openPath(_:)), keyEquivalent: "g")
        openPathItem.keyEquivalentModifierMask = [.command, .shift]
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

        // AI menu
        let aiMenuItem = NSMenuItem()
        mainMenu.addItem(aiMenuItem)
        let aiMenu = NSMenu(title: "AI")
        aiMenu.addItem(withTitle: "Improve Selection", action: #selector(aiImprove(_:)), keyEquivalent: "")
        aiMenu.addItem(withTitle: "Generate & Insert…", action: #selector(aiGenerate(_:)), keyEquivalent: "")
        aiMenu.addItem(withTitle: "Chat", action: #selector(aiChat(_:)), keyEquivalent: "")
        aiMenu.addItem(NSMenuItem.separator())
        aiMenu.addItem(withTitle: "Settings…", action: #selector(showAISettings(_:)), keyEquivalent: "")
        aiMenuItem.submenu = aiMenu

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

// MARK: - AI Settings window

/// Lets the user choose the active AI provider, edit its base URL + model, and store its
/// API key in the Keychain. The key is entered by the user in a secure field and never echoed.
final class AISettingsWindowController: NSObject {
    private let window: NSWindow
    private let popup = NSPopUpButton()
    private let baseField = NSTextField()
    private let modelField = NSTextField()
    private let keyField = NSSecureTextField()
    private let keyStatus = NSTextField(labelWithString: "")

    override init() {
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 540, height: 360),
                          styleMask: [.titled, .closable], backing: .buffered, defer: false)
        super.init()
        window.title = "AI Settings"
        window.isReleasedWhenClosed = false

        popup.addItems(withTitles: AI_PROVIDERS.map { $0.name })
        popup.target = self
        popup.action = #selector(providerChanged)
        popup.setContentHuggingPriority(.defaultLow, for: .horizontal)

        keyField.placeholderString = "Paste API key (stored in Keychain)"
        keyStatus.font = .systemFont(ofSize: 11)
        keyStatus.textColor = .secondaryLabelColor
        keyStatus.lineBreakMode = .byTruncatingTail

        let note = NSTextField(wrappingLabelWithString:
            "Keys are stored per-provider in the macOS Keychain and aren't shown again. Base URL and model are editable so you can target the exact endpoint and model your account allows.")
        note.font = .systemFont(ofSize: 11)
        note.textColor = .secondaryLabelColor

        let saveBtn = NSButton(title: "Save", target: self, action: #selector(save))
        saveBtn.bezelStyle = .rounded
        saveBtn.keyEquivalent = "\r"

        // A label of fixed width + a field that stretches to fill the row.
        func mkLabel(_ s: String) -> NSTextField {
            let l = NSTextField(labelWithString: s)
            l.alignment = .right
            l.translatesAutoresizingMaskIntoConstraints = false
            l.widthAnchor.constraint(equalToConstant: 78).isActive = true
            l.setContentHuggingPriority(.required, for: .horizontal)
            return l
        }
        func row(_ title: String, _ field: NSView) -> NSStackView {
            field.translatesAutoresizingMaskIntoConstraints = false
            field.setContentHuggingPriority(.defaultLow, for: .horizontal)
            let s = NSStackView(views: [mkLabel(title), field])
            s.orientation = .horizontal
            s.distribution = .fill
            s.spacing = 10
            return s
        }

        // Save button pushed to the right by a flexible spacer.
        let spacer = NSView()
        spacer.translatesAutoresizingMaskIntoConstraints = false
        spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        let btnRow = NSStackView(views: [spacer, saveBtn])
        btnRow.orientation = .horizontal
        btnRow.distribution = .fill

        let providerRow = row("Provider:", popup)
        let baseRow = row("Base URL:", baseField)
        let modelRow = row("Model:", modelField)
        let keyRow = row("API Key:", keyField)
        let statusRow = row("", keyStatus)

        let rows: [NSView] = [providerRow, baseRow, modelRow, keyRow, statusRow, note, btnRow]
        let stack = NSStackView(views: rows)
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 14
        stack.translatesAutoresizingMaskIntoConstraints = false

        let content = window.contentView!
        content.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.topAnchor.constraint(equalTo: content.topAnchor, constant: 26),
            stack.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 24),
            stack.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -24),
        ])
        // Make each row span the full width of the form.
        for v in rows { v.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true }

        // Select the active provider and load its fields.
        let active = AIService.shared.activeProvider()
        if let idx = AI_PROVIDERS.firstIndex(where: { $0.id == active.id }) { popup.selectItem(at: idx) }
        loadSelected()
    }

    func show() {
        window.center()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    private func selectedProvider() -> AIProvider { AI_PROVIDERS[max(0, popup.indexOfSelectedItem)] }

    private func loadSelected() {
        let p = selectedProvider()
        baseField.stringValue = AIService.shared.baseURL(for: p)
        modelField.stringValue = AIService.shared.model(for: p)
        keyField.stringValue = ""
        keyStatus.stringValue = (Keychain.get(p.id)?.isEmpty == false) ? "✓ A key is stored for \(p.name)." : "No key stored for \(p.name)."
    }

    @objc private func providerChanged() { loadSelected() }

    @objc private func save() {
        let p = selectedProvider()
        AIService.shared.setActive(p.id)
        AIService.shared.setBaseURL(baseField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines), for: p.id)
        AIService.shared.setModel(modelField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines), for: p.id)
        let key = keyField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        if !key.isEmpty {
            Keychain.set(key, account: p.id)
            keyField.stringValue = ""
        }
        keyStatus.stringValue = "Saved. " + ((Keychain.get(p.id)?.isEmpty == false) ? "✓ Key stored for \(p.name)." : "No key stored for \(p.name).")
    }
}

// MARK: - Entry point

let app = NSApplication.shared
app.setActivationPolicy(.regular)
let delegate = AppDelegate()
app.delegate = delegate
app.run()
