# PMM public governance adoption

Source repository: https://github.com/mdjs147/ai-dev-baseline
Source commit: 23a774836c3edda9a5d16f06b7f4e60e9d6950f8
Source tree: 4f4438874605b9e6ca154362fe70b299e2713d7d
Import route: first adoption into an empty product implementation, with explicitly authorized public self-contained replacements
Machine status: docs/agents/baseline-readiness.json

## Confirmed facts and scope

The public repository is PersonalMediaManager. The remote default is main; the approved independent integration target is dot/pmm-rebuild-20261005-gates. The initial stage contained README and historical WF-001 only. BA-001 and the per-file adaptation plan were independently reviewed, published and read back at eb739ca7d99d519f47462c23616774c8588631b3 before implementation.

The task starts from a new isolated checkout. No old application code, tests, local gate or old passing count is imported. Existing confirmed product/design directions are restated in docs/requirements/retained-decisions.md; engineering proposals and unresolved policies keep their original decision status. Old local work is preserved outside this project.

All 11 environment dimensions are explicitly recorded in readiness/environment-profile.json. Current executable scope is Python/Git/Bash governance on Linux x86_64. The intended .NET/C#/MVC/SQLite/EF/HostedServices/UI/product-test stack is retained future input, not current implementation or platform acceptance. Runtime ranges are supported acceptance ranges, not claims that every patch was tested. Entry points probe exact actual runtime values. Other platforms need separate review.

## Applicability and replacements

The published BA-001-public-adaptation.md enumerates every core and stack-dotnet file before implementation. Only the five audited readiness program/schema/template/regression assets are byte-preserved; their fixed Git blobs are in source-assets.json. All private/source-specific governance prose is replaced by project-authored public instructions. No private personal descriptions, conversations or raw source documents are copied.

The generic objectives of stack-dotnet verification/language skills and encoding/build configuration are active here: scripts/verify_governance.py supplies failure-sensitive verification; the PMM writing policy and independent reviewer enforce equivalent scope/status language; baseline asset checks enforce original byte identity; UTF-8 reads and public-tree inspection reject invalid text/runtime artifacts. Their .NET-specific build, database, IIS and PowerShell mechanics have no current object and are not reported as passed. Future introduction of technical targets requires a new applicability review before business writes.

ADO and source-specific deployment/database/model/shell restrictions are not inherited. Repository cards, actual runtime observations and PMM-specific workflow retain their generic objectives. No branch protection, secrets, credentials, external permissions or service deployments are configured by this task. There is no pending product choice blocking governance-only adoption; remaining product choices do not authorize real actions.

## Controls and actual invocation surfaces

| Control | Current implementation | Actual positive / negative acceptance |
|---|---|---|
| branch_gate | pmm_governance preflight, frozen profile and workflow | correct published/owned task; wrong branch/remote/dirty/scope rejection |
| readiness_gate | unchanged validator plus first-write/recovery/context/verification calls | actual inspect then ready-context; pending or drift rejection |
| verification_gate | verify_governance and mandatory nonempty suites | real passing invocation; injected failing test propagates failure |
| independent_review | reviewer guide and context/delivery record checks | real independent context record; same actor/execution or failed/stale result rejection |
| task_tracking | frozen published BA-001 card and profile | exact card on real authorized stage; changed/unpublished card rejection |
| documentation_impact | mandatory navigation/writing/domain/ADR controls | current connected entry points; broken link rejection |
| secret_handling | public_tree checks plus full independent semantic review | clean complete tree; injected credential-like fixture rejection |
| trigger_coverage | root instructions, local hooks and CI verifier | invoked hook/entrypoints; wrong actor and removed-call detection |
| baseline_sync | pinned source assets, online update discovery and context checks | exact pinned assets/current context; changed source/asset or same-source drift rejection |

Each machine control report binds its own real command/output pair and implementation Git blobs. Upstream regression fixtures remain synthetic. Actual target acceptance is separately recorded and reviewed. An initial pending-context inspection is only an observation; it is not a readiness approval.

## Approval and evidence boundaries

Prior explicit scope approval covers public PMM adaptation and plan-first, task-branch, reviewed stage integration/push. It does not grant main integration, deployment, account access or security changes. The import confirmation points reuse these approved choices rather than inventing new decisions. A separate reviewer approves observed context and exact candidate; actor labels themselves are not cryptographic authentication.

Status remains pending until required context/control evidence is real and independently reviewed. Governance readiness never opens the product-write path. Final SHA-bound test/review/delivery evidence is created after the candidate commit as sidecars. Temporary injection changes are restored exactly. CI checks received candidates; hooks and text instructions are bypassable, and remote protection remains unverified.

## Classified ADR namespace synchronization

The revision-2 plan was published and read back before dependent updates. Its source is final baseline main23a7748. The five imported readiness assets are unchanged byte-for-byte. GEN12/TECH33/USER0 remain source inventory; public replacements are mapped by category. Existing project ADR-0001 stays at its original path and content. New project decisions use full PRJ IDs with a reviewed registry; no current project decision is renumbered. Baseline user decisions are empty and no private preference or permission rule is imported.

## GOV002 successor repair

The BA001 source/profile/card/evidence at a59311a is retained history. Active task authority now comes from immutable published plans and reviewed workflow-state, with new GOV002 control/context reports. The five source tool blobs are unchanged. The one-time five-file publication was reviewed separately before implementation; its original BA001 CI result is recorded honestly. A passing historical adoption does not activate DEV001, prove the new cycle, or validate the .NET product stack. Ordinary successor publication and activation use the trusted previous-stage controller and independent exact-candidate review.

## GOV003 minimal documentation synchronization

The applicable documentation source is baseline main 5b283b9918cbea02d8176a59f282ec7fa9248ed0, published through baseline PR5 with successful CI37290792282. Only card continuation/actual handoff, required versus optional evidence and committed-candidate/sidecar/deliver ordering are adapted into issue-tracker, git-workflow and readiness README, with this adoption record as the fourth location. Existing stricter PMM gates remain. The five audited tool assets and machine source pin stay at 23a7748; this is not a tool/schema upgrade or wholesale baseline import. No stopped source work, private prose, source-specific deployment policy or new security/network/credential authority is imported.

The accepted DEV001 foundation remains a historical predecessor, including its own exact product checks. GOV003 changes only the governance representation of a later offline Catalog card and its inherited validation chain, with production business still disabled. Actual Chinese/English consistency for this card's changes is recorded separately with exact source/derived Git blobs and the real reviewer. Pending translations remain draft; no project-wide or baseline-English release is implied.

中文：本次适用文档源为基线main 5b283b9918cbea02d8176a59f282ec7fa9248ed0，经基线PR5发布且CI37290792282成功。仅将逐卡接续/实际交接、必需与可选证据分层、已提交候选/旁证/deliver顺序适配到issue-tracker、git-workflow和readiness README，本接入记录为第四处。PMM已有更严格门禁保留；五份审计工具与机器source pin仍为23a7748，不属于工具/schema升级或整包导入，不引入已停止源工作、私有文字、源项目部署政策或新增安全/网络/凭据权限。

已验收DEV001基座及其精确产品检查继续作为历史前驱。GOV003只修改后续离线Catalog卡的治理表示及继承验证链，生产业务仍禁用。本卡变化的实际中英一致性另以精确source/derived Git blob及真实审查者记录；待审翻译保持draft，不表示全项目或基线英文正式发布。
