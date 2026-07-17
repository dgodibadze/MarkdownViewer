#!/usr/bin/env python3
"""Regenerate windows/Resources/template.html from the Mac Resources/template.html.

The two templates are identical except for a small fixed delta (message bridge,
font stacks, shortcut labels, an in-page app-shortcut block, and the __escape
hook). NEVER hand-edit the Windows template — edit the Mac one and re-run:

    python3 windows/regen-template.py

Every substitution asserts exactly one occurrence, so a Mac-template refactor
that breaks an anchor fails loudly here instead of silently drifting.
"""
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
SRC = ROOT / "Resources" / "template.html"
DST = ROOT / "windows" / "Resources" / "template.html"

src = SRC.read_text()


def sub(old, new):
    global src
    n = src.count(old)
    assert n == 1, f"expected exactly 1 occurrence, got {n}: {old[:70]!r}"
    src = src.replace(old, new)


# ---- Font stacks -----------------------------------------------------------
sub('font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", "Helvetica Neue", Arial, sans-serif;',
    'font-family: "Segoe UI", -apple-system, BlinkMacSystemFont, "Helvetica Neue", Arial, sans-serif;')
sub('font-family: ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, monospace;',
    'font-family: Consolas, "Cascadia Mono", ui-monospace, Menlo, monospace;')
sub('font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Arial, sans-serif;',
    'font-family: "Segoe UI", -apple-system, Arial, sans-serif;')

# ---- Shortcut labels / comments --------------------------------------------
sub('title="Previous (⇧⏎)"', 'title="Previous (Shift+Enter)"')
sub('title="Next (⏎)"', 'title="Next (Enter)"')
sub('/* ---- Document zoom (Cmd +/-, Cmd 0 to reset). Scales preview + editor. ---- */',
    '/* ---- Document zoom (Ctrl +/-, Ctrl 0 to reset). Scales preview + editor. ---- */')
sub('  // ---- Document zoom: Cmd/Ctrl +  /  Cmd/Ctrl -  /  Cmd/Ctrl 0 (reset) ----',
    '  // ---- Document zoom: Ctrl +  /  Ctrl -  /  Ctrl 0 (reset) ----')
sub('  // Called by Swift after a successful disk write.',
    '  // Called by the native side after a successful disk write.')
sub('  // Called by Swift right before a disk write so menu/close-triggered saves',
    '  // Called by the native side right before a disk write so menu/close saves')
sub("    // Keep Swift's cached copy current for menu/close-triggered saves.",
    "    // Keep the native cached copy current for menu/close-triggered saves.")

# ---- Bridge: WebView2 (chrome.webview) first, WKWebView fallback ------------
sub("""  // ---- Swift bridge (no-ops gracefully if not present) ----
  function bridge(msg) {
    try {
      if (window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.bridge) {
        window.webkit.messageHandlers.bridge.postMessage(msg);
      }
    } catch (e) {}
  }""",
    """  // ---- Native bridge: WebView2 (chrome.webview) first, WKWebView as fallback
  // so the template stays portable between the Windows and macOS apps. ----
  function bridge(msg) {
    try {
      if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
        window.chrome.webview.postMessage(msg);
        return;
      }
      if (window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.bridge) {
        window.webkit.messageHandlers.bridge.postMessage(msg);
      }
    } catch (e) {}
  }""")

# ---- In-page app shortcuts (WinForms accelerators can be swallowed) ---------
sub("""  // Cmd/Ctrl+S saves. Shift excluded: ⇧⌘S / Ctrl+Shift+S is Save As, which
  // is handled natively (macOS menu) or by the in-page shortcut block (Windows).
  document.addEventListener('keydown', function (e) {
    if ((e.metaKey || e.ctrlKey) && !e.shiftKey && (e.key === 's' || e.key === 'S')) {
      e.preventDefault();
      save();
    }
  });""",
    """  // Ctrl+S saves. Shift excluded: Ctrl+Shift+S is Save As, routed to the
  // native side by the in-page shortcut block below.
  document.addEventListener('keydown', function (e) {
    if ((e.metaKey || e.ctrlKey) && !e.shiftKey && (e.key === 's' || e.key === 'S')) {
      e.preventDefault();
      save();
    }
  });

  // ---- App shortcuts (handled in-page; native accelerators can be swallowed
  // by the WebView, so everything routes through here or the bridge) ----
  document.addEventListener('keydown', function (e) {
    if (e.key === 'F5') { e.preventDefault(); bridge({ action: 'reload' }); return; }
    // Swallow browser-chrome accelerators that make no sense here.
    if (e.key === 'F3' || e.key === 'F7' || e.key === 'F11') { e.preventDefault(); return; }
    if (!(e.ctrlKey || e.metaKey)) return;
    var k = (e.key || '').toLowerCase();
    if (k === '1') { e.preventDefault(); setMode('preview'); }
    else if (k === '2') { e.preventDefault(); setMode('edit'); }
    else if (k === '3') { e.preventDefault(); setMode('split'); }
    else if (k === 's' && e.shiftKey) { e.preventDefault(); bridge({ action: 'saveAs' }); }
    else if (k === 'n' && !e.shiftKey) { e.preventDefault(); bridge({ action: 'newFile' }); }
    else if (k === 'o' && !e.shiftKey) { e.preventDefault(); bridge({ action: 'open' }); }
    else if (k === 'g' && e.shiftKey) { e.preventDefault(); bridge({ action: 'openPath' }); }
    else if (k === 'w') { e.preventDefault(); bridge({ action: 'closeTab' }); }
    else if (k === 'r') { e.preventDefault(); bridge({ action: 'reload' }); }
    else if (k === 'h') { e.preventDefault(); window.__findReplace(); }
    else if (k === 't') { e.preventDefault(); window.__toggleTOC && window.__toggleTOC(); }
    else if (k === 'p') { e.preventDefault(); bridge({ action: 'print' }); }
    else if (k === 'j' || k === 'u') { e.preventDefault(); }   // downloads / view-source
  });""")

# ---- __escape hook (Escape never reaches the page in WinForms) --------------
sub("""    function close() { bar.hidden = true; backdrop.innerHTML = ''; editor.focus(); }""",
    """    function close() { bar.hidden = true; backdrop.innerHTML = ''; editor.focus(); }
    // Escape is a WinForms dialog key: the WebView host receives it instead of
    // the page, so the native side calls this hook to close the find bar.
    window.__escape = function () { if (!bar.hidden) close(); };""")

DST.write_text(src)
print(f"regenerated {DST} ({len(src)} bytes)")
