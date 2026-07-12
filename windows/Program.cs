// MarkdownViewer for Windows — a port of the macOS app (Sources/main.swift).
// One C# file drives a WebView2 per tab that renders the bundled HTML template;
// C# ↔ JS talk over the same small message bridge the Mac app uses.

using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MarkdownViewer;

// MARK: - AI providers, secret store, networking

enum ProviderKind { OpenAI, Anthropic, Gemini }

sealed class AIProvider
{
    public string Id;
    public string Name;
    public ProviderKind Kind;
    public string DefaultBaseUrl;
    public string DefaultModel;
}

static class Providers
{
    // Built-in providers. Base URL + model are user-editable in Settings, so unusual or new
    // endpoints (and the exact model ids each account has access to) can be corrected by the user.
    public static readonly AIProvider[] All =
    {
        new AIProvider { Id = "groq",      Name = "Groq",        Kind = ProviderKind.OpenAI,
                         DefaultBaseUrl = "https://api.groq.com/openai/v1",            DefaultModel = "llama-3.3-70b-versatile" },
        new AIProvider { Id = "nous",      Name = "Nous Portal", Kind = ProviderKind.OpenAI,
                         DefaultBaseUrl = "https://inference-api.nousresearch.com/v1", DefaultModel = "Hermes-3-Llama-3.1-70B" },
        new AIProvider { Id = "openai",    Name = "OpenAI",      Kind = ProviderKind.OpenAI,
                         DefaultBaseUrl = "https://api.openai.com/v1",                 DefaultModel = "gpt-4o" },
        new AIProvider { Id = "anthropic", Name = "Anthropic",   Kind = ProviderKind.Anthropic,
                         DefaultBaseUrl = "https://api.anthropic.com",                 DefaultModel = "claude-3-5-sonnet-latest" },
        new AIProvider { Id = "gemini",    Name = "Gemini",      Kind = ProviderKind.Gemini,
                         DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta", DefaultModel = "gemini-1.5-flash" },
    };
}

sealed class ChatMessage { public string Role; public string Content; }   // role: "user" | "assistant"
sealed class AIPrompt { public string System; public List<ChatMessage> Messages = new(); }

/// Simple persisted settings (the Windows analog of UserDefaults): a JSON
/// dictionary in %APPDATA%\MarkdownViewer\settings.json.
static class Settings
{
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MarkdownViewer");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static Dictionary<string, string> cache;

    static Dictionary<string, string> Load()
    {
        if (cache != null) return cache;
        try
        {
            cache = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath))
                : new Dictionary<string, string>();
        }
        catch { cache = new Dictionary<string, string>(); }
        return cache ??= new Dictionary<string, string>();
    }

    public static string Get(string key)
    {
        Load().TryGetValue(key, out var v);
        return v;
    }

    public static void Set(string key, string value)
    {
        Load()[key] = value;
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* settings are best-effort */ }
    }
}

/// Minimal secret store: one DPAPI-encrypted file per provider id (the Windows
/// analog of the macOS Keychain). Keys are encrypted for the current user.
static class SecretStore
{
    static string KeyPath(string account) => Path.Combine(Settings.Dir, "keys", account + ".bin");

    public static void Set(string value, string account)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(Settings.Dir, "keys"));
            var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(KeyPath(account), enc);
        }
        catch { /* best-effort */ }
    }

    public static string Get(string account)
    {
        try
        {
            var path = KeyPath(account);
            if (!File.Exists(path)) return null;
            var dec = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        catch { return null; }
    }
}

/// Builds and sends provider requests. Reads the active provider + overrides from Settings.
static class AIService
{
    static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

    public static AIProvider ActiveProvider()
    {
        var id = Settings.Get("ai.activeProvider") ?? "groq";
        return Providers.All.FirstOrDefault(p => p.Id == id) ?? Providers.All[0];
    }
    public static string BaseUrl(AIProvider p)
    {
        var v = Settings.Get("ai.baseURL." + p.Id);
        return string.IsNullOrEmpty(v) ? p.DefaultBaseUrl : v;
    }
    public static string Model(AIProvider p)
    {
        var v = Settings.Get("ai.model." + p.Id);
        return string.IsNullOrEmpty(v) ? p.DefaultModel : v;
    }
    public static void SetActive(string id) => Settings.Set("ai.activeProvider", id);
    public static void SetBaseUrl(string v, string id) => Settings.Set("ai.baseURL." + id, v);
    public static void SetModel(string v, string id) => Settings.Set("ai.model." + id, v);

