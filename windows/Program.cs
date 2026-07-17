// MarkdownViewer for Windows — a port of the macOS app (Sources/main.swift).
// One C# file drives a WebView2 per tab that renders the bundled HTML template;
// C# ↔ JS talk over the same small message bridge the Mac app uses.
// Keep behavior in sync with main.swift — both implement the pipeline described
// in Resources/DESIGN.md (rendering, save/dirty invariants, live reload).

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MarkdownViewer;

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

// MARK: - Markdown rendering

/// Builds the full HTML document for a markdown file by substituting tokens
/// in the bundled template. Asset CSS/JS are referenced via absolute file URLs
/// into the app's Resources directory; relative images in the markdown resolve
/// against the markdown file's own directory via <base href>.
static class Renderer
{
    public static string ResourcesDir =>
        Path.Combine(AppContext.BaseDirectory, "Resources");

    /// Renders the markdown file (or an empty untitled document when
    /// `markdownFile` is null) into `tempFile`.
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
        if (markdownFile != null)
        {
            try { mdText = File.ReadAllText(markdownFile); }   // UTF-8 with BOM detection, lossy on bad bytes
            catch (Exception e) { return $"Could not read the markdown file.\n{e.Message}"; }
        }
        else
        {
            mdText = "";
        }

        // Resources directory as a file:// URL string (assets live here).
        var resDir = new Uri(ResourcesDir).AbsoluteUri.TrimEnd('/');
        // Markdown file's directory for resolving relative images/links (needs trailing slash).
        // Untitled documents have no directory yet — use the user profile as a placeholder.
        var dir = markdownFile != null
            ? Path.GetDirectoryName(Path.GetFullPath(markdownFile)) ?? ""
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var baseDir = new Uri(dir + Path.DirectorySeparatorChar).AbsoluteUri;
        var title = markdownFile != null ? Path.GetFileName(markdownFile) : "Untitled";

        // __MARKDOWN__ must be substituted LAST: it injects arbitrary document
        // text, and any token replaced after it would also match occurrences of
        // that token *inside* the document (e.g. a markdown file that mentions
        // "__TITLE__" literally would get corrupted).
        template = template.Replace("__RES__", resDir);
        template = template.Replace("__BASE__", baseDir);
        template = template.Replace("__TITLE__", HtmlEscape(title));
        template = template.Replace("__MARKDOWN__", JsString(mdText));

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

/// One open document: a TabPage hosting a WebView2, with live reload,
/// dirty tracking, and the JS message bridge (the analog of the Mac app's
/// ViewerWindowController — Windows uses tabs in one window instead of
/// macOS native window tabbing). FilePath == null means a new, never-saved
/// "Untitled" document; the first save asks where to put it.
sealed class ViewerTab : TabPage
{
    public string FilePath { get; private set; }
    readonly MainForm host;
    readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill };
    readonly string tempFile;
    readonly System.Windows.Forms.Timer watchTimer = new System.Windows.Forms.Timer { Interval = 1000 };
    DateTime? lastModified;

    /// True when the in-page editor has unsaved changes. While dirty, the
    /// external-change live-reload is suspended so it can't clobber edits.
    public bool IsDirty { get; private set; }
    /// Latest editor text pushed from the page — the fallback for saves when
    /// the page can't answer. Seeded from disk on every load/reload so a save
    /// before any edit can never write an empty or stale copy.
    string lastText = "";
    /// Mode to force once the page finishes loading (e.g. "split" for new
    /// documents). Calling __setMode before the page loads is a silent no-op,
    /// so this is applied from NavigationCompleted instead.
    public string StartMode;
    /// True when the file on disk used CRLF line endings. The textarea's
    /// `value` is LF-normalized by the HTML spec, so without re-applying CRLF
    /// on save a one-character edit would silently rewrite every line ending.
    bool usesCrLf;

    public string DisplayName => FilePath != null ? Path.GetFileName(FilePath) : "Untitled";

