"""Fresh PMM governance tests; isolated clones and injected violations only."""
import contextlib
import copy
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'scripts'))
import pmm_governance as g
import verify_governance as verify

class GovernanceTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(prefix='pmm-governance-fixture-');self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name)/'repo'
        subprocess.run(['git','clone','--quiet','--no-hardlinks',str(ROOT),str(self.root)],check=True,capture_output=True)
        # Historical BA001 compatibility fixture, not current successor acceptance.
        self.git("checkout", "--detach", "a59311ae16b08d0268babc933abf7403a14a315f")
        for key,value in [('user.name','PMM synthetic fixture'),('user.email','fixture@example.invalid'),('pmm.actorId',g.AUTHOR)]:self.git('config',key,value)
        self.git('remote','set-url','origin',g.REPOSITORY)
        self.git('update-ref','refs/remotes/origin/main','c09befa02cc27a0dc358322779798bb5e6d295e4')
        self.git('update-ref','refs/remotes/origin/'+g.STAGE.removeprefix('refs/heads/'),'a59311ae16b08d0268babc933abf7403a14a315f')
        self.git('symbolic-ref','refs/remotes/origin/HEAD','refs/remotes/origin/main')
        self.git('checkout','-B',g.TASK.removeprefix('refs/heads/'))
        self.git('add','.')
        self.git('commit','--allow-empty','-m','Synthetic governance target fixture')
        # Historical hooks observe a real isolated bare remote, independent of
        # whichever task or remote is active in the enclosing source checkout.
        bare=Path(self.temp.name)/'historical-remote.git'
        subprocess.run(['git','clone','--quiet','--bare',str(self.root),str(bare)],check=True,capture_output=True)
        for ref,oid in [('refs/heads/main','c09befa02cc27a0dc358322779798bb5e6d295e4'),(g.STAGE,'a59311ae16b08d0268babc933abf7403a14a315f')]:
            subprocess.run(['git','--git-dir',str(bare),'update-ref',ref,oid],check=True,capture_output=True)
        subprocess.run(['git','--git-dir',str(bare),'symbolic-ref','HEAD','refs/heads/main'],check=True,capture_output=True)
        self.transport_dir=Path(self.temp.name)/'transport';self.transport_dir.mkdir()
        adapter="import subprocess\n_original=subprocess.run\ndef _run(args,*a,**kw):\n    if isinstance(args,list) and args[:2]==['git','ls-remote']:\n        if 'origin' in args: args=["+repr(str(bare))+" if x=='origin' else x for x in args]\n        else: return subprocess.CompletedProcess(args,1,'','Synthetic historical upstream discovery unavailable')\n    return _original(args,*a,**kw)\nsubprocess.run=_run\n"
        (self.transport_dir/'sitecustomize.py').write_text(adapter)
    def git(self,*args):return g.v.git(self.root,*args)
    def write_json(self,path,value):(self.root/path).write_text(json.dumps(value)+'\n')
    def profile(self):return g.v.read_json(self.root/'docs/agents/governance-profile.json')
    def reject(self,fn,pattern):
        with self.assertRaisesRegex(g.v.Invalid,pattern):fn()
    def test_correct_published_owned_scope_and_actual_runtime(self):
        g.cards(self.root,live=False);g.branch_owner(self.root,g.AUTHOR);g.scoped(self.root);g.runtime(self.root);g.baseline(self.root);g.documentation(self.root)
    def test_pending_repair_is_limited_to_published_scope(self):
        m=g.v.read_json(self.root/g.MANIFEST);m['status']='pending';self.write_json(g.MANIFEST,m)
        g.context(self.root,'adoption',allow_pending=True)
        self.reject(lambda:g.context(self.root,'business',allow_pending=True),'Business writes blocked')
    def test_ready_never_unlocks_business(self):
        m=g.v.read_json(self.root/g.MANIFEST);m['status']='ready';self.write_json(g.MANIFEST,m)
        self.reject(lambda:g.context(self.root,'business'),'Business writes blocked')
    def test_phase_change_requires_review(self):
        p=self.profile();p['phase']='product';self.write_json('docs/agents/governance-profile.json',p)
        self.reject(lambda:g.read_profile(self.root),'Phase transition')
    def test_wrong_actor_rejected(self):self.reject(lambda:g.branch_owner(self.root,'independent-reviewer'),'authorized implementation actor')
    def test_missing_local_ownership_rejected(self):
        self.git('config','--unset','pmm.actorId');self.reject(lambda:g.branch_owner(self.root,g.AUTHOR),'ownership')
    def test_wrong_branch_rejected(self):
        self.git('checkout','-b','wrong-task');self.reject(lambda:g.branch_owner(self.root,g.AUTHOR),'Wrong task branch')
    def test_detached_ci_has_no_author_write_permission(self):
        self.git('checkout','--detach');self.reject(lambda:g.branch_owner(self.root,g.AUTHOR),'detached author')
    def test_wrong_fetch_remote_rejected(self):
        self.git('remote','set-url','origin','https://example.invalid/wrong.git');self.reject(lambda:g.cards(self.root,live=False),'Wrong actual')
    def test_wrong_push_remote_rejected(self):
        self.git('remote','set-url','--push','origin','https://example.invalid/wrong.git');self.reject(lambda:g.cards(self.root,live=False),'Wrong actual')
    def test_wrong_default_branch_rejected(self):
        self.git('symbolic-ref','refs/remotes/origin/HEAD','refs/remotes/origin/'+g.STAGE.removeprefix('refs/heads/'))
        self.reject(lambda:g.cards(self.root,live=False),'cached default')
    def test_changed_local_card_rejected(self):
        (self.root/'docs/workflow/BA-001.md').write_text('Changed scope\n');self.reject(lambda:g.cards(self.root,live=False),'card changed')
    def test_unpublished_stage_plan_rejected(self):
        self.git('update-ref','refs/remotes/origin/'+g.STAGE.removeprefix('refs/heads/'),'c09befa02cc27a0dc358322779798bb5e6d295e4')
        self.reject(lambda:g.cards(self.root,live=False),'not in actual stage history')
    def test_out_of_scope_path_rejected(self):
        (self.root/'src').mkdir();(self.root/'src/application.py').write_text('print(1)\n');self.reject(lambda:g.scoped(self.root),'exceeds frozen')
    def test_dirty_first_write_rejected(self):
        (self.root/'README.md').write_text('Uncommitted owned change\n')
        self.reject(lambda:g.preflight(self.root,'first-write',g.AUTHOR,'adoption',live=False),'clean owned worktree')
    def test_missing_navigation_rejected(self):
        (self.root/'AGENTS.md').write_text('[Missing](docs/missing.md)\n');self.reject(lambda:g.documentation(self.root),'Missing regular file')
    def test_source_identity_mismatch_rejected(self):
        m=g.v.read_json(self.root/g.MANIFEST);m['source']['commit']='a'*40;self.write_json(g.MANIFEST,m)
        self.reject(lambda:g.baseline(self.root),'source mismatch')
    def test_source_asset_byte_drift_rejected(self):
        p=self.root/'docs/agents/readiness/validate_readiness.py';p.write_text(p.read_text()+'\n# injected drift\n')
        self.reject(lambda:g.baseline(self.root),'source asset changed')
    def test_public_tree_secret_fixture_rejected(self):
        (self.root/'docs/agents/injected.txt').write_text('ghp_'+'a'*40)
        self.reject(lambda:g.public_tree(self.root),'credential-like')
    def test_wrong_git_target_rejected_before_mutation(self):
        self.reject(lambda:g.git_target(self.root,g.AUTHOR,'integrator','push','refs/heads/main'),'Forbidden Git target')
    def test_stage_git_role_is_checked(self):
        self.reject(lambda:g.git_target(self.root,g.AUTHOR,'author','push',g.STAGE),'Wrong role')
        self.git('update-ref','refs/remotes/origin/'+g.STAGE.removeprefix('refs/heads/'),'a59311ae16b08d0268babc933abf7403a14a315f')
        g.git_target(self.root,g.AUTHOR,'integrator','push',g.STAGE,self.git('rev-parse','HEAD'),self.git('rev-parse','refs/remotes/origin/'+g.STAGE.removeprefix('refs/heads/')),'origin',g.REPOSITORY,g.TASK,live=False)
    def test_first_write_and_recovery_invoke_context_gate(self):
        for phase in ['first-write','recover']:
            with mock.patch.object(g,'context',side_effect=g.v.Invalid('injected context refusal')) as called:
                self.reject(lambda:g.preflight(self.root,phase,g.AUTHOR,'adoption',live=False),'injected context refusal')
                called.assert_called_once()
    def test_delivery_rejects_nonpublished_base_before_evidence(self):
        self.reject(lambda:g.delivery(self.root,self.git('rev-parse','HEAD'),'c09befa02cc27a0dc358322779798bb5e6d295e4','absent.json'),'published BA-001')
    def test_git_registered_hook_lifecycle_and_call_removal(self):
        self.git('config','core.hooksPath','.githooks')
        env=dict(os.environ,PYTHONPATH=str(self.transport_dir),PMM_ACTOR='wrong-actor')
        wrong=subprocess.run(['git','commit','--allow-empty','-m','Rejected actor probe'],cwd=self.root,env=env,capture_output=True,text=True)
        self.assertNotEqual(wrong.returncode,0);self.assertIn('authorized implementation actor',wrong.stderr)
        env['PMM_ACTOR']=g.AUTHOR
        correct=subprocess.run(['git','commit','--allow-empty','-m','Accepted actual registered hook probe'],cwd=self.root,env=env,capture_output=True,text=True,timeout=120)
        self.assertEqual(correct.returncode,0,correct.stdout+correct.stderr);self.assertIn('PMM_GATE_VERIFIED commit',correct.stdout+correct.stderr)
        hook=self.root/'.githooks/pre-commit';hook.write_text('#!/bin/sh\ntrue\n');hook.chmod(0o755)
        self.git('add','.githooks/pre-commit');env['PMM_ACTOR']='wrong-actor'
        removed=subprocess.run(['git','commit','-m','Removed-call witness in isolated clone'],cwd=self.root,env=env,capture_output=True,text=True)
        self.assertEqual(removed.returncode,0,'The refusal assertion must detect an intentionally removed registered call')
        self.assertNotEqual(wrong.returncode,removed.returncode)
    def test_reduced_import_map_cannot_hide_validator_change(self):
        p='docs/agents/source-assets.json';a=g.v.read_json(self.root/p);a['git_blobs']={};self.write_json(p,a)
        (self.root/'docs/agents/readiness/validate_readiness.py').write_text('# changed validator\n')
        self.reject(lambda:g.baseline(self.root),'source asset map')
    def test_source_tree_binding_cannot_change(self):
        p='docs/agents/source-assets.json';a=g.v.read_json(self.root/p);a['source_tree']='b'*40;self.write_json(p,a)
        self.reject(lambda:g.baseline(self.root),'source tree mismatch')
    def test_task_branch_is_not_a_publication_target(self):
        self.reject(lambda:g.git_target(self.root,g.AUTHOR,'author','push',g.TASK),'Forbidden Git target')
    def test_detached_checkout_cannot_authorize_git_operation(self):
        candidate=self.git('rev-parse','HEAD');self.git('checkout','--detach')
        self.reject(lambda:g.git_target(self.root,g.AUTHOR,'integrator','push',g.STAGE,candidate,g.BASE,'origin',g.REPOSITORY,'HEAD',live=False),'cannot publish from this branch')
    def test_actual_outgoing_remote_is_bound(self):
        self.reject(lambda:g.git_target(self.root,g.AUTHOR,'integrator','push',g.STAGE,self.git('rev-parse','HEAD'),g.BASE,'origin','https://example.invalid/other.git',g.TASK,live=False),'Actual outgoing remote')
    def test_concurrent_remote_old_sha_is_rejected(self):
        self.reject(lambda:g.git_target(self.root,g.AUTHOR,'integrator','push',g.STAGE,self.git('rev-parse','HEAD'),'a'*40,'origin',g.REPOSITORY,g.TASK,live=False),'Remote target changed')
    def test_non_fast_forward_actual_target_is_rejected(self):
        candidate=self.git('rev-parse','HEAD')
        self.git('checkout','-b','remote-divergence',g.BASE)
        (self.root/'divergent.txt').write_text('Remote concurrent change\n');self.git('add','divergent.txt');self.git('commit','-m','Synthetic remote divergence')
        remote=self.git('rev-parse','HEAD');self.git('update-ref','refs/remotes/origin/'+g.STAGE.removeprefix('refs/heads/'),remote)
        self.git('checkout',g.TASK.removeprefix('refs/heads/'))
        self.reject(lambda:g.git_target(self.root,g.AUTHOR,'integrator','push',g.STAGE,candidate,remote,'origin',g.REPOSITORY,g.TASK,live=False),'Non-fast-forward')
    def test_actual_commit_hook_rejects_wrong_actor_and_removal_is_detected(self):
        env=dict(os.environ,PYTHONPATH=str(self.transport_dir),PMM_ACTOR='wrong-actor')
        actual=subprocess.run(['bash','.githooks/pre-commit'],cwd=self.root,env=env,capture_output=True,text=True)
        self.assertNotEqual(actual.returncode,0);self.assertIn('authorized implementation actor',actual.stderr)
        (self.root/'.githooks/pre-commit').write_text('#!/bin/sh\ntrue\n')
        bypass=subprocess.run(['bash','.githooks/pre-commit'],cwd=self.root,env=env,capture_output=True,text=True)
        self.assertEqual(bypass.returncode,0)
        self.assertNotEqual(actual.returncode,bypass.returncode,'Negative hook assertion must detect a removed call')
    def test_actual_push_hook_rejects_main_before_evidence(self):
        env=dict(os.environ,PYTHONPATH=str(self.transport_dir),PMM_ACTOR=g.AUTHOR,PMM_GIT_ROLE='integrator')
        result=subprocess.run(['bash','.githooks/pre-push'],input=g.TASK+' '+g.BASE+' refs/heads/main '+g.BASE+'\n',cwd=self.root,env=env,capture_output=True,text=True)
        self.assertNotEqual(result.returncode,0);self.assertIn('Forbidden Git target',result.stderr)
    def test_verifier_invokes_actual_context_gate(self):
        # If the call disappears, this test must fail instead of accepting mere file presence.
        with mock.patch.object(g,'ROOT',self.root),mock.patch.object(g,'runtime'),mock.patch.object(g,'baseline'),mock.patch.object(g,'public_tree'),mock.patch.object(g,'documentation'),mock.patch.object(g,'context',side_effect=g.v.Invalid('injected context refusal')) as called:
            with contextlib.redirect_stderr(io.StringIO()):result=verify.main([])
            self.assertEqual(result,1);called.assert_called_once()
    def test_verifier_propagates_required_suite_failure(self):
        with mock.patch.object(g,'ROOT',self.root),mock.patch.object(g,'runtime'),mock.patch.object(g,'baseline'),mock.patch.object(g,'public_tree'),mock.patch.object(g,'documentation'),mock.patch.object(g,'context'),mock.patch.object(unittest.TestLoader,'discover') as discover,mock.patch.object(g.v,'root_path',return_value=self.root),mock.patch.object(subprocess,'run') as command:
            discover.return_value.countTestCases.return_value=1;command.return_value.returncode=1
            with contextlib.redirect_stderr(io.StringIO()):self.assertEqual(verify.main([]),1)
    def test_verifier_rejects_zero_discovered_tests(self):
        with mock.patch.object(g,'ROOT',self.root),mock.patch.object(g,'runtime'),mock.patch.object(g,'baseline'),mock.patch.object(g,'public_tree'),mock.patch.object(g,'documentation'),mock.patch.object(g,'context'),mock.patch.object(unittest.TestLoader,'discover') as discover:
            discover.return_value.countTestCases.return_value=0
            with contextlib.redirect_stderr(io.StringIO()):self.assertEqual(verify.main([]),1)

if __name__=='__main__':unittest.main()
