# Builds MarkdownViewer for Windows.
#   .\build.ps1            -> Release build in bin\Release\net8.0-windows
#   .\build.ps1 -Publish   -> self-contained single-folder publish in .\dist
# Requires the .NET 8 SDK (winget install Microsoft.DotNet.SDK.8), Python 3,
# and internet on the first build (to restore WebView2, cached afterward).
param([switch]$Publish)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$root = Split-Path $PSScriptRoot -Parent

# Keep the generated Windows page in lockstep with the canonical Mac template.
$regen = Join-Path $PSScriptRoot 'regen-template.py'
if (Get-Command python3 -ErrorAction SilentlyContinue) { & python3 $regen }
elseif (Get-Command py -ErrorAction SilentlyContinue) { & py -3 $regen }
elseif (Get-Command python -ErrorAction SilentlyContinue) { & python $regen }
else { throw 'Python 3 is required to regenerate windows\Resources\template.html.' }
if ($LASTEXITCODE -ne 0) { throw 'Windows template regeneration failed.' }

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
    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $dist
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
