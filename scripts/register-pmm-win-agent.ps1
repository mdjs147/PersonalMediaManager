<#
.SYNOPSIS
    一键注册 pmm-win Azure DevOps Server self-hosted agent。

.DESCRIPTION
    按 docs/开发计划.md §0.7.7-A 步骤实现：
      1. 探活 Server（地址由 -ServerUrl 显式传入，内网实值见 CLAUDE.local.md）。
      2. 拉 connectionData 自动解出 agent 包下载地址（避免硬编码版本号）。
      3. 下载 vsts-agent-win-x64-*.zip 到 $env:TEMP。
      4. 解压到 -InstallDir（默认 C:\azagent\pmm-win）。
      5. 跑 config.cmd --unattended 注册到 Default pool，名字 pmm-win。
      6. 安装为 Windows 服务（默认账户 NT AUTHORITY\NetworkService）。
      7. 启动服务并验证 agent 在 pool 里上线。

    PAT 通过 -PatSecure（SecureString）传入，全程不落盘、不进事件日志。

.PARAMETER PatSecure
    Personal Access Token，作为 SecureString。生成方式：
      $pat = Read-Host -AsSecureString -Prompt 'PAT'
    PAT 只需勾 "Agent Pools (read & manage)" scope。

.PARAMETER ServerUrl
    Azure DevOps Server 入口（含 collection 段，形如 http://<azure-server>/<collection>）。
    必填——内网实值不入库（仓库镜像到公网 GitHub），见 CLAUDE.local.md。

.PARAMETER PoolName
    Agent pool 名。默认 Default（Server SKU 自带）。

.PARAMETER AgentName
    Agent 显示名。默认 pmm-win（与 azure-pipelines.yml demands 锁死）。

.PARAMETER InstallDir
    Agent 安装目录。强烈建议不放在项目盘（F:\）以免 VS 调试时与 agent 争 IO。
    默认 C:\azagent\pmm-win。

.PARAMETER ServiceAccount
    跑 agent service 的账户。默认 NT AUTHORITY\NetworkService（无密码、权限隔离）。
    若需访问网络共享 / 域账户授权，改成具体域账户并加 -ServiceAccountPassword。

.PARAMETER ServiceAccountPassword
    上面账户的密码（SecureString）。NetworkService / LocalSystem 不需要。

.PARAMETER SkipServerProbe
    跳过 Server 探活（仅当 Server 在 VPN 后、本机直连不通但 agent 机直连可通时用）。

.PARAMETER Reconfigure
    覆盖已有 agent 配置（先 remove 再 configure）。注意：会导致旧 agent 在 pool 里掉线。

.EXAMPLE
    # 标准用法（开发机 / 专用 agent 机都适用；ServerUrl 实值见 CLAUDE.local.md）
    $pat = Read-Host -AsSecureString -Prompt 'PAT'
    .\scripts\register-pmm-win-agent.ps1 -PatSecure $pat -ServerUrl http://<azure-server>/<collection>

.EXAMPLE
    # 覆盖重装
    .\scripts\register-pmm-win-agent.ps1 -PatSecure $pat -ServerUrl http://<azure-server>/<collection> -Reconfigure

.NOTES
    必须以管理员身份运行（注册 Windows 服务需要）。
    PowerShell 5.1+ 兼容（Windows 自带，无需额外安装）。
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [System.Security.SecureString]$PatSecure,

    # 内网实值不入库（公网镜像脱敏），必须显式传入；实值见 CLAUDE.local.md
    [Parameter(Mandatory = $true)]
    [string]$ServerUrl,
    [string]$PoolName = 'Default',
    [string]$AgentName = 'pmm-win',
    [string]$InstallDir = 'C:\azagent\pmm-win',
    [string]$ServiceAccount = 'NT AUTHORITY\NetworkService',
    [System.Security.SecureString]$ServiceAccountPassword,
    [switch]$SkipServerProbe,
    [switch]$Reconfigure
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---------- 工具函数 ----------

function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Message)
    Write-Host "    [OK] $Message" -ForegroundColor Green
}

function Write-Warn2 {
    param([string]$Message)
    Write-Host "    [警告] $Message" -ForegroundColor Yellow
}

