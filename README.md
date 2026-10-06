# PersonalMediaManager — rebuild paused

PMM is being rebuilt from zero on the independent `dot/pmm-rebuild-20261005-gates` branch. Main is unchanged. Old product code, tests, databases and acceptance counts are not reused.

## Current state

The rebuild is paused until the shared baseline repository is ready. On 2026-10-06 all baseline-generated content (governance tooling, hooks, CI, workflow cards, readiness evidence, the DEV001 Foundation/Web source and its tests) was removed from this branch. The pre-clear state is preserved at tag `archive/pmm-rebuild-pre-clear-20261006`.

This branch currently contains no source code, build or tests. All 219 business capabilities remain planned/not-run.

## Retained requirements and decisions

- [Retained product decisions](docs/requirements/retained-decisions.md)
- [Rebuild capability mapping](docs/requirements/rebuild-mapping.json)
- [DEV001 requirements](docs/requirements/dev001/README.md), [219-item traceability](docs/requirements/dev001/traceability.json) and [technical applicability](docs/requirements/dev001/technical-applicability.md)
- DEV002 offline contract: [中文](docs/requirements/dev002/contracts.zh-CN.md), [English](docs/requirements/dev002/contracts.en.md), [ownership plan](docs/requirements/dev002/ownership.json)
- [Project decision registry](docs/adr/project/PRJ-registry.md), [PRJ-0001 offline foundation](docs/adr/project/PRJ-0001-offline-foundation.zh-CN.md), [ADR-0001 governance adoption](docs/adr/project/ADR-0001-governance-adoption.zh-CN.md)
- [Domain boundary](docs/agents/domain.md)

The DEV001/DEV002 documents and both decision records describe work whose implementation and governance tooling are no longer on this branch. They are kept as design input only.

中文：重建已暂停，等待基线仓库就绪。2026-10-06 已从本分支移除全部基线生成内容（治理工具、hooks、CI、任务卡、readiness证据、DEV001源码与测试），清空前状态保存在 tag `archive/pmm-rebuild-pre-clear-20261006`。本分支当前没有源码、构建或测试；上列需求与决定文档仅作为设计输入保留，219项业务能力仍为planned/not-run。
