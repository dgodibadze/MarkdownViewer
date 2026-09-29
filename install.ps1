# MarkdownViewer one-line installer for Windows.
#   irm https://raw.githubusercontent.com/dgodibadze/MarkdownViewer/main/install.ps1 | iex
#   $env:MARKDOWNVIEWER_REF='dev'; irm https://raw.githubusercontent.com/dgodibadze/MarkdownViewer/dev/install.ps1 | iex
# Downloads the latest self-contained release (or, if none is published,
# builds `main` from source), installs it to
# %LOCALAPPDATA%\Programs\MarkdownViewer, ensures the WebView2 Runtime is
# present, and creates a Start Menu shortcut. No admin rights needed.
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repo = 'dgodibadze/MarkdownViewer'
$installRef = $env:MARKDOWNVIEWER_REF
if (-not $installRef) { $installRef = $env:MARKDOWNVIEWER_BRANCH }
$installFromSource = -not [string]::IsNullOrWhiteSpace($installRef)
if ($installFromSource) {
    if ($installRef.StartsWith('-') -or $installRef -notmatch '^[A-Za-z0-9._/-]+$') {
        throw "Unsafe branch/ref name: $installRef"
    }
    Write-Host "Installing MarkdownViewer from source ref '$installRef'..."
} else {
    Write-Host "Installing MarkdownViewer..."
}

# Native architecture (a 32-bit PowerShell on 64-bit Windows reports x86 here).
$osArch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
$arch = if ($osArch -eq 'ARM64') { 'arm64' } else { 'x64' }

$dest = Join-Path $env:LOCALAPPDATA 'Programs\MarkdownViewer'
$downloadId = [Guid]::NewGuid().ToString('N')
$zip = $null
$sourceRoot = $null
$srcExtract = $null

if (-not $installFromSource) {
    $release = $null
    try {
        $release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest"
    } catch {}
    $asset = $null
    if ($release) {
        $asset = $release.assets | Where-Object { $_.name -match "windows-$arch\.zip$" } | Select-Object -First 1
        if (-not $asset -and $arch -eq 'arm64') {
            Write-Host 'No native ARM64 build in this release; using the x64 build (runs under emulation).'
            $asset = $release.assets | Where-Object { $_.name -match 'windows-x64\.zip$' } | Select-Object -First 1
        }
    }
    if (-not $asset) {
        Write-Host "No published Windows release found for $repo; building 'main' from source instead."
        $installRef = 'main'
        $installFromSource = $true
    }
}

if (-not $installFromSource) {
    $zip = Join-Path $env:TEMP "MarkdownViewer-windows-$downloadId.zip"
    Write-Host "Downloading $($asset.name) ($([math]::Round($asset.size / 1MB)) MB)..."
    Invoke-WebRequest $asset.browser_download_url -OutFile $zip
    $expectedHash = $null
    if ($asset.digest -and $asset.digest -match '^sha256:([0-9a-fA-F]{64})$') {
        $expectedHash = $Matches[1]
    } else {
        $sumAsset = $release.assets | Where-Object { $_.name -eq "$($asset.name).sha256" } | Select-Object -First 1
        if ($sumAsset) {
            $sumPath = "$zip.sha256"
            Invoke-WebRequest $sumAsset.browser_download_url -OutFile $sumPath
            $expectedHash = ((Get-Content $sumPath -Raw).Trim() -split '\s+')[0]
            Remove-Item $sumPath -ErrorAction SilentlyContinue
        }
    }
    if (-not $expectedHash) {
        Remove-Item $zip -ErrorAction SilentlyContinue
        throw 'Release ZIP has no SHA-256 digest; refusing an unverified install.'
    }
    if ($expectedHash -notmatch '^[0-9a-fA-F]{64}$') {
        Remove-Item $zip -ErrorAction SilentlyContinue
        throw 'Release ZIP SHA-256 digest is malformed.'
    }
    $actualHash = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) {
        Remove-Item $zip -ErrorAction SilentlyContinue
        throw 'Release ZIP checksum verification failed.'
    }
} else {
    # .NET 8 SDK (installed per-machine via winget if missing). Git is not needed.
    function Test-Sdk8 {
        $dn = Get-Command dotnet -ErrorAction SilentlyContinue
        if (-not $dn) { return $false }
        return [bool](& dotnet --list-sdks 2>$null | Where-Object { $_ -match '^8\.' })
    }
    if (-not (Test-Sdk8)) {
        if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
            throw 'The .NET 8 SDK is required to build from source and winget is not available. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and run this again.'
        }
        Write-Host 'Installing the .NET 8 SDK (one-time)...'
        winget install --id Microsoft.DotNet.SDK.8 -e --silent --accept-package-agreements --accept-source-agreements
        $env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"
        if (-not (Test-Sdk8)) { throw 'The .NET 8 SDK install did not complete. Install it manually, open a new PowerShell, and run this again.' }
    }

    $srcZip = Join-Path $env:TEMP "MarkdownViewer-source-$downloadId.zip"
    $srcExtract = Join-Path $env:TEMP "MarkdownViewer-source-$downloadId"
    Write-Host "Downloading source for '$installRef'..."
    Invoke-WebRequest "https://github.com/$repo/archive/$installRef.zip" -OutFile $srcZip
    Expand-Archive $srcZip $srcExtract -Force
    Remove-Item $srcZip -ErrorAction SilentlyContinue
    $sourceRoot = (Get-ChildItem $srcExtract -Directory | Select-Object -First 1).FullName
    if (-not $sourceRoot -or -not (Test-Path (Join-Path $sourceRoot 'windows\build.ps1'))) {
        throw "Downloaded source for '$installRef' does not contain windows\build.ps1."
    }
    # Building runs windows\build.ps1, a script file. Fail early with the fix
    # instead of a cryptic SecurityError after the SDK download.
    $policy = Get-ExecutionPolicy
    if ($policy -in 'Restricted', 'AllSigned') {
        throw "PowerShell's execution policy ($policy) blocks the build script. In this window run:`n  Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass`nthen paste the install command again (it only affects this window). Or skip building and use the release ZIP."
    }
    Push-Location (Join-Path $sourceRoot 'windows')
    try {
        & .\build.ps1 -Publish -Arch $arch
        if ($LASTEXITCODE -ne 0) { throw 'Windows source build failed.' }
    } finally {
        Pop-Location
    }
}

