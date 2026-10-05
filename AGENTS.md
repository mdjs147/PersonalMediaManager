# PersonalMediaManager governance-only restart

The product implementation is empty. This phase only implements published BA-001 baseline adoption. Old rebuild code, tests, gates and pass counts are not inputs. Private baseline personal prose is not public project policy.

## Mandatory current workflow

1. Before the first write, run: python3 scripts/pmm_governance.py first-write --actor pmm-adoption-author --purpose adoption
2. On resume or repository/environment change, run: python3 scripts/pmm_governance.py recover --actor pmm-adoption-author --purpose adoption
3. Only the frozen published BA-001 governance scope may be repaired while adoption is pending. There is no unrestricted bypass. Business purpose always rejects in this phase, including when adoption is ready
4. The author owns the dedicated task branch. Independent review needs distinct actual actor and execution identities; unknown model is null. Actor records do not authenticate identities
5. Final verification runs python3 scripts/verify_governance.py. Delivery additionally requires the exact candidate, explicit published stage-derived base, and genuine sidecar test/review evidence
6. Integrate and push only the approved stage after review and acceptance. Never force refs, write main, publish a release, deploy, connect real accounts or modify security settings under this card

Local hook installation is explicit: git config core.hooksPath .githooks. Hooks and instructions are bypassable; CI checks received commits and does not prove remote branch protection. Connector publication must use the same target/evidence precheck and exact readback.

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

The reviewed source is pinned in the adoption record. Check the approved upstream branch at the first write session; inaccessible private upstream means update discovery is degraded, never that local controls are valid. Inspect/check-context still revalidate the current repository and environment at the same source SHA. No automatic source upgrade or automatic approval of new observations is allowed. Generic changes return to the source baseline through a separate authorized task.

治理接入就绪只表示当前治理目标已验；业务写入仍被阶段规则拒绝。新增产品目标、平台、配置或范围必须重新复核，不能继承历史实现状态。