    public static async Task<string> Complete(AIPrompt prompt)
    {
        var p = ActiveProvider();
        var key = SecretStore.Get(p.Id);
        if (string.IsNullOrEmpty(key))
            throw new Exception($"No API key set for {p.Name}. Open AI ▸ Settings… to add one.");

        using var req = BuildRequest(p, key, prompt);
        using var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if ((int)resp.StatusCode >= 400)
        {
            var msg = body.Length > 300 ? body.Substring(0, 300) : body;
            throw new Exception($"AI request failed (HTTP {(int)resp.StatusCode}). {msg}");
        }
        return Parse(p.Kind, body);
    }

    static HttpRequestMessage BuildRequest(AIProvider p, string key, AIPrompt prompt)
    {
        var baseUrl = BaseUrl(p).TrimEnd('/');
        var mdl = Model(p);
        HttpRequestMessage req;
        object json;

        switch (p.Kind)
        {
            case ProviderKind.OpenAI:
            {
                req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                var msgs = new List<object> { new { role = "system", content = prompt.System } };
                msgs.AddRange(prompt.Messages.Select(m => (object)new { role = m.Role, content = m.Content }));
                json = new { model = mdl, messages = msgs };
                break;
            }
            case ProviderKind.Anthropic:
            {
                req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/messages");
                req.Headers.TryAddWithoutValidation("x-api-key", key);
                req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                var msgs = prompt.Messages.Select(m => new { role = m.Role, content = m.Content });
                json = new { model = mdl, max_tokens = 2048, system = prompt.System, messages = msgs };
                break;
            }
            default: // Gemini
            {
                req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/models/{mdl}:generateContent?key={key}");
                var contents = prompt.Messages.Select(m => new
                {
                    role = m.Role == "assistant" ? "model" : "user",
                    parts = new[] { new { text = m.Content } }
                });
                json = new
                {
                    systemInstruction = new { parts = new[] { new { text = prompt.System } } },
                    contents
                };
                break;
            }
        }
        req.Content = new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json");
        return req;
    }

    static string Parse(ProviderKind kind, string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        switch (kind)
        {
            case ProviderKind.OpenAI:
                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var text))
                    return text.GetString();
                break;
            case ProviderKind.Anthropic:
                if (root.TryGetProperty("content", out var content))
                    foreach (var block in content.EnumerateArray())
                        if (block.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                            block.TryGetProperty("text", out var txt))
                            return txt.GetString();
                break;
            case ProviderKind.Gemini:
                if (root.TryGetProperty("candidates", out var cands) && cands.GetArrayLength() > 0 &&
                    cands[0].TryGetProperty("content", out var c) &&
                    c.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0 &&
                    parts[0].TryGetProperty("text", out var gt))
                    return gt.GetString();
                break;
        }
        throw new Exception("The AI service returned an unexpected response.");
    }
}

// MARK: - Markdown rendering

/// Builds the full HTML document for a markdown file by substituting tokens
/// in the bundled template. Asset CSS/JS are referenced via absolute file URLs
/// into the app's Resources directory; relative images in the markdown resolve
/// against the markdown file's own directory via <base href>.
static class Renderer
{
    public static string ResourcesDir =>
        Path.Combine(AppContext.BaseDirectory, "Resources");

    /// Renders the markdown file into `tempFile`.
    /// Returns null on success, or a human-readable error string on failure.
    public static string Render(string markdownFile, string tempFile)
    {
        var tplPath = Path.Combine(ResourcesDir, "template.html");
        if (!File.Exists(tplPath))
            return "Bundled template.html not found in app Resources.";
        string template;
        try { template = File.ReadAllText(tplPath, Encoding.UTF8); }
        catch (Exception e) { return $"Could not read bundled template at {tplPath}.\n{e.Message}"; }

        string mdText;
        try { mdText = File.ReadAllText(markdownFile); }   // UTF-8 with BOM detection, lossy on bad bytes
        catch (Exception e) { return $"Could not read the markdown file.\n{e.Message}"; }

        // Resources directory as a file:// URL string (assets live here).
        var resDir = new Uri(ResourcesDir).AbsoluteUri.TrimEnd('/');
        // Markdown file's directory for resolving relative images/links (needs trailing slash).
        var dir = Path.GetDirectoryName(Path.GetFullPath(markdownFile)) ?? "";
        var baseDir = new Uri(dir + Path.DirectorySeparatorChar).AbsoluteUri;

        template = template.Replace("__RES__", resDir);
        template = template.Replace("__BASE__", baseDir);
        template = template.Replace("__MARKDOWN__", JsString(mdText));
        template = template.Replace("__TITLE__", HtmlEscape(Path.GetFileName(markdownFile)));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tempFile));
            File.WriteAllText(tempFile, template, new UTF8Encoding(false));
            return null;
        }
        catch (Exception e)
        {
            return $"Could not write the render file at {tempFile}.\n{e.Message}";
        }
    }

    /// A JS string literal safe to embed inside a <script> block: the default
    /// System.Text.Json encoder escapes <, >, & so "</script>" can't break out.
    public static string JsString(string s) => JsonSerializer.Serialize(s);

    public static string HtmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

