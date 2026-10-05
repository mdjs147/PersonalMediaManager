"""Focused, fail-closed regression for this workflow's first-run CI setting.

This checks the maintained YAML layout, not an arbitrary YAML parser or actual
.NET certificate behavior. No .NET process or certificate command is executed.
"""
from pathlib import Path
import os
import re
import subprocess
import sys
import unittest


KEY = "DOTNET_GENERATE_ASPNET_CERTIFICATE"
WORKFLOW = Path(__file__).resolve().parents[2] / ".github/workflows/verify-governance.yml"


def check_workflow(text):
    block = (
        "jobs:\n  verify:\n    runs-on: ubuntu-24.04\n    env:\n"
        f"      {KEY}: 'false'\n    steps:\n"
    )
    if text.count(KEY) != 1 or block not in text:
        raise ValueError("CI certificate suppression must be a single job-level false setting")
    if re.search(r"\bdotnet\s+dev-certs\b", text):
        raise ValueError("Certificate commands are outside this CI workflow")


class CiCertificateTests(unittest.TestCase):
    def setUp(self):
        self.text = WORKFLOW.read_text(encoding="utf-8")

    def test_current_workflow_suppresses_before_every_step(self):
        check_workflow(self.text)

    def test_missing_setting_rejects(self):
        with self.assertRaises(ValueError):
            check_workflow(self.text.replace(f"      {KEY}: 'false'\n", ""))

    def test_enabled_or_expression_setting_rejects(self):
        for value in ("'true'", "false", "${{ vars.CERTIFICATE }}"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                check_workflow(self.text.replace("'false'", value, 1))

    def test_step_only_setting_rejects(self):
        changed = self.text.replace(f"    env:\n      {KEY}: 'false'\n", "")
        changed = changed.replace("          SHELL: /bin/bash", f"          SHELL: /bin/bash\n          {KEY}: 'false'")
        with self.assertRaises(ValueError):
            check_workflow(changed)

    def test_step_override_or_certificate_command_rejects(self):
        for added in (f"\n          {KEY}: 'true'\n", "\n        run: dotnet dev-certs https --trust\n"):
            with self.subTest(added=added), self.assertRaises(ValueError):
                check_workflow(self.text + added)

    def test_job_value_reaches_child_environment(self):
        check_workflow(self.text)
        value = re.search(rf"^      {KEY}: '([^']+)'$", self.text, re.M).group(1)
        result = subprocess.run(
            [sys.executable, "-c", f"import os; print(os.environ[{KEY!r}])"],
            env={**os.environ, KEY: value}, text=True, capture_output=True,
        )
        self.assertEqual(result.returncode, 0)
        self.assertEqual(result.stdout.strip(), "false")


if __name__ == "__main__":
    unittest.main()
