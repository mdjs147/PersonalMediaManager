#!/usr/bin/env python3
"""PMM governance-only entry points; local controls are not a security boundary."""
from __future__ import annotations
import argparse
import importlib.util
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
_validator_bytes = (ROOT / 'docs/agents/readiness/validate_readiness.py').read_bytes()
if hashlib.sha1(b'blob ' + str(len(_validator_bytes)).encode() + b'\0' + _validator_bytes).hexdigest() != '3877f6bd7fa1cc972f1166ff4484784143c13e6c':
    sys.exit('PMM_GATE_REJECTED: pinned validator integrity failed before import')
spec = importlib.util.spec_from_file_location('baseline_readiness', ROOT / 'docs/agents/readiness/validate_readiness.py')
v = importlib.util.module_from_spec(spec)
spec.loader.exec_module(v)
REPOSITORY = 'https://github.com/mdjs147/PersonalMediaManager.git'
STAGE = 'refs/heads/dot/pmm-rebuild-20261005-gates'
TASK = 'refs/heads/dot/task/pmm-baseline-adoption-20261005'
BASE = 'eb739ca7d99d519f47462c23616774c8588631b3'
SOURCE = '23a774836c3edda9a5d16f06b7f4e60e9d6950f8'
AUTHOR = 'pmm-adoption-author'
SOURCE_TREE = '4f4438874605b9e6ca154362fe70b299e2713d7d'
SOURCE_ASSETS = {'docs/agents/readiness/validate_readiness.py': '3877f6bd7fa1cc972f1166ff4484784143c13e6c', 'docs/agents/readiness/readiness.schema.json': 'a0b7322c774b088da823ae5673117616d4331536', 'docs/agents/readiness/test_readiness.py': '8d8a5e12fd07b7eb9f53bace9ecd710881cb5cd0', 'docs/agents/readiness/baseline-readiness.template.json': '44569a7c54c58feb61fd75c9d04157a1cc6af1ea', 'docs/agents/readiness/environment-profile.template.json': '54769fcbbd54bbc630b51adfacb5a5a09accf88b'}
FROZEN = {'docs/workflow/BA-001.md': 'bb8163bceb6d20fc851dd216f438eb7e9da60e83',
          'docs/workflow/BA-001-public-adaptation.md': '378e329d1e9e39844efb5da532b9797d9289adbb'}
EXACT = {'README.md', '.gitignore', 'AGENTS.md', 'CLAUDE.md',
         '.claude/rules/git-workflow.md', '.claude/rules/guard-discipline.md',
         '.claude/agents/code-reviewer.md', '.claude/agents/decision-advisor.md',
         '.githooks/pre-commit', '.githooks/pre-push', '.github/workflows/verify-governance.yml',
         'docs/runbook/README.md', 'docs/requirements/retained-decisions.md',
         'docs/workflow/BA-001-status.md', 'scripts/pmm_governance.py', 'scripts/verify_governance.py'}
PREFIXES = ('docs/agents/', 'docs/adr/', 'tests/governance/')
MANIFEST = 'docs/agents/baseline-readiness.json'

def need(ok, reason):
    v.require(ok, reason)

def run(args, root=ROOT):
    env = {k: val for k, val in os.environ.items() if not k.startswith('GIT_')}
    env.update(GIT_TERMINAL_PROMPT='0', GIT_NO_REPLACE_OBJECTS='1', LC_ALL='C')
    return subprocess.run(args, cwd=root, env=env, text=True, capture_output=True, timeout=90)

def read_profile(root):
    p = v.read_json(v.confined(root, 'docs/agents/governance-profile.json'))
    need(p['phase'] == 'governance-only', 'Phase transition needs a new reviewed adoption; business remains blocked')
    need(p['repository'] == REPOSITORY and p['default_branch'] == 'refs/heads/main', 'Repository/default target changed')
    need(p['stage_branch'] == STAGE and p['task_branch'] == TASK and p['plan_commit'] == BASE, 'Approved task/stage/base changed')
    need(p['frozen_cards'] == FROZEN, 'Frozen card identity changed')
    need(set(p['allowed_exact']) == EXACT and tuple(p['allowed_prefixes']) == PREFIXES, 'Published task scope changed')
    need(p['author']['actor_id'] == AUTHOR and p['author']['execution_id'] == 'pmm-adoption-20261005', 'Author ownership changed')
    return p