function Test-IsAdministrator {
    $current = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($current)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function ConvertFrom-SecureToPlain {
    param([System.Security.SecureString]$Secure)
    $ptr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
    try {
        return [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    }
}

# ---------- 0. 前置校验 ----------

Write-Step '前置校验'

if (-not (Test-IsAdministrator)) {
    throw '必须以管理员身份运行此脚本（注册 Windows 服务需要）。请以管理员身份重启 PowerShell 后再跑。'
}
Write-Ok '当前 PowerShell 已是管理员'

$psMajor = $PSVersionTable.PSVersion.Major
if ($psMajor -lt 5) {
    throw "PowerShell 版本过低（当前 $($PSVersionTable.PSVersion)），需 5.1+。"
}
Write-Ok "PowerShell 版本 $($PSVersionTable.PSVersion)"

if ($InstallDir -match '^[A-Z]:\\.*[一-龥]') {
    Write-Warn2 "InstallDir 含中文字符，agent 历来有路径兼容问题，建议改纯英文路径。"
}

if ($InstallDir -match '^F:\\') {
    Write-Warn2 "InstallDir 在 F:\ 盘（与项目源码同盘），建议改 C:\ 避免 IO 争用。"
}

# ---------- 1. Server 探活 ----------

if (-not $SkipServerProbe) {
    Write-Step "探活 Server：$ServerUrl"
    $probeUrl = "$ServerUrl/_apis/connectionData?api-version=6.0"
    try {
        $resp = Invoke-WebRequest -Uri $probeUrl -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
        if ($resp.StatusCode -ne 200) {
            throw "Server 返回 HTTP $($resp.StatusCode)"
        }
        Write-Ok "Server 在线（HTTP 200，$($resp.Content.Length) bytes）"
    }
    catch {
        throw @"
Server 探活失败：$($_.Exception.Message)
请先确认：
  1. 浏览器能否打开 $ServerUrl
  2. 若 502：去 Server 机器启动 IIS 应用池 "Microsoft Team Foundation Server Application Pool"
  3. 若超时：检查防火墙是否放行 80 端口
确认 Server 可达后重跑此脚本；或加 -SkipServerProbe 跳过（仅当确认 Server 端可达时）。
"@
    }
}
else {
    Write-Warn2 'Server 探活已跳过（-SkipServerProbe）'
}

# ---------- 2. 获取 agent 包下载地址 ----------

Write-Step '查询最新 agent 包下载地址'
$pkgApi = "$ServerUrl/_apis/distributedtask/packages/agent?platform=win-x64&`$top=1&api-version=6.0"

# PAT 走 Basic Auth
$patPlain = ConvertFrom-SecureToPlain $PatSecure
$authBytes = [Text.Encoding]::ASCII.GetBytes(":${patPlain}")
$authHeader = @{ Authorization = "Basic $([Convert]::ToBase64String($authBytes))" }

try {
    $pkgInfo = Invoke-RestMethod -Uri $pkgApi -Headers $authHeader -TimeoutSec 30 -ErrorAction Stop
}
catch {
    throw "查询 agent 包失败（PAT 是否正确？是否勾了 Agent Pools 权限？）：$($_.Exception.Message)"
}

if (-not $pkgInfo.value -or $pkgInfo.value.Count -eq 0) {
    throw "Server 未返回 win-x64 agent 包。"
}

$pkg = $pkgInfo.value[0]
$downloadUrl = $pkg.downloadUrl
$version = $pkg.version.major.ToString() + '.' + $pkg.version.minor + '.' + $pkg.version.patch
Write-Ok "agent 版本 $version"
Write-Ok "下载地址 $downloadUrl"

# ---------- 3. 下载 + 解压 ----------

Write-Step '下载 agent 压缩包'
$zipPath = Join-Path $env:TEMP "vsts-agent-win-x64-$version.zip"

if (Test-Path $zipPath) {
    Write-Ok "本地缓存命中：$zipPath（跳过下载）"
}
else {
    # 走 PAT 鉴权
    Invoke-WebRequest -Uri $downloadUrl -Headers $authHeader -OutFile $zipPath -UseBasicParsing -TimeoutSec 600
    $size = (Get-Item $zipPath).Length
    Write-Ok "下载完成：$zipPath（$([math]::Round($size/1MB, 1)) MB）"
}

Write-Step "解压到 $InstallDir"
if (Test-Path $InstallDir) {
    $existingConfig = Join-Path $InstallDir '.agent'
    if (Test-Path $existingConfig) {
        if (-not $Reconfigure) {
            throw "$InstallDir 已存在 agent 配置（.agent 文件）。如需覆盖，加 -Reconfigure。"
        }
        Write-Warn2 "检测到已有 agent 配置，将先 unconfigure 再重装"
        Push-Location $InstallDir
        try {
            & .\config.cmd remove --unattended --auth pat --token $patPlain
            if ($LASTEXITCODE -ne 0) {
                Write-Warn2 "unconfigure 退出码 $LASTEXITCODE，继续清理目录"
            }
        }
        finally {
            Pop-Location
        }
        Remove-Item -Recurse -Force $InstallDir
    }
    else {
        Write-Warn2 "$InstallDir 已存在但无 agent 配置，将清空"
        Remove-Item -Recurse -Force $InstallDir
    }
}

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Expand-Archive -Path $zipPath -DestinationPath $InstallDir -Force
Write-Ok "解压完成"

# ---------- 4. 注册 agent ----------

Write-Step "注册 agent 到 pool=$PoolName name=$AgentName"

$configArgs = @(
    '--unattended',
    '--url', $ServerUrl,
    '--auth', 'pat',
    '--token', $patPlain,
    '--pool', $PoolName,
    '--agent', $AgentName,
    '--replace',
    '--runAsService',
    '--windowsLogonAccount', $ServiceAccount
)

if ($PSBoundParameters.ContainsKey('ServiceAccountPassword')) {
    $svcPwdPlain = ConvertFrom-SecureToPlain $ServiceAccountPassword
    $configArgs += @('--windowsLogonPassword', $svcPwdPlain)
}
elseif ($ServiceAccount -notin @('NT AUTHORITY\NetworkService', 'NT AUTHORITY\LocalService', 'NT AUTHORITY\SYSTEM')) {
    throw "ServiceAccount=$ServiceAccount 不是内置账户，必须提供 -ServiceAccountPassword。"
}

Push-Location $InstallDir
try {
    & .\config.cmd @configArgs
    if ($LASTEXITCODE -ne 0) {
        throw "config.cmd 退出码 $LASTEXITCODE"
    }
}
finally {
    Pop-Location

    # 清零 PAT 明文（GC 之前先擦）
    $patPlain = $null
    if (Get-Variable -Name svcPwdPlain -ErrorAction SilentlyContinue) {
        $svcPwdPlain = $null
    }
    [GC]::Collect()
}

Write-Ok 'agent 已注册并安装为 Windows 服务'

# ---------- 5. 启动服务并验证 ----------

Write-Step '启动 agent 服务'

# Server SKU 服务名格式：vstsagent.<CollectionName>.<PoolName>.<AgentName>
# CollectionName 从 ServerUrl 末段提取
$collectionName = ([Uri]$ServerUrl).Segments[-1].TrimEnd('/')
$serviceName = "vstsagent.$collectionName.$PoolName.$AgentName"

$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (-not $svc) {
    throw "未找到服务 $serviceName（config.cmd 可能未成功安装服务）"
}

if ($svc.Status -ne 'Running') {
    Start-Service -Name $serviceName
    Start-Sleep -Seconds 2
    $svc.Refresh()
}

if ($svc.Status -eq 'Running') {
    Write-Ok "服务 $serviceName 已运行"
}
else {
    throw "服务 $serviceName 状态为 $($svc.Status)，请用事件查看器排查"
}

# ---------- 6. 收尾汇报 ----------

Write-Step '完成'
Write-Host @"

pmm-win agent 注册成功。

  Server     : $ServerUrl
  Pool       : $PoolName
  Agent name : $AgentName
  Service    : $serviceName
  Install    : $InstallDir
  Account    : $ServiceAccount

下一步：
  1. 浏览器访问 $ServerUrl/_settings/agentpools 应看到 $AgentName 在 $PoolName pool 里 Online。
  2. 在仓库根开 PR 触发 azure-pipelines.yml，WindowsAgent job 应被 $AgentName 拾起。

如需卸载：
  cd $InstallDir
  .\config.cmd remove --unattended --auth pat --token <PAT>
  Remove-Item -Recurse -Force $InstallDir
"@ -ForegroundColor Green
