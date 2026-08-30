<#
.SYNOPSIS
    校验所有 *Controller.cs 的 XML 文档注释符合 CLAUDE.md §五 「Controller / API 注释（Scalar UI 规范）」。

.DESCRIPTION
    五条强制规则（违反任一即非零退出，供 CI RedlineScan 阻断）：
      1. 每个 public Action 方法（标了 [HttpGet]/[HttpPost] 之一）必须有 <summary>
      2. <summary> 必须单行
      3. <summary> 去除标签和首尾空白后长度必须 ≤ 30 字符
      4. 必须有 <remarks>
      5. <remarks> 长度必须 > <summary>（否则等同把详情写在了 summary）

    本脚本走纯文本扫描（不依赖 Roslyn / dotnet CLI），用正则 + 行游标解析；
    Controller class 内的 [HttpXxx] 之上紧邻的 /// 注释块是检查目标。
#>

[CmdletBinding()]
param(
    [string]$SrcRoot = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrEmpty($SrcRoot)) {
    $SrcRoot = Join-Path $PSScriptRoot '..\src'
}

$violations = New-Object System.Collections.Generic.List[string]
$controllerFiles = Get-ChildItem -Path $SrcRoot -Filter '*Controller.cs' -Recurse -File

if ($controllerFiles.Count -eq 0) {
    Write-Host '=== check-xml-doc.ps1 跳过：未发现 Controller ===' -ForegroundColor Yellow
    exit 0
}

foreach ($file in $controllerFiles) {
    # 显式 UTF-8 读取：源码全部 UTF-8（多数无 BOM），PS 5.1 Get-Content 默认走本机代码页（zh-CN GBK）会乱码，
    # 导致 </summary> 字节被误解，正则匹配失败 → 误判 "缺 <summary>"。
    $lines = Get-Content -LiteralPath $file.FullName -Encoding UTF8

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i].TrimStart()
        # 命中 Action 属性（[HttpGet] / [HttpPost] / [HttpGet("...")] / [HttpPost("...")]）
        if ($line -notmatch '^\[\s*(HttpGet|HttpPost)\b') { continue }

        # 向上扫描连续的 /// 注释块
        $docLines = New-Object System.Collections.Generic.List[string]
        $j = $i - 1
        while ($j -ge 0) {
            $up = $lines[$j].TrimStart()
            if ($up.StartsWith('///')) {
                $docLines.Insert(0, $up.Substring(3).TrimStart())
                $j--
            } elseif ($up.StartsWith('[')) {
                # 中间夹着其他属性（如 [ProducesResponseType]），跳过继续向上
                $j--
            } elseif ([string]::IsNullOrWhiteSpace($up)) {
                $j--
            } else {
                break
            }
        }

        $relativePath = $file.FullName.Substring((Resolve-Path $SrcRoot).Path.Length).TrimStart('\', '/')
        $lineNo = $i + 1
        $tag = "${relativePath}:$lineNo"

        if ($docLines.Count -eq 0) {
            $violations.Add("[$tag] Action 缺 XML 注释（规则 1）")
            continue
        }

        $blockText = ($docLines -join "`n")

        # 1 + 2：抓 <summary>...</summary>（必须单行）
        $summaryMatch = [regex]::Match($blockText, '<summary>(?<inner>.*?)</summary>', 'Singleline')
        if (-not $summaryMatch.Success) {
            $violations.Add("[$tag] 缺 <summary>（规则 1）")
            continue
        }
        $summaryInner = $summaryMatch.Groups['inner'].Value
        if ($summaryInner -match "\r|\n") {
            $violations.Add("[$tag] <summary> 跨多行（规则 2，Scalar UI 要求单行）")
            continue
        }

        # 3：去标签 + trim 后 ≤ 30 字符
        $clean = $summaryInner -replace '<[^>]+>', ''
        $clean = $clean.Trim()
        if ($clean.Length -gt 30) {
            $violations.Add("[$tag] <summary> 长度 $($clean.Length) > 30（规则 3）：'$clean'")
        }

        # 4：必须有 <remarks>
        $remarksMatch = [regex]::Match($blockText, '<remarks>(?<inner>.*?)</remarks>', 'Singleline')
        if (-not $remarksMatch.Success) {
            $violations.Add("[$tag] 缺 <remarks>（规则 4）")
            continue
        }

        # 5：<remarks> 长度必须 > <summary>
        $remarksClean = ($remarksMatch.Groups['inner'].Value -replace '<[^>]+>', '').Trim()
        if ($remarksClean.Length -le $clean.Length) {
            $violations.Add("[$tag] <remarks> 长度 $($remarksClean.Length) ≤ <summary> $($clean.Length)（规则 5，详情应放 remarks）")
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host '=== check-xml-doc.ps1 失败 ===' -ForegroundColor Red
    $violations | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host "共 $($violations.Count) 条违规" -ForegroundColor Red
    exit 1
}

Write-Host "=== check-xml-doc.ps1 通过：$($controllerFiles.Count) 个 Controller 全部符合 §五 注释规范 ===" -ForegroundColor Green
exit 0