    public ViewerTab(MainForm host, string filePath)
    {
        this.host = host;
        FilePath = filePath != null ? Path.GetFullPath(filePath) : null;
        Text = DisplayName;
        ToolTipText = FilePath ?? "Unsaved document";
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
        // Apply a deferred start mode, then give the page keyboard focus.
        web.CoreWebView2.NavigationCompleted += (_, __) =>
        {
            if (StartMode != null) { var m = StartMode; StartMode = null; SetEditorMode(m, persist: false); }
            if (host.ActiveTab == this) FocusWeb();
        };
        // Open user-clicked http/https/mailto links in the default browser;
        // cancel every other remote navigation (scripted, <meta refresh> from
        // raw HTML in a document) WITHOUT opening the browser — a malicious
        // markdown file must not be able to auto-launch pages.
        web.CoreWebView2.NavigationStarting += (_, e) =>
        {
            var uri = e.Uri ?? "";
            if (uri.StartsWith("http://") || uri.StartsWith("https://") || uri.StartsWith("mailto:"))
            {
                e.Cancel = true;
                if (e.IsUserInitiated) OpenExternal(uri);
            }
        };
        web.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            var uri = e.Uri ?? "";
            if (e.IsUserInitiated &&
                (uri.StartsWith("http://") || uri.StartsWith("https://") || uri.StartsWith("mailto:")))
                OpenExternal(uri);
        };

