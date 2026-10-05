# GOV002 execution facts and acceptance boundary

The exact five-file plan was independently reviewed and published at 8437301c86c44e74beed3ac7220426741e67094b, tree 2998dff01d87942a54ecc98e1d317bf75e7f1669. All 90 original paths stayed byte-identical at plan publication; main stayed c09befa02cc27a0dc358322779798bb5e6d295e4. Its original BA001 CI run 37263893970 failed because the legacy public-tree check rejected the new DEV001 card. That failure is retained; no CI skip was introduced.

The successor implementation was frozen locally at 51ae1b4add2a2d71c99bd6cf9e39705c127000bf. It retains the five pinned upstream tool assets, historical BA001 cards and old reports. Its registered implementation, plan-preparation/publication and activation-preparation/publication hook paths were exercised with actual Git operations. Historical compatibility (36 tests), pinned baseline regressions (59 tests), and fresh successor regressions (101 tests) are separate results; nested fixture suites are not added again. Foundation-command stubs test invocation and failure propagation only, not a .NET product.

Nine current controls have genuine positive/rejection process pairs. Readiness and independent review were repeated against the genuinely reviewed ready context; earlier pending-repair observations remain separately preserved. Two verbose output files use lossless UTF-8 JSON with the original raw Git blob, preserving every original character while avoiding trailing-whitespace changes to test evidence. Current runtime/context, ready first-write and recovery were actually checked. Private upstream update discovery was unavailable and is recorded as degraded; local pinned checks were not bypassed.

This file is not an approval token or a final completion claim. Exact-candidate test/review/delivery sidecars, the distinct-real-execution local successor rehearsal and the published commit's remote CI are the final acceptance sources. The initial fixed-base bootstrap and discarded fixture setup attempts are not ordinary lifecycle success. Local fixtures do not establish public-remote or product acceptance. Every candidate change invalidates its final sidecars.

DEV001 remains planned for a new offline engineering foundation; it requires separately reviewed activation, actual stable SDK10 selection and fresh applicability/context. No business source, handler, worker, real service/account, media operation, deployment or main integration is introduced by GOV002.

中文：本页记录已核实的实施和控制事实，不把旧BA001通过、初始自举、合成回归或未完成的后续步骤当作最终交付。真正接受取决于精确候选旁证、不同真实执行的后继演练和远端CI；DEV001仍待独立激活，当前没有业务实现。
