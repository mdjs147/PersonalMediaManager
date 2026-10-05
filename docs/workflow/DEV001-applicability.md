# DEV001 applicability — planned, no product acceptance

Publication status: planned

Authoritative current executable target: Linux x86_64, Python 3.12.x, Git >=2.40,<3, Bash 5.x governance tooling. This is the only runtime target accepted under BA001; its historical evidence does not validate this new card. DEV001 activation additionally requires actual .NET 10 SDK verification, new build configuration, controlled dependency restoration, an inert offline host and fresh independently written foundation checks.

## Eleven dimensions

1. Language/runtime: .NET 10/C#14 intended; SDK availability, build and language checks not-run
2. Web host: single Kestrel/Razor MVC/HTTP/MCP intended; no server yet
3. Frontend: locally packaged Bootstrap5/jQuery/Axios intended; visual/interaction acceptance not-run
4. Database: isolated new SQLite3 intended; no legacy DB/backups may be probed, imported or migrated
5. Data access: EFCore10 Code First intended; schema/migration/concurrency evidence not-run
6. Jobs: durable HostedServices intended; cancellation/retry/recovery evidence not-run
7. Cache/messaging: no Redis or external message broker
8. Testing: xUnit, real isolated SQLite/filesystem, API contracts and Playwright intended; previous pass counts discarded
9. Hosting/CI: GitHub independent dot stage; main is read-only; fresh offline foundation build/test plus governance verification becomes scope only after activation
10. Deployment/network: no deployment, external accounts, paid AI, live TMDB or data-moving operation authorized by this card
11. AI/platform/shell: Linux governance currently observed; product Windows/Linux/macOS and Bash/PowerShell7 acceptance remain separate planned work; unknown model is null

## Controls and activation conditions

All nine generic controls remain mandatory: branch, readiness, verification, independent review, task tracking, documentation impact, secret handling, trigger coverage and baseline synchronization. Initial planning uses actual Git/Python gates, bilingual authority review and UTF-8/public-tree checks. After reviewed activation, new foundation work must run actual .NET build and isolated tests plus inert-host start checks. Product behavior/UI/DB acceptance has no implemented object and is not passed. Before a later implementation card starts, install only approved tooling, review exact SDK/runtime/platform and new configuration, bind new context and control evidence, and run first-write on that card.

GOV002 must be accepted first. DEV001 requires a separately assigned actor/execution and worktree, its published immutable scope, reviewed applicability and current gate checks. This document is a plan, not an approval report. Changes to platform, purpose, scope, source or actual repository/environment require review again.

## 中文摘要

本卡先复核需求与适用性，再从零建立最小工程骨架和离线基座；业务操作关闭。保留11维目标但不声称.NET、数据库、UI或多平台已验。所有通用控制继续适用；产品特定检查在有真实实现对象且另卡激活后执行，历史测试全部重置为未运行。
