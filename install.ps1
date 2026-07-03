# MarkdownViewer one-line installer for Windows.
#   irm https://raw.githubusercontent.com/dgodibadze/MarkdownViewer/main/install.ps1 | iex
# Downloads the latest self-contained release, installs it to
# %LOCALAPPDATA%\Programs\MarkdownViewer, ensures the WebView2 Runtime is
# present, and creates a Start Menu shortcut. No admin rights needed.
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repo = 'dgodibadze/MarkdownViewer'
Write-Host "Installing MarkdownViewer…"

$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest"
$asset = $release.assets | Where-Object { $_.name -match 'windows.*\.zip$' } | Select-Object -First 1
if (-not $asset) {
    throw "The latest release has no Windows .zip asset. Build from source instead: clone the repo and run windows\build.ps1 (needs the .NET 8 SDK)."
}

$dest = Join-Path $env:LOCALAPPDATA 'Programs\MarkdownViewer'
$zip = Join-Path $env:TEMP 'MarkdownViewer-windows.zip'
Write-Host "Downloading $($asset.name) ($([math]::Round($asset.size / 1MB)) MB)…"
Invoke-WebRequest $asset.browser_download_url -OutFile $zip

# Stop a running copy so files can be replaced on upgrade.
Get-Process MarkdownViewer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
Expand-Archive $zip $dest -Force
Remove-Item $zip -ErrorAction SilentlyContinue

# WebView2 Runtime (preinstalled on Windows 11; install silently if missing).
$wvKeys = @(
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
    'HKCU:\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
)
$hasWebView2 = $false
foreach ($k in $wvKeys) {
    try { if ((Get-ItemProperty $k -ErrorAction Stop).pv) { $hasWebView2 = $true } } catch {}
}
if (-not $hasWebView2) {
    Write-Host "Installing the WebView2 Runtime (one-time)…"
    $wvSetup = Join-Path $env:TEMP 'MicrosoftEdgeWebView2Setup.exe'
    Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $wvSetup
    Start-Process $wvSetup -ArgumentList '/silent', '/install' -Wait
    Remove-Item $wvSetup -ErrorAction SilentlyContinue
}

# Start Menu shortcut.
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'MarkdownViewer.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = Join-Path $dest 'MarkdownViewer.exe'
$link.WorkingDirectory = $dest
$link.Description = 'MarkdownViewer'
$link.Save()

Write-Host ""
Write-Host "✓ Installed to $dest"
Write-Host "  Launch it from the Start Menu (MarkdownViewer), or associate .md files:"
Write-Host "  right-click a .md file -> Open with -> Choose another app -> MarkdownViewer.exe"