// MARK: - Viewer tab

/// One open markdown file: a TabPage hosting a WebView2, with live reload,
/// dirty tracking, and the JS message bridge (the analog of the Mac app's
/// ViewerWindowController — Windows uses tabs in one window instead of
/// macOS native window tabbing).
sealed class ViewerTab : TabPage
{
    public readonly string FilePath;
    readonly MainForm host;
    readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill };
    readonly string tempFile;
    readonly System.Windows.Forms.Timer watchTimer = new System.Windows.Forms.Timer { Interval = 1000 };
    DateTime? lastModified;

    /// True when the in-page editor has unsaved changes. While dirty, the
    /// external-change live-reload is suspended so it can't clobber edits.
    public bool IsDirty { get; private set; }
    /// Latest editor text pushed from the page, used for menu/close-triggered saves.
    string lastText = "";

    public ViewerTab(MainForm host, string filePath)
    {
        this.host = host;
        FilePath = Path.GetFullPath(filePath);
        lastText = "";
        Text = Path.GetFileName(FilePath);
        ToolTipText = FilePath;
        var tmpDir = Path.Combine(Path.GetTempPath(), "MarkdownViewer");
        tempFile = Path.Combine(tmpDir, $"view-{Guid.NewGuid()}.html");
        Controls.Add(web);
    }

    public async Task Init()
    {
        var env = await App.GetWebViewEnvironment();
        await web.EnsureCoreWebView2Async(env);

        var s = web.CoreWebView2.Settings;
        // Browser accelerator keys stay enabled: disabling them also stops keys
        // like Escape from ever reaching the page. The template preventDefault()s
        // the ones we don't want (Ctrl+F/P, F3, F5, F7, F11) instead.
        s.IsZoomControlEnabled = false;               // template implements its own zoom
        s.AreDevToolsEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;

        web.CoreWebView2.WebMessageReceived += OnWebMessage;
        // Escape is treated as a WinForms dialog key and never reaches the page;
        // the wrapper re-raises it on the host control, so forward it by hand.
        web.PreviewKeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) e.IsInputKey = true; };
        web.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { e.Handled = true; RunJS("window.__escape && window.__escape()"); }
        };
        // Give the page keyboard focus once it has loaded, so typing works immediately.
        web.CoreWebView2.NavigationCompleted += (_, __) => { if (host.ActiveTab == this) FocusWeb(); };
        // Open http/https/mailto links in the default browser; allow local navigation.
        web.CoreWebView2.NavigationStarting += (_, e) =>
        {
            var uri = e.Uri ?? "";
            if (uri.StartsWith("http://") || uri.StartsWith("https://") || uri.StartsWith("mailto:"))
            {
                e.Cancel = true;
                OpenExternal(uri);
            }
        };
        web.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            var uri = e.Uri ?? "";
            if (uri.StartsWith("http://") || uri.StartsWith("https://") || uri.StartsWith("mailto:"))
                OpenExternal(uri);
        };

        RenderAndLoad();
        watchTimer.Tick += WatchTick;
        watchTimer.Start();
    }

    static void OpenExternal(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    public void RenderAndLoad()
    {
        var err = Renderer.Render(FilePath, tempFile);
        if (err != null)
        {
            var safe = Renderer.HtmlEscape(err);
            var html = "<html><body style='font-family:Segoe UI,sans-serif;padding:2rem;white-space:pre-wrap'>"
                     + "<h3>MarkdownViewer could not render</h3>"
                     + $"<b>File:</b> {Renderer.HtmlEscape(FilePath)}<br><br>{safe}</body></html>";
            web.CoreWebView2?.NavigateToString(html);
            return;
        }
        web.CoreWebView2?.Navigate(new Uri(tempFile).AbsoluteUri);
        lastModified = ModificationDate();
    }

    DateTime? ModificationDate()
    {
        try { return File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : null; }
        catch { return null; }
    }

    // Live reload: poll the file's modification date once per second.
    // Suspended while there are unsaved edits so we never clobber them.
    void WatchTick(object sender, EventArgs e)
    {
        if (IsDirty) return;
        var current = ModificationDate();
        if (current.HasValue && current != lastModified)
        {
            lastModified = current;
            RenderAndLoad();
        }
    }

    // MARK: Editor bridge — receives messages posted from the page.

    void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(e.WebMessageAsJson); }
        catch { return; }
        using (doc)
        {
            var body = doc.RootElement;
            if (body.ValueKind != JsonValueKind.Object ||
                !body.TryGetProperty("action", out var actionEl)) return;

            switch (actionEl.GetString())
            {
                case "dirty":
                    SetDirty(body.TryGetProperty("dirty", out var d) && d.ValueKind == JsonValueKind.True);
                    break;
                case "change":
                    if (body.TryGetProperty("text", out var t1)) lastText = t1.GetString() ?? "";
                    break;
                case "save":
                    if (body.TryGetProperty("text", out var t2)) lastText = t2.GetString() ?? "";
                    Save();
                    break;
                case "setWrap":
                    host.UpdateWrapState(body.TryGetProperty("wrap", out var w) && w.ValueKind == JsonValueKind.True);
                    break;
                case "ai":
                    HandleAI(body);
                    break;
                case "open":
                    host.ShowOpenDialog();
                    break;
                case "openPath":
                    host.ShowOpenPathDialog();
                    break;
                case "closeTab":
                    host.CloseTab(this);
                    break;
                case "reload":
                    ReloadFromDisk();
                    break;
            }
        }
    }

    void SetDirty(bool dirty)
    {
        IsDirty = dirty;
        Text = (dirty ? "● " : "") + Path.GetFileName(FilePath);
    }

    /// Builds an AI prompt from a page request and sends the result back via `__aiResult`.
    async void HandleAI(JsonElement body)
    {
        if (!body.TryGetProperty("id", out var idEl) || !body.TryGetProperty("mode", out var modeEl)) return;
        var id = idEl.GetInt32();
        var mode = modeEl.GetString();
        string Str(string name) => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
        var userPrompt = Str("prompt");
        var selection = Str("selection");
        var context = Str("context");

        AIPrompt prompt;
        switch (mode)
        {
            case "improve":
                prompt = new AIPrompt
                {
                    System = "You are a markdown editing assistant. Revise the user's selected text per their instruction. Return ONLY the revised markdown — no preamble, no code fences, no commentary.",
                    Messages = { new ChatMessage { Role = "user", Content = $"Instruction: {userPrompt}\n\nText to revise:\n{selection}" } }
                };
                break;
            case "generate":
                prompt = new AIPrompt
                {
                    System = "You write Markdown. Return ONLY the requested markdown content — no commentary, no surrounding code fences.",
                    Messages = { new ChatMessage { Role = "user", Content = userPrompt } }
                };
                break;
            case "chat":
                var msgs = new List<ChatMessage>();
                if (body.TryGetProperty("history", out var history) && history.ValueKind == JsonValueKind.Array)
                    foreach (var h in history.EnumerateArray())
                        if (h.TryGetProperty("role", out var r) && h.TryGetProperty("content", out var c))
                            msgs.Add(new ChatMessage
                            {
                                Role = r.GetString() == "assistant" ? "assistant" : "user",
                                Content = c.GetString() ?? ""
                            });
                msgs.Add(new ChatMessage { Role = "user", Content = userPrompt });
                prompt = new AIPrompt
                {
                    System = "You are a helpful assistant answering questions about the user's markdown document. Be concise. The current document is:\n\n" + context,
                    Messages = msgs
                };
                break;
            default:
                return;
        }

        try
        {
            var text = await AIService.Complete(prompt);
            RunJS($"window.__aiResult && window.__aiResult({id}, {Renderer.JsString(text)})");
        }
        catch (Exception ex)
        {
            RunJS($"window.__aiError && window.__aiError({id}, {Renderer.JsString(ex.Message)})");
        }
    }

    /// Runs arbitrary JS in the page (used by the AI/View menu items).
    public void RunJS(string js)
    {
        try { web.CoreWebView2?.ExecuteScriptAsync(js); } catch { }
    }

    /// Moves real keyboard focus into the WebView so in-page focus() calls work.
    public void FocusWeb()
    {
        try { web.Focus(); } catch { }
    }

    /// Writes the latest editor text back to the markdown file on disk.
    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, lastText, new UTF8Encoding(false));
            // Treat our own write as already-seen so the poll doesn't reload it.
            lastModified = ModificationDate();
            SetDirty(false);
            RunJS("window.__onSaved && window.__onSaved()");
        }
        catch (Exception e)
        {
            MessageBox.Show($"{FilePath}\n\n{e.Message}", "Could not save the file.",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// Re-renders from disk (View ▸ Reload). Guards a dirty buffer first.
    public void ReloadFromDisk()
    {
        if (IsDirty)
        {
            var r = MessageBox.Show(
                $"“{Path.GetFileName(FilePath)}” has unsaved changes. Reload from disk and discard them?",
                "Reload", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;
            SetDirty(false);
        }
        RenderAndLoad();
    }

    public void SetEditorMode(string mode)
    {
        var safe = mode.Replace("'", "");
        FocusWeb();
        RunJS($"window.__setMode && window.__setMode('{safe}')");
    }

    public void ToggleWrapInPage() => RunJS("window.__toggleWrap && window.__toggleWrap()");
    public void FindInPage() { FocusWeb(); RunJS("window.__find && window.__find()"); }
    public void FindReplaceInPage() { FocusWeb(); RunJS("window.__findReplace && window.__findReplace()"); }

    public void Stop()
    {
        watchTimer.Stop();
        watchTimer.Dispose();
        try { File.Delete(tempFile); } catch { }
    }
}

// MARK: - Main window (menus + tabs)

sealed class MainForm : Form
{
    readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
    readonly Label emptyHint = new Label
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        Text = "Open a Markdown file — Ctrl+O\n(or drag one onto this window)",
        ForeColor = Color.Gray,
    };
    ToolStripMenuItem wrapMenuItem;
    AISettingsForm aiSettings;

    public MainForm(string[] args)
    {
        Text = "MarkdownViewer";
        MinimumSize = new Size(420, 320);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(900, 760);
        AllowDrop = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // Dock order matters: the top-docked menu must be added LAST so the
        // fill-docked controls are laid out into the space below it (otherwise
        // the tab headers hide underneath the menu strip).
        Controls.Add(emptyHint);
        Controls.Add(tabs);
        Controls.Add(BuildMenu());
        tabs.BringToFront();

        tabs.SelectedIndexChanged += (_, __) => { UpdateTitle(); ActiveTab?.FocusWeb(); };
        // Middle-click a tab header to close it.
        tabs.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Middle) return;
            for (int i = 0; i < tabs.TabCount; i++)
                if (tabs.GetTabRect(i).Contains(e.Location)) { CloseTab((ViewerTab)tabs.TabPages[i]); break; }
        };

        DragEnter += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            foreach (var f in (string[])e.Data.GetData(DataFormats.FileDrop))
                if (File.Exists(f)) OpenFile(f);
        };

        Shown += async (_, __) =>
        {
            var opened = false;
            foreach (var a in args)
                if (File.Exists(a)) { await OpenFileAsync(a); opened = true; }
            if (!opened) ShowOpenDialog();
        };

        FormClosing += OnFormClosing;
        UpdateEmptyState();
        StartPipeServer();
    }

    /// Listens for file paths sent by later app instances (see App.Main) so
    /// "Open with" from Explorer lands as a tab in this window.
    void StartPipeServer()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(App.PipeName, PipeDirection.In);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    var paths = new List<string>();
                    string line;
                    while ((line = reader.ReadLine()) != null)
                        if (line.Length > 0) paths.Add(line);
                    BeginInvoke(() =>
                    {
                        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                        Activate();
                        foreach (var p in paths)
                            if (File.Exists(p)) OpenFile(p);
                    });
                }
                catch { /* pipe hiccups shouldn't kill the listener */ }
            }
        }) { IsBackground = true };
        thread.Start();
    }

    void UpdateEmptyState()
    {
        // The empty TabControl would paint over the hint, so swap visibility.
        tabs.Visible = tabs.TabCount > 0;
        emptyHint.Visible = tabs.TabCount == 0;
    }

    void UpdateTitle()
    {
        Text = tabs.SelectedTab is ViewerTab t
            ? Path.GetFileName(t.FilePath) + " — MarkdownViewer"
            : "MarkdownViewer";
    }

    public ViewerTab ActiveTab => tabs.SelectedTab as ViewerTab;
    ViewerTab Current => ActiveTab;

    public async void OpenFile(string path) => await OpenFileAsync(path);

    public async Task OpenFileAsync(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (ViewerTab t in tabs.TabPages)
            if (string.Equals(t.FilePath, full, StringComparison.OrdinalIgnoreCase))
            {
                tabs.SelectedTab = t;
                return;
            }
        var tab = new ViewerTab(this, full);
        tabs.TabPages.Add(tab);
        tabs.SelectedTab = tab;
        UpdateEmptyState();
        UpdateTitle();
        await tab.Init();
    }

    /// Warn before closing a tab that has unsaved edits. Returns true if closed.
    public bool CloseTab(ViewerTab tab)
    {
        if (tab == null) return false;
        if (tab.IsDirty)
        {
            var r = MessageBox.Show(
                $"Do you want to save the changes you made to “{Path.GetFileName(tab.FilePath)}”?\n\nYour changes will be lost if you don't save them.",
                "Unsaved Changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) tab.Save();
        }
        tab.Stop();
        tabs.TabPages.Remove(tab);
        tab.Dispose();
        UpdateEmptyState();
        UpdateTitle();
        return true;
    }

    void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        foreach (var tab in tabs.TabPages.Cast<ViewerTab>().ToArray())
        {
            if (tab.IsDirty)
            {
                tabs.SelectedTab = tab;
                var r = MessageBox.Show(
                    $"Do you want to save the changes you made to “{Path.GetFileName(tab.FilePath)}”?\n\nYour changes will be lost if you don't save them.",
                    "Unsaved Changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (r == DialogResult.Cancel) { e.Cancel = true; return; }
                if (r == DialogResult.Yes) tab.Save();
            }
            tab.Stop();
        }
    }

    /// Called from the page (via the `setWrap` bridge action) to keep the menu checkmark synced.
    public void UpdateWrapState(bool on)
    {
        if (wrapMenuItem != null) wrapMenuItem.Checked = on;
    }

    public void ShowOpenDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Markdown files|*.md;*.markdown;*.mdown;*.mkd;*.mkdn;*.mdwn;*.markdn;*.mdtxt;*.text;*.rmd;*.qmd;*.mdx;*.mdc|All files|*.*",
            Title = "Open Markdown File",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            foreach (var f in dlg.FileNames) OpenFile(f);
    }

    /// Open by typing or pasting an absolute path, e.g. C:\Users\James\USER.md
    public void ShowOpenPathDialog()
    {
        using var form = new Form
        {
            Text = "Open Path",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(500, 110),
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
        };
        var label = new Label { Text = @"Paste the full path to a Markdown file. Example: C:\Users\James\USER.md", Location = new Point(12, 10), AutoSize = true };
        var field = new TextBox { Location = new Point(12, 36), Width = 476 };
        // Pre-fill from the clipboard when it already looks like a path.
        try
        {
            var clip = (Clipboard.GetText() ?? "").Trim();
            if (clip.Length > 2 && (clip[1] == ':' || clip.StartsWith("\\\\") || clip.StartsWith("file://")))
                field.Text = clip;
        }
        catch { }
        var ok = new Button { Text = "Open", DialogResult = DialogResult.OK, Location = new Point(332, 70), Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(413, 70), Width = 75 };
        form.Controls.AddRange(new Control[] { label, field, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        if (form.ShowDialog(this) == DialogResult.OK)
            OpenFromString(field.Text);
    }

    /// Normalize a pasted path string and open it if it points to a real file.
    void OpenFromString(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return;
        // Strip surrounding quotes (paths copied from a terminal/Explorer are often quoted).
        if ((s.StartsWith("\"") && s.EndsWith("\"")) || (s.StartsWith("'") && s.EndsWith("'")))
            s = s.Substring(1, s.Length - 2);
        if (s.StartsWith("file://"))
        {
            try { s = new Uri(s).LocalPath; } catch { }
        }
        if (File.Exists(s)) OpenFile(s);
        else MessageBox.Show(s, "File not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // MARK: About + bundled docs

    void ShowAbout()
    {
        using var about = new AboutForm(this);
        about.ShowDialog(this);
    }

    /// Opens a bundled markdown doc by copying it to a throwaway temp file and viewing that
    /// (so any edits never touch the file inside the app folder).
    public void OpenBundledDoc(string name)
    {
        var src = Path.Combine(Renderer.ResourcesDir, name);
        if (!File.Exists(src)) { System.Media.SystemSounds.Beep.Play(); return; }
        var tmpDir = Path.Combine(Path.GetTempPath(), "MarkdownViewer");
        Directory.CreateDirectory(tmpDir);
        var dst = Path.Combine(tmpDir, name);
        try { File.Copy(src, dst, true); }
        catch { System.Media.SystemSounds.Beep.Play(); return; }
        OpenFile(dst);
    }

    // MARK: Menu

    MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();

        // File
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(Item("Open…", Keys.Control | Keys.O, (_, __) => ShowOpenDialog()));
        file.DropDownItems.Add(Item("Open Path…", Keys.Control | Keys.Shift | Keys.G, (_, __) => ShowOpenPathDialog()));
        file.DropDownItems.Add(Item("Save", Keys.Control | Keys.S, (_, __) => Current?.Save()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("Close Tab", Keys.Control | Keys.W, (_, __) => CloseTab(Current)));
        file.DropDownItems.Add(Item("Exit", Keys.Alt | Keys.F4, (_, __) => Close()));

        // Edit
        var edit = new ToolStripMenuItem("&Edit");
        edit.DropDownItems.Add(Item("Find…", Keys.Control | Keys.F, (_, __) => Current?.FindInPage()));
        edit.DropDownItems.Add(Item("Find and Replace…", Keys.Control | Keys.H, (_, __) => Current?.FindReplaceInPage()));

        // View
        var view = new ToolStripMenuItem("&View");
        view.DropDownItems.Add(Item("Preview", Keys.Control | Keys.D1, (_, __) => Current?.SetEditorMode("preview")));
        view.DropDownItems.Add(Item("Edit", Keys.Control | Keys.D2, (_, __) => Current?.SetEditorMode("edit")));
        view.DropDownItems.Add(Item("Split", Keys.Control | Keys.D3, (_, __) => Current?.SetEditorMode("split")));
        view.DropDownItems.Add(new ToolStripSeparator());
        wrapMenuItem = Item("Wrap Lines", Keys.None, (_, __) => Current?.ToggleWrapInPage());
        wrapMenuItem.Checked = true;
        view.DropDownItems.Add(wrapMenuItem);
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(Item("Reload", Keys.Control | Keys.R, (_, __) => Current?.ReloadFromDisk()));

        // AI
        var ai = new ToolStripMenuItem("&AI");
        ai.DropDownItems.Add(Item("Improve Selection", Keys.None, (_, __) => Current?.RunJS("window.__aiImprove && window.__aiImprove()")));
        ai.DropDownItems.Add(Item("Generate && Insert…", Keys.None, (_, __) => Current?.RunJS("window.__aiGenerate && window.__aiGenerate()")));
        ai.DropDownItems.Add(Item("Chat", Keys.None, (_, __) => Current?.RunJS("window.__toggleChat && window.__toggleChat()")));
        ai.DropDownItems.Add(new ToolStripSeparator());
        ai.DropDownItems.Add(Item("Settings…", Keys.None, (_, __) =>
        {
            aiSettings ??= new AISettingsForm();
            aiSettings.ShowFront(this);
        }));

        // Help
        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(Item("About MarkdownViewer", Keys.None, (_, __) => ShowAbout()));

        menu.Items.AddRange(new ToolStripItem[] { file, edit, view, ai, help });
        MainMenuStrip = menu;
        return menu;
    }

    static ToolStripMenuItem Item(string text, Keys keys, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(text, null, onClick);
        if (keys != Keys.None) item.ShortcutKeys = keys;
        return item;
    }
}

// MARK: - AI Settings window

/// Lets the user choose the active AI provider, edit its base URL + model, and store its
/// API key encrypted with DPAPI. The key is entered in a password field and never echoed.
sealed class AISettingsForm : Form
{
    readonly ComboBox popup = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly TextBox baseField = new TextBox();
    readonly TextBox modelField = new TextBox();
    readonly TextBox keyField = new TextBox { UseSystemPasswordChar = true };
    readonly Label keyStatus = new Label { AutoSize = true, ForeColor = Color.Gray };

    public AISettingsForm()
    {
        Text = "AI Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(540, 300);

        // Closing hides so stored state (selection) survives, like the Mac panel.
        FormClosing += (_, e) => { e.Cancel = true; Hide(); };

        int labelX = 16, fieldX = 104, fieldW = 416, y = 20, rowH = 34;
        Label MkLabel(string s) => new Label { Text = s, Location = new Point(labelX, y + 4), Width = 84, TextAlign = ContentAlignment.MiddleRight };
        void Row(string title, Control field)
        {
            Controls.Add(MkLabel(title));
            field.Location = new Point(fieldX, y);
            field.Width = fieldW;
            Controls.Add(field);
            y += rowH;
        }

        popup.Items.AddRange(Providers.All.Select(p => (object)p.Name).ToArray());
        popup.SelectedIndexChanged += (_, __) => LoadSelected();

        Row("Provider:", popup);
        Row("Base URL:", baseField);
        Row("Model:", modelField);
        Row("API Key:", keyField);
        keyField.PlaceholderText = "Paste API key (stored encrypted per user)";

        keyStatus.Location = new Point(fieldX, y); y += 26;
        Controls.Add(keyStatus);

        var note = new Label
        {
            Text = "Keys are stored per-provider, encrypted with Windows DPAPI for your user account, and aren't shown again. "
                 + "Base URL and model are editable so you can target the exact endpoint and model your account allows.",
            Location = new Point(fieldX, y),
            Size = new Size(fieldW, 48),
            ForeColor = Color.Gray,
        };
        Controls.Add(note);
        y += 56;

        var saveBtn = new Button { Text = "Save", Location = new Point(fieldX + fieldW - 85, y), Width = 85 };
        saveBtn.Click += (_, __) => SaveClicked();
        AcceptButton = saveBtn;
        Controls.Add(saveBtn);

        // Select the active provider and load its fields.
        var active = AIService.ActiveProvider();
        popup.SelectedIndex = Math.Max(0, Array.FindIndex(Providers.All, p => p.Id == active.Id));
        LoadSelected();
    }

    public void ShowFront(Form owner)
    {
        if (!Visible) Show(owner);
        Activate();
    }

    AIProvider Selected => Providers.All[Math.Max(0, popup.SelectedIndex)];

    void LoadSelected()
    {
        var p = Selected;
        baseField.Text = AIService.BaseUrl(p);
        modelField.Text = AIService.Model(p);
        keyField.Text = "";
        keyStatus.Text = !string.IsNullOrEmpty(SecretStore.Get(p.Id))
            ? $"✓ A key is stored for {p.Name}." : $"No key stored for {p.Name}.";
    }

    void SaveClicked()
    {
        var p = Selected;
        AIService.SetActive(p.Id);
        AIService.SetBaseUrl(baseField.Text.Trim(), p.Id);
        AIService.SetModel(modelField.Text.Trim(), p.Id);
        var key = keyField.Text.Trim();
        if (key.Length > 0)
        {
            SecretStore.Set(key, p.Id);
            keyField.Text = "";
        }
        keyStatus.Text = "Saved. " + (!string.IsNullOrEmpty(SecretStore.Get(p.Id))
            ? $"✓ Key stored for {p.Name}." : $"No key stored for {p.Name}.");
    }
}

// MARK: - About window

sealed class AboutForm : Form
{
    public AboutForm(MainForm host)
    {
        Text = "About MarkdownViewer";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(380, 300);

        var version = Application.ProductVersion.Split('+')[0];

        var icon = new PictureBox
        {
            Size = new Size(84, 84),
            Location = new Point((380 - 84) / 2, 24),
            SizeMode = PictureBoxSizeMode.Zoom,
        };
        try { icon.Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath)?.ToBitmap(); } catch { }

        var name = new Label
        {
            Text = "MarkdownViewer",
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(0, 116), Size = new Size(380, 28),
        };
        var ver = new Label
        {
            Text = "Version " + version,
            ForeColor = Color.Gray,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(0, 146), Size = new Size(380, 20),
        };
        var author = new Label
        {
            Text = "by David Godibadze",
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(0, 168), Size = new Size(380, 20),
        };

        var changelog = new Button { Text = "Changelog", Location = new Point(66, 216), Width = 110 };
        changelog.Click += (_, __) => { host.OpenBundledDoc("CHANGELOG.md"); Close(); };
        var arch = new Button { Text = "Architecture / Design", Location = new Point(186, 216), Width = 130 };
        arch.Click += (_, __) => { host.OpenBundledDoc("ARCHITECTURE.md"); Close(); };

        Controls.AddRange(new Control[] { icon, name, ver, author, changelog, arch });
    }
}

// MARK: - Entry point

static class App
{
    static Task<CoreWebView2Environment> envTask;

    /// Shared WebView2 environment (one browser process for all tabs), with the
    /// profile stored under %LOCALAPPDATA%\MarkdownViewer.
    public static Task<CoreWebView2Environment> GetWebViewEnvironment()
    {
        return envTask ??= CoreWebView2Environment.CreateAsync(null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "MarkdownViewer", "WebView2"));
    }

    public const string PipeName = "MarkdownViewer-open";

    [STAThread]
    static void Main(string[] args)
    {
        // Single instance: later launches forward their file paths to the
        // first instance over a named pipe and exit (the Windows analog of
        // macOS routing opened files to the running app).
        using var mutex = new Mutex(true, "Local\\MarkdownViewerSingleton", out var isFirst);
        if (!isFirst)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(2000);
                using var writer = new StreamWriter(client);
                foreach (var a in args)
                    if (File.Exists(a)) writer.WriteLine(Path.GetFullPath(a));
                writer.Flush();
                return;
            }
            catch { /* first instance unreachable — fall through and open normally */ }
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(args));
    }
}
