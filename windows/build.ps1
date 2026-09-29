# Builds MarkdownViewer for Windows.
#   .\build.ps1            -> Release build in bin\Release\net8.0-windows
#   .\build.ps1 -Publish   -> self-contained single-folder publish in .\dist
# Requires the .NET 8 SDK (winget install Microsoft.DotNet.SDK.8) and internet
# on the first build (to restore WebView2, cached afterward). Python 3 is
# optional: it only re-syncs the generated template, which is committed.
param([switch]$Publish)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$root = Split-Path $PSScriptRoot -Parent

# Keep the generated Windows page in lockstep with the canonical Mac template.
$regen = Join-Path $PSScriptRoot 'regen-template.py'
$python = $null
foreach ($cand in @(@('python3'), @('py', '-3'), @('python'))) {
    if (-not (Get-Command $cand[0] -ErrorAction SilentlyContinue)) { continue }
    # The Microsoft Store "python3" stub exists on PATH but fails to run.
    $extra = @($cand | Select-Object -Skip 1)
    & $cand[0] @extra -c 'import sys; sys.exit(0 if sys.version_info[0] == 3 else 1)' 2>$null
    if ($LASTEXITCODE -eq 0) { $python = $cand; break }
}
if ($python) {
    $extra = @($python | Select-Object -Skip 1)
    & $python[0] @extra $regen
    if ($LASTEXITCODE -ne 0) { throw 'Windows template regeneration failed.' }
} else {
    Write-Host 'Python 3 not found; using the committed windows\Resources\template.html (CI keeps it in sync).'
}

# Refuse to build with modified/corrupt vendored renderer assets.
$manifest = Join-Path $root 'Resources\SHA256SUMS'
foreach ($line in Get-Content $manifest) {
    if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) { continue }
    if ($line -notmatch '^([0-9a-fA-F]{64})\s+\*?(.+)$') { throw "Malformed checksum line: $line" }
    $expected = $Matches[1]
    $relative = $Matches[2].Replace([char]'/', [IO.Path]::DirectorySeparatorChar)
    $assetPath = Join-Path $root $relative
    if (-not (Test-Path $assetPath -PathType Leaf)) { throw "Missing renderer asset: $relative" }
    $actual = (Get-FileHash $assetPath -Algorithm SHA256).Hash
    if ($actual -ne $expected) { throw "Renderer asset checksum mismatch: $relative" }
}

if ($Publish) {
    $dist = Join-Path $PSScriptRoot 'dist'
    if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o $dist
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
    $zip = Join-Path $PSScriptRoot 'MarkdownViewer-windows-x64.zip'
    Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zip -Force
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256" -Encoding ascii
    Write-Host "`nPublished to $PSScriptRoot\dist\MarkdownViewer.exe"
    Write-Host "Release archive: $zip"
    Write-Host "Checksum: $zip.sha256"
} else {
    dotnet build -c Release
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
    Write-Host "`nBuilt: $PSScriptRoot\bin\Release\net8.0-windows\MarkdownViewer.exe"
}