$destParent = Split-Path $dest -Parent
New-Item $destParent -ItemType Directory -Force | Out-Null
$swapId = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $destParent ".MarkdownViewer.stage.$swapId"
$backup = Join-Path $destParent ".MarkdownViewer.backup.$swapId"
$activated = $false
try {
    # Extract and validate the replacement while the existing app is untouched.
    if ($installFromSource) {
        $publishDir = Join-Path $sourceRoot 'windows\dist'
        if (-not (Test-Path $publishDir -PathType Container)) { throw 'Source build did not produce windows\dist.' }
        New-Item $stage -ItemType Directory -Force | Out-Null
        Copy-Item (Join-Path $publishDir '*') $stage -Recurse -Force
    } else {
        Expand-Archive $zip $stage -Force
    }
    $stagedExe = Join-Path $stage 'MarkdownViewer.exe'
    $stagedTemplate = Join-Path $stage 'Resources\template.html'
    if (-not (Test-Path $stagedExe -PathType Leaf) -or (Get-Item $stagedExe).Length -eq 0 -or
        -not (Test-Path $stagedTemplate -PathType Leaf)) {
        throw 'Release ZIP does not contain a valid MarkdownViewer application.'
    }

# Ask a running copy to close normally so its unsaved-change prompts run. Never
# force-kill it during an upgrade: that can discard open documents.
    $running = @(Get-Process MarkdownViewer -ErrorAction SilentlyContinue)
    foreach ($process in $running) { [void]$process.CloseMainWindow() }
    if ($running.Count -gt 0) {
        Write-Host 'Waiting for MarkdownViewer to close...'
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Process MarkdownViewer -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
        }
        if (Get-Process MarkdownViewer -ErrorAction SilentlyContinue) {
            throw 'MarkdownViewer is still open (possibly waiting for an unsaved-changes decision). Close it and run the installer again.'
        }
    }

    if (Test-Path $dest) { Move-Item -LiteralPath $dest -Destination $backup }
    try {
        Move-Item -LiteralPath $stage -Destination $dest
        $activated = $true
    } catch {
        if (-not (Test-Path $dest) -and (Test-Path $backup)) {
            Move-Item -LiteralPath $backup -Destination $dest
        }
        throw
    }
} finally {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
    if ($activated -and (Test-Path $backup)) { Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue }
    if ($zip) { Remove-Item $zip -ErrorAction SilentlyContinue }
    if ($srcExtract -and (Test-Path $srcExtract)) { Remove-Item $srcExtract -Recurse -Force -ErrorAction SilentlyContinue }
}

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
    Write-Host "Installing the WebView2 Runtime (one-time)..."
    $wvSetup = Join-Path $env:TEMP 'MicrosoftEdgeWebView2Setup.exe'
    Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $wvSetup
    try {
        $signature = Get-AuthenticodeSignature $wvSetup
        if ($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate -or
            $signature.SignerCertificate.Subject -notmatch '(?:CN|O)=Microsoft Corporation(?:,|$)') {
            throw 'WebView2 installer is not validly signed by Microsoft; refusing to run it.'
        }
        $wvProcess = Start-Process $wvSetup -ArgumentList '/silent', '/install' -Wait -PassThru
        if ($wvProcess.ExitCode -ne 0) { throw "WebView2 installer failed with exit code $($wvProcess.ExitCode)." }
    } finally {
        Remove-Item $wvSetup -ErrorAction SilentlyContinue
    }
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
Write-Host "Installed to $dest"
Write-Host "  Launch it from the Start Menu (MarkdownViewer), or associate .md files:"
Write-Host "  right-click a .md file -> Open with -> Choose another app -> MarkdownViewer.exe"
