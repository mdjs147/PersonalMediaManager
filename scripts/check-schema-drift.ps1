<#
.SYNOPSIS
    校验 src/PersonalMediaManager.Frontend/src/api/schema.d.ts 是否与后端 OpenAPI 一致（CI 红线 P3 #12）。

.DESCRIPTION
    后端 Controller 改了形状但前端 schema 没重生 → 类型契约漂移 → 前端 IDE / tsc 拿到的还是旧形状。
    本脚本在 CI 强制把"后端是 schema 单一源" 这件事执行起来：

      1. 通过 PmmHostFactory + TestServer（无 Kestrel / 无 WinForms 托盘）跑一个 xUnit 测试，
         把 /openapi/v1.json 落盘到 artifacts/openapi.json。
         为什么不直接启 Launcher：CI agent 跑在 NetworkService 无桌面 Session，
         WinForms NotifyIcon 起不来（Application.Run 阻塞或抛异常）。
      2. 跑 openapi-typescript artifacts/openapi.json -o src/PersonalMediaManager.Frontend/src/api/schema.d.ts。
      3. git diff --exit-code 校验输出与仓库已提交版本一致；不一致即 CI 失败 + 给出本地修复指引。

.NOTES
    本脚本不依赖 dotnet CLI 解析 csproj，纯 dotnet test + npx 调用。
#>

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

$artifactsDir = Join-Path $repoRoot 'artifacts'
$openApiJson  = Join-Path $artifactsDir 'openapi.json'
$schemaPath   = Join-Path $repoRoot 'src\PersonalMediaManager.Frontend\src\api\schema.d.ts'
$frontendDir  = Join-Path $repoRoot 'src\PersonalMediaManager.Frontend'

if (Test-Path $artifactsDir) {
    Remove-Item $openApiJson -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $artifactsDir -Force | Out-Null

Write-Host '[1/3] 跑 OpenAPI dump 测试 → artifacts/openapi.json' -ForegroundColor Cyan
$env:PMM_OPENAPI_DUMP_PATH = $artifactsDir
try {
    & dotnet test tests/PersonalMediaManager.Host.Tests/PersonalMediaManager.Host.Tests.csproj `
        --filter 'FullyQualifiedName=PersonalMediaManager.Host.Tests.Diagnostics.OpenApiAndScalarTests.OpenApi_DumpToArtifact_ForCi' `
        -c Release --nologo
}
finally {
    Remove-Item Env:\PMM_OPENAPI_DUMP_PATH -ErrorAction SilentlyContinue
}
if ($LASTEXITCODE -ne 0) { throw 'OpenAPI dump 测试失败' }
if (-not (Test-Path $openApiJson)) { throw "未生成 $openApiJson（测试可能未执行）" }

Write-Host '[2/3] 跑 openapi-typescript 重生 schema.d.ts' -ForegroundColor Cyan
Push-Location $frontendDir
try {
    if (-not (Test-Path 'node_modules')) {
        & npm ci
        if ($LASTEXITCODE -ne 0) { throw 'npm ci 失败' }
    }
    & npx --no-install openapi-typescript $openApiJson -o src/api/schema.d.ts
    if ($LASTEXITCODE -ne 0) { throw 'openapi-typescript 失败' }
}
finally {
    Pop-Location
}

Write-Host '[3/3] git diff --exit-code 校验 schema.d.ts' -ForegroundColor Cyan
# 关掉 ErrorAction Stop：PS 5.1 在 `2>&1` 父调用下会把 git 的 LF/CRLF warning 包装成 NativeCommandError 误判失败。
$prevErrorAction = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& git diff --exit-code -- $schemaPath 2>$null
$diffExit = $LASTEXITCODE
$ErrorActionPreference = $prevErrorAction
if ($diffExit -ne 0) {
    Write-Host ''
    Write-Host '❌ schema.d.ts 与后端 OpenAPI 已漂移。本地修复步骤：' -ForegroundColor Red
    Write-Host '   1. dotnet run --project src/PersonalMediaManager.Launcher  # 启 Host'
    Write-Host '   2. cd src/PersonalMediaManager.Frontend && npm run gen:api  # 重生 schema'
    Write-Host '   3. git add src/PersonalMediaManager.Frontend/src/api/schema.d.ts && git commit'
    exit 1
}

Write-Host '✓ schema.d.ts 与后端 OpenAPI 一致' -ForegroundColor Green
