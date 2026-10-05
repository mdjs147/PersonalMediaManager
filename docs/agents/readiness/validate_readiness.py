#!/usr/bin/env python3
"""Read-only, fail-closed adoption/context and candidate-evidence checks (stdlib)."""
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import subprocess
import sys
from urllib.parse import urlsplit

HERE = Path(__file__).resolve().parent
MIN_CONTROLS = (
    'branch_gate', 'readiness_gate', 'verification_gate', 'independent_review',
    'task_tracking', 'documentation_impact', 'secret_handling', 'trigger_coverage',
    'baseline_sync',
)
MIN_ENTRYPOINTS = (
    'agent_instructions', 'secondary_instructions', 'workflow_rules',
    'guard_rules', 'reviewer', 'verification',
)
MIN_NAVIGATION = (
    'adoption', 'task_tracking', 'architecture', 'runbook', 'domain', 'writing', 'design',
)
DIMENSIONS = (
    'language_runtime', 'web_host', 'frontend', 'database', 'data_access',
    'background_jobs', 'cache_messaging', 'testing', 'hosting_ci',
    'deployment_network', 'ai_platform_shell',
)


class Invalid(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise Invalid(message)


def no_duplicates(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'Duplicate JSON key: ' + key)
        result[key] = value
    return result


def read_json(path):
    try:
        require(path.stat().st_size <= 2_000_000, 'JSON exceeds 2 MB: ' + str(path))
        return json.loads(path.read_text(encoding='utf-8'), object_pairs_hook=no_duplicates,
                          parse_constant=lambda value: (_ for _ in ()).throw(Invalid('Invalid JSON number: ' + value)))
    except (OSError, UnicodeError, json.JSONDecodeError, RecursionError) as exc:
        raise Invalid('Cannot read JSON: ' + str(path) + ': ' + str(exc)) from exc


def validate_schema(value, schema, definitions, location='$'):
    """本包使用的 JSON Schema 子集；未知关键字不得被静默忽略。"""
    supported = {'$schema', '$id', '$defs', '$ref', 'title', 'description', 'type',
                 'properties', 'required', 'additionalProperties', 'items', 'minItems',
                 'uniqueItems', 'minLength', 'minProperties', 'pattern', 'enum', 'const'}
    require(not (set(schema) - supported), 'Unsupported schema keyword at ' + location)
    if '$ref' in schema:
        ref = schema['$ref']
        require(ref.startswith('#/$defs/'), 'Only local schema refs are supported')
        name = ref.removeprefix('#/$defs/')
        require(name in definitions, 'Unknown schema reference: ' + ref)
        validate_schema(value, definitions[name], definitions, location)
        return
    kind = schema.get('type')
    choices = kind if isinstance(kind, list) else [kind]
    matches = {'object': type(value) is dict, 'array': type(value) is list,
               'string': type(value) is str, 'integer': type(value) is int,
               'boolean': type(value) is bool, 'null': value is None}
    require(kind is None or any(matches.get(item, False) for item in choices),
            location + ': invalid type')
    if 'enum' in schema:
        require(value in schema['enum'], location + ': invalid enum value')
    if 'const' in schema:
        require(type(value) is type(schema['const']) and value == schema['const'], location + ': invalid constant')
    if isinstance(value, str):
        require(len(value.strip()) >= schema.get('minLength', 0), location + ': empty string')
        if 'pattern' in schema:
            require(re.fullmatch(schema['pattern'], value) is not None, location + ': invalid format')
    if type(value) is dict:
        require(len(value) >= schema.get('minProperties', 0), location + ': empty object')
        require(set(schema.get('required', [])) <= value.keys(), location + ': missing required fields')
        properties = schema.get('properties', {})
        for key, item in value.items():
            if key in properties:
                validate_schema(item, properties[key], definitions, location + '.' + key)
            else:
                extra = schema.get('additionalProperties', True)
                require(extra is not False, location + ': unknown field ' + key)
                if isinstance(extra, dict):
                    validate_schema(item, extra, definitions, location + '.' + key)
    if type(value) is list:
        require(len(value) >= schema.get('minItems', 0), location + ': too few items')
        if schema.get('uniqueItems'):
            encoded = [json.dumps(item, sort_keys=True) for item in value]
            require(len(encoded) == len(set(encoded)), location + ': duplicate items')
        for index, item in enumerate(value):
            validate_schema(item, schema.get('items', {}), definitions, location + '[' + str(index) + ']')


def validate_document(value, definition):
    schema = read_json(HERE / 'readiness.schema.json')
    validate_schema(value, schema['$defs'][definition], schema['$defs'])


def relative_path(value):
    require(isinstance(value, str) and value.strip() == value and value, 'Invalid repository-relative path')
    require(not any(ord(char) < 32 for char in value), 'Control character in path')
    require('\\' not in value and ':' not in value, 'Unsafe path: ' + value)
    path = PurePosixPath(value)
    require(not path.is_absolute() and all(part.casefold() not in ('', '.', '..', '.git') for part in value.split('/')),
            'Unsafe path: ' + value)
    return path


def confined(root, value, nonempty=True):
    relative_path(value)
    target = root / value
    current = root
    for part in PurePosixPath(value).parts:
        current = current / part
        require(not current.is_symlink(), 'Symlink is not an evidence/entrypoint path: ' + value)
    require(target.resolve().is_relative_to(root), 'Path escapes repository: ' + value)
    require(target.is_file(), 'Missing regular file: ' + value)
    if nonempty:
        require(target.stat().st_size > 0, 'Empty file: ' + value)
    return target


def git(root, *args, optional=False, binary=False):
    # 不使用 shell，不继承会改变仓库寻址或对象替换的 Git 环境变量。
    env = {key: value for key, value in os.environ.items() if not key.startswith('GIT_')}
    env.update({'GIT_OPTIONAL_LOCKS': '0', 'GIT_NO_REPLACE_OBJECTS': '1', 'LC_ALL': 'C'})
    run = subprocess.run(['git', '-C', str(root), *args], capture_output=True, env=env, timeout=30)
    if run.returncode:
        if optional:
            return None
        raise Invalid('Git command failed: ' + ' '.join(args[:2]))
    return run.stdout if binary else run.stdout.decode('utf-8', errors='strict').strip()


def root_path(value):
    root = Path(value).resolve()
    require(root.is_dir(), 'Repository directory is missing')
    require(Path(git(root, 'rev-parse', '--show-toplevel')).resolve() == root, '--root must be the Git worktree root')
    return root


def blob(root, path):
    confined(root, path)
    return git(root, 'hash-object', '--no-filters', '--', path)


def safe_remote(url):
    # URL 中不能携带凭据；上下文报告不得扩散 token。
    if '://' in url:
        try:
            parsed = urlsplit(url)
        except ValueError as exc:
            raise Invalid('Malformed remote URL') from exc
        require(parsed.password is None and parsed.query == '' and parsed.fragment == '',
                'Credential/query-bearing remote URL must be cleaned before inspection')
        require(parsed.username in (None, 'git') if parsed.scheme == 'ssh' else parsed.username is None,
                'Credential-bearing remote URL must be cleaned before inspection')
    return url


def config_marker(path):
    name = PurePosixPath(path).name.lower()
    return (name in {'package.json', 'package-lock.json', 'pnpm-lock.yaml', 'yarn.lock',
                     'pyproject.toml', 'requirements.txt', 'poetry.lock', 'uv.lock', 'go.mod',
                     'go.sum', 'cargo.toml', 'cargo.lock', 'pom.xml', 'build.gradle',
                     'gemfile', 'gemfile.lock', 'composer.json', 'composer.lock',
                     'global.json', 'nuget.config', 'dockerfile', 'compose.yml',
                     'compose.yaml', 'docker-compose.yml', 'docker-compose.yaml',
                     'azure-pipelines.yml', '.gitlab-ci.yml', 'jenkinsfile',
                     '.tool-versions', '.python-version', '.node-version', '.nvmrc'}
            or name.endswith(('.csproj', '.fsproj', '.vbproj', '.sln', '.slnx', '.tf'))
            or path.startswith(('.github/workflows/', '.circleci/', '.devcontainer/')))


def observed_context(root, manifest):
    repo = manifest['repository']
    for ref in (repo['base_ref'], repo['default_branch']):
        require(ref.startswith('refs/heads/') or ref.startswith('refs/remotes/'), 'Use full branch refs')
        require(git(root, 'check-ref-format', ref, optional=True) is not None, 'Invalid branch ref')
        require(git(root, 'rev-parse', '--verify', ref + '^{commit}', optional=True), 'Missing branch ref: ' + ref)
    remotes = {}
    for remote in git(root, 'remote').splitlines():
        remotes[remote] = {
            'fetch': [safe_remote(url) for url in git(root, 'remote', 'get-url', '--all', remote).splitlines()],
            'push': [safe_remote(url) for url in git(root, 'remote', 'get-url', '--push', '--all', remote).splitlines()],
        }
    if repo['mode'] == 'local-only':
        require(not remotes and repo['primary_remote'] is None, 'local-only requires no configured remotes')
        local_id = git(root, 'config', '--get', 'baseline.repositoryId', optional=True)
        require(local_id and local_id == repo['local_id'], 'local-only repository identity changed or unavailable')
        configured_default = git(root, 'config', '--get', 'baseline.defaultBranch', optional=True)
        require(configured_default == repo['default_branch'],
                'local-only requires git config baseline.defaultBranch <full local branch ref>')
        require(repo['default_branch'].startswith('refs/heads/'), 'local-only default must be a local branch')
        default_branch = configured_default
    else:
        require(repo['local_id'] is None, 'Remote profile must not use local_id')
        primary = repo['primary_remote']
        require(primary in remotes, 'Primary remote is missing')
        default_branch = git(root, 'symbolic-ref', '--quiet', 'refs/remotes/' + primary + '/HEAD', optional=True)
        require(default_branch == repo['default_branch'], 'Observed remote default branch changed or unavailable')
    env_path = confined(root, manifest['environment_file'])
    environment = read_json(env_path)
    validate_document(environment, 'environment')
    execution_platform = {'system': platform.system(), 'machine': platform.machine(),
                          'shell': Path(os.environ.get('SHELL', os.environ.get('COMSPEC', 'unknown'))).name}
    require(execution_platform in environment['execution_platforms'], 'Execution platform is not in the reviewed environment profile')
    sources = {path: blob(root, path) for path in environment['source_paths']}
    # 自动清单覆盖常见配置新增/删除；项目特有配置必须列入 source_paths。
    names = git(root, 'ls-files', '-z', '--cached', '--others', '--exclude-standard', binary=True).decode('utf-8').split('\0')
    inventory = {path: blob(root, path) for path in sorted(set(names)) if path and config_marker(path)}
    require(git(root, 'rev-parse', '--is-shallow-repository') == 'false', 'Full Git history is required; shallow root identity is incomplete')
    return {
        'root_commits': sorted(git(root, 'rev-list', '--max-parents=0', repo['base_ref']).splitlines()),
        'local_id': repo['local_id'],
        'remotes': remotes, 'default_branch': default_branch,
        'base_ref': repo['base_ref'], 'source': dict(manifest['source']),
        'environment': environment, 'environment_blob': blob(root, manifest['environment_file']),
        'environment_sources': sources, 'config_inventory': inventory,
    }


def timestamp(value):
    try:
        parsed = dt.datetime.fromisoformat(value.replace('Z', '+00:00'))
    except ValueError as exc:
        raise Invalid('Invalid evidence timestamp') from exc
    require(parsed.tzinfo is not None, 'Evidence timestamp must include timezone')
    require(parsed <= dt.datetime.now(dt.timezone.utc) + dt.timedelta(minutes=5), 'Evidence timestamp is in the future')


def bound_files(root, paths):
    for path in paths:
        confined(root, path)



def markdown_targets(root, source):
    """解析本地 Markdown 内联链接和单独的 @AGENTS.md 导入；排除注释与代码。"""
    text = confined(root, source).read_text(encoding='utf-8')
    text = re.sub(r'<!--.*?-->', '', text, flags=re.S)
    text = re.sub(r'(?m)^ {0,3}(```+|~~~+).*?^ {0,3}\1[^\n]*$', '', text, flags=re.S | re.M)
    text = re.sub(r'`[^`]*`', '', text)
    raw = re.findall(r'(?<!!)\[[^\]\n]+\]\(([^\s()]+)\)', text)
    if re.search(r'(?m)^@AGENTS\.md\s*$', text):
        raw.append('AGENTS.md')
    targets = set()
    for target in raw:
        if '://' in target or target.startswith('#'):
            continue
        target = target.split('#', 1)[0]
        relative_path(target)
        resolved = (PurePosixPath(source).parent / target).as_posix()
        confined(root, resolved)
        targets.add(resolved)
    return targets

def check_context(root, manifest):
    validate_document(manifest, 'manifest')
    require(manifest['status'] == 'ready', 'Adoption is pending; readiness is not established')
    # 最小控制项独立于可编辑 schema，不能用空映射或缩小 required 绕过。
    for key, minimum in (('controls', MIN_CONTROLS), ('entrypoints', MIN_ENTRYPOINTS), ('navigation', MIN_NAVIGATION)):
        require(set(minimum) <= manifest[key].keys(), 'Missing fixed ' + key)
    require(set(manifest['source']['commit']) != {'0'}, 'Source baseline commit is still a placeholder')
    context = observed_context(root, manifest)
    validate_document(context, 'context')
    for value in context['environment']['dimensions'].values():
        require(value.strip().casefold() not in {'pending', 'unknown', 'todo', 'tbd'}, 'Environment dimension has not been reviewed')
    require(manifest['context'] == context, 'Repository/environment drift: re-inspect and review, even when source SHA is unchanged')
    review = manifest['context_review']
    require(review['status'] == 'approved', 'Context review is pending')
    timestamp(review['reviewed_at'])
    context_report = read_json(confined(root, review['report']))
    validate_document(context_report, 'context_report')
    require(context_report['result'] == 'approved' and context_report['context'] == context,
            'Context review does not approve the current observed context')
    require(context_report['reviewer'] == review['reviewer'] and context_report['adopter'] == review['adopter'], 'Context author/reviewer mismatch')
    for identity in ('actor_id', 'execution_id'):
        require(review['reviewer'][identity].strip().casefold() != review['adopter'][identity].strip().casefold(), 'Context reviewer is not independent: ' + identity)
    require(context_report['reviewed_at'] == review['reviewed_at'], 'Context review timestamp mismatch')
    for group in ('entrypoints', 'navigation'):
        bound_files(root, manifest[group].values())
    require(manifest['entrypoints']['agent_instructions'] == 'AGENTS.md' and manifest['entrypoints']['secondary_instructions'] == 'CLAUDE.md', 'Root AGENTS.md and CLAUDE.md are mandatory fixed entrypoints')
    targets = set()
    for anchor in ('AGENTS.md', 'CLAUDE.md'):
        targets.update(markdown_targets(root, anchor))
    require('AGENTS.md' in markdown_targets(root, 'CLAUDE.md'), 'CLAUDE.md must link or import AGENTS.md')
    for path in set(manifest['entrypoints'].values()) | set(manifest['navigation'].values()):
        require(path in ('AGENTS.md', 'CLAUDE.md') or path in targets, 'Entrypoint navigation does not reference target: ' + path)
    for control_id, control in manifest['controls'].items():
        require(control['status'] != 'pending', 'Pending control: ' + control_id)
        require(control['implementation'] and control['verification'], 'Missing implementation/evidence: ' + control_id)
        if control['status'] in ('adapted', 'omitted'):
            require(control['reason'].strip() and control['replacement'].strip(), 'Adapted/omitted control needs reason and replacement: ' + control_id)
        require(control['objective'].strip(), 'Control objective is missing: ' + control_id)
        bound_files(root, control['implementation'])
        actual = {path: blob(root, path) for path in control['implementation']}
        for path in control['verification']:
            report = read_json(confined(root, path))
            validate_document(report, 'control_report')
            require(report['control_id'] == control_id and report['result'] == 'passed' and report['exit_code'] == 0,
                    'Control verification did not pass: ' + control_id)
            require(report['implementation_blobs'] == actual, 'Stale implementation evidence: ' + control_id)
            for name, expected in (('positive_check', 'accepted'), ('negative_check', 'rejected')):
                check = report[name]
                require(check['observed_result'] == expected and check['expected_result'] == expected, 'Control positive/negative result mismatch: ' + control_id)
                require(check['observed_exit'] == check['expected_exit'], 'Control positive/negative exit mismatch: ' + control_id)
                confined(root, check['output'])
            timestamp(report['observed_at'])
    return context


def oid(value):
    require(re.fullmatch(r'(?:[0-9a-f]{40}|[0-9a-f]{64})', value) is not None, 'Use a full lowercase Git object ID')


def candidate_blob(root, commit, path):
    relative_path(path)
    record = git(root, 'ls-tree', '-z', commit, '--', path, binary=True)
    require(record, 'Required path is absent from candidate: ' + path)
    metadata, observed_path = record.rstrip(b'\0').split(b'\t', 1)
    mode, kind, object_id = metadata.decode().split(' ')
    require(observed_path.decode() == path and kind == 'blob' and mode in ('100644', '100755'),
            'Candidate path is not a regular file: ' + path)
    require(object_id == blob(root, path), 'Working file differs from candidate: ' + path)


def deliver(root, manifest, manifest_path, candidate, evidence_path, base):
    context = check_context(root, manifest)
    oid(candidate)
    require(git(root, 'rev-parse', '--verify', 'HEAD') == candidate, 'Candidate is not current HEAD')
    require(git(root, 'status', '--porcelain', '--untracked-files=no') == '', 'Tracked working tree/index is dirty')
    evidence = read_json(confined(root, evidence_path))
    validate_document(evidence, 'delivery')
    tree = git(root, 'rev-parse', candidate + '^{tree}')
    oid(base)
    require(git(root, 'merge-base', '--is-ancestor', base, context['base_ref'], optional=True) is not None, 'Task base does not belong to the declared base branch history')
    require(base != candidate, 'Delivery needs a nonempty task diff')
    require(git(root, 'merge-base', '--is-ancestor', base, candidate, optional=True) is not None, 'Base is not a candidate ancestor')
    paths = sorted(git(root, 'diff', '--name-only', '--no-renames', '-z', base, candidate, binary=True).decode('utf-8').strip('\0').split('\0'))
    binding = {'candidate_commit': candidate, 'candidate_tree': tree, 'base_commit': base, 'diff_paths': paths}
    for key, expected in binding.items():
        require(evidence[key] == expected, 'Delivery ' + key + ' does not match Git')
    task = evidence['task']
    require(set(paths) <= set(task['scope_paths']), 'Changed paths exceed task scope')
    for path in task['scope_paths']:
        relative_path(path)
    candidate_blob(root, candidate, task['path'])
    required_paths = {manifest_path, manifest['environment_file'], manifest['context_review']['report'],
                      *manifest['entrypoints'].values(), *manifest['navigation'].values(),
                      *context['environment_sources'], *context['config_inventory']}
    for control in manifest['controls'].values():
        required_paths.update(control['implementation'])
        required_paths.update(control['verification'])
        for report_path in control['verification']:
            control_report = read_json(confined(root, report_path))
            required_paths.update(control_report[name]['output'] for name in ('positive_check', 'negative_check'))
    for path in required_paths:
        candidate_blob(root, candidate, path)
    allowed_sidecars = {evidence_path, evidence['verification_report'], evidence['review_report']}
    for key, definition, success in (('verification_report', 'verification_report', 'passed'),
                                     ('review_report', 'review_report', 'approved')):
        report = read_json(confined(root, evidence[key]))
        validate_document(report, definition)
        for field, expected in binding.items():
            require(report[field] == expected, key + ' is stale: ' + field)
        require(report['task_path'] == task['path'], 'Report task mismatch')
        require(report['result'] == success, 'Report is not successful: ' + key)
        timestamp(report['observed_at'])
        if key == 'verification_report':
            for check in report['checks']:
                require(check['result'] == 'passed' and check['exit_code'] == 0, 'Verification check failed')
                confined(root, check['output'])
                allowed_sidecars.add(check['output'])
        else:
            require(report['blocking_findings'] == 0, 'Review has blocking findings')
            for identity in ('actor_id', 'execution_id'):
                require(report['reviewer'][identity].strip().casefold() != task['author'][identity].strip().casefold(),
                        'Reviewer is not independent from author: ' + identity)
    # 允许精确列出的交付旁证，拒绝其他未跟踪文件影响候选验收。
    untracked = {path for path in git(root, 'ls-files', '-z', '--others', '--exclude-standard', binary=True).decode('utf-8').split('\0') if path}
    require(untracked <= allowed_sidecars, 'Untracked files outside declared delivery evidence')
    return binding


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('phase', choices=('inspect', 'check-context', 'deliver'))
    parser.add_argument('--root', default='.')
    parser.add_argument('--manifest', default='docs/agents/baseline-readiness.json')
    parser.add_argument('--candidate')
    parser.add_argument('--base')
    parser.add_argument('--evidence')
    args = parser.parse_args(argv)
    try:
        root = root_path(args.root)
        manifest = read_json(confined(root, args.manifest))
        validate_document(manifest, 'manifest')
        if args.phase == 'inspect':
            require(args.candidate is None and args.evidence is None and args.base is None, 'Candidate/evidence only apply to deliver')
            print(json.dumps(observed_context(root, manifest), indent=2, ensure_ascii=False))
        elif args.phase == 'check-context':
            require(args.candidate is None and args.evidence is None and args.base is None, 'Candidate/evidence only apply to deliver')
            check_context(root, manifest)
            print('PASS: current repository, environment, navigation and controls are ready')
        else:
            require(args.candidate is not None and args.evidence is not None and args.base is not None, 'deliver requires --candidate, --base and --evidence')
            result = deliver(root, manifest, args.manifest, args.candidate, args.evidence, args.base)
            print('PASS: delivery evidence matches ' + result['candidate_commit'])
        return 0
    except (Invalid, OSError, UnicodeError, subprocess.TimeoutExpired, RecursionError) as exc:
        print('FAIL: ' + str(exc), file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
