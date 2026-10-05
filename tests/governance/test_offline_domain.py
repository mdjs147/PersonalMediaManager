"""Bounded offline-domain declarations and real callsite routing.

Git fixtures and SDK/runner stubs below are control tests, not .NET product,
production-host, public-remote or offline Catalog acceptance.
"""
import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import textwrap
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import pmm_governance as g
import pmm_workflow as w
import test_successor as fixtures


class OfflineDomainDeclarationTests(unittest.TestCase):
    def setUp(self):
        self.plan = w.object_json(ROOT, fixtures.FIXTURE_PLAN, "docs/workflow/GOV002-plan-publication.json")
        self.task = self.plan["tasks"]["DEV001"]
        self.task.update(phase="development-offline-domain", purpose="offline-domain",
                         allowed_exact=["src/PersonalMediaManager.Catalog/Catalog.csproj"],
                         allowed_prefixes=["src/PersonalMediaManager.Catalog/Model/", "tests/catalog/"])

    def descriptor(self):
        return w.descriptor(self.plan, "DEV001")

    def test_explicit_offline_stage_and_bounded_catalog_are_accepted(self):
        self.assertIs(self.descriptor(), self.task)
        self.assertEqual(w.PHASES["development-offline-domain"], "offline-domain")
        self.assertIn("development-offline-domain", w.DOTNET_PHASES)
        self.assertFalse(self.task["business_allowed"])

    def test_non_catalog_source_and_neighbor_prefixes_reject(self):
        for path in ("src/", "src/PersonalMediaManager.Web/", "src/PersonalMediaManager.Foundation/",
                     "src/PersonalMediaManager.Catalog", "src/PersonalMediaManager.CatalogSibling/"):
            with self.subTest(path=path):
                self.task["allowed_prefixes"] = [path]
                with self.assertRaises(g.v.Invalid):
                    self.descriptor()

    def test_source_ancestor_prefixes_cannot_bypass_catalog_boundary(self):
        for prefix in ("s", "sr", "src"):
            with self.subTest(prefix=prefix):
                self.task["allowed_prefixes"] = [prefix]
                with self.assertRaisesRegex(g.v.Invalid, "prefix overlaps"):
                    self.descriptor()

    def test_business_self_grant_still_rejects(self):
        self.task["business_allowed"] = True
        with self.assertRaisesRegex(g.v.Invalid, "premature business"):
            self.descriptor()

    def test_unknown_or_mismatched_phase_purpose_rejects(self):
        for phase, purpose in (("unknown", "offline-domain"), ("development-foundation", "offline-domain"),
                               ("development-offline-domain", "foundation"), ("development-offline-domain", "business")):
            with self.subTest(phase=phase, purpose=purpose):
                self.task.update(phase=phase, purpose=purpose)
                with self.assertRaisesRegex(g.v.Invalid, "Unknown phase/purpose"):
                    self.descriptor()

    def test_preexisting_phase_contracts_remain_accepted(self):
        for phase, purpose in (("development-foundation", "foundation"), ("governance-maintenance", "governance"),
                               ("requirements-design", "requirements")):
            with self.subTest(phase=phase):
                self.task.update(phase=phase, purpose=purpose, allowed_exact=["README.md"], allowed_prefixes=[])
                self.descriptor()

    def test_offline_context_rejects_bad_sdk_and_removed_gate_is_detected(self):
        state = w.read_json(ROOT / w.STATE)
        report = w.read_json(ROOT / state["activation_review"])
        def refusal_oracle():
            with self.assertRaisesRegex(g.v.Invalid, "Unreviewed .NET SDK"):
                w.context(ROOT, "offline-domain")
        with mock.patch.object(w, "load_task", return_value=(state, self.task)), \
             mock.patch.object(g, "run", return_value=subprocess.CompletedProcess([], 0, "9.0.100\n", "")), \
             mock.patch.object(w.v, "check_context"), mock.patch.object(w.v, "observed_context", return_value=report["context"]):
            refusal_oracle()
            with mock.patch.object(w, "DOTNET_PHASES", {"development-foundation"}):
                with self.assertRaises(AssertionError):
                    refusal_oracle()

    def test_pending_offline_context_cannot_use_governance_repair(self):
        state = w.read_json(ROOT / w.STATE)
        with mock.patch.object(w, "load_task", return_value=(state, self.task)), \
             mock.patch.object(w, "read_json", return_value={"status": "pending"}), \
             mock.patch.object(g, "run", return_value=subprocess.CompletedProcess([], 0, "10.0.401\n", "")):
            with self.assertRaisesRegex(g.v.Invalid, "Only published governance repair"):
                w.context(ROOT, "offline-domain", allow_pending=True)

    def test_actual_ci_phase_snippet_selects_both_dotnet_stages(self):
        workflow = (ROOT / ".github/workflows/verify-governance.yml").read_text()
        program = textwrap.dedent(workflow.split("python3 - <<'PYCODE'", 1)[1].split("\n", 1)[1].split("          PYCODE", 1)[0])
        self.assertIn("if: steps.task-phase.outputs.dotnet_required == 'true'", workflow)
        with tempfile.TemporaryDirectory(prefix="pmm-phase-snippet-") as temp:
            root = Path(temp)
            state = root / w.STATE
            state.parent.mkdir(parents=True)
            for phase, expected in (("development-foundation", "true"), ("development-offline-domain", "true"),
                                    ("governance-maintenance", "false"), ("unknown", "false")):
                state.write_text(json.dumps({"activation": {"phase": phase}}))
                result = subprocess.run([sys.executable, "-c", program], cwd=root, text=True, capture_output=True)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(result.stdout.strip(), "dotnet_required=" + expected)


