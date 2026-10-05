"""Fresh GOV002 regressions in disposable real-Git fixture repositories.

All remote observations in this module are synthetic. No fixture evidence may
be exported as product, public-remote, or production acceptance evidence.
"""
from __future__ import annotations

import contextlib
import copy
import datetime as dt
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

ROOT = Path(__file__).resolve().parents[2]
FIXTURE_PLAN = "8437301c86c44e74beed3ac7220426741e67094b"
FIXTURE_MAIN = "c09befa02cc27a0dc358322779798bb5e6d295e4"
sys.path.insert(0, str(ROOT / "scripts"))
import pmm_governance as g
import pmm_workflow as w
import verify_governance as verify


class SuccessorTests(unittest.TestCase):
    """Each test gets a separate repository, index, refs and worktree."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="pmm-successor-synthetic-")
        self.addCleanup(self.temp.cleanup)
        self.home = Path(self.temp.name)
        self.root = self.home / "author-one"
        self.command("git", "clone", "--quiet", "--no-hardlinks", str(ROOT), str(self.root), cwd=self.home)
        # The immutable empty-product plan is fixture authority. A future active
        # task, environment, manifest or source tree in ROOT is never imported.
        self.git("checkout", "--detach", FIXTURE_PLAN)
        current_entrypoints = {
            "scripts/pmm_governance.py", "scripts/pmm_workflow.py", "scripts/verify_governance.py",
            ".githooks/pre-commit", ".githooks/pre-push", ".github/workflows/verify-governance.yml",
            "AGENTS.md", "CLAUDE.md", ".claude/rules/git-workflow.md",
            ".claude/rules/guard-discipline.md", ".claude/agents/code-reviewer.md",
        }
        names = g.v.git(ROOT, "ls-files", "--cached", "--others", "--exclude-standard").splitlines()
        for name in names:
            if name not in current_entrypoints and not name.startswith("tests/governance/"):
                continue
            source = ROOT / name
            if not source.is_file():
                continue
            target = self.root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
        self.git("config", "user.name", "Synthetic PMM regression author")
        self.git("config", "user.email", "synthetic@example.invalid")
        self.git("config", "core.hooksPath", str(self.home / "no-hooks"))
        self.git("config", "extensions.worktreeConfig", "true")
        self.git("remote", "set-url", "origin", g.REPOSITORY)
        self.git("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main")
        self.git("update-ref", "refs/remotes/origin/main", FIXTURE_MAIN)
        self.git("update-ref", "refs/remotes/origin/" + g.STAGE.removeprefix("refs/heads/"), FIXTURE_PLAN)
        self.git("add", "--all")
        self.git("commit", "--quiet", "--allow-empty", "-m", "Synthetic fresh governance fixture inputs")
        plan_path = "docs/workflow/GOV002-plan-publication.json"
        self.task = w.descriptor(w.object_json(self.root, FIXTURE_PLAN, plan_path), "GOV002")
        self.author = copy.deepcopy(self.task["author"])
        self.state = {
            "schema_version": 1, "anchor": w.ANCHOR, "active_task": "GOV002",
            "plan_commit": FIXTURE_PLAN, "plan_path": plan_path,
            "activation": {"task_id": "GOV002", "plan_commit": FIXTURE_PLAN, "base_commit": FIXTURE_PLAN,
                           "phase": self.task["phase"], "purpose": self.task["purpose"], "author": self.author,
                           "authorized_executions": [self.author], "predecessors": {"BA-001": {"commit": w.ANCHOR}}},
            "activation_review": "docs/agents/readiness/evidence/synthetic-activation.json",
        }
        self.git("checkout", "-B", self.task["task_branch"].removeprefix("refs/heads/"))
        self.assign(self.author)
        self.write_json(w.STATE, self.state)
        self.synthetic_baseline(self.author)
        self.activation_review()
        self.candidate = self.commit("Synthetic initial reviewed activation")
        self.bare = self.home / "synthetic-remote.git"
        self.command("git", "init", "--quiet", "--bare", "--initial-branch=main", str(self.bare))
        self.publish(self.git("rev-parse", "refs/remotes/origin/main"), "refs/heads/main")
        self.publish(self.git("rev-parse", "refs/remotes/origin/" + w.STAGE.removeprefix("refs/heads/")))
        original_run = g.run
        def transport(args, root=g.ROOT):
            if args[:2] == ["git", "ls-remote"]:
                if "origin" in args:
                    redirected = [str(self.bare) if x == "origin" else x for x in args]
                    return original_run(redirected, root)
                return subprocess.CompletedProcess(args, 1, "", "Synthetic fixture: upstream discovery unavailable")
            return original_run(args, root)
        patcher = mock.patch.object(g, "run", side_effect=transport)
        patcher.start()
        self.addCleanup(patcher.stop)

    def command(self, *args, cwd=None, check=True, env=None, input=None):
        actual_env = {key: value for key, value in os.environ.items() if not key.startswith("GIT_")}
        actual_env.update(SHELL="/bin/bash", GIT_TERMINAL_PROMPT="0", GIT_NO_REPLACE_OBJECTS="1", LC_ALL="C")
        if env:
            actual_env.update(env)
        result = subprocess.run(args, cwd=cwd or self.root, env=actual_env,
                                input=input, text=True, capture_output=True, timeout=120)
        if check:
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        return result

    def git(self, *args, root=None):
        return self.command("git", *args, cwd=root or self.root).stdout.strip()

    def read_json(self, path, root=None):
        return json.loads(((root or self.root) / path).read_text())

    def write_json(self, path, value, root=None):
        target = (root or self.root) / path
        self.assertTrue(target.resolve().is_relative_to(self.home.resolve()),
                        "Synthetic test evidence must remain in its disposable fixture")
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n")

    def reject(self, call, pattern):
        with self.assertRaisesRegex(g.v.Invalid, pattern):
            call()

    def commit(self, message="Synthetic candidate", root=None, env=None, amend=False):
        self.git("add", "--all", "--", ".", ":(top,glob,exclude)synthetic-*", root=root)
        self.command("git", "commit", "--quiet", "--allow-empty", *( ["--amend"] if amend else [] ), "-m", message, cwd=root or self.root, env=env)
        return self.git("rev-parse", "HEAD", root=root)


    def synthetic_baseline(self, author):
        """Build schema fixtures only under the per-test temporary directory.

        These records deliberately identify themselves as synthetic. They test
        evidence bindings and never stand in for the independent real review.
        """
        manifest = self.read_json(g.MANIFEST)
        now = dt.datetime.now(dt.timezone.utc).isoformat()
        manifest["status"] = "ready"
        reviewer = {"actor_id": "synthetic-context-reviewer", "execution_id": "synthetic-context-review-exec", "model": None}
        report_path = "docs/agents/readiness/evidence/synthetic-context.json"
        manifest["context_review"] = {
            "status": "approved", "reviewer": reviewer, "adopter": author,
            "reviewed_at": now, "report": report_path,
        }
        for control_id, control in manifest["controls"].items():
            control.update(status="implemented", implementation=["scripts/pmm_workflow.py"],
                           verification=["docs/agents/readiness/evidence/synthetic-" + control_id + ".json"])
            checks = {}
            for name, result, code in (("positive_check", "accepted", 0), ("negative_check", "rejected", 1)):
                output = "docs/agents/readiness/evidence/synthetic-" + name + ".txt"
                (self.root / output).write_text("Synthetic unit-test schema fixture only; no production acceptance.\n")
                checks[name] = {"command": "synthetic-schema-fixture", "expected_result": result,
                                "observed_result": result, "expected_exit": code, "observed_exit": code, "output": output}
            self.write_json(control["verification"][0], {
                "schema_version": 1, "control_id": control_id, "result": "passed",
                "command": "synthetic-schema-fixture-not-production-evidence", "exit_code": 0,
                "observed_at": now, "implementation_blobs": {"scripts/pmm_workflow.py": g.v.blob(self.root, "scripts/pmm_workflow.py")},
                **checks,
            })
        manifest["context"] = g.v.observed_context(self.root, manifest)
        self.write_json(report_path, {
            "schema_version": 1, "result": "approved", "context": manifest["context"],
            "reviewer": reviewer, "adopter": author, "scope": "repository-environment-adoption",
            "reviewed_at": now, "findings": "Synthetic binding fixture; not a production review.",
        })
        self.write_json(g.MANIFEST, manifest)
        return manifest


    def assign(self, actor, root=None):
        self.git("config", "--worktree", "pmm.actorId", actor["actor_id"], root=root)
        self.git("config", "--worktree", "pmm.executionId", actor["execution_id"], root=root)

    def publish(self, candidate, target=None):
        """Local bare-remote push/readback, never the configured public origin."""
        target = target or w.STAGE
        self.git("push", "--quiet", str(self.bare), candidate + ":" + target)
        observed = self.git("ls-remote", str(self.bare), target).split()[0]
        self.assertEqual(observed, candidate)
        self.git("update-ref", "refs/remotes/origin/" + target.removeprefix("refs/heads/"), observed)
        return observed

    def activation_review(self):
        self.write_json(w.STATE, self.state)
        self.write_json(self.state["activation_review"], {
            "result": "approved", "blocking_findings": 0,
            "reviewer": {"actor_id": "synthetic-activation-reviewer", "execution_id": "synthetic-activation-review-exec", "model": None},
            "observed_at": dt.datetime.now(dt.timezone.utc).isoformat(),
            "state_blob": g.v.blob(self.root, w.STATE), "task_id": self.state["active_task"],
            "plan_commit": self.state["plan_commit"],
            "context": g.v.observed_context(self.root, self.read_json(g.MANIFEST)),
            "findings": "Synthetic isolated fixture approval, not production acceptance.",
        })

    def preflight(self, phase="first-write", actor=None, execution=None, purpose=None):
        return w.preflight(self.root, phase, actor or self.author["actor_id"],
                           execution or self.author["execution_id"], purpose or self.task["purpose"], live=False)

    def target(self, **overrides):
        args = dict(root=self.root, actor=self.author["actor_id"], execution=self.author["execution_id"],
                    role="integrator", action="push", target=w.STAGE, candidate=self.git("rev-parse", "HEAD"),
                    expected_old=self.git("rev-parse", "refs/remotes/origin/" + w.STAGE.removeprefix("refs/heads/")),
                    remote_name="origin", remote_location=w.REPOSITORY, local_ref=self.task["task_branch"], live=False)
        args.update(overrides)
        return w.git_target(**args)

    def sidecars(self):
        candidate = self.git("rev-parse", "HEAD")
        binding = w.binding(self.root, self.state["activation"]["base_commit"], candidate)
        now = dt.datetime.now(dt.timezone.utc).isoformat()
        evidence = {"schema_version": 1, **binding,
                    "task": {"path": self.task["card"], "scope_paths": binding["diff_paths"], "author": self.author},
                    "verification_report": self.delivery_path("synthetic-delivery-tests.json"), "review_report": self.delivery_path("synthetic-delivery-review.json")}
        result = self.command(sys.executable, "-c", "import subprocess; subprocess.run(['git','diff','--check'],check=True); print('Synthetic candidate diff check passed')")
        (self.root / self.delivery_directory()).mkdir(parents=True, exist_ok=True)
        (self.root / self.delivery_path("synthetic-delivery-output.txt")).write_text(result.stdout + result.stderr)
        self.write_json(evidence["verification_report"], {
            "schema_version": 1, "kind": "verification", **binding, "task_path": self.task["card"],
            "result": "passed", "observed_at": now, "executed_by": self.author,
            "checks": [{"command": "synthetic isolated git diff --check", "exit_code": result.returncode,
                        "result": "passed", "output": self.delivery_path("synthetic-delivery-output.txt")}],
        })
        self.write_json(evidence["review_report"], {
            "schema_version": 1, "kind": "review", **binding, "task_path": self.task["card"],
            "result": "approved", "observed_at": now,
            "reviewer": {"actor_id": "synthetic-final-reviewer", "execution_id": "synthetic-final-review-exec", "model": None},
            "blocking_findings": 0, "findings": "Synthetic binding fixture only, not real final acceptance.",
        })
        self.write_json(self.delivery_path("synthetic-delivery.json"), evidence)
        self.binding = binding
        return evidence

    def deliver(self):
        return w.delivery(self.root, self.binding["candidate_commit"], self.binding["base_commit"], self.delivery_path("synthetic-delivery.json"))

    def test_fresh_published_task_first_write_recover_and_commit(self):
        state, task = w.load_task(self.root, live=True)
        self.assertEqual(state["active_task"], "GOV002")
        self.assertFalse(task["business_allowed"])
        for phase in ("first-write", "recover", "commit"):
            with self.subTest(phase=phase):
                self.preflight(phase)

    def test_frozen_local_plan_cannot_grant_its_own_scope(self):
        plan = self.read_json(self.state["plan_path"])
        plan["tasks"]["GOV002"]["allowed_prefixes"].append("src/")
        self.write_json(self.state["plan_path"], plan)
        self.reject(lambda: w.load_task(self.root), "Frozen published plan changed")

    def test_committed_changed_card_is_still_frozen(self):
        (self.root / self.task["card"]).write_text("Changed frozen card\n")
        self.commit()
        self.reject(lambda: w.load_task(self.root), "Frozen published plan changed")

    def test_historical_ba001_card_cannot_be_rewritten(self):
        (self.root / "docs/workflow/BA-001.md").write_text("Changed historical card\n")
        self.reject(lambda: w.load_task(self.root), "Historical BA001 card changed")

    def test_unpublished_card_commit_rejected(self):
        self.state["plan_commit"] = self.git("rev-parse", "HEAD")
        self.write_json(w.STATE, self.state)
        self.reject(lambda: w.load_task(self.root), "Unpublished plan")

    def test_unknown_task_rejected(self):
        self.state["active_task"] = "UNDECLARED"
        self.write_json(w.STATE, self.state)
        self.reject(lambda: w.load_task(self.root), "Unknown task")

    def test_old_ba001_task_cannot_be_reactivated(self):
        self.state["active_task"] = "BA-001"
        self.write_json(w.STATE, self.state)
        self.reject(lambda: w.load_task(self.root), "Unknown task")

    def test_out_of_scope_untracked_business_file_rejected(self):
        path = self.root / "src/business.py"
        path.parent.mkdir()
        path.write_text("# Synthetic forbidden product source\n")
        self.reject(lambda: w.scope(self.root, self.state, self.task), "exceeds published task scope")

    def test_out_of_scope_committed_file_rejected(self):
        (self.root / "unapproved.txt").write_text("Synthetic unapproved input\n")
        self.commit()
        self.reject(lambda: w.scope(self.root, self.state, self.task), "exceeds published task scope")

    def test_old_or_unknown_actor_rejected(self):
        for actor in (g.AUTHOR, "unknown-actor"):
            with self.subTest(actor=actor):
                self.reject(lambda: self.preflight(actor=actor), "Unauthorized actor/execution")

    def test_wrong_execution_rejected(self):
        self.reject(lambda: self.preflight(execution="different-execution"), "Unauthorized actor/execution")

    def test_missing_worktree_execution_ownership_rejected(self):
        self.git("config", "--worktree", "--unset", "pmm.executionId")
        self.reject(self.preflight, "Worktree ownership")

    def test_wrong_branch_rejected(self):
        self.git("checkout", "-b", "synthetic-wrong-branch")
        self.reject(self.preflight, "Wrong task branch")

    def test_detached_worktree_rejected(self):
        self.git("checkout", "--detach")
        self.reject(self.preflight, "detached worktree")

    def test_dirty_first_write_and_recovery_rejected(self):
        (self.root / "README.md").write_text("Synthetic dirty input\n")
        for phase in ("first-write", "recover"):
            with self.subTest(phase=phase):
                self.reject(lambda: self.preflight(phase), "clean owned worktree")

    def test_unstaged_commit_rejected(self):
        (self.root / "README.md").write_text("Synthetic unstaged input\n")
        self.reject(lambda: self.preflight("commit"), "Unstaged")

    def test_unknown_operation_phase_rejected(self):
        self.reject(lambda: self.preflight("unrecognized-operation"), "(?i)(phase|operation)")

    def test_activation_phase_self_grant_rejected(self):
        self.state["activation"]["phase"] = "product"
        self.activation_review()
        self.reject(lambda: w.load_task(self.root), "phase/purpose self-grant")

    def test_business_and_wrong_task_purpose_rejected(self):
        for purpose in ("business", "foundation", "adoption"):
            with self.subTest(purpose=purpose):
                self.reject(lambda: w.context(self.root, purpose), "wrong task purpose|Business")

    def test_missing_predecessor_acceptance_rejected(self):
        self.state["activation"]["predecessors"] = {}
        self.activation_review()
        self.reject(lambda: w.load_task(self.root), "Missing predecessor acceptance")

    def test_same_actor_or_execution_activation_review_rejected(self):
        path = self.state["activation_review"]
        original = self.read_json(path)
        for key in ("actor_id", "execution_id"):
            with self.subTest(key=key):
                report = copy.deepcopy(original)
                report["reviewer"][key] = self.author[key]
                self.write_json(path, report)
                self.reject(lambda: w.load_task(self.root), "Review not independent: " + key)
        self.write_json(path, original)

    def test_stale_activation_state_binding_rejected(self):
        self.state["unreviewed_change"] = True
        self.write_json(w.STATE, self.state)
        self.reject(lambda: w.load_task(self.root), "Stale activation review")

    def test_context_environment_drift_rejected(self):
        path = "docs/agents/readiness/environment-profile.json"
        environment = self.read_json(path)
        environment["dimensions"]["testing"] += " Synthetic changed target."
        self.write_json(path, environment)
        self.reject(lambda: w.context(self.root, "governance"), "Repository/environment drift")

    def test_context_control_implementation_drift_rejected(self):
        path = self.root / "scripts/pmm_workflow.py"
        path.write_text(path.read_text() + "\n# Synthetic unreviewed implementation change\n")
        self.reject(lambda: w.context(self.root, "governance"), "Stale implementation evidence|Repository/environment drift")

    def test_missing_generic_control_rejected(self):
        manifest = self.read_json(g.MANIFEST)
        del manifest["controls"]["branch_gate"]
        self.write_json(g.MANIFEST, manifest)
        self.reject(lambda: w.context(self.root, "governance"), "(?i)(required|fixed)")

    def test_missing_navigation_rejected(self):
        (self.root / "AGENTS.md").write_text("Synthetic disconnected navigation\n")
        self.reject(lambda: w.context(self.root, "governance"), "navigation")

    def test_pending_context_only_allows_published_governance_repair(self):
        manifest = self.read_json(g.MANIFEST)
        manifest["status"] = "pending"
        self.write_json(g.MANIFEST, manifest)
        w.context(self.root, "governance", allow_pending=True)
        self.reject(lambda: w.context(self.root, "governance"), "pending")
        self.reject(lambda: w.context(self.root, "business", allow_pending=True), "Business")

    def test_pinned_upstream_source_asset_cannot_change(self):
        path = self.root / "docs/agents/readiness/validate_readiness.py"
        path.write_text(path.read_text() + "\n# Synthetic source drift\n")
        self.reject(lambda: g.baseline(self.root), "Imported source asset changed")

    def test_private_input_rejected(self):
        (self.root / "docs/agents/readiness/evidence/private.txt").write_text("ghp_" + "a" * 40)
        self.reject(lambda: w.public_tree(self.root), "Private/credential-like")

    def test_binary_input_rejected(self):
        (self.root / "docs/agents/readiness/evidence/data.db").write_bytes(b"synthetic database")
        self.reject(lambda: w.public_tree(self.root), "Binary/runtime data")

    def test_wrong_fetch_remote_rejected(self):
        self.git("remote", "set-url", "origin", "https://example.invalid/wrong.git")
        self.reject(lambda: w.load_task(self.root), "Wrong actual remote")

    def test_wrong_push_remote_rejected(self):
        self.git("remote", "set-url", "--push", "origin", "https://example.invalid/wrong.git")
        self.reject(lambda: w.load_task(self.root), "Wrong actual remote")

    def test_wrong_default_branch_rejected(self):
        self.git("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/" + w.STAGE.removeprefix("refs/heads/"))
        self.reject(lambda: w.load_task(self.root), "Wrong default branch")

    def test_delivery_valid_exact_candidate(self):
        self.sidecars()
        self.assertEqual(self.deliver(), self.binding)

    def test_delivery_stale_candidate_tree_base_and_diff_rejected(self):
        evidence = self.sidecars()
        for field, value in (("candidate_commit", "a" * 40), ("candidate_tree", "b" * 40),
                             ("base_commit", "c" * 40), ("diff_paths", ["README.md"])):
            with self.subTest(field=field):
                changed = copy.deepcopy(evidence)
                changed[field] = value
                if field == "diff_paths":
                    changed["task"]["scope_paths"] = value
                self.write_json(self.delivery_path("synthetic-delivery.json"), changed)
                self.reject(self.deliver, "does not match Git")
        self.write_json(self.delivery_path("synthetic-delivery.json"), evidence)

    def test_delivery_stale_test_and_review_report_rejected(self):
        evidence = self.sidecars()
        for path in (evidence["verification_report"], evidence["review_report"]):
            with self.subTest(path=path):
                original = self.read_json(path)
                changed = copy.deepcopy(original)
                changed["candidate_tree"] = "c" * 40
                self.write_json(path, changed)
                self.reject(self.deliver, "is stale")
                self.write_json(path, original)

    def test_delivery_empty_or_failed_test_report_rejected(self):
        evidence = self.sidecars()
        original = self.read_json(evidence["verification_report"])
        for checks in ([], [{**original["checks"][0], "exit_code": 1, "result": "failed"}]):
            with self.subTest(checks=checks):
                changed = copy.deepcopy(original)
                changed["checks"] = checks
                self.write_json(evidence["verification_report"], changed)
                if not checks:
                    (self.root / self.delivery_path("synthetic-delivery-output.txt")).unlink()
                self.reject(self.deliver, "(?i)(too few|empty|failed|minItems|minimum|short)")
                if not checks:
                    (self.root / self.delivery_path("synthetic-delivery-output.txt")).write_text("Synthetic restored output\n")

    def test_delivery_missing_output_rejected(self):
        self.sidecars()
        (self.root / self.delivery_path("synthetic-delivery-output.txt")).unlink()
        self.reject(self.deliver, "Missing regular file")

    def test_delivery_same_execution_reviewer_rejected(self):
        evidence = self.sidecars()
        review = self.read_json(evidence["review_report"])
        review["reviewer"]["execution_id"] = self.author["execution_id"]
        self.write_json(evidence["review_report"], review)
        self.reject(self.deliver, "(?i)(independent)")

    def test_delivery_changed_working_tree_rejected(self):
        self.sidecars()
        (self.root / "README.md").write_text("Synthetic change after freeze\n")
        self.reject(self.deliver, "dirty")

    def test_delivery_undeclared_sidecar_rejected(self):
        self.sidecars()
        (self.root / "undeclared-sidecar.txt").write_text("Synthetic extra evidence\n")
        self.reject(self.deliver, "exceeds published task scope|Untracked files")

    def test_main_and_unapproved_stage_git_targets_rejected(self):
        for target in ("refs/heads/main", "refs/heads/dot/unapproved-stage", self.task["task_branch"]):
            with self.subTest(target=target):
                self.reject(lambda: self.target(target=target), "Forbidden Git target")

    def test_wrong_git_role_remote_local_ref_and_candidate_rejected(self):
        cases = (({"role": "author"}, "Wrong Git role"),
                 ({"remote_name": "upstream"}, "outgoing remote"),
                 ({"remote_location": "https://example.invalid/wrong.git"}, "outgoing remote"),
                 ({"local_ref": "refs/heads/main"}, "Outgoing ref"),
                 ({"candidate": self.state["plan_commit"]}, "candidate not HEAD"))
        for kwargs, pattern in cases:
            with self.subTest(kwargs=kwargs):
                self.reject(lambda: self.target(**kwargs), pattern)

    def test_concurrent_remote_target_change_rejected(self):
        self.reject(lambda: self.target(expected_old="a" * 40), "Remote target changed")

    def test_git_target_non_fast_forward_rejected(self):
        self.git("checkout", "-b", "synthetic-divergence", self.state["plan_commit"])
        (self.root / "README.md").write_text("Synthetic concurrent stage change\n")
        divergent = self.commit()
        self.git("update-ref", "refs/remotes/origin/" + w.STAGE.removeprefix("refs/heads/"), divergent)
        self.git("checkout", self.task["task_branch"].removeprefix("refs/heads/"))
        self.reject(self.target, "fast-forward")

    def test_real_bare_remote_live_drift_rejected(self):
        self.git("push", "--quiet", str(self.bare), "HEAD:" + w.STAGE)
        self.reject(lambda: w.load_task(self.root, live=True), "Live/cached stage drift")

    def test_first_write_recovery_commit_invoke_context_gate(self):
        for phase in ("first-write", "recover", "commit"):
            with self.subTest(phase=phase), mock.patch.object(w, "context", side_effect=g.v.Invalid("synthetic context call witness")) as called:
                self.reject(lambda: self.preflight(phase), "synthetic context call witness")
                called.assert_called_once()

    def test_verification_invokes_actual_context_gate(self):
        with mock.patch.object(w, "context", side_effect=g.v.Invalid("synthetic verification call witness")) as called:
            self.reject(lambda: w.verify(self.root), "synthetic verification call witness")
            called.assert_called_once()

    def test_verification_rejects_empty_required_suite(self):
        with mock.patch.object(unittest.TestLoader, "discover") as discover:
            discover.return_value.countTestCases.return_value = 0
            self.reject(lambda: w.verify(self.root), "Required suite empty")

    def test_verification_propagates_suite_failure(self):
        real_run = subprocess.run
        def fail_suite(args, **kwargs):
            if args[:3] == [sys.executable, "-m", "unittest"]:
                return subprocess.CompletedProcess(args, 7)
            return real_run(args, **kwargs)
        with mock.patch.object(unittest.TestLoader, "discover") as discover, mock.patch.object(subprocess, "run", side_effect=fail_suite):
            discover.return_value.countTestCases.return_value = 1
            self.reject(lambda: w.verify(self.root), "Required verification failed")


    def plan_fixture(self, mutate=None, hooked=False):
        """Build an additive second task, starting from an accepted fixture stage."""
        old = self.git("rev-parse", "HEAD")
        if not hooked:
            self.publish(old)
        path = "docs/workflow/SYN002-plan-publication.json"
        task = {
            "card": "docs/workflow/SYN002.md", "status": "planned",
            "phase": "requirements-design", "purpose": "requirements",
            "task_branch": "refs/heads/dot/task/synthetic-successor-two",
            "author": {"actor_id": "synthetic-second-author", "execution_id": "synthetic-second-exec", "model": None},
            "dependencies": ["GOV002"],
            "allowed_exact": ["README.md", w.STATE, g.MANIFEST, "docs/agents/readiness/environment-profile.json"],
            "allowed_prefixes": ["docs/agents/readiness/evidence/"],
            "business_allowed": False,
            "applicability": "docs/workflow/SYN002-applicability.md",
        }
        plan = {"schema_version": 1, "kind": "successor-plan-publication", "status": "planned",
                "repository": w.REPOSITORY, "default_ref": "refs/heads/main", "target_ref": w.STAGE,
                "expected_old": old, "author": self.author,
                "publication_branch": "refs/heads/dot/task/synthetic-publication-two",
                "publication_paths": [path, task["card"], task["applicability"]], "tasks": {"SYN002": task}}
        third = copy.deepcopy(task)
        third.update(card="docs/workflow/SYN003.md", task_branch="refs/heads/dot/task/synthetic-successor-three",
                     author={"actor_id": "synthetic-third-author", "execution_id": "synthetic-third-exec", "model": None},
                     dependencies=["SYN002"])
        plan["tasks"]["SYN003"] = third
        plan["publication_paths"].append(third["card"])
        self.git("checkout", "-b", plan["publication_branch"].removeprefix("refs/heads/"))
        (self.root / third["card"]).write_text("# SYN003 same-plan synthetic successor\n\nPublication status: planned\nNo product acceptance.\n")
        (self.root / task["card"]).write_text("# SYN002 synthetic successor\n\nPublication status: planned\nNo product acceptance.\n")
        (self.root / task["applicability"]).write_text("Synthetic governance fixture: Python/Git only, no business writes.\n")
        if mutate:
            mutate(plan)
        self.write_json(path, plan)
        env = None
        if hooked:
            env = {**self.subprocess_hook_environment(), "PMM_OPERATION": "plan", "PMM_OLD_SHA": old,
                   "PMM_PLAN": path, "PMM_TRUSTED_CONTROLLER": str(self.export_trusted_controller(old))}
            self.last_plan_env = env
        candidate = self.commit("Synthetic additive successor plan", env=env)
        review = "synthetic-plan-review.json"
        self.bound_review(review, old, candidate)
        return old, candidate, path, review, plan

    def bound_review(self, path, old, candidate, author=None):
        author = author or self.author
        report = {
            **w.binding(self.root, old, candidate), "result": "approved", "blocking_findings": 0,
            "reviewer": {"actor_id": "synthetic-separate-reviewer", "execution_id": "synthetic-separate-review-exec", "model": None},
            "observed_at": dt.datetime.now(dt.timezone.utc).isoformat(),
            "findings": "Synthetic Git binding only; not a production review.",
        }
        self.write_json(path, report)
        return report

    def validate_plan(self, values):
        old, candidate, path, review, _ = values
        return w.validate_plan(self.root, old, candidate, path, review,
                               self.author["actor_id"], self.author["execution_id"], live=True)

    def test_additive_plan_and_readback_then_second_actor_worktree_cycle(self):
        # First task: genuine fixture gate calls, candidate freeze and sidecars.
        self.preflight()
        self.preflight("recover")
        evidence = self.sidecars()
        first_binding = self.deliver()
        first_acceptance = self.capture_acceptance()
        for path in (self.delivery_path("synthetic-delivery.json"), evidence["verification_report"], evidence["review_report"], self.delivery_path("synthetic-delivery-output.txt")):
            (self.root / path).unlink()
        self.target()
        values = self.plan_fixture()
        self.validate_plan(values)
        old, plan_commit, _, review, plan = values
        self.assertEqual(old, first_binding["candidate_commit"])
        self.publish(plan_commit)
        (self.root / review).unlink()

        # A real linked worktree with a different author/execution owns task two.
        first_root = self.root
        first_author = copy.deepcopy(self.author)
        second_root = self.home / "author-two"
        task = plan["tasks"]["SYN002"]
        self.git("worktree", "add", "-b", task["task_branch"].removeprefix("refs/heads/"), str(second_root), plan_commit)
        self.root = second_root
        self.author = task["author"]
        self.task = task
        self.assign(first_author)
        self.assertNotEqual(first_root, self.root)
        self.assertNotEqual(self.git("config", "--worktree", "pmm.actorId", root=first_root), self.author["actor_id"])
        first_receipt = self.install_acceptance(first_acceptance)
        self.state = {
            "schema_version": 1, "anchor": w.ANCHOR, "active_task": "SYN002",
            "plan_commit": plan_commit, "plan_path": values[2],
            "activation": {"task_id": "SYN002", "plan_commit": plan_commit, "base_commit": plan_commit,
                           "phase": task["phase"], "purpose": task["purpose"], "author": self.author,
                           "authorized_executions": [self.author],
                           "predecessors": {"GOV002": first_receipt}},
            "activation_review": "docs/agents/readiness/evidence/synthetic-activation.json",
        }
        self.write_json(w.STATE, self.state)
        self.synthetic_baseline(self.author)
        self.activation_review()
        activation_candidate = self.commit("Synthetic reviewed second task activation")
        activation_review = "synthetic-activation-candidate-review.json"
        self.bound_review(activation_review, plan_commit, activation_candidate)
        w.validate_activation(self.root, plan_commit, activation_candidate, activation_review,
                              actor=first_author["actor_id"], execution=first_author["execution_id"], live=True)
        (self.root / activation_review).unlink()
        self.publish(activation_candidate)
        self.assign(self.author)
        self.preflight()
        self.preflight("recover")
        (self.root / "README.md").write_text("Synthetic second-task requirements-only result.\n")
        self.git("add", "README.md")
        self.preflight("commit")
        self.commit("Synthetic second-task delivery candidate")
        self.sidecars()
        second_binding = self.deliver()
        second_acceptance = self.capture_acceptance()
        self.assertNotEqual(second_binding["candidate_commit"], first_binding["candidate_commit"])
        # Exact sidecars are validated separately; remove them before scoped target check.
        for path in (self.delivery_path("synthetic-delivery.json"), self.delivery_path("synthetic-delivery-tests.json"), self.delivery_path("synthetic-delivery-review.json"), self.delivery_path("synthetic-delivery-output.txt")):
            (self.root / path).unlink()
        self.target()
        self.publish(second_binding["candidate_commit"])
        self.assertEqual(w.remote(self.root, live=True), second_binding["candidate_commit"])

        # Published task switch invalidates recovery from the previous task/worktree.
        self.reject(lambda: w.preflight(first_root, "recover", first_author["actor_id"],
                                       first_author["execution_id"], "governance", live=False),
                    "Activation has not been published unchanged")

        # A third task shares this published plan but has a fresh construction
        # base. Its scope must not include implementation inherited from task two.
        prior_author = copy.deepcopy(self.author)
        second_root = self.root
        third_task = plan["tasks"]["SYN003"]
        third_root = self.home / "author-three"
        second_commit = second_binding["candidate_commit"]
        self.git("worktree", "add", "-b", third_task["task_branch"].removeprefix("refs/heads/"), str(third_root), second_commit)
        self.root = third_root
        self.task = third_task
        self.author = third_task["author"]
        self.assign(prior_author)
        second_receipt = self.install_acceptance(second_acceptance)
        self.state.update(active_task="SYN003")
        self.state["activation"] = {
            "task_id": "SYN003", "plan_commit": plan_commit, "base_commit": second_commit,
            "phase": third_task["phase"], "purpose": third_task["purpose"], "author": self.author,
            "authorized_executions": [self.author],
            "predecessors": {"SYN002": second_receipt},
        }
        self.write_json(w.STATE, self.state)
        self.synthetic_baseline(self.author)
        self.activation_review()
        third_activation = self.commit("Synthetic same-plan third-task activation")
        self.bound_review(activation_review, second_commit, third_activation)
        w.validate_activation(self.root, second_commit, third_activation, activation_review,
                              actor=prior_author["actor_id"], execution=prior_author["execution_id"], live=True)
        (self.root / activation_review).unlink()
        self.publish(third_activation)
        self.assign(self.author)
        self.preflight()
        self.preflight("recover")
        self.assertEqual(self.state["plan_commit"], plan_commit)
        self.assertNotEqual(self.state["activation"]["base_commit"], plan_commit)
        self.assertNotIn("scripts/pmm_workflow.py", w.scope(self.root, self.state, self.task))
        self.sidecars()
        third_binding = self.deliver()
        self.assertEqual(third_binding["base_commit"], second_commit)
        self.reject(lambda: w.preflight(second_root, "recover", prior_author["actor_id"],
                                       prior_author["execution_id"], "requirements", live=False),
                    "Activation has not been published unchanged")

    def test_plan_missing_applicability_rejected(self):
        values = self.plan_fixture(lambda plan: plan["tasks"]["SYN002"].pop("applicability"))
        self.reject(lambda: self.validate_plan(values), "Missing applicability")

    def test_plan_unknown_phase_rejected(self):
        values = self.plan_fixture(lambda plan: plan["tasks"]["SYN002"].update(phase="product"))
        self.reject(lambda: self.validate_plan(values), "Unknown phase/purpose")

    def test_plan_business_self_grant_rejected(self):
        values = self.plan_fixture(lambda plan: plan["tasks"]["SYN002"].update(business_allowed=True))
        self.reject(lambda: self.validate_plan(values), "premature business")

    def test_plan_cannot_reuse_bootstrap(self):
        values = self.plan_fixture(lambda plan: plan.update(bootstrap={"one_time": True}))
        self.reject(lambda: self.validate_plan(values), "bootstrap")

    def test_plan_cannot_include_unlisted_addition(self):
        values = list(self.plan_fixture())
        (self.root / "docs/workflow/SYN-unlisted.md").write_text("Synthetic undeclared addition\n")
        values[1] = self.commit(amend=True)
        self.bound_review(values[3], values[0], values[1])
        self.reject(lambda: self.validate_plan(values), "exact declared files")

    def test_plan_cannot_overwrite_prior_document(self):
        values = list(self.plan_fixture(lambda plan: plan["publication_paths"].append("README.md")))
        (self.root / "README.md").write_text("Synthetic attempted old-tree edit\n")
        values[1] = self.commit(amend=True)
        self.bound_review(values[3], values[0], values[1])
        self.reject(lambda: self.validate_plan(values), "cannot overwrite existing file")

    def test_plan_review_must_bind_exact_candidate(self):
        values = self.plan_fixture()
        review = self.read_json(values[3])
        review["candidate_tree"] = "c" * 40
        self.write_json(values[3], review)
        self.reject(lambda: self.validate_plan(values), "Stale review candidate_tree")

    def test_plan_review_cannot_share_author_execution(self):
        values = self.plan_fixture()
        review = self.read_json(values[3])
        review["reviewer"]["execution_id"] = self.author["execution_id"]
        self.write_json(values[3], review)
        self.reject(lambda: self.validate_plan(values), "not independent: execution_id")

    def test_plan_must_use_trusted_previous_controller(self):
        values = list(self.plan_fixture())
        path = self.root / "scripts/pmm_workflow.py"
        path.write_text(path.read_text() + "\n# Synthetic candidate controller edit\n")
        values[1] = self.commit(amend=True)
        with mock.patch.object(w, "ROOT", self.root):
            self.reject(lambda: self.validate_plan(values), "trusted old tree")

    def subprocess_hook_environment(self):
        bin_dir = self.home / "fixture-bin"
        bin_dir.mkdir(exist_ok=True)
        launcher = bin_dir / "python3"
        launcher.write_text("#!" + sys.executable + "\n" + '''
import os
from pathlib import Path
import sys
script = Path(sys.argv[1]).resolve()
sys.path.insert(0, str(script.parent))
import pmm_governance as g
import pmm_workflow as w
if os.environ.get("PMM_SYNTHETIC_REPOSITORY"):
    w.REPOSITORY = os.environ["PMM_SYNTHETIC_REPOSITORY"]
original = g.run
def transport(args, root=g.ROOT):
    if args[:2] == ["git", "ls-remote"]:
        if "origin" in args:
            return original([os.environ["PMM_SYNTHETIC_BARE"] if x == "origin" else x for x in args], root)
        import subprocess
        return subprocess.CompletedProcess(args, 1, "", "Synthetic unavailable upstream")
    return original(args, root)
g.run = transport
sys.exit((w.main if script.name == "pmm_workflow.py" else g.main)(sys.argv[2:]))
''')
        launcher.chmod(0o755)
        return {"PATH": str(bin_dir) + os.pathsep + os.environ["PATH"],
                "PMM_SYNTHETIC_BARE": str(self.bare), "PMM_SYNTHETIC_REPOSITORY": w.REPOSITORY, "PMM_ACTOR": self.author["actor_id"],
                "PMM_EXECUTION": self.author["execution_id"], "PMM_PURPOSE": self.task["purpose"],
                "PMM_GIT_ROLE": "integrator", "PMM_TASK_BASE": self.state["activation"]["base_commit"],
                "PMM_DELIVERY_EVIDENCE": self.delivery_path("synthetic-delivery.json")}

    def test_actual_registered_commit_hook_and_removed_call_witness(self):
        self.git("config", "core.hooksPath", ".githooks")
        env = self.subprocess_hook_environment()
        rejected = self.command("git", "commit", "--allow-empty", "-m", "Synthetic wrong actor", env={**env, "PMM_ACTOR": "wrong-actor"}, check=False)
        self.assertNotEqual(rejected.returncode, 0, rejected.stdout + rejected.stderr)
        self.assertIn("Unauthorized actor/execution", rejected.stderr)
        accepted = self.command("git", "commit", "--allow-empty", "-m", "Synthetic registered-hook success", env=env, check=False)
        self.assertEqual(accepted.returncode, 0, accepted.stdout + accepted.stderr)
        self.assertIn("PMM_SUCCESSOR_GATE_VERIFIED commit", accepted.stdout + accepted.stderr)
        hook = self.root / ".githooks/pre-commit"
        hook.write_text("#!/bin/sh\ntrue\n")
        hook.chmod(0o755)
        self.git("add", ".githooks/pre-commit")
        bypassed = self.command("git", "commit", "-m", "Synthetic removed-call witness", env={**env, "PMM_ACTOR": "wrong-actor"}, check=False)
        self.assertEqual(bypassed.returncode, 0, bypassed.stdout + bypassed.stderr)
        # The original refusal oracle demonstrably becomes red after call removal.
        with self.assertRaises(AssertionError):
            self.assertNotEqual(bypassed.returncode, 0)

    def test_actual_push_hook_rejects_main_and_removed_call_witness(self):
        env = self.subprocess_hook_environment()
        data = self.task["task_branch"] + " " + self.candidate + " refs/heads/main " + self.state["plan_commit"] + "\n"
        rejected = self.command("bash", ".githooks/pre-push", "origin", w.REPOSITORY, env=env, input=data, check=False)
        self.assertNotEqual(rejected.returncode, 0, rejected.stdout + rejected.stderr)
        self.assertIn("Forbidden Git target", rejected.stderr)
        hook = self.root / ".githooks/pre-push"
        hook.write_text("#!/bin/sh\ntrue\n")
        bypassed = self.command("bash", ".githooks/pre-push", "origin", w.REPOSITORY, env=env, input=data, check=False)
        self.assertEqual(bypassed.returncode, 0)
        with self.assertRaises(AssertionError):
            self.assertNotEqual(bypassed.returncode, 0)

    def test_actual_cli_first_write_and_recovery_use_successor_gate(self):
        env = self.subprocess_hook_environment()
        for phase in ("first-write", "recover"):
            with self.subTest(phase=phase):
                result = self.command("python3", "scripts/pmm_governance.py", phase,
                                      "--actor", self.author["actor_id"], "--execution", self.author["execution_id"],
                                      "--purpose", "governance", env=env, check=False)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn("PMM_SUCCESSOR_GATE_VERIFIED " + phase, result.stdout)

    def test_ci_entrypoint_dispatches_successor_verifier(self):
        with mock.patch.object(g, "ROOT", self.root), mock.patch.object(w, "verify", side_effect=g.v.Invalid("synthetic CI dispatch witness")) as called:
            with contextlib.redirect_stderr(io.StringIO()):
                result = verify.main([])
            self.assertEqual(result, 1)
            called.assert_called_once()


    def test_actual_push_hook_runs_target_and_delivery_checks(self):
        self.sidecars()
        env = self.subprocess_hook_environment()
        data = self.task["task_branch"] + " " + self.candidate + " " + w.STAGE + " " + self.state["plan_commit"] + "\n"
        accepted = self.command("bash", ".githooks/pre-push", "origin", w.REPOSITORY, env=env, input=data, check=False)
        self.assertEqual(accepted.returncode, 0, accepted.stdout + accepted.stderr)
        self.assertIn("GIT_TARGET_VERIFIED", accepted.stdout)
        self.assertIn("PMM_SUCCESSOR_DELIVERY_VERIFIED", accepted.stdout)
        evidence = self.read_json(self.delivery_path("synthetic-delivery.json"))
        evidence["candidate_tree"] = "a" * 40
        self.write_json(self.delivery_path("synthetic-delivery.json"), evidence)
        rejected = self.command("bash", ".githooks/pre-push", "origin", w.REPOSITORY, env=env, input=data, check=False)
        self.assertNotEqual(rejected.returncode, 0)
        self.assertIn("does not match Git", rejected.stderr)

    def test_published_activation_cannot_be_reassigned_by_candidate(self):
        self.publish(self.candidate)
        actor = {"actor_id": "unreviewed-actor", "execution_id": "unreviewed-execution", "model": None}
        self.state["activation"]["authorized_executions"].append(actor)
        self.activation_review()
        self.reject(lambda: w.load_task(self.root), "Activation has not been published unchanged")

    def test_activation_construction_base_must_be_stage_derived(self):
        self.state["activation"]["base_commit"] = self.candidate
        self.activation_review()
        self.reject(lambda: w.load_task(self.root), "activation base is not stage-derived")

    def test_plan_publication_cannot_relabel_main_as_its_branch(self):
        values = self.plan_fixture(lambda plan: plan.update(publication_branch="refs/heads/main"))
        self.reject(lambda: self.validate_plan(values), "Forbidden plan branch")

    def test_plan_publication_rejects_wrong_actual_initiator(self):
        values = self.plan_fixture()
        self.reject(lambda: w.validate_plan(self.root, *values[:4], "wrong-actor", "wrong-execution", live=True), "Unauthorized planner")

    def test_locked_linked_worktree_rejected(self):
        self.git("checkout", "-b", "synthetic-holder")
        linked = self.home / "locked-author"
        self.git("worktree", "add", str(linked), self.task["task_branch"].removeprefix("refs/heads/"))
        self.assign(self.author, root=linked)
        self.git("worktree", "lock", "--reason", "Synthetic safety check", str(linked))
        self.reject(lambda: w.preflight(linked, "recover", self.author["actor_id"], self.author["execution_id"], "governance", live=False), "Unsafe/unowned worktree")
        self.git("worktree", "unlock", str(linked))


    def local_remote_trust_seed(self):
        """Seed a labelled local-only authority for real registered push tests.

        Scaffolding precedes the lifecycle under test. There is no public origin
        write; only the repository declaration is parameterized for the fixture.
        """
        original = self.read_json(self.state["plan_path"])
        path = "docs/workflow/SYNTHETIC-local-trust-plan.json"
        original["repository"] = str(self.bare)
        original["publication_paths"] = [path, *[p for p in original["publication_paths"] if p != self.state["plan_path"]]]
        self.write_json(path, original)
        (self.root / w.STATE).unlink()
        fixture_plan = self.commit("Synthetic local-remote trust seed, not production publication")
        self.publish(fixture_plan)
        self.git("remote", "set-url", "origin", str(self.bare))
        patcher = mock.patch.object(w, "REPOSITORY", str(self.bare))
        patcher.start()
        self.addCleanup(patcher.stop)
        self.state.update(plan_commit=fixture_plan, plan_path=path)
        self.state["activation"].update(plan_commit=fixture_plan, base_commit=fixture_plan)
        self.write_json(w.STATE, self.state)
        self.synthetic_baseline(self.author)
        self.activation_review()
        self.candidate = self.commit("Synthetic initial activation against local trust seed")
        self.preflight()
        return fixture_plan

    def export_trusted_controller(self, commit):
        target = self.home / ("trusted-controller-" + commit[:12])
        target.mkdir(exist_ok=True)
        names = self.git("ls-tree", "-r", "--name-only", commit).splitlines()
        for name in names:
            destination = target / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            result = subprocess.run(["git", "show", commit + ":" + name], cwd=self.root,
                                    capture_output=True, check=True)
            destination.write_bytes(result.stdout)
        return target / "scripts/pmm_workflow.py"


    def test_registered_hooks_allow_entire_published_successor_cycle(self):
        self.local_remote_trust_seed()
        self.git("config", "core.hooksPath", ".githooks")
        initial_env = self.subprocess_hook_environment()
        self.sidecars()
        initial_acceptance = self.capture_acceptance()
        pushed = self.command("git", "push", "origin", "HEAD:" + w.STAGE, env=initial_env)
        self.assertIn("PMM_SUCCESSOR_DELIVERY_VERIFIED", pushed.stdout + pushed.stderr)
        accepted_initial = self.git("rev-parse", "HEAD")
        self.assertEqual(self.git("ls-remote", "origin", w.STAGE).split()[0], accepted_initial)
        for file in (self.root / self.delivery_directory()).iterdir():
            file.unlink()

        values = self.plan_fixture(hooked=True)
        old, plan_commit, plan_path, review_path, plan = values
        self.assertEqual(old, accepted_initial)
        plan_env = {**self.last_plan_env, "PMM_PUBLICATION_REVIEW": review_path}
        published = self.command("git", "push", "origin", "HEAD:" + w.STAGE, env=plan_env)
        self.assertIn("PLAN_PUBLICATION_VERIFIED", published.stdout + published.stderr)
        self.assertEqual(self.git("ls-remote", "origin", w.STAGE).split()[0], plan_commit)

        prior_author = copy.deepcopy(self.author)
        task = plan["tasks"]["SYN002"]
        second_root = self.home / "hooked-author-two"
        self.git("worktree", "add", "-b", task["task_branch"].removeprefix("refs/heads/"), str(second_root), plan_commit)
        self.root = second_root
        self.task = task
        self.author = task["author"]
        self.assign(prior_author)
        initial_receipt = self.install_acceptance(initial_acceptance)
        self.state = {
            "schema_version": 1, "anchor": w.ANCHOR, "active_task": "SYN002",
            "plan_commit": plan_commit, "plan_path": plan_path,
            "activation": {"task_id": "SYN002", "plan_commit": plan_commit, "base_commit": plan_commit,
                           "phase": task["phase"], "purpose": task["purpose"], "author": self.author,
                           "authorized_executions": [self.author],
                           "predecessors": {"GOV002": initial_receipt}},
            "activation_review": "docs/agents/readiness/evidence/synthetic-activation.json",
        }
        self.write_json(w.STATE, self.state)
        self.synthetic_baseline(self.author)
        self.activation_review()
        activation_env = {**self.subprocess_hook_environment(), "PMM_OPERATION": "activate", "PMM_OLD_SHA": plan_commit,
                          "PMM_ACTOR": prior_author["actor_id"], "PMM_EXECUTION": prior_author["execution_id"],
                          "PMM_TRUSTED_CONTROLLER": str(self.export_trusted_controller(plan_commit))}
        activation_candidate = self.commit("Synthetic registered-hook activation", env=activation_env)
        candidate_review = "synthetic-activation-candidate-review.json"
        self.bound_review(candidate_review, plan_commit, activation_candidate)
        activation_env["PMM_PUBLICATION_REVIEW"] = candidate_review
        activated = self.command("git", "push", "origin", "HEAD:" + w.STAGE, env=activation_env)
        self.assertIn("ACTIVATION_VERIFIED", activated.stdout + activated.stderr)
        self.assertEqual(self.git("ls-remote", "origin", w.STAGE).split()[0], activation_candidate)
        (self.root / candidate_review).unlink()
        self.assign(self.author)
        implementation_env = self.subprocess_hook_environment()
        for phase in ("first-write", "recover"):
            checked = self.command("python3", "scripts/pmm_governance.py", phase, env=implementation_env)
            self.assertIn("PMM_SUCCESSOR_GATE_VERIFIED " + phase, checked.stdout)
        (self.root / "README.md").write_text("Synthetic governed successor implementation fixture.\n")
        final_candidate = self.commit("Synthetic registered-hook successor implementation", env=implementation_env)
        self.sidecars()
        completed = self.command("git", "push", "origin", "HEAD:" + w.STAGE, env=implementation_env)
        self.assertIn("PMM_SUCCESSOR_DELIVERY_VERIFIED", completed.stdout + completed.stderr)
        self.assertEqual(self.git("ls-remote", "origin", w.STAGE).split()[0], final_candidate)
        self.assertEqual(self.git("config", "core.hooksPath"), ".githooks")


    def test_intermediate_out_of_scope_commit_cannot_be_erased(self):
        path = self.root / "forbidden-history.txt"
        path.write_text("Synthetic disallowed historical edit\n")
        self.commit()
        path.unlink()
        self.commit()
        self.assertNotIn("forbidden-history.txt", self.git("diff", "--name-only", self.state["plan_commit"], "HEAD"))
        self.reject(lambda: w.scope(self.root, self.state, self.task), "exceeds published task scope")

    def test_tracked_out_of_scope_file_cannot_be_called_a_sidecar(self):
        (self.root / "forbidden-sidecar.json").write_text("{}\n")
        self.commit()
        self.reject(lambda: w.scope(self.root, self.state, self.task, extra_untracked=("forbidden-sidecar.json",)), "exceeds published task scope")

    def test_removed_intermediate_secret_still_blocks_delivery(self):
        path = self.root / "docs/agents/readiness/evidence/synthetic-leak.txt"
        path.write_text("ghp_" + "z" * 40)
        self.commit()
        path.unlink()
        self.commit()
        self.sidecars()
        self.reject(self.deliver, "Secret/private candidate input")

    def test_candidate_symlink_rejected(self):
        (self.root / "docs/agents/readiness/evidence/synthetic-link.md").symlink_to("../../../../README.md")
        candidate = self.commit()
        self.reject(lambda: w.audit_candidate(self.root, candidate), "Nonregular candidate path")

    def test_binary_content_with_text_extension_rejected(self):
        (self.root / "docs/agents/readiness/evidence/synthetic-binary.md").write_bytes(b"text-prefix\x00binary-payload")
        candidate = self.commit()
        self.reject(lambda: w.audit_candidate(self.root, candidate), "(?i)binary")

    def test_duplicate_json_plan_key_rejected(self):
        values = list(self.plan_fixture())
        path = self.root / values[2]
        path.write_text(path.read_text().replace('"status": "planned"', '"status": "planned", "status": "planned"', 1))
        values[1] = self.commit(amend=True)
        self.bound_review(values[3], values[0], values[1])
        self.reject(lambda: self.validate_plan(values), "Duplicate JSON key")

    def test_plan_extra_intermediate_commit_rejected(self):
        values = list(self.plan_fixture())
        values[1] = self.commit("Synthetic extra publication commit")
        self.bound_review(values[3], values[0], values[1])
        self.reject(lambda: self.validate_plan(values), "one direct child commit")

    def test_published_same_task_handoff_preserves_history_and_enables_new_execution(self):
        old = self.git("rev-parse", "HEAD")
        self.publish(old)
        replacement = {"actor_id": "synthetic-reassigned-author", "execution_id": "synthetic-handoff-execution", "model": None}
        original_author = copy.deepcopy(self.author)
        original_base = self.state["activation"]["base_commit"]
        self.state["activation"]["authorized_executions"].append(replacement)
        self.activation_review()
        candidate = self.commit("Synthetic independently reviewed same-task handoff")
        review_path = "synthetic-handoff-review.json"
        self.bound_review(review_path, old, candidate)
        w.validate_activation(self.root, old, candidate, review_path,
                              actor=original_author["actor_id"], execution=original_author["execution_id"], live=True)
        self.publish(candidate)
        (self.root / review_path).unlink()
        self.assign(replacement)
        w.preflight(self.root, "recover", replacement["actor_id"], replacement["execution_id"], "governance", live=True)
        current, _ = w.load_task(self.root)
        self.assertEqual(current["activation"]["author"], original_author)
        self.assertEqual(current["activation"]["base_commit"], original_base)
        self.assertIn(original_author, current["activation"]["authorized_executions"])

    def test_same_task_handoff_cannot_reset_construction_base(self):
        prior = copy.deepcopy(self.state)
        self.state["activation"]["base_commit"] = self.candidate
        self.reject(lambda: w.activation_base(self.root, self.state, prior, self.candidate), "Handoff cannot change scope/base")

    def test_same_task_handoff_cannot_erase_historical_actors(self):
        prior = copy.deepcopy(self.state)
        self.state["activation"]["authorized_executions"] = [{"actor_id": "synthetic-only-new-author", "execution_id": "synthetic-only-new-execution", "model": None}]
        self.reject(lambda: w.activation_base(self.root, self.state, prior, self.candidate), "cannot erase historical participants")


    def test_fixture_remains_governance_only_when_source_checkout_is_foundation_active(self):
        source = self.root
        source_state = self.read_json(w.STATE)
        source_state["active_task"] = "SYNTHETIC-FOUNDATION-ACTIVE"
        source_state["activation"].update(phase="development-foundation", purpose="foundation")
        self.write_json(w.STATE, source_state)
        environment_path = "docs/agents/readiness/environment-profile.json"
        environment = self.read_json(environment_path)
        environment["dimensions"]["language_runtime"] = "Synthetic future foundation environment"
        self.write_json(environment_path, environment)
        product = source / "src/PersonalMediaManager.Foundation/Synthetic.cs"
        product.parent.mkdir(parents=True)
        product.write_text("// Synthetic new foundation input, never copied to governance fixtures.\n")
        self.commit("Synthetic future active checkout used only to test fixture isolation")
        probe = SuccessorTests(methodName="test_fresh_published_task_first_write_recover_and_commit")
        try:
            with mock.patch.object(sys.modules[__name__], "ROOT", source):
                probe.setUp()
            state, task = w.load_task(probe.root)
            self.assertEqual(state["active_task"], "GOV002")
            self.assertEqual(state["plan_commit"], FIXTURE_PLAN)
            self.assertFalse((probe.root / "src").exists())
            self.assertNotIn("future foundation", probe.read_json(environment_path)["dimensions"]["language_runtime"])
            probe.preflight()
        finally:
            probe.doCleanups()


    def delivery_directory(self):
        return "docs/agents/readiness/evidence/synthetic-delivery/" + self.state["active_task"].lower()

    def delivery_path(self, filename):
        return self.delivery_directory() + "/" + filename

    def capture_acceptance(self):
        evidence = self.read_json(self.delivery_path("synthetic-delivery.json"))
        verification = self.read_json(evidence["verification_report"])
        return {"review": self.read_json(evidence["review_report"]), "verification": verification,
                "outputs": {check["output"]: (self.root / check["output"]).read_text() for check in verification["checks"]}}

    def install_acceptance(self, captured):
        commit = captured["review"]["candidate_commit"]
        prefix = "docs/agents/readiness/evidence/synthetic-predecessor/" + commit[:12] + "/"
        report = prefix + "review.json"
        verification_report = prefix + "verification.json"
        acceptance_review = prefix + "acceptance.json"
        self.write_json(report, captured["review"])
        self.write_json(verification_report, captured["verification"])
        for path, content in captured["outputs"].items():
            target = self.root / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(content)
        bindings = {key: captured["review"][key] for key in ("candidate_commit", "candidate_tree", "base_commit", "diff_paths", "task_path")}
        self.write_json(acceptance_review, {
            **bindings, "result": "approved", "blocking_findings": 0,
            "reviewer": {"actor_id": "synthetic-acceptance-reviewer", "execution_id": "synthetic-acceptance-review-exec", "model": None},
            "observed_at": dt.datetime.now(dt.timezone.utc).isoformat(),
            "findings": "Synthetic predecessor evidence-binding fixture; not a production attestation.",
            "evidence_blobs": {path: g.v.blob(self.root, path) for path in (report, verification_report, *captured["outputs"])},
        })
        return {"commit": commit, "report": report, "verification_report": verification_report,
                "acceptance_review": acceptance_review}


    def predecessor_fixture(self):
        self.sidecars()
        captured = self.capture_acceptance()
        self.deliver()
        for file in (self.root / self.delivery_directory()).iterdir():
            file.unlink()
        values = self.plan_fixture()
        self.validate_plan(values)
        old, plan_commit, plan_path, _, plan = values
        self.publish(plan_commit)
        self.task = plan["tasks"]["SYN002"]
        self.author = self.task["author"]
        receipt = self.install_acceptance(captured)
        self.state = {
            "schema_version": 1, "anchor": w.ANCHOR, "active_task": "SYN002",
            "plan_commit": plan_commit, "plan_path": plan_path,
            "activation": {"task_id": "SYN002", "plan_commit": plan_commit, "base_commit": plan_commit,
                           "phase": self.task["phase"], "purpose": self.task["purpose"], "author": self.author,
                           "authorized_executions": [self.author], "predecessors": {"GOV002": receipt}},
            "activation_review": "docs/agents/readiness/evidence/synthetic-activation.json",
        }
        self.write_json(w.STATE, self.state)
        self.synthetic_baseline(self.author)
        self.activation_review()
        w.load_task(self.root, allow_unpublished_activation=True)
        return receipt, captured

    def test_predecessor_verification_wrong_base_rejected(self):
        receipt, _ = self.predecessor_fixture()
        report = self.read_json(receipt["verification_report"])
        report["base_commit"] = "a" * 40
        self.write_json(receipt["verification_report"], report)
        self.reject(lambda: w.load_task(self.root, allow_unpublished_activation=True), "Stale predecessor full binding: base_commit")

    def test_predecessor_review_wrong_task_rejected(self):
        receipt, _ = self.predecessor_fixture()
        report = self.read_json(receipt["report"])
        report["task_path"] = self.task["card"]
        self.write_json(receipt["report"], report)
        self.reject(lambda: w.load_task(self.root, allow_unpublished_activation=True), "Stale predecessor full binding: task_path")

    def test_predecessor_reviewer_cannot_reuse_actual_author_execution(self):
        receipt, captured = self.predecessor_fixture()
        report = self.read_json(receipt["report"])
        report["reviewer"]["execution_id"] = captured["verification"]["executed_by"]["execution_id"]
        self.write_json(receipt["report"], report)
        self.reject(lambda: w.load_task(self.root, allow_unpublished_activation=True), "not independent: execution_id")

    def test_predecessor_output_change_invalidates_independent_acceptance(self):
        _, captured = self.predecessor_fixture()
        output = next(iter(captured["outputs"]))
        (self.root / output).write_text("Synthetic tampered predecessor output\n")
        self.reject(lambda: w.load_task(self.root, allow_unpublished_activation=True), "evidence changed after independent acceptance")


    def foundation_stub_task(self):
        """Return branch-selection inputs only; no .NET acceptance is claimed."""
        self.write_json("global.json", {"sdk": {"version": "10.0.100", "allowPrerelease": False}})
        task = copy.deepcopy(self.task)
        task.update(phase="development-foundation", purpose="foundation")
        task["allowed_exact"].append("global.json")
        return task

    def foundation_stub_files(self, lock=True, inert=True):
        if lock:
            target = self.root / "src/SyntheticFixture/packages.lock.json"
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text("{}\n")
            self.git("add", str(target.relative_to(self.root)))
        if inert:
            target = self.root / "tests/foundation/verify_foundation.py"
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text("# Synthetic stub input; not product verification.\n")

    def foundation_stub_run(self, fail=None, tests="Passed: 1", inert_code=0, inert_output="FOUNDATION_INERT_AND_ISOLATED_VERIFIED"):
        original = g.run
        def command(args, root=g.ROOT):
            if args[:2] == ["dotnet", "--version"]:
                return subprocess.CompletedProcess(args, 0, "10.0.100\n", "")
            if args and args[0] == "dotnet":
                return subprocess.CompletedProcess(args, 1 if args[1] == fail else 0, tests if args[1] == "test" else "Synthetic SDK stub\n", "")
            if args and Path(args[-1]).name == "verify_foundation.py":
                return subprocess.CompletedProcess(args, inert_code, inert_output, "")
            return original(args, root)
        return command

    def test_foundation_context_rejects_missing_and_wrong_sdk_stub(self):
        task = self.foundation_stub_task()
        for code, output in ((127, ""), (0, "9.0.100\n")):
            with self.subTest(code=code, output=output), mock.patch.object(w, "load_task", return_value=(self.state, task)), mock.patch.object(g, "run", return_value=subprocess.CompletedProcess(["dotnet", "--version"], code, output, "")):
                self.reject(lambda: w.context(self.root, "foundation"), "Unreviewed .NET SDK")

    def test_pending_foundation_cannot_use_governance_repair_exception(self):
        manifest = self.read_json(g.MANIFEST)
        manifest["status"] = "pending"
        self.write_json(g.MANIFEST, manifest)
        with mock.patch.object(w, "load_task", return_value=(self.state, self.foundation_stub_task())), mock.patch.object(g, "run", side_effect=self.foundation_stub_run()):
            self.reject(lambda: w.context(self.root, "foundation", allow_pending=True), "Only published governance repair")

    def test_foundation_missing_dependency_lock_rejected_before_commands(self):
        with mock.patch.object(g, "run") as called:
            self.reject(lambda: w.foundation_validation(self.root), "dependency lock files missing")
            called.assert_not_called()

    def test_foundation_restore_build_test_failures_propagate_from_stubs(self):
        self.foundation_stub_files()
        for phase in ("restore", "build", "test"):
            with self.subTest(phase=phase), mock.patch.object(g, "run", side_effect=self.foundation_stub_run(fail=phase)):
                self.reject(lambda: w.foundation_validation(self.root), "Foundation build/test failed")

    def test_foundation_empty_test_execution_rejected_from_stub(self):
        self.foundation_stub_files()
        with mock.patch.object(g, "run", side_effect=self.foundation_stub_run(tests="Passed: 0 Total: 0")):
            self.reject(lambda: w.foundation_validation(self.root), "No executed foundation tests")

    def test_foundation_missing_inert_checker_rejected(self):
        self.foundation_stub_files(inert=False)
        with mock.patch.object(g, "run", side_effect=self.foundation_stub_run()):
            self.reject(lambda: w.foundation_validation(self.root), "Missing regular file")

    def test_foundation_failed_or_unmarked_inert_stub_rejected(self):
        self.foundation_stub_files()
        for code, output in ((1, "FOUNDATION_INERT_AND_ISOLATED_VERIFIED"), (0, "Synthetic missing marker")):
            with self.subTest(code=code, output=output), mock.patch.object(g, "run", side_effect=self.foundation_stub_run(inert_code=code, inert_output=output)):
                self.reject(lambda: w.foundation_validation(self.root), "Inert/isolated foundation check")

    def test_foundation_stub_success_invokes_every_required_command(self):
        self.foundation_stub_files()
        with mock.patch.object(g, "run", side_effect=self.foundation_stub_run()) as called:
            w.foundation_validation(self.root)
        commands = [call.args[0] for call in called.call_args_list]
        self.assertEqual([args[1] for args in commands[:3]], ["restore", "build", "test"])
        self.assertIn("--locked-mode", commands[0])
        self.assertIn("--no-build", commands[2])
        self.assertEqual(Path(commands[3][-1]).name, "verify_foundation.py")

    def test_foundation_activation_only_runs_governance_suites_and_says_product_not_run(self):
        # Keep the immutable upstream suite and run a tiny real project suite;
        # the SDK result alone is stubbed, so this cannot claim .NET execution.
        task = self.foundation_stub_task()
        self.synthetic_baseline(self.author)
        self.activation_review()
        shutil.rmtree(self.root / "tests/governance")
        (self.root / "tests/governance").mkdir()
        (self.root / "tests/governance/test_synthetic_activation.py").write_text(
            "import unittest\nclass SyntheticGovernance(unittest.TestCase):\n    def test_control(self): self.assertTrue(True)\n")
        base = self.commit("Synthetic small governance suite before activation")
        (self.root / "docs/agents/readiness/evidence/synthetic-activation-only.txt").write_text("Synthetic activation-only change\n")
        self.commit("Synthetic activation-only candidate")
        state = copy.deepcopy(self.state)
        state["activation"]["base_commit"] = base
        real_run = subprocess.run
        output = io.StringIO()
        with mock.patch.object(w, "load_task", return_value=(state, task)), mock.patch.object(g, "run", side_effect=self.foundation_stub_run()), mock.patch.object(w, "foundation_validation", side_effect=AssertionError("Product validation must be NOT-RUN")) as foundation, mock.patch.object(subprocess, "run", wraps=real_run) as invoked, contextlib.redirect_stdout(output):
            self.assertEqual(w.verify(self.root), 0)
        foundation.assert_not_called()
        suites = [call.args[0] for call in invoked.call_args_list if call.args and call.args[0][:3] == [sys.executable, "-m", "unittest"]]
        self.assertEqual(len(suites), 2)
        self.assertIn("FOUNDATION_ACTIVATION_ONLY", output.getvalue())
        self.assertIn("NOT-RUN", output.getvalue())

    def test_foundation_success_return_cannot_change_frozen_candidate_or_sidecars(self):
        task = self.foundation_stub_task()
        task["purpose"] = self.task["purpose"]
        self.synthetic_baseline(self.author)
        self.activation_review()
        self.commit("Synthetic pinned SDK for mutation-only branch probe")
        self.sidecars()
        mutations = (
            ("tracked", lambda: (self.root / "README.md").write_text("Synthetic mutation during successful checker\n"), "dirty"),
            ("sidecar", lambda: (self.root / self.delivery_path("synthetic-delivery-output.txt")).write_text("Synthetic changed frozen output\n"), "changed frozen evidence"),
            ("head", lambda: self.git("commit", "--quiet", "--allow-empty", "-m", "Synthetic hidden checker commit"), "Candidate is not current HEAD"),
        )
        original_head = self.git("rev-parse", "HEAD")
        original_readme = (self.root / "README.md").read_text()
        original_output = (self.root / self.delivery_path("synthetic-delivery-output.txt")).read_text()
        for name, change, pattern in mutations:
            with self.subTest(change=name), mock.patch.object(w, "load_task", return_value=(self.state, task)), mock.patch.object(g, "run", side_effect=self.foundation_stub_run()), mock.patch.object(w, "foundation_validation", side_effect=lambda root: change()):
                self.reject(self.deliver, pattern)
            self.git("update-ref", "HEAD", original_head)
            (self.root / "README.md").write_text(original_readme)
            (self.root / self.delivery_path("synthetic-delivery-output.txt")).write_text(original_output)

    def test_zero_exit_verification_cannot_mutate_tracked_files_index_or_head(self):
        for mutation in ("tracked", "index", "head"):
            with self.subTest(mutation=mutation):
                original_head = self.git("rev-parse", "HEAD")
                original_readme = (self.root / "README.md").read_text()
                real_run = subprocess.run
                changed = []
                def malicious_suite(args, **kwargs):
                    if args[:3] == [sys.executable, "-m", "unittest"]:
                        if not changed:
                            changed.append(True)
                            if mutation in ("tracked", "index"):
                                (self.root / "README.md").write_text("Synthetic zero-exit suite mutation\n")
                                if mutation == "index":
                                    self.git("add", "README.md")
                            else:
                                self.git("commit", "--quiet", "--allow-empty", "-m", "Synthetic zero-exit suite commit")
                        return subprocess.CompletedProcess(args, 0)
                    return real_run(args, **kwargs)
                with mock.patch.object(unittest.TestLoader, "discover") as discover, mock.patch.object(subprocess, "run", side_effect=malicious_suite):
                    discover.return_value.countTestCases.return_value = 1
                    self.reject(lambda: w.verify(self.root), "Verification changed candidate/index/tracked files")
                self.git("update-ref", "HEAD", original_head)
                (self.root / "README.md").write_text(original_readme)
                self.git("reset", "--quiet", "HEAD", "--", "README.md")

    def test_merge_commit_history_rejected_even_with_scoped_final_tree(self):
        self.git("checkout", "-b", "synthetic-side-history")
        (self.root / "README.md").write_text("Synthetic side history\n")
        self.commit()
        self.git("checkout", self.task["task_branch"].removeprefix("refs/heads/"))
        (self.root / "docs/agents/readiness/evidence/synthetic-main-history.txt").write_text("Synthetic main history\n")
        self.commit()
        self.git("merge", "--no-ff", "-m", "Synthetic unapproved merge", "synthetic-side-history")
        self.reject(lambda: w.scope(self.root, self.state, self.task), "Unapproved merge/root")

    def test_replacement_refs_rejected(self):
        self.git("update-ref", "refs/replace/" + self.candidate, self.state["plan_commit"])
        self.reject(lambda: w.load_task(self.root), "Replacement refs")

    def test_grafted_history_rejected(self):
        path = Path(self.git("rev-parse", "--git-path", "info/grafts"))
        if not path.is_absolute():
            path = self.root / path
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("# Synthetic empty graft marker\n")
        self.reject(lambda: w.load_task(self.root), "Grafted history")


    def test_foundation_missing_sdk_pin_rejected_before_sdk_probe(self):
        task = self.foundation_stub_task()
        (self.root / "global.json").unlink()
        with mock.patch.object(w, "load_task", return_value=(self.state, task)), mock.patch.object(g, "run") as probe:
            self.reject(lambda: w.context(self.root, "foundation"), "Missing regular file: global.json")
            probe.assert_not_called()


if __name__ == "__main__":
    unittest.main()