        RenderAndLoad();
        watchTimer.Tick += WatchTick;
        StartWatching();
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
                     + $"<b>File:</b> {Renderer.HtmlEscape(FilePath ?? "Untitled")}<br><br>{safe}</body></html>";
            web.CoreWebView2?.NavigateToString(html);
            // Mark the current mtime as seen even on failure — otherwise the
            // 1 Hz watcher re-renders the error page every second, forever.
            lastModified = ModificationDate();
            return;
        }
        web.CoreWebView2?.Navigate(new Uri(tempFile).AbsoluteUri);
        lastModified = ModificationDate();
        // Seed the save cache with what's actually on disk. Without this, a
        // menu-triggered Save before any edit would write "" and wipe the file;
        // after an external-change reload it would silently revert the file.
        if (FilePath != null)
            try { lastText = File.ReadAllText(FilePath); usesCrLf = lastText.Contains("\r\n"); } catch { }
    }

    DateTime? ModificationDate()
    {
        try { return FilePath != null && File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : null; }
        catch { return null; }
    }

    void StartWatching()
    {
        if (FilePath == null) return;   // nothing on disk to watch yet
        watchTimer.Start();
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
                case "saveAs":
                    SaveAsCommand();
                    break;
                case "setWrap":
                    host.UpdateWrapState(body.TryGetProperty("wrap", out var w) && w.ValueKind == JsonValueKind.True);
                    break;
                case "newFile":
                    host.NewDocument();
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
                case "print":
                    PrintDoc();
                    break;
            }
        }
    }

    void SetDirty(bool dirty)
    {
        IsDirty = dirty;
        Text = (dirty ? "● " : "") + DisplayName;
    }

    /// Runs arbitrary JS in the page.
    public void RunJS(string js)
    {
        try { web.CoreWebView2?.ExecuteScriptAsync(js); } catch { }
    }

    /// Moves real keyboard focus into the WebView so in-page focus() calls work.
    public void FocusWeb()
    {
        try { web.Focus(); } catch { }
    }

    /// Saves the document: pulls the live editor text from the page, then writes
    /// it. Falls back to the cached `lastText` when the page can't answer (e.g.
    /// the error page is showing). Untitled documents run a save dialog first
    /// (default name "Untitled.md"; the user may type any other extension).
    /// Returns false when the save didn't happen (dialog cancelled/write failed).
    public async Task<bool> SaveAsync()
    {
        try
        {
            var r = await web.CoreWebView2.ExecuteScriptAsync("window.__getText ? window.__getText() : null");
            if (!string.IsNullOrEmpty(r) && r != "null")
                lastText = JsonSerializer.Deserialize<string>(r) ?? lastText;
        }
        catch { /* fall back to the cached copy */ }
        if (FilePath == null) return SaveAs();
        return WriteToDisk();
    }

    public async void Save() => await SaveAsync();

    /// File ▸ Save As… (Ctrl+Shift+S): pulls the live editor text, then asks
    /// where to write. The tab then points at the new file (title, watching,
    /// recents) — the original file keeps whatever was last saved to it.
    public async Task<bool> SaveAsAsync()
    {
        try
        {
            var r = await web.CoreWebView2.ExecuteScriptAsync("window.__getText ? window.__getText() : null");
            if (!string.IsNullOrEmpty(r) && r != "null")
                lastText = JsonSerializer.Deserialize<string>(r) ?? lastText;
        }
        catch { /* fall back to the cached copy */ }
        return SaveAs();
    }

    public async void SaveAsCommand() => await SaveAsAsync();

    /// The save dialog: first save of an Untitled document, or Save As… for a
    /// file-backed one (pre-filled with the current name/folder). Defaults to
    /// .md but the "All files" filter lets the user keep any typed extension.
    bool SaveAs()
    {
        using var dlg = new SaveFileDialog
        {
            FileName = FilePath != null ? Path.GetFileName(FilePath) : "Untitled.md",
            InitialDirectory = FilePath != null ? Path.GetDirectoryName(FilePath) ?? "" : "",
            Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*",
            DefaultExt = "md",
            AddExtension = true,
            Title = "Save Markdown File",
        };
        if (dlg.ShowDialog(host) != DialogResult.OK) return false;
        FilePath = Path.GetFullPath(dlg.FileName);
        if (!WriteToDisk()) return false;
        ToolTipText = FilePath;
        host.UpdateTitle();
        StartWatching();
        // Re-render so <base href> points at the real folder (relative images
        // now resolve) — editor content equals what was saved.
        RenderAndLoad();
        host.NoteRecent(FilePath);
        host.SaveSession();
        return true;
    }

    /// Writes the latest editor text back to the markdown file on disk.
    bool WriteToDisk()
    {
        if (FilePath == null) return false;
        try
        {
            // Preserve the file's original line endings: the editor always
            // hands back LF, so normalize first, then re-apply CRLF if that's
            // what the file used.
            var text = lastText.Replace("\r\n", "\n");
            if (usesCrLf) text = text.Replace("\n", "\r\n");
            File.WriteAllText(FilePath, text, new UTF8Encoding(false));
            // Treat our own write as already-seen so the poll doesn't reload it.
            lastModified = ModificationDate();
            SetDirty(false);
            RunJS("window.__onSaved && window.__onSaved()");
            return true;
        }
        catch (Exception e)
        {
            MessageBox.Show($"{FilePath}\n\n{e.Message}", "Could not save the file.",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    /// Re-renders from disk (View ▸ Reload From Disk). Guards a dirty buffer
    /// first. No-op for Untitled documents — there is no disk copy to reload.
    public void ReloadFromDisk()
    {
        if (FilePath == null) { System.Media.SystemSounds.Beep.Play(); return; }
        if (IsDirty)
        {
            var r = MessageBox.Show(
                $"“{DisplayName}” has unsaved changes. Reload from disk and discard them?",
                "Reload", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;
            SetDirty(false);
        }
        RenderAndLoad();
    }

    /// `persist: false` applies the mode without overwriting the remembered
    /// preference (used for the deferred StartMode of new documents).
    public void SetEditorMode(string mode, bool persist = true)
    {
        var safe = mode.Replace("'", "");
        FocusWeb();
        RunJS($"window.__setMode && window.__setMode('{safe}', {(persist ? "true" : "false")})");
    }

    public void ToggleWrapInPage() => RunJS("window.__toggleWrap && window.__toggleWrap()");
    public void FindInPage() { FocusWeb(); RunJS("window.__find && window.__find()"); }
    public void FindReplaceInPage() { FocusWeb(); RunJS("window.__findReplace && window.__findReplace()"); }
    public void ToggleTOCInPage() => RunJS("window.__toggleTOC && window.__toggleTOC()");
    public void ZoomIn() => RunJS("window.__zoomIn && window.__zoomIn()");
    public void ZoomOut() => RunJS("window.__zoomOut && window.__zoomOut()");
    public void ZoomReset() => RunJS("window.__zoomReset && window.__zoomReset()");

    /// File ▸ Print… (Ctrl+P): the browser print dialog on the rendered
    /// preview (print CSS hides the toolbar/editor); "Save as PDF" there
    /// doubles as PDF export.
    public void PrintDoc()
    {
        try { web.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser); } catch { }
    }

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
        Text = "Open a Markdown file — Ctrl+O\nNew file — Ctrl+N\n(or drag one onto this window)",
        ForeColor = Color.Gray,
    };
    ToolStripMenuItem wrapMenuItem;
    bool forceClose;

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
            if (!opened)
            {
                // Reopen last session (don't reshuffle Open Recent doing it).
                foreach (var p in ReadSession().Where(File.Exists))
                {
                    await OpenFileAsync(p, noteAsRecent: false);
                    opened = true;
                }
            }
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

    public void UpdateTitle()
    {
        Text = tabs.SelectedTab is ViewerTab t
            ? t.DisplayName + " — MarkdownViewer"
            : "MarkdownViewer";
    }

    public ViewerTab ActiveTab => tabs.SelectedTab as ViewerTab;
    ViewerTab Current => ActiveTab;

    public async void OpenFile(string path) => await OpenFileAsync(path);

    public async Task OpenFileAsync(string path, bool noteAsRecent = true)
    {
        var full = Path.GetFullPath(path);
        foreach (ViewerTab t in tabs.TabPages)
            if (t.FilePath != null && string.Equals(t.FilePath, full, StringComparison.OrdinalIgnoreCase))
            {
                tabs.SelectedTab = t;
                return;
            }
        var tab = new ViewerTab(this, full);
        tabs.TabPages.Add(tab);
        tabs.SelectedTab = tab;
        UpdateEmptyState();
        UpdateTitle();
        if (noteAsRecent) NoteRecent(full);
        SaveSession();
        await tab.Init();
    }

    // MARK: Session restore (reopen last open documents on plain launch)

    const string SessionKey = "sessionFiles";
    /// Suppresses session rewrites while tabs close during app exit —
    /// otherwise closing would empty the list one tab at a time.
    bool isExiting;

    List<string> ReadSession()
    {
        try { return JsonSerializer.Deserialize<List<string>>(Settings.Get(SessionKey) ?? "[]") ?? new List<string>(); }
        catch { return new List<string>(); }
    }

    public void SaveSession()
    {
        if (isExiting) return;
        var list = tabs.TabPages.Cast<ViewerTab>()
            .Where(t => t.FilePath != null).Select(t => t.FilePath).ToList();
        Settings.Set(SessionKey, JsonSerializer.Serialize(list));
    }

    /// File ▸ New: a blank Untitled document opening in Split mode; the first
    /// save asks for a location (default .md, any typed extension accepted).
    public async void NewDocument()
    {
        var tab = new ViewerTab(this, null) { StartMode = "split" };
        tabs.TabPages.Add(tab);
        tabs.SelectedTab = tab;
        UpdateEmptyState();
        UpdateTitle();
        await tab.Init();
    }

    // MARK: Recent files (persisted in settings.json, shown under File ▸ Open Recent)

    const string RecentsKey = "recentFiles";
    const int RecentsMax = 10;

    List<string> RecentPaths()
    {
        try { return JsonSerializer.Deserialize<List<string>>(Settings.Get(RecentsKey) ?? "[]") ?? new List<string>(); }
        catch { return new List<string>(); }
    }

    public void NoteRecent(string path)
    {
        var list = RecentPaths();
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > RecentsMax) list = list.Take(RecentsMax).ToList();
        Settings.Set(RecentsKey, JsonSerializer.Serialize(list));
    }

    /// Rebuilds File ▸ Open Recent every time it's about to show.
    void RebuildRecentMenu(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        var existing = RecentPaths().Where(File.Exists).ToList();
        foreach (var path in existing)
        {
            var item = new ToolStripMenuItem(Path.GetFileName(path)) { ToolTipText = path };
            var p = path;
            item.Click += (_, __) => OpenFile(p);
            menu.DropDownItems.Add(item);
        }
        if (existing.Count == 0)
            menu.DropDownItems.Add(new ToolStripMenuItem("No Recent Files") { Enabled = false });
        menu.DropDownItems.Add(new ToolStripSeparator());
        var clear = new ToolStripMenuItem("Clear Menu") { Enabled = existing.Count > 0 };
        clear.Click += (_, __) => Settings.Set(RecentsKey, "[]");
        menu.DropDownItems.Add(clear);
    }

    /// Warn before closing a tab that has unsaved edits. Returns true if closed.
    public async Task<bool> CloseTabAsync(ViewerTab tab)
    {
        if (tab == null) return false;
        if (tab.IsDirty)
        {
            var r = MessageBox.Show(
                $"Do you want to save the changes you made to “{tab.DisplayName}”?\n\nYour changes will be lost if you don't save them.",
                "Unsaved Changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (r == DialogResult.Cancel) return false;
            // A cancelled save dialog (Untitled) or failed write keeps the tab
            // open — closing anyway would throw the document away.
            if (r == DialogResult.Yes && !await tab.SaveAsync()) return false;
        }
        tab.Stop();
        tabs.TabPages.Remove(tab);
        tab.Dispose();
        UpdateEmptyState();
        UpdateTitle();
        SaveSession();
        return true;
    }

    public async void CloseTab(ViewerTab tab) => await CloseTabAsync(tab);

    void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        // Snapshot the session before any tab teardown, then freeze it.
        SaveSession();
        isExiting = true;
        if (!forceClose)
        {
            var dirty = tabs.TabPages.Cast<ViewerTab>().Where(t => t.IsDirty).ToArray();
            if (dirty.Length > 0)
            {
                // Saves are async (they pull the live text from the page), so
                // cancel this close and re-close once they're all settled.
                e.Cancel = true;
                FinishClose(dirty);
                return;
            }
        }
        foreach (ViewerTab t in tabs.TabPages) t.Stop();
    }

    async void FinishClose(ViewerTab[] dirty)
    {
        foreach (var tab in dirty)
        {
            tabs.SelectedTab = tab;
            var r = MessageBox.Show(
                $"Do you want to save the changes you made to “{tab.DisplayName}”?\n\nYour changes will be lost if you don't save them.",
                "Unsaved Changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            // Aborting the exit un-freezes session tracking (see OnFormClosing).
            if (r == DialogResult.Cancel) { isExiting = false; return; }
            if (r == DialogResult.Yes && !await tab.SaveAsync()) { isExiting = false; return; }
        }
        forceClose = true;
        Close();
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
        // The README references images under docs/ — copy the bundled folder
        // next to the temp file so those relative paths resolve.
        var docsSrc = Path.Combine(Renderer.ResourcesDir, "docs");
        if (Directory.Exists(docsSrc))
            try { CopyDirectory(docsSrc, Path.Combine(tmpDir, "docs")); } catch { }
        _ = OpenFileAsync(dst, noteAsRecent: false);
    }

    static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    // MARK: Menu

    MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();

        // File
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(Item("New", Keys.Control | Keys.N, (_, __) => NewDocument()));
        file.DropDownItems.Add(Item("Open…", Keys.Control | Keys.O, (_, __) => ShowOpenDialog()));
        file.DropDownItems.Add(Item("Open Path…", Keys.Control | Keys.Shift | Keys.G, (_, __) => ShowOpenPathDialog()));
        var openRecent = new ToolStripMenuItem("Open Recent");
        openRecent.DropDownOpening += (_, __) => RebuildRecentMenu(openRecent);
        RebuildRecentMenu(openRecent);   // seed so the submenu arrow shows
        file.DropDownItems.Add(openRecent);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("Save", Keys.Control | Keys.S, (_, __) => Current?.Save()));
        file.DropDownItems.Add(Item("Save As…", Keys.Control | Keys.Shift | Keys.S, (_, __) => Current?.SaveAsCommand()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("Print…", Keys.Control | Keys.P, (_, __) => Current?.PrintDoc()));
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
        view.DropDownItems.Add(Item("Table of Contents", Keys.Control | Keys.T, (_, __) => Current?.ToggleTOCInPage()));
        wrapMenuItem = Item("Wrap Lines", Keys.None, (_, __) => Current?.ToggleWrapInPage());
        wrapMenuItem.Checked = true;
        view.DropDownItems.Add(wrapMenuItem);
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(Item("Zoom In", Keys.Control | Keys.Oemplus, (_, __) => Current?.ZoomIn()));
        view.DropDownItems.Add(Item("Zoom Out", Keys.Control | Keys.OemMinus, (_, __) => Current?.ZoomOut()));
        view.DropDownItems.Add(Item("Actual Size", Keys.Control | Keys.D0, (_, __) => Current?.ZoomReset()));
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(Item("Reload From Disk", Keys.Control | Keys.R, (_, __) => Current?.ReloadFromDisk()));

        // Help
        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(Item("About MarkdownViewer", Keys.None, (_, __) => ShowAbout()));

        menu.Items.AddRange(new ToolStripItem[] { file, edit, view, help });
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

// MARK: - About window

sealed class AboutForm : Form
{
    public AboutForm(MainForm host)
    {
        Text = "About MarkdownViewer";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(500, 300);

        var version = Application.ProductVersion.Split('+')[0];

        var icon = new PictureBox
        {
            Size = new Size(84, 84),
            Location = new Point((500 - 84) / 2, 24),
            SizeMode = PictureBoxSizeMode.Zoom,
        };
        try { icon.Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath)?.ToBitmap(); } catch { }

        var name = new Label
        {
            Text = "MarkdownViewer",
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(0, 116), Size = new Size(500, 28),
        };
        var ver = new Label
        {
            Text = "Version " + version,
            ForeColor = Color.Gray,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(0, 146), Size = new Size(500, 20),
        };
        var author = new Label
        {
            Text = "by David Godibadze",
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(0, 168), Size = new Size(500, 20),
        };

        var readme = new Button { Text = "Read Me", Location = new Point(20, 216), Width = 110 };
        readme.Click += (_, __) => { host.OpenBundledDoc("README.md"); Close(); };
        var changelog = new Button { Text = "Changelog", Location = new Point(140, 216), Width = 110 };
        changelog.Click += (_, __) => { host.OpenBundledDoc("CHANGELOG.md"); Close(); };
        var arch = new Button { Text = "Architecture", Location = new Point(260, 216), Width = 110 };
        arch.Click += (_, __) => { host.OpenBundledDoc("ARCHITECTURE.md"); Close(); };
        var design = new Button { Text = "Design", Location = new Point(380, 216), Width = 110 };
        design.Click += (_, __) => { host.OpenBundledDoc("DESIGN.md"); Close(); };

        Controls.AddRange(new Control[] { icon, name, ver, author, readme, changelog, arch, design });
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