class OfflineDomainRoutingTests(unittest.TestCase):
    def setUp(self):
        self.fixture = fixtures.SuccessorTests(methodName="runTest")
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.task = self.fixture.foundation_stub_task()
        self.task.update(phase="development-offline-domain", purpose="offline-domain")
        self.fixture.synthetic_baseline(self.fixture.author)
        self.fixture.activation_review()
        self.fixture.commit("Synthetic offline routing inputs, not product acceptance")

    def test_verify_offline_phase_cannot_take_empty_foundation_activation_exemption(self):
        f = self.fixture
        base = f.git("rev-parse", "HEAD")
        (f.root / "docs/agents/readiness/evidence/synthetic-offline-activation.txt").write_text("Synthetic metadata-only delta\n")
        f.commit("Synthetic metadata-only offline candidate")
        state = copy.deepcopy(f.state)
        state["activation"]["base_commit"] = base
        real_run = subprocess.run
        def route_only(args, **kwargs):
            if args[:3] == [sys.executable, "-m", "unittest"]:
                return subprocess.CompletedProcess(args, 0)
            return real_run(args, **kwargs)
        self.assertFalse((f.root / "PersonalMediaManager.sln").exists())
        with mock.patch.object(w, "load_task", return_value=(state, self.task)), \
             mock.patch.object(w, "context"), mock.patch.object(subprocess, "run", side_effect=route_only), \
             mock.patch.object(unittest.TestLoader, "discover") as discover, \
             mock.patch.object(w, "foundation_validation", side_effect=g.v.Invalid("shared offline validation witness")) as shared:
            discover.return_value.countTestCases.return_value = 1
            with self.assertRaisesRegex(g.v.Invalid, "shared offline validation witness"):
                w.verify(f.root)
            shared.assert_called_once_with(f.root)

    def test_delivery_offline_phase_invokes_shared_validation(self):
        f = self.fixture
        f.sidecars()
        with mock.patch.object(w, "load_task", return_value=(f.state, self.task)), \
             mock.patch.object(w, "context"), \
             mock.patch.object(w, "foundation_validation", side_effect=g.v.Invalid("shared offline delivery witness")) as shared:
            with self.assertRaisesRegex(g.v.Invalid, "shared offline delivery witness"):
                f.deliver()
            shared.assert_called_once_with(f.root)


if __name__ == "__main__":
    unittest.main()
