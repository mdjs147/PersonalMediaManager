# PersonalMediaManager — fresh offline foundation

PMM is being rebuilt from zero on the independent `dot/pmm-rebuild-20261005-gates` stage. Main is unchanged. Old product code, tests, databases and acceptance counts are not reused.

## Current work

DEV001 has a newly written .NET 10 Foundation library, one inert Razor/Kestrel host and independently written foundation tests. Implementation acceptance is tracked in [DEV001 status](docs/workflow/DEV001-status.md); a source draft is not an accepted product.

- Only GET/HEAD `/` and `/health`, bound to IPv4 loopback
- No business HTTP/MCP handlers, custom workers, accounts, key creation, real database/media access or external service clients
- Only new temporary SQLite/filesystem data in tests; all 219 business capabilities remain planned/not-run
- Linux evidence is separate from Windows/macOS/PowerShell7, which remain not-run

## Build and inspect

Use the stable SDK pinned in global.json. Before invoking the SDK, set `DOTNET_GENERATE_ASPNET_CERTIFICATE=false`. The reviewed local build also sets `DOTNET_PROCESSOR_COUNT=1`, `DOTNET_CLI_USE_MSBUILD_SERVER=0` and `MSBUILDDISABLENODEREUSE=1` for in-process builds. No certificate generation or trust command is needed.

```sh
dotnet restore PersonalMediaManager.sln --locked-mode --source https://api.nuget.org/v3/index.json
dotnet build PersonalMediaManager.sln --no-restore
dotnet test PersonalMediaManager.sln --no-build --no-restore
python3 tests/foundation/verify_foundation.py
```

For a local read-only inspection, run the built Web DLL with `--port 5080`; the listener still binds only 127.0.0.1. A production server, account setup and deployment are outside this card.

## Requirements and boundaries

Read [the current requirements](docs/requirements/dev001/README.md), [219-item traceability](docs/requirements/dev001/traceability.json), [technical applicability](docs/requirements/dev001/technical-applicability.md), [foundation architecture decision](docs/adr/project/PRJ-0001-offline-foundation.zh-CN.md) and [test risk map](tests/foundation/README.md).

Start governed work with [AGENTS.md](AGENTS.md), the [immutable published plan](docs/workflow/GOV002-plan-publication.json) and [current workflow state](docs/agents/workflow-state.json). Earlier governance-only domain/adoption notes remain unchanged historical checkpoints; the linked DEV001 requirements describe the current engineering foundation. No application business domain has been implemented. Publication requires fresh candidate-bound validation and independent review; local declarations/hooks are not authentication or remote branch protection.

中文：全新工程骨架与业务功能验收严格区分。当前只有惯性loopback页面、工程健康响应及独立隔离测试；全部219项业务仍planned/not-run。共享业务规则、权限、恢复与真实数据操作须后续有界卡，main、发布和部署另行确认。
