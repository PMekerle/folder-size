# Build Folder Size as a single-file, self-contained portable Windows .exe.
# Output: <repo>\publish\FolderSize.exe  (no .NET install required to run)
#
# Usage:
#   .\scripts\build-portable.ps1            # x64 (default)
#   .\scripts\build-portable.ps1 -Arch arm64
#   .\scripts\build-portable.ps1 -Open      # also reveal output in Explorer

param(
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64',
    [switch]$Open
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot 'FolderSize'
$publishDir = Join-Path $repoRoot 'publish'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK is not on PATH. Install .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0"
}

if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}
New-Item -ItemType Directory -Path $publishDir | Out-Null

$rid = "win-$Arch"
Write-Host "Building Folder Size  ($rid, self-contained, single file)" -ForegroundColor Cyan
Write-Host "  Project : $projectDir"
Write-Host "  Output  : $publishDir"

dotnet publish "$projectDir\FolderSize.csproj" `
    -c Release `
    -r $rid `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -p:PublishReadyToRun=true `
    -o "$publishDir" `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

$exe = Join-Path $publishDir 'FolderSize.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe but it wasn't produced." }

$bytes = (Get-Item $exe).Length
$mb    = [math]::Round($bytes / 1MB, 1)
Write-Host ""
Write-Host "OK  ->  $exe  ($mb MB)" -ForegroundColor Green
Write-Host "    Self-contained: no .NET install needed on the target machine."

if ($Open) {
    Start-Process explorer.exe "/select,`"$exe`""
}
