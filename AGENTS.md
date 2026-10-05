# PersonalMediaManager governed successor workflow

Product development restarts from confirmed design inputs. Old product implementation, tests and acceptance counts are excluded. The current task is resolved from the immutable published plan referenced by [workflow state](docs/agents/workflow-state.json); the BA001 profile is a preserved adoption snapshot, not the active task authority.

## Mandatory current workflow

1. Set the actual PMM_ACTOR, PMM_EXECUTION and published PMM_PURPOSE. Local pmm.actorId and pmm.executionId must match this owned worktree
2. Before a first write run `python3 scripts/pmm_governance.py first-write`; on resume/environment/repository change run `python3 scripts/pmm_governance.py recover`
3. Publish a new planned card first using the trusted controller exported from the accepted old stage. `plan` validates an exact additive document candidate with independent review, current old SHA and explicit target. It performs no mutation. Do not run candidate code to grant candidate scope
4. A published card stays planned until separate reviewed activation. `activate` permits only state/context/evidence changes, verifies predecessor acceptance and existing published plan, and rejects mixed implementation. Publish and read back activation before the next ordinary first-write
5. An actual author/execution must be assigned. Reviewed activation may list distinct authorized executions for a legitimate handoff; labels do not authenticate identities. Reviewer must be distinct from every involved author/execution
6. Run `python3 scripts/verify_governance.py`. Pending context allows only the published governance repair scope. Foundation and offline-domain additionally require actual .NET10, locked restore, fresh build/tests and inert/isolated-host checks. Offline-domain source is limited to the published Catalog paths. Production business operations stay blocked
7. Freeze the candidate, collect genuine candidate/base/tree/diff test and independent review sidecars, run `git-target` and `deliver`, then fast-forward only the approved dot stage. Read back the exact SHA and its CI. Never write main, force a ref, release, deploy or connect real services under these cards

Hooks remain installed via `git config core.hooksPath .githooks`. Hook/instruction enforcement is bypassable; CI validates received commits and is not proof of remote branch protection. GitHub connector publication requires the same exact prechecks and readback; update_ref(force:false) has no CAS, so recheck immediately before and after and stop on drift.

## Required navigation

- [Secondary instructions](CLAUDE.md)
- [Workflow](.claude/rules/git-workflow.md)
- [Guard discipline](.claude/rules/guard-discipline.md)
- [Independent reviewer](.claude/agents/code-reviewer.md)
- [Validator](docs/agents/readiness/validate_readiness.py)
- [Adoption](docs/agents/baseline-adoption.md)
- [Task tracking](docs/agents/issue-tracker.md)
- [Architecture index](docs/adr/README.md)
- [Runbook](docs/runbook/README.md)
- [Domain boundary](docs/agents/domain.md)
- [Writing](docs/agents/bilingual-writing/CORE.zh-CN.md)
- [Design](docs/agents/interactive-design.md)

## Baseline synchronization

Pinned source and five audited tool assets stay unchanged. Discover source updates before a write session; inaccessible private upstream is a recorded discovery limitation, never a waiver of local validation. Same-source repository/environment/configuration changes still invalidate context. Generic improvements are coordinated separately with the baseline repository; no automatic pin upgrade.

Current card, role, scope and phase come from an immutable published plan and independent activation; historical adoption is not permission for a new card. GOV003 changes governance declarations only. A later offline-domain card must be published and activated separately; it may grant only explicit Catalog source paths while production business remains disabled. Both .NET phases require the actual stable SDK, locked restore, build, nonempty tests and inert isolated-host checks. Governance readiness and actual successor acceptance are separate conclusions.

中文：当前卡、角色、范围和阶段来自已发布不可变计划及独立激活，历史接入不能充当新卡许可。GOV003只改治理声明；后续offline-domain卡须另行发布并激活，只可授予明确Catalog源码范围，生产业务继续禁用。两个.NET阶段都要求实际稳定SDK、锁定恢复、构建、非空测试与惯性隔离宿主检查。治理ready与实际后继验收是不同结论。
