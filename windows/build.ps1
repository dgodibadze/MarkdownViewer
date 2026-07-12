# Builds MarkdownViewer for Windows.
#   .\build.ps1            -> Release build in bin\Release\net8.0-windows
#   .\build.ps1 -Publish   -> self-contained single-folder publish in .\dist
# Requires the .NET 8 SDK (winget install Microsoft.DotNet.SDK.8) and internet
# on the first build (to restore the WebView2 NuGet package, cached afterward).
param([switch]$Publish)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if ($Publish) {
    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
    Write-Host "`nPublished to $PSScriptRoot\dist\MarkdownViewer.exe"
} else {
    dotnet build -c Release
    Write-Host "`nBuilt: $PSScriptRoot\bin\Release\net8.0-windows\MarkdownViewer.exe"
}
