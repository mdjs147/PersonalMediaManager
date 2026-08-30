<#
.SYNOPSIS
    Windows Release packaging script (E4.1).

.DESCRIPTION
    Pipeline:
      1) Build frontend: npm install + npm run build:host (vite -> dist -> copy to Host wwwroot)
      2) Publish Launcher: dotnet publish -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true
      3) Output: dist/win/PersonalMediaManager.exe

    Run from repo root. Designed to be called by Azure Pipelines on Windows agent (pmm-win) and by devs locally.

.PARAMETER Configuration
    Release | Debug. Default Release.

.PARAMETER OutputDir
    Output directory for the packaged exe. Default dist/win.
#>

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDir = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
if ([string]::IsNullOrEmpty($OutputDir)) {
    $OutputDir = Join-Path $repoRoot 'dist\win'
}

Write-Host '=== [1/3] Frontend build ===' -ForegroundColor Cyan
$frontendDir = Join-Path $repoRoot 'src\PersonalMediaManager.Frontend'
Push-Location $frontendDir
try {
    if (-not (Test-Path 'node_modules')) {
        Write-Host '  npm install...' -ForegroundColor Gray
        & npm install --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'npm install failed' }
    }
    Write-Host '  npm run build:host...' -ForegroundColor Gray
    & npm run build:host
    if ($LASTEXITCODE -ne 0) { throw 'npm run build:host failed' }
} finally {
    Pop-Location
}

Write-Host '=== [2/3] Launcher publish (win-x64, single file, self-contained) ===' -ForegroundColor Cyan
$launcherCsproj = Join-Path $repoRoot 'src\PersonalMediaManager.Launcher\PersonalMediaManager.Launcher.csproj'
$publishDir = Join-Path $repoRoot 'src\PersonalMediaManager.Launcher\bin\Release\net10.0-windows\win-x64\publish'
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}

& dotnet publish $launcherCsproj `
    -c $Configuration `
    -f net10.0-windows `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=embedded
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Write-Host '=== [3/3] Copy artifacts ===' -ForegroundColor Cyan
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}
$exe = Join-Path $publishDir 'PersonalMediaManager.exe'
if (-not (Test-Path $exe)) {
    throw "Expected exe not found: $exe"
}
Copy-Item $exe -Destination $OutputDir -Force

# appsettings.json 与前端 wwwroot 已嵌入 Host 程序集（随单文件 exe 内置），无需再外置拷贝 —— 产物就是单一 exe

Write-Host ("=== PASSED: " + (Join-Path $OutputDir 'PersonalMediaManager.exe') + " ===") -ForegroundColor Green
exit 0