def runtime(root):
    read_profile(root)
    need(platform.system() == 'Linux' and platform.machine() == 'x86_64', 'Unreviewed execution platform')
    need(Path(os.environ.get('SHELL', '')).name == 'bash', 'Unreviewed actual shell')
    need(sys.version_info[:2] == (3, 12), 'Python runtime outside reviewed 3.12.x range')
    versions = {'python': platform.python_version()}
    for name, args, pattern in [('git', ['git', '--version'], r'git version (\d+)\.(\d+)\.([^\s]+)'),
                                 ('bash', ['bash', '--version'], r'GNU bash, version (\d+)\.(\d+)\.([^\s]+)')]:
        result = run(args, root)
        match = re.search(pattern, result.stdout)
        need(result.returncode == 0 and match is not None, 'Runtime probe failed: ' + name)
        major, minor = map(int, match.groups()[:2])
        need((major == 2 and minor >= 40) if name == 'git' else major == 5, 'Runtime outside reviewed range: ' + name)
        versions[name] = '.'.join(match.groups())
    print('RUNTIME_OBSERVED ' + json.dumps(versions, sort_keys=True))
    return versions

def remote_context(root, live=True):
    read_profile(root)
    need(v.git(root, 'remote').splitlines() == ['origin'], 'Unexpected or missing remote')
    for args in [('remote', 'get-url', '--all', 'origin'), ('remote', 'get-url', '--push', '--all', 'origin')]:
        need(v.git(root, *args).splitlines() == [REPOSITORY], 'Wrong actual fetch/push remote')
    need(v.git(root, 'symbolic-ref', 'refs/remotes/origin/HEAD') == 'refs/remotes/origin/main', 'Wrong cached default branch')
    need(v.git(root, 'rev-parse', '--is-shallow-repository') == 'false', 'Complete Git history is required')
    if live:
        result = run(['git', 'ls-remote', '--symref', 'origin', 'HEAD', 'refs/heads/main', STAGE], root)
        need(result.returncode == 0, 'Live remote observation failed; do not rely on cached refs')
        lines = result.stdout.splitlines()
        need('ref: refs/heads/main\tHEAD' in lines, 'Live default branch is not approved main')
        refs = {line.split('\t')[1]: line.split('\t')[0] for line in lines if '\t' in line and not line.startswith('ref: ')}
        for ref, local in [('refs/heads/main', 'refs/remotes/origin/main'), (STAGE, 'refs/remotes/origin/' + STAGE.removeprefix('refs/heads/'))]:
            need(ref in refs and refs[ref] == v.git(root, 'rev-parse', local), 'Live/cached remote drift: fetch and review ' + ref)
        print('LIVE_REMOTE_OBSERVED ' + json.dumps(refs, sort_keys=True))

def cards(root, live=True):
    remote_context(root, live)
    stage = 'refs/remotes/origin/' + STAGE.removeprefix('refs/heads/')
    need(v.git(root, 'merge-base', '--is-ancestor', BASE, stage, optional=True) is not None, 'Published plan is not in actual stage history')
    for path, expected in FROZEN.items():
        need(v.git(root, 'rev-parse', stage + ':' + path) == expected, 'Card was not published unchanged on authorized stage: ' + path)
        need(v.blob(root, path) == expected and v.git(root, 'rev-parse', BASE + ':' + path) == expected, 'Local or publication card changed: ' + path)
    print('PUBLISHED_CARD_VERIFIED ' + BASE)

def scoped(root):
    read_profile(root)
    paths = set()
    for args in [('diff', '--name-only', '--no-renames', BASE, 'HEAD'), ('diff', '--name-only', '--no-renames'),
                 ('diff', '--cached', '--name-only', '--no-renames'), ('ls-files', '--others', '--exclude-standard')]:
        paths.update(v.git(root, *args).splitlines())
    for path in paths:
        v.relative_path(path)
        need(path in EXACT or path.startswith(PREFIXES), 'Path exceeds frozen BA-001 scope: ' + path)
    print('SCOPE_VERIFIED ' + str(len(paths)) + ' changed/input paths')
    return paths

