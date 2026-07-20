// MarkdownViewer for Windows — a port of the macOS app (Sources/main.swift).
// One C# file drives a WebView2 per tab that renders the bundled HTML template;
// C# ↔ JS talk over the same small message bridge the Mac app uses.
// Keep behavior in sync with main.swift — both implement the pipeline described
// in Resources/DESIGN.md (rendering, save/dirty invariants, live reload).

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
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
/// in the bundled template. Assets and document-relative files use separate,
/// scoped origins rather than unrestricted file URLs.
static class Renderer
{
    public static string ResourcesDir =>
        Path.Combine(AppContext.BaseDirectory, "Resources");

    /// Renders the markdown file (or an empty untitled document when
    /// `markdownFile` is null) into `tempFile`.
    /// Returns null on success, or a human-readable error string on failure.
    public static string Render(string markdownFile, string markdownText, string tempFile)
    {
        var tplPath = Path.Combine(ResourcesDir, "template.html");
        if (!File.Exists(tplPath))
            return "Bundled template.html not found in app Resources.";
        string template;
        try { template = File.ReadAllText(tplPath, Encoding.UTF8); }
        catch (Exception e) { return $"Could not read bundled template at {tplPath}.\n{e.Message}"; }

        // WebView2 maps these private hosts to the bundle and the current
        // document directory. The page never receives unrestricted file URLs.
        var resDir = "https://appassets.local";
        var baseDir = "https://document.local/";
        var title = markdownFile != null ? Path.GetFileName(markdownFile) : "Untitled";

        // __MARKDOWN__ must be substituted LAST: it injects arbitrary document
        // text, and any token replaced after it would also match occurrences of
        // that token *inside* the document (e.g. a markdown file that mentions
        // "__TITLE__" literally would get corrupted).
        var markdownSlot = $"__MDV_MARKDOWN_SLOT_{Guid.NewGuid():N}__";
        template = template.Replace("__MARKDOWN__", markdownSlot);
        template = template.Replace("__RES__", resDir);
        template = template.Replace("__BASE__", baseDir);
        template = template.Replace("__TITLE__", HtmlEscape(title));
        template = template.Replace(markdownSlot, JsString(markdownText));

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

enum TextEncodingKind { Utf8, Utf8Bom, Utf16LE, Utf16BE, Utf32LE, Utf32BE, Lossy }
enum NewlineKind { Lf, CrLf, Cr, Mixed }
sealed record DiskFingerprint(long Length, long ModifiedTicks, string Sha256)
{
    public static DiskFingerprint Read(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            return FromData(path, data);
        }
        catch { return null; }
    }

    public static DiskFingerprint FromData(string path, byte[] data)
    {
        long modifiedTicks;
        try { modifiedTicks = File.GetLastWriteTimeUtc(path).Ticks; }
        catch { modifiedTicks = 0; }
        return new DiskFingerprint(data.LongLength, modifiedTicks,
            Convert.ToHexString(SHA256.HashData(data)));
    }
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
    bool hasValidTextSnapshot;
    /// Mode to force once the page finishes loading (e.g. "split" for new
    /// documents). Calling __setMode before the page loads is a silent no-op,
    /// so this is applied from NavigationCompleted instead.
    public string StartMode;
    /// Original text encoding and newline convention, preserved on save.
    TextEncodingKind textEncoding = TextEncodingKind.Utf8;
    NewlineKind newlineKind = NewlineKind.Lf;
    DiskFingerprint lastDiskFingerprint;

    public string DisplayName => FilePath != null ? Path.GetFileName(FilePath) : "Untitled";

    public ViewerTab(MainForm host, string filePath)
    {
        this.host = host;
        FilePath = filePath != null ? Path.GetFullPath(filePath) : null;
        hasValidTextSnapshot = FilePath == null;
        Text = DisplayName;
        ToolTipText = FilePath ?? "Unsaved document";
        var tmpDir = Path.Combine(Path.GetTempPath(), "MarkdownViewer");
        tempFile = Path.Combine(tmpDir, $"view-{Guid.NewGuid()}.html");
        Controls.Add(web);
    }

    public async Task Init()
    {
        try
        {
            var env = await App.GetWebViewEnvironment();
            await web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            // Without this, a missing WebView2 runtime throws inside an
            // `async void` caller and terminates the whole process with no
            // explanation. Show guidance and leave the tab blank instead.
            MessageBox.Show(
                "MarkdownViewer could not start its embedded browser (Microsoft Edge WebView2 Runtime)."
                + "\n\nInstall the WebView2 Runtime from Microsoft and reopen the file."
                + "\n\n" + ex.Message,
                "WebView2 unavailable", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var s = web.CoreWebView2.Settings;
        // Browser accelerator keys stay enabled: disabling them also stops keys
        // like Escape from ever reaching the page. The template preventDefault()s
        // the ones we don't want (Ctrl+F/P, F3, F5, F7, F11) instead.
        s.IsZoomControlEnabled = false;               // template implements its own zoom
        s.AreDevToolsEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "appassets.local", Renderer.ResourcesDir,
            CoreWebView2HostResourceAccessKind.DenyCors);
        web.CoreWebView2.AddWebResourceRequestedFilter(
            "https://document.local/*", CoreWebView2WebResourceContext.All);
        web.CoreWebView2.WebResourceRequested += OnDocumentResourceRequested;

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
            if (IsTrustedPage(uri) || uri == "about:blank") return;
            e.Cancel = true;
            if (!e.IsUserInitiated) return;
            if (TryDocumentUrl(uri, out var local))
            {
                if (IsMarkdownPath(local)) host.OpenFile(local);
                else OpenLocalFile(local);
                return;
            }
            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
                (parsed.Scheme == "http" || parsed.Scheme == "https" || parsed.Scheme == "mailto") &&
                parsed.Host != "appassets.local" && parsed.Host != "document.local")
                OpenExternal(uri);
        };
        web.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            var uri = e.Uri ?? "";
            if (!e.IsUserInitiated) return;
            if (TryDocumentUrl(uri, out var local))
            {
                if (IsMarkdownPath(local)) host.OpenFile(local);
                else OpenLocalFile(local);
                return;
            }
            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
                (parsed.Scheme == "http" || parsed.Scheme == "https" || parsed.Scheme == "mailto") &&
                parsed.Host != "appassets.local" && parsed.Host != "document.local") OpenExternal(uri);
        };

        RenderAndLoad();
        watchTimer.Tick += WatchTick;
        StartWatching();
    }

    static void OpenExternal(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    bool IsTrustedPage(string uri)
    {
        try
        {
            return string.Equals(CanonicalLocalPath(new Uri(uri).LocalPath),
                CanonicalLocalPath(tempFile), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static bool IsMarkdownPath(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        return new[] { "md", "markdown", "mdown", "mkd", "mkdn", "mdwn", "markdn",
            "mdtxt", "text", "txt", "rmd", "qmd", "mdx", "mdc" }
            .Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    static bool IsSafeLocalOpen(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
        return new[] { "bmp", "csv", "gif", "heic", "jpeg", "jpg", "json", "log",
            "m4a", "mov", "mp3", "mp4", "pdf", "png", "rtf", "tif", "tiff",
            "tsv", "txt", "wav", "webp" }.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    static void OpenLocalFile(string path)
    {
        if (IsSafeLocalOpen(path)) { OpenExternal(path); return; }
        try
        {
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            if (Directory.Exists(path)) info.ArgumentList.Add(path);
            else
            {
                info.ArgumentList.Add("/select,");
                info.ArgumentList.Add(path);
            }
            Process.Start(info);
        }
        catch { }
    }

    internal static string CanonicalLocalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var volume = Path.GetPathRoot(full) ?? throw new IOException("Path has no filesystem root.");
        var current = volume;
        var parts = full.Substring(volume.Length).Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                var resolved = info.ResolveLinkTarget(true);
                if (resolved != null) current = Path.GetFullPath(resolved.FullName);
            }
        }
        return Path.GetFullPath(current);
    }

    bool TryDocumentUrl(string uri, out string localPath)
    {
        localPath = null;
        try
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
                !string.Equals(parsed.Host, "document.local", StringComparison.OrdinalIgnoreCase)) return false;
            var lexicalRoot = Path.GetFullPath(FilePath != null
                ? Path.GetDirectoryName(FilePath) ?? ""
                : Path.GetDirectoryName(tempFile) ?? "");
            var relative = Uri.UnescapeDataString(parsed.AbsolutePath)
                .TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            if (relative.IndexOf(':') >= 0 || relative.IndexOf('\0') >= 0) return false;
            var root = CanonicalLocalPath(lexicalRoot);
            var candidate = CanonicalLocalPath(Path.Combine(lexicalRoot, relative));
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)) return false;
            localPath = candidate;
            return true;
        }
        catch { return false; }
    }

    void OnDocumentResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        const long maxResourceBytes = 64L * 1024 * 1024;
        try
        {
            if (!TryDocumentUrl(e.Request.Uri, out var path) || !File.Exists(path))
                throw new FileNotFoundException();
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                info.Length > maxResourceBytes) throw new IOException("Resource is not a permitted regular file.");
            var stream = new MemoryStream(ReadBoundedFile(path, maxResourceBytes), writable: false);
            var headers = $"Content-Type: {MimeType(path)}\r\nCache-Control: no-store";
            e.Response = web.CoreWebView2.Environment.CreateWebResourceResponse(
                stream, 200, "OK", headers);
        }
        catch
        {
            e.Response = web.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(Array.Empty<byte>()), 404, "Not Found", "Cache-Control: no-store");
        }
    }

    static byte[] ReadBoundedFile(string path, long maximum)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        if (source.Length < 0 || source.Length > maximum) throw new IOException("Resource is too large.");
        var data = new byte[(int)source.Length];
        var offset = 0;
        while (offset < data.Length)
        {
            var count = source.Read(data, offset, data.Length - offset);
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
        if (source.ReadByte() != -1) throw new IOException("Resource changed while it was read.");
        return data;
    }

    static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".avif" => "image/avif", ".bmp" => "image/bmp", ".gif" => "image/gif",
        ".heic" => "image/heic", ".jpeg" or ".jpg" => "image/jpeg",
        ".png" => "image/png", ".svg" => "image/svg+xml", ".webp" => "image/webp",
        ".m4a" => "audio/mp4", ".mp3" => "audio/mpeg", ".wav" => "audio/wav",
        ".mov" => "video/quicktime", ".mp4" => "video/mp4", ".webm" => "video/webm",
        ".txt" => "text/plain; charset=utf-8", _ => "application/octet-stream"
    };

    public void RenderAndLoad()
    {
        string err = null;
        if (FilePath != null)
        {
            try
            {
                // One byte snapshot drives the editor text, rendered preview,
                // and conflict baseline, closing the read/render/fingerprint race.
                var data = File.ReadAllBytes(FilePath);
                LoadDiskState(data);
            }
            catch (Exception e) { err = $"Could not read the markdown file.\n{e.Message}"; }
        }
        if (err == null) err = Renderer.Render(FilePath, lastText, tempFile);
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
        if (FilePath == null) lastModified = null;
    }

    void LoadDiskState(byte[] data)
    {
        try
        {
            if (data.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            {
                try { lastText = new UTF8Encoding(false, true).GetString(data, 3, data.Length - 3); textEncoding = TextEncodingKind.Utf8Bom; }
                catch { lastText = Encoding.UTF8.GetString(data, 3, data.Length - 3); textEncoding = TextEncodingKind.Lossy; }
            }
            else if (data.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
            {
                try { lastText = new UTF32Encoding(false, false, true).GetString(data, 4, data.Length - 4); textEncoding = TextEncodingKind.Utf32LE; }
                catch
                {
                    // FF FE 00 00 is also a valid UTF-16LE BOM followed by
                    // U+0000 — try that before declaring the file undecodable,
                    // otherwise a save would rewrite it as garbled UTF-8.
                    try { lastText = new UnicodeEncoding(false, false, true).GetString(data, 2, data.Length - 2); textEncoding = TextEncodingKind.Utf16LE; }
                    catch { lastText = new UTF32Encoding(false, false, false).GetString(data, 4, data.Length - 4); textEncoding = TextEncodingKind.Lossy; }
                }
            }
            else if (data.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
            {
                try { lastText = new UTF32Encoding(true, false, true).GetString(data, 4, data.Length - 4); textEncoding = TextEncodingKind.Utf32BE; }
                catch { lastText = new UTF32Encoding(true, false, false).GetString(data, 4, data.Length - 4); textEncoding = TextEncodingKind.Lossy; }
            }
            else if (data.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
            {
                try { lastText = new UnicodeEncoding(false, false, true).GetString(data, 2, data.Length - 2); textEncoding = TextEncodingKind.Utf16LE; }
                catch { lastText = Encoding.Unicode.GetString(data, 2, data.Length - 2); textEncoding = TextEncodingKind.Lossy; }
            }
            else if (data.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            {
                try { lastText = new UnicodeEncoding(true, false, true).GetString(data, 2, data.Length - 2); textEncoding = TextEncodingKind.Utf16BE; }
                catch { lastText = Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2); textEncoding = TextEncodingKind.Lossy; }
            }
            else
            {
                try { lastText = new UTF8Encoding(false, true).GetString(data); textEncoding = TextEncodingKind.Utf8; }
                catch { lastText = Encoding.UTF8.GetString(data); textEncoding = TextEncodingKind.Lossy; }
            }
            newlineKind = DetectNewlines(lastText);
            hasValidTextSnapshot = true;
            lastDiskFingerprint = DiskFingerprint.FromData(FilePath, data);
            lastModified = new DateTime(lastDiskFingerprint.ModifiedTicks, DateTimeKind.Utc);
        }
        catch { }
    }

    static NewlineKind DetectNewlines(string text)
    {
        var rest = text.Replace("\r\n", "");
        var crlf = text.Contains("\r\n");
        var lf = rest.Contains('\n');
        var cr = rest.Contains('\r');
        if ((crlf ? 1 : 0) + (lf ? 1 : 0) + (cr ? 1 : 0) > 1) return NewlineKind.Mixed;
        if (crlf) return NewlineKind.CrLf;
        if (cr) return NewlineKind.Cr;
        return NewlineKind.Lf;
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
        if (current != lastModified)
        {
            lastModified = current;
            RenderAndLoad();
        }
    }

    // MARK: Editor bridge — receives messages posted from the page.

    void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsTrustedPage(e.Source)) return;
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
                    // Deferred: closing a clean tab synchronously would Dispose
                    // this WebView2 inside its own WebMessageReceived handler —
                    // a documented WebView2 reentrancy hazard (dirty tabs were
                    // safe only because the save `await` broke the call stack).
                    host.BeginInvoke(() => host.CloseTab(this));
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

    // MARK: Edit menu commands
    //
    // macOS gets Undo/Cut/Copy/… free from the AppKit responder chain; WinForms
    // has none, so the Edit menu routes here. Ctrl+Z/X/C/V/A themselves are NOT
    // registered as menu accelerators — WebView2's own browser accelerators
    // already handle them inside the textarea, and a WinForms accelerator would
    // intercept the key before the page ever saw it. These exist for the menu
    // (discoverability + mouse access); the keyboard path stays native.
    //
    // Clipboard payloads move through the native side because Chromium refuses
    // execCommand('cut'/'copy'/'paste') without a user gesture, and a script
    // injected via ExecuteScriptAsync does not count as one.

    public void EditUndo() => RunJS("window.__editCmd && window.__editCmd('undo')");
    public void EditRedo() => RunJS("window.__editCmd && window.__editCmd('redo')");
    public void EditSelectAll() => RunJS("window.__editCmd && window.__editCmd('selectAll')");
    public void EditDelete() => RunJS("window.__editCmd && window.__editCmd('delete')");

    /// The editor's current selection, or "" when the page can't answer.
    async Task<string> SelectionAsync()
    {
        try
        {
            var r = await web.CoreWebView2.ExecuteScriptAsync(
                "window.__editSelection ? window.__editSelection() : null");
            if (string.IsNullOrEmpty(r) || r == "null") return "";
            return JsonSerializer.Deserialize<string>(r) ?? "";
        }
        catch { return ""; }
    }

    /// Copies the selection to the clipboard. Returns false if there was none
    /// (so Cut knows not to delete anything).
    async Task<bool> CopySelectionAsync()
    {
        var sel = await SelectionAsync();
        if (sel.Length == 0) return false;
        try { Clipboard.SetText(sel); } catch { return false; }
        return true;
    }

    public async void EditCopy() => await CopySelectionAsync();

    public async void EditCut()
    {
        if (await CopySelectionAsync()) EditDelete();
    }

    public void EditPaste()
    {
        string text;
        try { text = Clipboard.GetText() ?? ""; } catch { return; }
        if (text.Length == 0) return;
        RunJS("window.__editInsert && window.__editInsert(" + Renderer.JsString(text) + ")");
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
        var target = Path.GetFullPath(dlg.FileName);
        if (host.ActivateIfOpen(target, this))
        {
            MessageBox.Show("Close the other tab before saving to the same path.",
                "That file is already open", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        var sameAsCurrent = FilePath != null && string.Equals(
            CanonicalLocalPath(FilePath), CanonicalLocalPath(target),
            StringComparison.OrdinalIgnoreCase);
        if (!WriteToDisk(target, checkConflict: sameAsCurrent)) return false;
        FilePath = target;
        Text = DisplayName;   // tab header — SetDirty(false) ran with the old name
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
    bool WriteToDisk(string targetPath = null, bool checkConflict = true)
    {
        // The WinForms timer still ticks inside modal dialog message loops
        // (unlike NSTimer during NSAlert.runModal), so a live-reload could fire
        // under the conflict/format prompts below and swap lastText to the
        // external content just before the user's "overwrite" answer applies.
        // Suspend the watcher for the duration of the save.
        var resumeWatch = watchTimer.Enabled;
        watchTimer.Stop();
        try { return WriteToDiskCore(targetPath, checkConflict); }
        finally { if (resumeWatch) watchTimer.Start(); }
    }

    bool WriteToDiskCore(string targetPath, bool checkConflict)
    {
        var target = targetPath ?? FilePath;
        if (target == null) return false;
        if (!hasValidTextSnapshot)
        {
            MessageBox.Show("Fix access to the original file and reload it before saving a copy.",
                "There is no readable document content to save", MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }
        if (checkConflict)
        {
            var current = DiskFingerprint.Read(target);
            if (current != lastDiskFingerprint)
            {
                var message = current == null
                    ? "The file was removed or is no longer readable. Saving now will recreate it."
                    : "The file changed outside MarkdownViewer. Saving now will overwrite that change.";
                if (MessageBox.Show(message, "File changed on disk", MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning) != DialogResult.OK) return false;
            }
            else if (!IsDirty) return true;
        }
        var outputEncoding = textEncoding;
        var outputNewline = newlineKind;
        if (outputEncoding == TextEncodingKind.Lossy || outputNewline == NewlineKind.Mixed)
        {
            var savingCopy = targetPath != null && (FilePath == null || !string.Equals(
                CanonicalLocalPath(FilePath), CanonicalLocalPath(target),
                StringComparison.OrdinalIgnoreCase));
            var message = outputEncoding == TextEncodingKind.Lossy && outputNewline == NewlineKind.Mixed
                ? savingCopy
                    ? "This file has an unknown encoding and mixed line endings. Save a UTF-8 copy with LF line endings?"
                    : "This file has an unknown encoding and mixed line endings. Convert to UTF-8/LF and overwrite it?"
                : outputEncoding == TextEncodingKind.Lossy
                    ? savingCopy
                        ? "This file is not valid UTF-8/UTF-16/UTF-32. Save a UTF-8 copy?"
                        : "This file is not valid UTF-8/UTF-16/UTF-32. Convert to UTF-8 and overwrite it?"
                    : "This file mixes line-ending styles. Save with LF line endings?";
            if (MessageBox.Show(message, "Format conversion required", MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning) != DialogResult.OK) return false;
            if (outputEncoding == TextEncodingKind.Lossy) outputEncoding = TextEncodingKind.Utf8;
            if (outputNewline == NewlineKind.Mixed) outputNewline = NewlineKind.Lf;
        }
        try
        {
            // Preserve the file's original line endings: the editor always
            // hands back LF, so normalize first, then re-apply CRLF if that's
            // what the file used.
            var text = lastText.Replace("\r\n", "\n").Replace("\r", "\n");
            if (outputNewline == NewlineKind.CrLf) text = text.Replace("\n", "\r\n");
            else if (outputNewline == NewlineKind.Cr) text = text.Replace("\n", "\r");
            byte[] body;
            byte[] bom = Array.Empty<byte>();
            switch (outputEncoding)
            {
                case TextEncodingKind.Utf8: body = new UTF8Encoding(false).GetBytes(text); break;
                case TextEncodingKind.Utf8Bom:
                    bom = new byte[] { 0xEF, 0xBB, 0xBF }; body = new UTF8Encoding(false).GetBytes(text); break;
                case TextEncodingKind.Utf16LE:
                    bom = new byte[] { 0xFF, 0xFE }; body = Encoding.Unicode.GetBytes(text); break;
                case TextEncodingKind.Utf16BE:
                    bom = new byte[] { 0xFE, 0xFF }; body = Encoding.BigEndianUnicode.GetBytes(text); break;
                case TextEncodingKind.Utf32LE:
                    bom = new byte[] { 0xFF, 0xFE, 0x00, 0x00 }; body = new UTF32Encoding(false, false).GetBytes(text); break;
                case TextEncodingKind.Utf32BE:
                    bom = new byte[] { 0x00, 0x00, 0xFE, 0xFF }; body = new UTF32Encoding(true, false).GetBytes(text); break;
                default: return false;
            }
            var data = new byte[bom.Length + body.Length];
            Buffer.BlockCopy(bom, 0, data, 0, bom.Length);
            Buffer.BlockCopy(body, 0, data, bom.Length, body.Length);
            WriteAtomically(target, data);
            // Treat our own write as already-seen so the poll doesn't reload it.
            lastModified = File.GetLastWriteTimeUtc(target);
            lastDiskFingerprint = DiskFingerprint.Read(target);
            textEncoding = outputEncoding;
            newlineKind = outputNewline;
            SetDirty(false);
            RunJS("window.__onSaved && window.__onSaved()");
            return true;
        }
        catch (Exception e)
        {
            MessageBox.Show($"{target}\n\n{e.Message}", "Could not save the file.",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    static void WriteAtomically(string target, byte[] data)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(target)) ?? Path.GetTempPath();
        var temp = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(data, 0, data.Length);
                stream.Flush(true);
            }
            if (File.Exists(target))
            {
                try { File.Replace(temp, target, null, true); }
                catch (PlatformNotSupportedException) { File.Move(temp, target, true); }
            }
            else File.Move(temp, target);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
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
    public async void PrintDoc()
    {
        try
        {
            await web.CoreWebView2.ExecuteScriptAsync("window.__preparePrint && window.__preparePrint()");
            for (var i = 0; i < 200; i++)
            {
                var ready = await web.CoreWebView2.ExecuteScriptAsync("window.__printReady !== false");
                if (string.Equals(ready, "true", StringComparison.OrdinalIgnoreCase)) break;
                await Task.Delay(25);
            }
        }
        catch { }
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

    public MainForm(string[] args, bool isFirstInstance = true)
    {
        Text = "MarkdownViewer";
        MinimumSize = new Size(420, 320);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(900, 760);
        RestoreWindowPlacement();   // mirrors the Mac frame-autosave behavior
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

        // Only markdown-ish files open as documents (mirrors the Mac
        // DropWebView filter). Without this any dropped file — an image, an
        // .exe — would open as a tab and be rendered as if it were text.
        DragEnter += (_, e) =>
        {
            e.Effect = DroppedMarkdownFiles(e.Data).Count > 0
                ? DragDropEffects.Copy : DragDropEffects.None;
        };
        DragOver += (_, e) =>
        {
            e.Effect = DroppedMarkdownFiles(e.Data).Count > 0
                ? DragDropEffects.Copy : DragDropEffects.None;
        };
        DragDrop += (_, e) =>
        {
            foreach (var f in DroppedMarkdownFiles(e.Data)) OpenFile(f);
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
        // Only the instance that owns the singleton mutex may listen — a
        // mutex-fallthrough second instance constructing the (max-1-instance)
        // pipe would throw instantly, get swallowed, and retry in a tight loop.
        if (isFirstInstance) StartPipeServer();
    }

    // MARK: Window frame persistence (the Mac side's setFrameAutosaveName analog)

    const string WindowKey = "windowPlacement";

    void RestoreWindowPlacement()
    {
        try
        {
            var raw = Settings.Get(WindowKey);
            if (raw == null) return;
            var p = JsonSerializer.Deserialize<int[]>(raw);
            if (p == null || p.Length != 4 || p[2] < 420 || p[3] < 320) return;
            var r = new Rectangle(p[0], p[1], p[2], p[3]);
            // Only restore a frame that is still at least partly on a screen
            // (monitors get unplugged between sessions).
            foreach (var screen in Screen.AllScreens)
                if (screen.WorkingArea.IntersectsWith(r))
                {
                    StartPosition = FormStartPosition.Manual;
                    Bounds = r;
                    return;
                }
        }
        catch { /* a bad saved frame just means "use the default" */ }
    }

    void SaveWindowPlacement()
    {
        try
        {
            var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            Settings.Set(WindowKey, JsonSerializer.Serialize(new[] { r.X, r.Y, r.Width, r.Height }));
        }
        catch { /* best-effort */ }
    }

    /// The markdown files in a drag payload, in drop order. Empty when the
    /// drag carries no files or nothing with a markdown-ish extension.
    static List<string> DroppedMarkdownFiles(IDataObject data)
    {
        var hits = new List<string>();
        try
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return hits;
            var paths = data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null) return hits;
            foreach (var p in paths)
                if (File.Exists(p) && ViewerTab.IsMarkdownPath(p)) hits.Add(p);
        }
        catch { /* a malformed drag payload just means "nothing to open" */ }
        return hits;
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
                catch { Thread.Sleep(500); /* pipe hiccups shouldn't kill the listener — or spin a core */ }
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

    public bool ActivateIfOpen(string path, ViewerTab excluded = null)
    {
        var full = ViewerTab.CanonicalLocalPath(path);
        foreach (ViewerTab tab in tabs.TabPages)
            if (tab != excluded && tab.FilePath != null &&
                string.Equals(ViewerTab.CanonicalLocalPath(tab.FilePath), full,
                    StringComparison.OrdinalIgnoreCase))
            { tabs.SelectedTab = tab; return true; }
        return false;
    }

    public async void OpenFile(string path) => await OpenFileAsync(path);

    public async Task OpenFileAsync(string path, bool noteAsRecent = true)
    {
        var full = ViewerTab.CanonicalLocalPath(path);
        foreach (ViewerTab t in tabs.TabPages)
            if (t.FilePath != null && string.Equals(ViewerTab.CanonicalLocalPath(t.FilePath), full,
                StringComparison.OrdinalIgnoreCase))
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

    // Shell recents (Explorer Quick Access / taskbar Jump List) — the Windows
    // analog of the Mac side's NSDocumentController.noteNewRecentDocumentURL.
    const uint SHARD_PATHW = 0x3;
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern void SHAddToRecentDocs(uint uFlags, string pv);

    List<string> RecentPaths()
    {
        try { return JsonSerializer.Deserialize<List<string>>(Settings.Get(RecentsKey) ?? "[]") ?? new List<string>(); }
        catch { return new List<string>(); }
    }

    public void NoteRecent(string path)
    {
        var list = RecentPaths();
        // Compare canonically (resolves junctions/symlinks and fixes case) so
        // one file reached by two path spellings doesn't get two entries —
        // matches the Mac canonicalPath dedupe.
        string key;
        try { key = ViewerTab.CanonicalLocalPath(path); } catch { key = path; }
        list.RemoveAll(p =>
        {
            string other;
            try { other = ViewerTab.CanonicalLocalPath(p); } catch { other = p; }
            return string.Equals(other, key, StringComparison.OrdinalIgnoreCase);
        });
        list.Insert(0, path);
        if (list.Count > RecentsMax) list = list.Take(RecentsMax).ToList();
        Settings.Set(RecentsKey, JsonSerializer.Serialize(list));
        try { SHAddToRecentDocs(SHARD_PATHW, path); } catch { /* shell recents are best-effort */ }
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
        clear.Click += (_, __) =>
        {
            Settings.Set(RecentsKey, "[]");
            // null path = clear the shell's usage data too (matches the Mac
            // side's clearRecentDocuments).
            try { SHAddToRecentDocs(SHARD_PATHW, null); } catch { }
        };
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
        SaveWindowPlacement();
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
        // Collect every Save / Don't Save / Cancel answer FIRST, then perform
        // the saves — matching the Mac quit flow, where a Cancel on any
        // document aborts the exit before a single write has happened.
        var toSave = new List<ViewerTab>();
        foreach (var tab in dirty)
        {
            tabs.SelectedTab = tab;
            var r = MessageBox.Show(
                $"Do you want to save the changes you made to “{tab.DisplayName}”?\n\nYour changes will be lost if you don't save them.",
                "Unsaved Changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            // Aborting the exit un-freezes session tracking (see OnFormClosing).
            if (r == DialogResult.Cancel) { isExiting = false; return; }
            if (r == DialogResult.Yes) toSave.Add(tab);
        }
        foreach (var tab in toSave)
        {
            tabs.SelectedTab = tab;
            // A cancelled save dialog (Untitled) or failed write aborts the exit.
            if (!await tab.SaveAsync()) { isExiting = false; return; }
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
            // Mirrors the Mac open panel, whose plain-text UTType conformance
            // pulls in *.txt — spell it out here, Windows filters are literal.
            Filter = "Markdown files|*.md;*.markdown;*.mdown;*.mkd;*.mkdn;*.mdwn;*.markdn;*.mdtxt;*.text;*.txt;*.rmd;*.qmd;*.mdx;*.mdc|All files|*.*",
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
        // Non-modal, like the Mac About window: you can keep reading a document
        // while the bundled docs are open. Disposes itself on close.
        var about = new AboutForm(this);
        about.FormClosed += (_, __) => about.Dispose();
        about.Show(this);
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
        // Undo/Redo/Cut/Copy/Paste/Delete/Select All carry *display-only*
        // shortcut labels: WebView2's browser accelerators already handle those
        // keys inside the textarea, and registering them here would intercept
        // the key before the page saw it. See ViewerTab's edit-command block.
        var edit = new ToolStripMenuItem("&Edit");
        edit.DropDownItems.Add(DisplayItem("Undo", "Ctrl+Z", (_, __) => Current?.EditUndo()));
        edit.DropDownItems.Add(DisplayItem("Redo", "Ctrl+Y", (_, __) => Current?.EditRedo()));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(DisplayItem("Cut", "Ctrl+X", (_, __) => Current?.EditCut()));
        edit.DropDownItems.Add(DisplayItem("Copy", "Ctrl+C", (_, __) => Current?.EditCopy()));
        edit.DropDownItems.Add(DisplayItem("Paste", "Ctrl+V", (_, __) => Current?.EditPaste()));
        edit.DropDownItems.Add(DisplayItem("Delete", "", (_, __) => Current?.EditDelete()));
        edit.DropDownItems.Add(DisplayItem("Select All", "Ctrl+A", (_, __) => Current?.EditSelectAll()));
        edit.DropDownItems.Add(new ToolStripSeparator());
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

    /// A menu item that *shows* a shortcut without registering it, for keys the
    /// WebView already handles natively (see the Edit menu).
    static ToolStripMenuItem DisplayItem(string text, string shortcut, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(text, null, onClick);
        if (shortcut.Length > 0)
        {
            item.ShortcutKeyDisplayString = shortcut;
            item.ShowShortcutKeys = true;
        }
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

        // The window stays open after a doc button — matching the Mac About
        // window, which is reused across clicks rather than closed.
        var readme = new Button { Text = "Read Me", Location = new Point(20, 216), Width = 110 };
        readme.Click += (_, __) => host.OpenBundledDoc("README.md");
        var changelog = new Button { Text = "Changelog", Location = new Point(140, 216), Width = 110 };
        changelog.Click += (_, __) => host.OpenBundledDoc("CHANGELOG.md");
        var arch = new Button { Text = "Architecture", Location = new Point(260, 216), Width = 110 };
        arch.Click += (_, __) => host.OpenBundledDoc("ARCHITECTURE.md");
        var design = new Button { Text = "Design", Location = new Point(380, 216), Width = 110 };
        design.Click += (_, __) => host.OpenBundledDoc("DESIGN.md");

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
        var t = envTask;
        // Never cache a failed attempt — a transient failure would otherwise
        // poison every tab opened for the rest of the session.
        if (t == null || t.IsFaulted || t.IsCanceled)
            envTask = t = CoreWebView2Environment.CreateAsync(null,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "MarkdownViewer", "WebView2"));
        return t;
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
        Application.Run(new MainForm(args, isFirst));
    }
}
