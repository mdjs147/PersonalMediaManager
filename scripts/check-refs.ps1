<#
.SYNOPSIS
    校验 src/ 项目之间的引用关系是否符合 CLAUDE.md §十二 强制单向依赖。

.DESCRIPTION
    违规即非零退出，供 CI（Azure Pipelines RedlineScan stage）阻断。

    强制单向依赖图（任何反向 / 跨层引用都是违规）：
        Launcher → Host
        Host → Application + Infrastructure.{Persistence, External, Platform}
        Infrastructure.Persistence → Application + Domain
        Infrastructure.External    → Application
        Infrastructure.Platform    → Application
        Application → Domain
        Domain → (无)

    豁免（不计入 .NET 矩阵）：
        Host → PersonalMediaManager.Frontend（.esproj）
        依据 CLAUDE.md §十二：Frontend 是 JS 工程（Microsoft.VisualStudio.JavaScript.SDK），不计入
        7 .NET 项目矩阵；Host 对它的 ProjectReference（BuildReference=false）是钦定的前端构建集成
        设计（vite 构建由 Host._PmmBuildFrontend 显式触发）。豁免走显式 allowlist（$esprojAllowed），
        其余项目引用 .esproj 仍会被检出。

.NOTES
    本脚本不依赖 dotnet CLI 解析 — 用 XML 直接读 .csproj 的 ProjectReference 节点，避免 SDK 版本差异。
#>

[CmdletBinding()]
param(
    [string]$SrcRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# $PSScriptRoot 在脚本主体内一定有值；放 param default 上 PS 5.1 偶发空字符串
if ([string]::IsNullOrEmpty($SrcRoot)) {
    $SrcRoot = Join-Path $PSScriptRoot '..\src'
}

# 允许的引用矩阵：键=项目短名，值=允许引用的项目短名集合
$allowed = @{
    'PersonalMediaManager.Domain'                       = @()
    'PersonalMediaManager.Application'                  = @('PersonalMediaManager.Domain')
    'PersonalMediaManager.Infrastructure.Persistence'   = @('PersonalMediaManager.Application', 'PersonalMediaManager.Domain')
    'PersonalMediaManager.Infrastructure.External'      = @('PersonalMediaManager.Application')
    'PersonalMediaManager.Infrastructure.Platform'      = @('PersonalMediaManager.Application')
    'PersonalMediaManager.Host'                         = @(
        'PersonalMediaManager.Application',
        'PersonalMediaManager.Infrastructure.Persistence',
        'PersonalMediaManager.Infrastructure.External',
        'PersonalMediaManager.Infrastructure.Platform'
    )
    'PersonalMediaManager.Launcher'                     = @('PersonalMediaManager.Host')
}

# .esproj（JS 工程）引用豁免清单：键=引用方项目短名，值=允许引用的 .esproj 短名集合。
# 依据 CLAUDE.md §十二：Host → Frontend 的 ProjectReference（BuildReference=false）是钦定的前端
# 构建集成设计（Host._PmmBuildFrontend 显式 <MSBuild> 触发 vite 构建），Frontend 不计入 .NET 矩阵。
# 刻意用显式 allowlist 而非「目标是 .esproj 一律跳过」：防止其它项目（如 Domain / Application）
# 私引 JS 工程时被静默放行，保持红线扫描 deny-by-default。
$esprojAllowed = @{
    'PersonalMediaManager.Host' = @('PersonalMediaManager.Frontend')
}

$violations = New-Object System.Collections.Generic.List[string]

foreach ($projDir in Get-ChildItem -Path $SrcRoot -Directory) {
    $csprojFile = Get-ChildItem -Path $projDir.FullName -Filter '*.csproj' | Select-Object -First 1
    if ($null -eq $csprojFile) {
        continue
    }

    $projName = [System.IO.Path]::GetFileNameWithoutExtension($csprojFile.Name)
    if (-not $allowed.ContainsKey($projName)) {
        $violations.Add("[未知项目] src/$projName 不在引用矩阵中；如确为新增项目，请同步更新 scripts/check-refs.ps1 的 `$allowed 字典")
        continue
    }

    [xml]$xml = Get-Content -LiteralPath $csprojFile.FullName -Raw

    # PS XML：多个 ItemGroup 是数组，单个是对象；Strict mode 下需先确认属性存在
    if (-not $xml.Project.PSObject.Properties['ItemGroup']) { continue }
    $itemGroups = @($xml.Project.ItemGroup)
    foreach ($itemGroup in $itemGroups) {
        if ($null -eq $itemGroup) { continue }
        if (-not $itemGroup.PSObject.Properties['ProjectReference']) { continue }

        $refs = @($itemGroup.ProjectReference)
        foreach ($ref in $refs) {
            if ($null -eq $ref -or [string]::IsNullOrEmpty($ref.Include)) { continue }
            $refName = [System.IO.Path]::GetFileNameWithoutExtension($ref.Include)

            # JS 工程（.esproj）引用不进 .NET 矩阵，单独走豁免清单（依据见 $esprojAllowed 注释）；
            # 同时校验扩展名确为 .esproj —— 若有人建同名 .csproj 冒充 Frontend，仍落入矩阵被检出
            if ([System.IO.Path]::GetExtension($ref.Include) -eq '.esproj') {
                if ($esprojAllowed.ContainsKey($projName) -and $esprojAllowed[$projName] -contains $refName) {
                    continue
                }
                $violations.Add("[非法 .esproj 引用] $projName -> $refName（不在 `$esprojAllowed 豁免清单内；如确为钦定集成，请同步更新 scripts/check-refs.ps1 并在 CLAUDE.md §十二 记录依据）")
                continue
            }

            # Host 直接引 Domain（CLAUDE.md §0.5 红线之一）
            if ($projName -eq 'PersonalMediaManager.Host' -and $refName -eq 'PersonalMediaManager.Domain') {
                $violations.Add("[Host 直引 Domain] $projName -> $refName（违反 §0.5 红线；必须经 Application 编排）")
                continue
            }

            if ($allowed[$projName] -notcontains $refName) {
                $allowedJoined = ($allowed[$projName] -join ', ')
                $violations.Add("[非法引用] $projName -> $refName（允许集合：$allowedJoined）")
            }
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host '=== check-refs.ps1 失败 ===' -ForegroundColor Red
    $violations | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host '=== check-refs.ps1 通过：所有项目引用符合单向依赖矩阵 ===' -ForegroundColor Green
exit 0