def branch_owner(root, actor):
    need(actor == AUTHOR, 'Caller is not the authorized implementation actor')
    need(v.git(root, 'config', '--get', 'pmm.actorId', optional=True) == AUTHOR, 'Worktree ownership not established')
    need(v.git(root, 'symbolic-ref', '--quiet', 'HEAD', optional=True) == TASK, 'Wrong task branch or detached author checkout')
    need(v.git(root, 'merge-base', '--is-ancestor', BASE, 'HEAD', optional=True) is not None, 'Task does not descend from published plan')
    entries = v.git(root, 'worktree', 'list', '--porcelain').split('\n\n')
    owned = [x for x in entries if 'worktree ' + str(root) in x.splitlines()]
    need(len(owned) == 1 and ('branch ' + TASK) in owned[0].splitlines(), 'Unsafe/unowned worktree')
    need(not any(line.startswith(('locked', 'prunable')) for line in owned[0].splitlines()), 'Worktree is locked or prunable')

def baseline(root, online=False):
    p = read_profile(root)
    m = v.read_json(v.confined(root, MANIFEST))
    assets = v.read_json(v.confined(root, 'docs/agents/source-assets.json'))
    need(m['source']['commit'] == SOURCE == p['upstream']['commit'] == assets['source_commit'], 'Pinned baseline source mismatch')
    need(assets['source_tree'] == SOURCE_TREE == p['upstream']['tree'], 'Pinned source tree mismatch')
    need(assets['git_blobs'] == SOURCE_ASSETS, 'Required source asset map is missing, altered or reduced')
    for path, expected in SOURCE_ASSETS.items():
        need(v.blob(root, path) == expected, 'Imported source asset changed: ' + path)
    if online:
        result = run(['git', 'ls-remote', p['upstream']['repository'], p['upstream']['ref']], root)
        if result.returncode:
            print('UPSTREAM_UPDATE_DISCOVERY_DEGRADED: private source unavailable; local gates still required')
        else:
            rows = result.stdout.splitlines()
            need(len(rows) == 1 and rows[0].split()[0] == SOURCE, 'Upstream changed or approved source ref missing; review synchronization')
            print('UPSTREAM_PIN_OBSERVED ' + SOURCE)
    print('BASELINE_ASSETS_VERIFIED ' + SOURCE)

def documentation(root):
    m = v.read_json(v.confined(root, MANIFEST))
    targets = v.markdown_targets(root, 'AGENTS.md') | v.markdown_targets(root, 'CLAUDE.md')
    need('AGENTS.md' in v.markdown_targets(root, 'CLAUDE.md'), 'Secondary instructions do not import primary entry')
    for group, minimum in [('entrypoints', v.MIN_ENTRYPOINTS), ('navigation', v.MIN_NAVIGATION)]:
        need(set(minimum) <= m[group].keys(), 'Fixed navigation/control entry omitted')
        for path in m[group].values():
            v.confined(root, path)
            need(path in {'AGENTS.md', 'CLAUDE.md'} or path in targets, 'Required navigation is disconnected: ' + path)
    print('DOCUMENTATION_NAVIGATION_VERIFIED')

