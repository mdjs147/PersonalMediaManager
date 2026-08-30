<#
.SYNOPSIS
    Verify Launcher.csproj does not pull in cross-platform UI frameworks forbidden by CLAUDE.md SecVIII.

.DESCRIPTION
    Forbidden packages:
      - Eto.Forms / Eto.Platform.*
      - Avalonia / Avalonia.*
      - Microsoft.WindowsAppSDK
      - Microsoft.Maui / Microsoft.Maui.*

    Launcher must use Windows native (WinForms NotifyIcon); macOS support removed 2026-06-11.
    Scans src/PersonalMediaManager.Launcher for PackageReference in .csproj and using directives in .cs;
    any forbidden package/namespace match returns non-zero exit.
#>

[CmdletBinding()]
param(
    [string]$LauncherDir = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrEmpty($LauncherDir)) {
    $LauncherDir = Join-Path $PSScriptRoot '..\src\PersonalMediaManager.Launcher'
}

$forbiddenPackages = @(
    'Eto.Forms',
    'Eto.Platform',
    'Avalonia',
    'Microsoft.WindowsAppSDK',
    'Microsoft.Maui'
)

$violations = New-Object System.Collections.Generic.List[string]

# Scan .csproj PackageReference
foreach ($csproj in Get-ChildItem -Path $LauncherDir -Filter '*.csproj' -Recurse) {
    [xml]$xml = Get-Content -LiteralPath $csproj.FullName -Raw
    if (-not $xml.Project.PSObject.Properties['ItemGroup']) { continue }
    foreach ($itemGroup in @($xml.Project.ItemGroup)) {
        if ($null -eq $itemGroup) { continue }
        if (-not $itemGroup.PSObject.Properties['PackageReference']) { continue }
        foreach ($pkg in @($itemGroup.PackageReference)) {
            if ($null -eq $pkg -or [string]::IsNullOrEmpty($pkg.Include)) { continue }
            foreach ($forbidden in $forbiddenPackages) {
                if ($pkg.Include -eq $forbidden -or $pkg.Include.StartsWith("$forbidden.")) {
                    $rel = $csproj.FullName.Substring($LauncherDir.Length).TrimStart('\','/')
                    $violations.Add("[Launcher redline] $rel uses forbidden package: $($pkg.Include) (CLAUDE.md SecVIII)")
                }
            }
        }
    }
}

# Scan *.cs using directives (defense in depth against direct namespace import via transitive deps)
$forbiddenNamespaces = @(
    'Eto.Forms',
    'Avalonia.',
    'Microsoft.UI.',
    'Microsoft.Maui'
)
foreach ($cs in Get-ChildItem -Path $LauncherDir -Filter '*.cs' -Recurse | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) {
    $content = Get-Content -LiteralPath $cs.FullName -Raw
    foreach ($ns in $forbiddenNamespaces) {
        if ($content -match "using\s+$([regex]::Escape($ns))") {
            $rel = $cs.FullName.Substring($LauncherDir.Length).TrimStart('\','/')
            $violations.Add("[Launcher redline] $rel using forbidden namespace: $ns (CLAUDE.md SecVIII)")
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host '=== check-redlines.ps1 FAILED ===' -ForegroundColor Red
    $violations | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host '=== check-redlines.ps1 PASSED: Launcher does not introduce forbidden cross-platform UI frameworks ===' -ForegroundColor Green
exit 0
