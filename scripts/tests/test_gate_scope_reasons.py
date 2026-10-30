"""The scope line's reason and the report a run prints.

A narrowed or full tier tells its reader why it chose that tier and what the
caller may print; `scripts/verify.sh` parses exactly the keys here. Split from
`test_gate_scope.py` (the allow-lists and the change-set plumbing) so each file
keeps one subject and stays under the file line limit.

The cases run over a disposable repository: the reason names the path or the
request that decided the tier, so a rewording that stops naming it fails here.
"""
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

# `python3 -m unittest scripts.tests.test_gate_scope_reasons` from the repository
# root must find the classifier, the shared fixture helper and the repository
# fixture the sibling file defines: verify.sh's stage sets PYTHONPATH, a caller
# may not.
sys.path[:0] = [str(Path(__file__).resolve().parent.parent), str(Path(__file__).resolve().parent)]

import gate_scope  # noqa: E402
from test_gate_scope import Repository  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent.parent


class Reasons(Repository):
    """The reason string names what decided the tier, and the report carries it."""

    def test_a_requested_full_scope_widens_a_docs_change(self):
        self.change(("docs/a.md", "text\n"))
        scope = self.scope(EVIDGATE_GATE_SCOPE="full")
        self.assertEqual(scope.tier, "full")
        # The scope line prints this beside `full`: it must name the widening,
        # not the tier the changed paths alone allowed.
        self.assertIn("widened the docs tier", scope.reason)

    def test_a_full_report_carries_what_the_scope_line_prints(self):
        self.change(("src/Evidgate.Api/Program.cs", "// x\n"))
        report = gate_scope.render(self.scope())
        keys = [line.split(":", 1)[0] for line in report.splitlines()]
        # A full tier runs every stage, so it has no `skipped:` line: the caller
        # still has to be able to print `full (<reason>, N changed path(s), base)`.
        self.assertEqual(keys, ["tier", "reason", "changed", "base", "stages"])
        self.assertIn("tier: full", report)
        self.assertIn("reason: src/Evidgate.Api/Program.cs", report)

    def test_the_report_carries_every_fact_verify_sh_parses(self):
        self.change(("docs/a.md", "text\n"))
        report = gate_scope.render(self.scope())
        keys = [line.split(":", 1)[0] for line in report.splitlines()]
        self.assertEqual(keys[:5], ["tier", "reason", "changed", "base", "stages"])
        self.assertIn("tier: docs", report)
        self.assertIn("skipped: restore", report)


class CommandLine(Repository):
    """The one entry point `scripts/verify.sh` calls: stdout lines, always exit 0."""

    def setUp(self):
        super().setUp()
        self.repo.write("docs/note.md", "text\n")
        self.repo.commit()

    def run_cli(self, cwd=None, **env):
        """Run the entry point the way verify.sh does: a plain `python3 scripts/gate_scope.py`."""
        environment = {key: value for key, value in os.environ.items() if not key.startswith("EVIDGATE_")}
        environment.update(env)
        completed = subprocess.run(
            [sys.executable, str(ROOT / "scripts/gate_scope.py")],
            cwd=self.repo.path if cwd is None else cwd,
            capture_output=True,
            text=True,
            env=environment,
            check=False,
        )
        self.output = completed.stdout
        return completed.returncode

    def test_a_docs_change_prints_the_tier_and_its_stages(self):
        self.assertEqual(self.run_cli(), 0)
        self.assertIn("tier: docs", self.output)
        self.assertIn("stages: prettier", self.output)
        self.assertIn("check-gate-scope", self.output)
        self.assertNotIn("restore", self.output.split("stages:", 1)[1].split("skipped:", 1)[0])

    def test_a_requested_scope_reaches_the_report(self):
        self.assertEqual(self.run_cli(EVIDGATE_GATE_SCOPE="docs"), 0)
        self.assertIn("tier: docs", self.output)
        self.assertEqual(self.run_cli(EVIDGATE_GATE_SCOPE="full"), 0)
        self.assertIn("tier: full", self.output)
        self.assertIn("reason: EVIDGATE_GATE_SCOPE=full widened the docs tier", self.output)

    def test_outside_a_work_tree_the_report_is_a_full_gate(self):
        # A caller in a directory that is not a work tree gets a red-by-default
        # answer, not a traceback: git itself has nothing to read there.
        outside = tempfile.mkdtemp(prefix="gate-scope-outside-")
        self.addCleanup(shutil.rmtree, outside, ignore_errors=True)
        self.assertEqual(self.run_cli(cwd=outside), 0)
        self.assertIn("tier: full", self.output)
        self.assertIn("not inside a git work tree", self.output)


if __name__ == "__main__":
    unittest.main()