def public_tree(root):
    names = set(v.git(root, 'ls-files', '--cached', '--others', '--exclude-standard').splitlines())
    allowed_existing = {'docs/workflow/WF-001.md', *FROZEN}
    patterns = [r'gh[pousr]_[A-Za-z0-9]{30,}', r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----', r'libfile_[a-z0-9]{20,}']
    for name in names:
        need(name in EXACT or name in allowed_existing or name.startswith(PREFIXES), 'Unapproved publication path: ' + name)
        path = v.confined(root, name, nonempty=False)
        need(path.suffix.lower() not in {'.dll', '.exe', '.sqlite', '.db', '.zip', '.safetensors'}, 'Binary/runtime data in public tree')
        text = path.read_text(encoding='utf-8')
        need(not any(re.search(pattern, text) for pattern in patterns), 'Private/credential-like content in public tree: ' + name)
    print('PUBLIC_TREE_SCREENED ' + str(len(names)) + ' files; independent semantic review still required')

def reviewed_context(root):
    m = v.read_json(v.confined(root, MANIFEST)); cr = m['context_review']
    report = v.read_json(v.confined(root, cr['report']))
    v.validate_document(report, 'context_report')
    need(report['result'] == 'approved' and cr['status'] == 'approved', 'Independent context review missing/failed')
    need(report['context'] == v.observed_context(root, m) == m['context'], 'Context review is stale')
    need(report['reviewer'] == cr['reviewer'] and report['adopter'] == cr['adopter'], 'Review actor binding changed')
    for key in ('actor_id', 'execution_id'):
        need(report['reviewer'][key].strip().casefold() != report['adopter'][key].strip().casefold(), 'Context reviewer is not independent: ' + key)
    print('INDEPENDENT_CONTEXT_RECORD_VERIFIED; actual reviewer work must be independently traced')

def context(root, purpose, allow_pending=False):
    p = read_profile(root)
    need(purpose != 'business', 'Business writes blocked in governance-only phase even if readiness is ready')
    m = v.read_json(v.confined(root, MANIFEST))
    if allow_pending and m['status'] == 'pending':
        need(purpose == 'adoption', 'Pending repair is restricted to adoption purpose')
        # Scope and published-card checks are mandatory before this narrow path.
        v.validate_document(m, 'manifest'); v.observed_context(root, m)
        print('ADOPTION_REPAIR_ONLY: observed pending context; no business readiness claim')
    else:
        v.check_context(root, m)
        print('CONTEXT_READY_VERIFIED; product writes remain blocked')

def preflight(root, phase, actor, purpose, live=True):
    branch_owner(root, actor); cards(root, live); scoped(root)
    runtime(root); baseline(root, online=live); documentation(root)
    if phase in {'first-write', 'recover'}:
        need(v.git(root, 'status', '--porcelain') == '', 'First-write/recovery requires a clean owned worktree')
    if phase == 'commit':
        need(v.git(root, 'diff', '--name-only') == '', 'Unstaged changes must be resolved before commit')
    context(root, purpose, allow_pending=True)
    print('PMM_GATE_VERIFIED ' + phase)

def git_target(root, actor, role, action, target, candidate=None, expected_old=None,
               remote_name=None, remote_location=None, local_ref=None, live=True):
    read_profile(root)
    need(actor == AUTHOR, 'Unauthorized Git actor')
    need(action in {'push', 'merge'}, 'Unsupported Git operation')
    need(target == STAGE, 'Forbidden Git target; only the approved stage may be published')
    need(role == 'integrator', 'Wrong role for Git target')
    need(action != 'merge' or target == STAGE, 'Only stage integration is authorized')
    need(remote_name == 'origin' and remote_location == REPOSITORY, 'Actual outgoing remote name/location is not authorized')
    need(candidate and expected_old, 'Exact outgoing candidate and observed remote old SHA are required')
    v.oid(candidate); v.oid(expected_old)
    need(v.git(root, 'config', '--get', 'pmm.actorId', optional=True) == AUTHOR, 'Worktree ownership not established')
    branch = v.git(root, 'symbolic-ref', '--quiet', 'HEAD', optional=True)
    need(branch in ({TASK, STAGE} if role == 'integrator' else {TASK}), 'Git role cannot publish from this branch')
    need(local_ref in {branch, 'HEAD'}, 'Outgoing local ref is not the current owned branch')
    need(v.git(root, 'rev-parse', 'HEAD') == candidate, 'Outgoing candidate is not current HEAD')
    need(v.git(root, 'merge-base', '--is-ancestor', BASE, candidate, optional=True) is not None, 'Outgoing candidate is outside task history')
    remote_context(root, live)
    if live:
        result = run(['git', 'ls-remote', 'origin', target], root)
        need(result.returncode == 0, 'Cannot observe actual outgoing remote target')
        rows = result.stdout.splitlines()
        need(len(rows) <= 1, 'Ambiguous remote target')
        actual_old = rows[0].split()[0] if rows else '0' * 40
    else:
        actual_old = v.git(root, 'rev-parse', '--verify', 'refs/remotes/origin/' + target.removeprefix('refs/heads/'), optional=True) or '0' * 40
    need(actual_old == expected_old, 'Remote target changed since observation; stop concurrent update')
    need(actual_old != '0' * 40, 'Stage creation/deletion is not authorized')
    need(v.git(root, 'merge-base', '--is-ancestor', actual_old, candidate, optional=True) is not None,
         'Non-fast-forward target update is forbidden')
    scoped(root)
    print('GIT_TARGET_VERIFIED ' + action + ' ' + remote_location + ' ' + target + ' ' + actual_old + ' -> ' + candidate + '; precheck performs no Git mutation')

def delivery(root, candidate, base, evidence_path):
    need(base == BASE, 'Delivery base must be the published BA-001 plan commit')
    p = read_profile(root)
    evidence = v.read_json(v.confined(root, evidence_path))
    v.validate_document(evidence, 'delivery')
    need(evidence['task']['path'] == 'docs/workflow/BA-001.md', 'Delivery task must be frozen BA-001')
    need(evidence['task']['author'] == p['author'], 'Delivery author does not match assigned actor/execution')
    need(evidence['task']['scope_paths'] == evidence['diff_paths'], 'Delivery scope must enumerate the exact changed paths')
    for path in evidence['diff_paths']:
        need(path in EXACT or path.startswith(PREFIXES), 'Delivery path exceeds published scope: ' + path)
    runtime(root); cards(root); baseline(root); public_tree(root); documentation(root)
    result = v.deliver(root, v.read_json(v.confined(root, MANIFEST)), MANIFEST, candidate, evidence_path, base)
    print('PMM_DELIVERY_VERIFIED ' + result['candidate_commit'])
    return result

def main(argv=None):
    if (ROOT / "docs/agents/workflow-state.json").exists():
        import pmm_workflow
        return pmm_workflow.main(argv)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['first-write','recover','commit','runtime','cards','scope','baseline','documentation','public-tree','review-context','inspect','context','git-target','deliver'])
    parser.add_argument('--root', default=str(ROOT)); parser.add_argument('--actor', default=os.environ.get('PMM_ACTOR'))
    parser.add_argument('--purpose', choices=['adoption','business'], default='adoption')
    parser.add_argument('--role', choices=['author','integrator']); parser.add_argument('--action', choices=['push','merge']); parser.add_argument('--target')
    parser.add_argument('--remote-name'); parser.add_argument('--remote-location'); parser.add_argument('--old-sha'); parser.add_argument('--local-ref');
    parser.add_argument('--candidate'); parser.add_argument('--base'); parser.add_argument('--evidence'); parser.add_argument('--online', action='store_true')
    args = parser.parse_args(argv)
    try:
        root = v.root_path(args.root); read_profile(root)
        if args.command in {'first-write','recover','commit'}: preflight(root,args.command,args.actor,args.purpose)
        elif args.command == 'runtime': runtime(root)
        elif args.command == 'cards': cards(root)
        elif args.command == 'scope': scoped(root)
        elif args.command == 'baseline': baseline(root,args.online)
        elif args.command == 'documentation': documentation(root)
        elif args.command == 'public-tree': public_tree(root)
        elif args.command == 'review-context': reviewed_context(root)
        elif args.command == 'inspect': print(json.dumps(v.observed_context(root,v.read_json(v.confined(root,MANIFEST))),indent=2))
        elif args.command == 'context': context(root,args.purpose)
        elif args.command == 'git-target': git_target(root,args.actor,args.role,args.action,args.target,args.candidate,args.old_sha,args.remote_name,args.remote_location,args.local_ref)
        else:
            need(args.candidate and args.base and args.evidence,'Delivery requires candidate, explicit base and evidence')
            delivery(root,args.candidate,args.base,args.evidence)
        return 0
    except (v.Invalid,OSError,KeyError,UnicodeError,subprocess.TimeoutExpired) as exc:
        print('PMM_GATE_REJECTED: '+str(exc),file=sys.stderr);return 1
if __name__=='__main__': sys.exit(main())
