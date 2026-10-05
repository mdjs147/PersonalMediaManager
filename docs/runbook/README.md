# Current-task runbook

Read AGENTS.md and the immutable published plan referenced by docs/agents/workflow-state.json. The BA001 profile and old evidence are historical adoption records; do not reuse its actor, task branch or passing count to open the current card.

1. Observe/fetch origin's real main and authorized dot stage; stop on unexpected URLs, default, stage or source drift
2. Establish the actual assigned PMM_ACTOR and PMM_EXECUTION, matching local pmm.actorId/pmm.executionId. Set PMM_PURPOSE from the published task. Only use the task's dedicated owned worktree
3. Before writes: `python3 scripts/pmm_governance.py first-write`; after resume or repository/environment change: `python3 scripts/pmm_governance.py recover`
4. Keep hooks enabled. Normal work uses PMM_OPERATION=implementation. Planned documents use plan mode and a trusted old-tree exported controller; reviewed metadata activation/handoff uses activate mode. See .claude/rules/git-workflow.md for required exact old-SHA/plan/review parameters. Both modes have real registered pre-commit/pre-push checks
5. Run `python3 scripts/verify_governance.py`; pending governance repair may use the explicitly labelled adoption-bootstrap check, but it is not final acceptance
6. Freeze the candidate, obtain genuine tests and independent review bound to its exact tree/base/diff, then run exact git-target and deliver. Only fast-forward the approved stage; read back exact SHA and CI

A detached CI checkout can verify but cannot gain author write permission. Missing published authority, unpublished/stale activation, dirty or unowned worktree, failed required checks or changed target stops dependent writes. A new platform/phase or legitimate new execution needs fresh explicit review; an old ready flag does not substitute for it.

中文：先核实当前任务、实际执行身份、工作区及远端，再运行首次写入或恢复入口。计划、激活和实现分别有受控hook路径；最终独审、精确候选与远端CI不得省略。治理检查和离线演练不代表产品能力通过。

Foundation activation includes the independently reviewed stable SDK10 global.json pin (allowPrerelease=false), so local and clean CI SDK selection agree. CI uses the official setup-dotnet v6 pinned revision; subsequent locked restore uses the official NuGet feed. Reference: https://github.com/actions/setup-dotnet and https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-restore. These are tooling steps, not live business integrations.
