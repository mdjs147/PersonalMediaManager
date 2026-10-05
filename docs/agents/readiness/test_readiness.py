#!/usr/bin/env python3
"""Synthetic temporary repositories only; no production review or test attestations."""
from __future__ import annotations

import copy
import datetime as dt
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

import validate_readiness as v

NOW = dt.datetime.now(dt.timezone.utc).isoformat()


class ReadinessTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='synthetic-readiness-test-')
        self.root = Path(self.temp.name).resolve()
        self.addCleanup(self.temp.cleanup)
        self.git('init', '-b', 'main')
        self.git('config', 'user.name', 'Synthetic Fixture Author')
        self.git('config', 'user.email', 'synthetic@example.invalid')
        self.git('config', 'baseline.defaultBranch', 'refs/heads/main')
        self.git('config', 'baseline.repositoryId', 'synthetic-local-repository-id')
        self.manifest_path = 'docs/agents/baseline-readiness.json'
        self.m = v.read_json(v.HERE / 'baseline-readiness.template.json')
        self.m['source']['commit'] = '1' * 40
        self.m['repository'] = {'mode': 'local-only', 'reason': 'Synthetic offline unit test, no production remote',
                                'primary_remote': None, 'local_id': 'synthetic-local-repository-id', 'default_branch': 'refs/heads/main', 'base_ref': 'refs/heads/main'}
        env = {'schema_version': 1, 'dimensions': {key: 'Synthetic test: not applicable to production' for key in v.DIMENSIONS},
               'source_paths': ['pyproject.toml'],
               'execution_platforms': [{'system': v.platform.system(), 'machine': v.platform.machine(),
                                         'shell': Path(os.environ.get('SHELL', os.environ.get('COMSPEC', 'unknown'))).name}]}
        self.write(self.m['environment_file'], env)
        self.write('pyproject.toml', '[project]\nname="synthetic-fixture"\nversion="0.0.0"\n')
        for path in set(self.m['entrypoints'].values()) | set(self.m['navigation'].values()):
            self.write(path, 'Synthetic fixture only.\n')
        self.write('AGENTS.md', '\n'.join('[' + path + '](' + path + ')' for path in set(self.m['entrypoints'].values()) | set(self.m['navigation'].values())))
        self.write('CLAUDE.md', '@AGENTS.md\n')
        self.write('control-positive.txt', 'Synthetic positive result only\n')
        self.write('control-negative.txt', 'Synthetic negative result only\n')
        self.write('controls.txt', 'Synthetic declared implementation, never production evidence.\n')
        self.git('add', '.')
        self.git('commit', '-m', 'Synthetic base fixture')
        self.git('checkout', '-b', 'task-fixture')
        for key, control in self.m['controls'].items():
            control.update(status='implemented', implementation=['controls.txt'], verification=['evidence/control-' + key + '.json'])
            self.write(control['verification'][0], {'schema_version': 1, 'control_id': key, 'result': 'passed',
                       'command': 'synthetic-fixture-command', 'exit_code': 0, 'observed_at': NOW,
                       'positive_check': {'command': 'synthetic-positive', 'expected_result': 'accepted', 'observed_result': 'accepted', 'expected_exit': 0, 'observed_exit': 0, 'output': 'control-positive.txt'},
                       'negative_check': {'command': 'synthetic-negative', 'expected_result': 'rejected', 'observed_result': 'rejected', 'expected_exit': 1, 'observed_exit': 1, 'output': 'control-negative.txt'},
                       'implementation_blobs': {'controls.txt': v.blob(self.root, 'controls.txt')}})
        self.m['context_review'] = {'status': 'approved', 'reviewer': {'actor_id': 'synthetic-context-reviewer', 'execution_id': 'context-review-1', 'model': None},
                                    'adopter': {'actor_id': 'synthetic-adopter', 'execution_id': 'context-adopt-1', 'model': None},
                                    'reviewed_at': NOW, 'report': 'evidence/context.json'}
        self.m['status'] = 'ready'
        self.refresh_context()
        self.write('tasks/card.md', 'Synthetic fixture task scope and acceptance only.\n')
        self.git('add', '.')
        self.git('commit', '-m', 'Synthetic candidate')
        self.candidate = self.git('rev-parse', 'HEAD')
        self.binding = {'candidate_commit': self.candidate, 'candidate_tree': self.git('rev-parse', 'HEAD^{tree}'),
                        'base_commit': self.git('rev-parse', 'main'),
                        'diff_paths': sorted(self.git('diff', '--name-only', '--no-renames', 'main', 'HEAD').splitlines())}
        self.author = {'actor_id': 'synthetic-author', 'execution_id': 'synthetic-execution-a', 'model': None}
        self.e = {'schema_version': 1, **self.binding,
                  'task': {'path': 'tasks/card.md', 'scope_paths': self.binding['diff_paths'], 'author': self.author},
                  'verification_report': 'delivery-tests.json', 'review_report': 'delivery-review.json'}
        self.test_report = {'schema_version': 1, 'kind': 'verification', **self.binding,
                            'task_path': 'tasks/card.md', 'result': 'passed', 'observed_at': NOW,
                            'executed_by': self.author,
                            'checks': [{'command': 'synthetic-positive-and-negative-fixture', 'exit_code': 0,
                                        'result': 'passed', 'output': 'delivery-output.txt'}]}
        self.review_report = {'schema_version': 1, 'kind': 'review', **self.binding,
                              'task_path': 'tasks/card.md', 'result': 'approved', 'observed_at': NOW,
                              'reviewer': {'actor_id': 'synthetic-reviewer', 'execution_id': 'synthetic-execution-b', 'model': None},
                              'blocking_findings': 0, 'findings': 'Synthetic fixture approval, not a real review.'}
        self.write('delivery.json', self.e)
        self.write('delivery-tests.json', self.test_report)
        self.write('delivery-review.json', self.review_report)
        self.write('delivery-output.txt', 'Synthetic fixture output only.\n')

    def git(self, *args):
        return v.git(self.root, *args)

    def write(self, path, content):
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps(content, indent=2) + '\n' if isinstance(content, dict) else content, encoding='utf-8')

    def refresh_context(self):
        self.m['context'] = v.observed_context(self.root, self.m)
        self.write('evidence/context.json', {'schema_version': 1, 'result': 'approved', 'context': self.m['context'],
                    'reviewer': self.m['context_review']['reviewer'], 'adopter': self.m['context_review']['adopter'],
                    'scope': 'repository-environment-adoption', 'reviewed_at': NOW, 'findings': 'Synthetic context reviewed.'})
        self.write(self.manifest_path, self.m)

    def ready(self):
        return v.check_context(self.root, self.m)

    def delivery(self):
        return v.deliver(self.root, self.m, self.manifest_path, self.candidate, 'delivery.json', self.binding['base_commit'])

    def reject_context(self, pattern=None):
        with self.assertRaisesRegex(v.Invalid, pattern or '.'):
            self.ready()

    def reject_delivery(self, pattern=None):
        with self.assertRaisesRegex(v.Invalid, pattern or '.'):
            self.delivery()

    def test_ready_local_only_and_exact_delivery(self):
        self.ready()
        self.assertEqual(self.delivery(), self.binding)

    def test_pending_template_fails(self):
        self.m = v.read_json(v.HERE / 'baseline-readiness.template.json')
        self.reject_context('pending')

    def test_ready_boolean_is_not_status(self):
        self.m['status'] = True
        self.reject_context('invalid type')

    def test_missing_fixed_controls(self):
        self.m['controls'] = {}
        self.reject_context('missing required')

    def test_empty_navigation_rejected(self):
        self.m['navigation'] = {}
        self.reject_context('missing required')

    def test_pending_control_cannot_be_ready(self):
        self.m['controls']['branch_gate']['status'] = 'pending'
        self.reject_context('Pending control')

    def test_omitted_control_without_replacement_rejected(self):
        self.m['controls']['branch_gate']['status'] = 'omitted'
        self.reject_context('reason and replacement')

    def test_adapted_with_preserved_objective_and_evidence(self):
        self.m['controls']['branch_gate'].update(status='adapted', reason='Synthetic differing platform', replacement='Synthetic alternative implementation')
        self.ready()

    def test_missing_control_report_rejected(self):
        (self.root / self.m['controls']['branch_gate']['verification'][0]).unlink()
        self.reject_context('Missing regular')

    def test_failed_control_report_rejected(self):
        path = self.m['controls']['branch_gate']['verification'][0]
        report = v.read_json(self.root / path)
        report['result'] = 'failed'
        self.write(path, report)
        self.reject_context('did not pass')

    def test_nonzero_control_exit_rejected(self):
        path = self.m['controls']['branch_gate']['verification'][0]
        report = v.read_json(self.root / path)
        report['exit_code'] = 2
        self.write(path, report)
        self.reject_context('did not pass')

    def test_stale_control_implementation_rejected(self):
        self.write('controls.txt', 'Changed implementation\n')
        self.reject_context('Stale implementation')

    def test_entrypoint_missing_rejected(self):
        (self.root / 'CLAUDE.md').unlink()
        self.reject_context('Missing regular')

    def test_navigation_not_linked_from_entrypoints_rejected(self):
        self.write('AGENTS.md', 'No navigation here\n')
        self.reject_context('navigation does not reference')

    def test_symlink_file_rejected(self):
        path = self.root / 'CLAUDE.md'
        path.unlink()
        path.symlink_to(self.root / 'AGENTS.md')
        self.reject_context('Symlink')

    def test_path_traversal_rejected(self):
        self.m['navigation']['adoption'] = '../outside.md'
        self.reject_context('Unsafe path')

    def test_backslash_and_git_paths_rejected(self):
        for path in ['..\\escape', '.git/config', '/etc/passwd', 'docs/../escape', 'docs//file', 'C:/file']:
            with self.subTest(path=path), self.assertRaises(v.Invalid):
                v.relative_path(path)

    def test_cannot_redirect_root_agents_to_bypass_deletion(self):
        self.m['entrypoints']['agent_instructions'] = 'controls.txt'
        (self.root / 'AGENTS.md').unlink()
        self.reject_context('mandatory fixed')

    def test_comment_only_navigation_rejected(self):
        text = (self.root / 'AGENTS.md').read_text()
        self.write('AGENTS.md', '<!--' + text + '-->')
        self.reject_context('navigation does not reference')

    def test_code_only_navigation_rejected(self):
        text = (self.root / 'AGENTS.md').read_text()
        self.write('AGENTS.md', '```markdown\n' + text + '\n```\n')
        self.reject_context('navigation does not reference')

    def test_bad_markdown_link_rejected(self):
        self.write('AGENTS.md', '[rules](missing-file.md)\n')
        self.reject_context('Missing regular')

    def test_context_self_review_rejected(self):
        self.m['context_review']['reviewer'] = self.m['context_review']['adopter']
        self.refresh_context()
        self.reject_context('not independent')

    def test_negative_control_case_not_rejected(self):
        path = self.m['controls']['branch_gate']['verification'][0]
        report = v.read_json(self.root / path)
        report['negative_check']['observed_result'] = 'accepted'
        self.write(path, report)
        self.reject_context('positive/negative result mismatch')

    def test_local_identity_change_rejected(self):
        self.git('config', 'baseline.repositoryId', 'another-synthetic-id')
        self.reject_context('identity changed')

    def test_new_worktree_is_portable_without_manifest_edits(self):
        with tempfile.TemporaryDirectory(prefix='synthetic-worktree-') as directory:
            target = Path(directory) / 'checkout'
            self.git('worktree', 'add', '--detach', str(target), self.candidate)
            try:
                v.check_context(target.resolve(), self.m)
            finally:
                self.git('worktree', 'remove', str(target))

    def test_unapproved_runtime_platform_rejected(self):
        env = v.read_json(self.root / self.m['environment_file'])
        env['execution_platforms'] = [{'system': 'SyntheticOtherOS', 'machine': 'fixture', 'shell': 'fixture'}]
        self.write(self.m['environment_file'], env)
        self.reject_context('Execution platform')

    def test_same_source_sha_remote_added_rejected(self):
        self.git('remote', 'add', 'origin', 'https://example.invalid/synthetic.git')
        self.reject_context('local-only')

    def test_remote_profile_valid_and_remote_migration_rejected(self):
        self.git('remote', 'add', 'origin', 'https://example.invalid/synthetic.git')
        self.git('update-ref', 'refs/remotes/origin/main', self.git('rev-parse', 'main'))
        self.git('symbolic-ref', 'refs/remotes/origin/HEAD', 'refs/remotes/origin/main')
        self.m['repository'].update(mode='remote', local_id=None, primary_remote='origin', default_branch='refs/remotes/origin/main', base_ref='refs/remotes/origin/main')
        self.refresh_context()
        self.ready()
        self.git('remote', 'set-url', 'origin', 'https://example.invalid/migrated.git')
        self.reject_context('drift')

    def test_default_branch_change_rejected(self):
        self.git('config', 'baseline.defaultBranch', 'refs/heads/task-fixture')
        self.reject_context('defaultBranch')

    def test_new_task_branch_does_not_invalidate_context(self):
        self.git('checkout', '-b', 'another-task')
        self.ready()

    def test_base_branch_advance_preserves_valid_governance_context(self):
        self.git('update-ref', 'refs/heads/main', self.candidate)
        self.ready()
        self.delivery()

    def test_merged_main_fresh_clone_context_passes(self):
        self.git('remote', 'add', 'origin', 'https://example.invalid/synthetic.git')
        self.git('update-ref', 'refs/remotes/origin/main', self.git('rev-parse', 'main'))
        self.git('symbolic-ref', 'refs/remotes/origin/HEAD', 'refs/remotes/origin/main')
        self.m['repository'].update(mode='remote', local_id=None, primary_remote='origin', default_branch='refs/remotes/origin/main', base_ref='refs/remotes/origin/main')
        self.refresh_context()
        self.git('add', self.manifest_path, 'evidence/context.json')
        self.git('commit', '-m', 'Synthetic portable remote context')
        self.git('checkout', 'main')
        self.git('merge', '--ff-only', 'task-fixture')
        with tempfile.TemporaryDirectory(prefix='synthetic-fresh-clone-') as directory:
            target = Path(directory) / 'checkout'
            self.git('clone', '--no-hardlinks', str(self.root), str(target))
            v.git(target, 'remote', 'set-url', 'origin', 'https://example.invalid/synthetic.git')
            clone_manifest = v.read_json(target / self.manifest_path)
            v.check_context(target.resolve(), clone_manifest)

    def test_source_version_change_requires_context_review(self):
        self.m['source']['commit'] = '2' * 40
        self.reject_context('drift')

    def test_delivery_cannot_silently_change_explicit_task_base(self):
        self.e['base_commit'] = self.candidate
        self.write('delivery.json', self.e)
        self.reject_delivery('does not match Git')

    def test_environment_config_change_rejected_same_source_sha(self):
        self.write('pyproject.toml', '[project]\nname="changed"\n')
        self.reject_context('drift')

    def test_new_ci_configuration_rejected(self):
        self.write('.github/workflows/new.yml', 'name: synthetic\n')
        self.reject_context('drift')

    def test_missing_environment_sources_rejected(self):
        env = v.read_json(self.root / self.m['environment_file'])
        env['source_paths'] = []
        self.write(self.m['environment_file'], env)
        self.reject_context('too few')

    def test_unknown_environment_not_ready(self):
        env = v.read_json(self.root / self.m['environment_file'])
        env['dimensions']['deployment_network'] = 'pending'
        self.write(self.m['environment_file'], env)
        self.refresh_context()
        self.reject_context('has not been reviewed')

    def test_failed_context_approval_rejected(self):
        report = v.read_json(self.root / 'evidence/context.json')
        report['result'] = 'rejected'
        self.write('evidence/context.json', report)
        self.reject_context('does not approve')

    def test_inspect_is_read_only_and_pending_allowed(self):
        self.m['status'] = 'pending'
        self.write(self.manifest_path, self.m)
        before = {str(p.relative_to(self.root)): p.read_bytes() for p in self.root.rglob('*') if p.is_file() and '.git' not in p.parts}
        command = subprocess.run([os.sys.executable, str(v.HERE / 'validate_readiness.py'), 'inspect', '--root', str(self.root)], capture_output=True, text=True)
        self.assertEqual(command.returncode, 0, command.stderr)
        self.assertEqual(json.loads(command.stdout), self.m['context'])
        after = {str(p.relative_to(self.root)): p.read_bytes() for p in self.root.rglob('*') if p.is_file() and '.git' not in p.parts}
        self.assertEqual(before, after)

    def test_delivery_candidate_mismatch_rejected(self):
        self.e['candidate_commit'] = '2' * 40
        self.write('delivery.json', self.e)
        self.reject_delivery('does not match Git')

    def test_delivery_stale_review_tree_rejected(self):
        self.review_report['candidate_tree'] = '2' * 40
        self.write('delivery-review.json', self.review_report)
        self.reject_delivery('is stale')

    def test_delivery_failed_review_rejected(self):
        self.review_report['result'] = 'changes_requested'
        self.write('delivery-review.json', self.review_report)
        self.reject_delivery('not successful')

    def test_delivery_self_review_rejected(self):
        self.review_report['reviewer'] = self.author
        self.write('delivery-review.json', self.review_report)
        self.reject_delivery('not independent')

    def test_delivery_same_execution_different_name_rejected(self):
        self.review_report['reviewer']['execution_id'] = self.author['execution_id']
        self.write('delivery-review.json', self.review_report)
        self.reject_delivery('not independent')

    def test_delivery_blocking_findings_rejected(self):
        self.review_report['blocking_findings'] = 1
        self.write('delivery-review.json', self.review_report)
        self.reject_delivery('blocking')

    def test_delivery_failed_test_rejected(self):
        self.test_report['checks'][0]['exit_code'] = 1
        self.write('delivery-tests.json', self.test_report)
        self.reject_delivery('Verification check failed')

    def test_delivery_missing_test_output_rejected(self):
        (self.root / 'delivery-output.txt').unlink()
        self.reject_delivery('Missing regular')

    def test_delivery_out_of_scope_rejected(self):
        self.e['task']['scope_paths'] = ['tasks/card.md']
        self.write('delivery.json', self.e)
        self.reject_delivery('exceed task scope')

    def test_delivery_missing_task_rejected(self):
        self.e['task']['path'] = 'tasks/missing.md'
        self.write('delivery.json', self.e)
        self.reject_delivery('absent from candidate')

    def test_delivery_dirty_tracked_tree_rejected(self):
        self.write('tasks/card.md', 'Dirty changes\n')
        self.reject_delivery('dirty')

    def test_delivery_undeclared_untracked_file_rejected(self):
        self.write('unlisted-source.py', 'print("synthetic")\n')
        self.reject_delivery('Untracked files')

    def test_duplicate_json_keys_rejected(self):
        self.write('duplicate.json', '{"status":"pending","status":"ready"}')
        with self.assertRaisesRegex(v.Invalid, 'Duplicate'):
            v.read_json(self.root / 'duplicate.json')

    def test_nan_and_malformed_json_rejected(self):
        for content in ['{"n":NaN}', '{bad', '[null']:
            self.write('bad.json', content)
            with self.subTest(content=content), self.assertRaises(v.Invalid):
                v.read_json(self.root / 'bad.json')

    def test_schema_unknown_fields_rejected(self):
        self.m['unrecognized'] = 'anything'
        self.reject_context('unknown field')

    def test_schema_does_not_treat_boolean_as_integer(self):
        self.m['schema_version'] = True
        self.reject_context('invalid constant')

    def test_unknown_schema_keywords_fail_closed(self):
        with self.assertRaisesRegex(v.Invalid, 'Unsupported'):
            v.validate_schema({}, {'unevaluatedProperties': False}, {})

    def test_timestamp_in_future_rejected(self):
        self.review_report['observed_at'] = '2999-01-01T00:00:00Z'
        self.write('delivery-review.json', self.review_report)
        self.reject_delivery('future')

    def test_remote_credentials_not_echoed(self):
        self.git('remote', 'add', 'origin', 'https://synthetic-user:NOT-A-REAL-SECRET@example.invalid/repo.git')
        with self.assertRaises(v.Invalid) as raised:
            v.observed_context(self.root, self.m)
        self.assertNotIn('NOT-A-REAL-SECRET', str(raised.exception))


if __name__ == '__main__':
    unittest.main()
