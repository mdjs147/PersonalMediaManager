# Governance runbook

Current supported execution: Linux x86_64, Bash 5.x, Python 3.12.x, Git >=2.40 and <3. These are acceptance ranges, not a claim that every version was tested. Actual versions are probed and recorded on each entry run. Product runtimes and Windows/macOS remain untested here.

When the current Bash session does not export SHELL, establish it from the running Bash process before invoking the gates: export SHELL="$(readlink /proc/$$/exe)". An absent or different shell is rejected rather than assumed.

Commands from repository root:
- python3 scripts/pmm_governance.py first-write --actor pmm-adoption-author --purpose adoption
- python3 scripts/pmm_governance.py recover --actor pmm-adoption-author --purpose adoption
- python3 scripts/verify_governance.py
- python3 scripts/pmm_governance.py deliver --candidate FULL_SHA --base PUBLISHED_TASK_BASE --evidence RELATIVE_SIDECAR

CI obtains full history, establishes origin/HEAD and the stage baseline, executes runtime probes plus the same verification entrypoint. Detached CI cannot use author/write entrypoints. Private upstream update discovery can be unavailable; this never bypasses local context checks.

On drift or failure stop dependent writes, preserve outputs and repair only the authorized scope. Never refresh approved context automatically or weaken checks to make CI pass.
