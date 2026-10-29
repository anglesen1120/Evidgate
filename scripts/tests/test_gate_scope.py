"""Path-scoped gate tiers: which verify.sh stages a change set can skip.

Two layers are tested apart. `tier_of_path`/`tier_of_paths` name the class of a
path and of a whole change set with no git involved, which is where the
allow-lists live; the `ChangeSet` cases then drive the real classifier over a
disposable repository so the git plumbing (committed range, working tree,
untracked files, base resolution) is exercised too, not just the tables.

The fixture repository comes from `review_blockers_fixtures` (the
`check-review-blockers` gate's helper): one disposable repo per test, no
dependence on user git config.
"""
import io
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path

import gate_scope
from review_blockers import gitio
from review_blockers_fixtures import RepoTest, git

ROOT = Path(__file__).resolve().parent.parent.parent


class PathClasses(unittest.TestCase):
    """The allow-lists: every class the contract names, one path each."""

    DOCS = (
        "docs/engineering/omp-setup.md",
        "docs/status-carried.md",
        "plans/features/01-foundation/01.2.1-x.md",
        "AGENTS.md",
        "STATUS.md",
        "DECISIONS.md",
        "REVIEW.md",
        "README.md",
    )
    NO_DOTNET = (
        "apps/web/src/app/app.component.ts",
        "apps/web/package.json",
        "packages/sdk/src/client.ts",
        "fixtures/web-api/requests.json",
        "scripts/check-rls.py",
        "scripts/tests/test_check_rls.py",
        "scripts/check-review-blockers.baseline.json",
        "scripts/review_blockers/rules.py",
        "package.json",
        "package-lock.json",
        "tsconfig.json",
        "tsconfig.spec.json",
        "eslint.config.mjs",
        ".prettierrc",
        ".prettierrc.json",
        ".prettierignore",
    )
    FULL = (
        ".github/workflows/ci.yml",
        "scripts/verify.sh",
        "scripts/locked-run.sh",
        "scripts/gate_scope.py",
        "scripts/tests/test_gate_scope.py",
        "scripts/dev/down.sh",
        "Directory.Build.props",
        "Directory.Packages.props",
        "global.json",
        "nuget.config",
        "Evidgate.slnx",
        "src/Evidgate.Api/Program.cs",
        "tests/Isolation.Tests/Harness.cs",
        "migrations/0055_x.sql",
        "contracts/openapi.yaml",
        "infra/config-matrix.json",
        ".editorconfig",
        ".config/dotnet-tools.json",
        "Dockerfile",
        "infra/docker/Dockerfile.api",
        "packages.lock.json",
        "artifacts/licence-report.json",
        "docs/design/landing-page.html",
        "docs/README.txt",
        "unknown-root-file",
    )

    def test_documentation_is_the_docs_tier(self):
        for path in self.DOCS:
            with self.subTest(path=path):
                self.assertEqual(gate_scope.tier_of_path(path), ("docs", "documentation"))

    def test_web_and_script_paths_skip_the_dotnet_stages(self):
        for path in self.NO_DOTNET:
            with self.subTest(path=path):
                tier, why = gate_scope.tier_of_path(path)
                self.assertEqual(tier, "no-dotnet", why)

    def test_every_other_path_needs_the_full_gate(self):
        for path in self.FULL:
            with self.subTest(path=path):
                tier, why = gate_scope.tier_of_path(path)
                self.assertEqual(tier, "full", why)

    def test_the_classifier_its_own_files_are_gate_files(self):
        # The gate files are full-tier even though they sit under scripts/.
        for path in gate_scope.GATE_PATHS:
            with self.subTest(path=path):
                self.assertEqual(gate_scope.tier_of_path(path)[0], "full")

    def test_untracked_harness_state_is_not_a_change(self):
        self.assertEqual(gate_scope.tier_of_path(".omp/coordinator/prompt.md")[0], "skip")

    def test_one_documentation_path_is_the_docs_tier(self):
        self.assertEqual(gate_scope.tier_of_paths(["docs/a.md"]), ("docs", gate_scope.DOCS_REASON))

    def test_a_web_path_makes_the_whole_set_no_dotnet(self):
        self.assertEqual(
            gate_scope.tier_of_paths(["docs/a.md", "apps/web/src/a.ts"]),
            ("no-dotnet", gate_scope.NO_DOTNET_REASON),
        )

    def test_one_unsafe_path_makes_the_whole_set_full(self):
        tier, why = gate_scope.tier_of_paths(["apps/web/src/a.ts", "src/Program.cs"])
        self.assertEqual(tier, "full")
        self.assertIn("src/Program.cs", why)

    def test_no_path_at_all_is_full(self):
        self.assertEqual(gate_scope.tier_of_paths([])[0], "full")
        self.assertEqual(gate_scope.tier_of_paths([".omp/x/y.md"])[0], "full")


class StageSelection(unittest.TestCase):
    def test_the_mirror_matches_verify_sh(self):
        # verify.sh authors the canonical order (`--list-stages`) and this module
        # mirrors it, so the list cannot drift in silence.
        listed = subprocess.run(
            ["bash", str(ROOT / "scripts/verify.sh"), "--list-stages"],
            capture_output=True,
            text=True,
            check=True,
        ).stdout.split()
        self.assertEqual(listed, list(gate_scope.CANONICAL_STAGES))

    def test_full_runs_every_stage(self):
        self.assertEqual(gate_scope.stages_for("full"), list(gate_scope.CANONICAL_STAGES))

    def test_no_dotnet_drops_the_dotnet_and_docker_stages_only(self):
        stages = gate_scope.stages_for("no-dotnet")
        dropped = [name for name in gate_scope.CANONICAL_STAGES if name not in stages]
        self.assertEqual(sorted(dropped), sorted(gate_scope.SKIPPED_WHEN_NARROWED))
        # The web half and the Dockerfile pin check stay: they build nothing.
        for name in ("tsc-build", "tsc-spec", "vitest", "eslint", "image-pins"):
            self.assertIn(name, stages)

    def test_docs_is_the_formatter_plus_every_check_stage(self):
        stages = gate_scope.stages_for("docs")
        expected = [
            name
            for name in gate_scope.CANONICAL_STAGES
            if name.startswith("check-") or name == "prettier"
        ]
        self.assertEqual(stages, expected)
        self.assertIn("check-gate-scope", stages)

    def test_an_unknown_tier_is_a_full_gate(self):
        self.assertEqual(gate_scope.stages_for("nothing-like-it"), list(gate_scope.CANONICAL_STAGES))


class Requests(unittest.TestCase):
    """`EVIDGATE_GATE_SCOPE` widens a computed tier and can never narrow it."""

    def test_auto_keeps_the_computed_tier(self):
        for tier in gate_scope.TIER_ORDER:
            with self.subTest(tier=tier):
                self.assertEqual(gate_scope.apply_request(tier, "auto"), (tier, None))
                self.assertEqual(gate_scope.apply_request(tier, ""), (tier, None))

    def test_a_narrower_request_is_refused(self):
        tier, note = gate_scope.apply_request("full", "docs")
        self.assertEqual(tier, "full")
        self.assertIn("refused", note)
        self.assertEqual(gate_scope.apply_request("no-dotnet", "docs")[0], "no-dotnet")

    def test_a_wider_request_is_honoured(self):
        tier, note = gate_scope.apply_request("docs", "full")
        self.assertEqual(tier, "full")
        self.assertIn("widened", note)
        self.assertEqual(gate_scope.apply_request("docs", "no-dotnet")[0], "no-dotnet")

    def test_an_unreadable_request_is_a_full_gate(self):
        tier, note = gate_scope.apply_request("docs", "quick")
        self.assertEqual(tier, "full")
        self.assertIn("auto|docs|no-dotnet|full", note)


class ChangeSet(RepoTest):
    """The classifier over a disposable repository, one commit or file per case."""

    def setUp(self):
        super().setUp()
        # The default base chain starts at origin/develop; a local `develop` is
        # what a clean clone has (the smoke build's case).
        git(self.repo.path, "branch", "develop")

    def scope(self, **env):
        return gate_scope.evaluate({"EVIDGATE_REVIEW_BASE": "", **env})

    def change(self, *pairs):
        base = self.repo.head()
        for path, text in pairs:
            self.repo.write(path, text)
        self.repo.commit()
        return base

    def test_a_documentation_only_commit_is_the_docs_tier(self):
        base = self.change(("docs/note.md", "text\n"), ("STATUS.md", "row\n"))
        scope = gate_scope.evaluate({"EVIDGATE_REVIEW_BASE": base})
        self.assertEqual(scope.tier, "docs")
        self.assertEqual(scope.changed, 2)
        self.assertEqual(scope.base, gitio.short(base))
        self.assertEqual(scope.stages(), gate_scope.stages_for("docs"))

    def test_a_web_only_commit_is_the_no_dotnet_tier(self):
        self.change(("apps/web/src/x.ts", "export const x = 1;\n"))
        self.assertEqual(self.scope().tier, "no-dotnet")

    def test_a_non_gate_script_commit_is_the_no_dotnet_tier(self):
        self.change(("scripts/check-rls.py", "# probe\n"))
        self.assertEqual(self.scope().tier, "no-dotnet")

    def test_a_mixed_documentation_and_web_commit_stays_no_dotnet(self):
        self.change(("docs/a.md", "text\n"), ("packages/sdk/src/a.ts", "export {};\n"))
        self.assertEqual(self.scope().tier, "no-dotnet")

    def test_a_source_commit_is_the_full_tier(self):
        self.change(("src/Evidgate.Api/Program.cs", "// x\n"))
        scope = self.scope()
        self.assertEqual(scope.tier, "full")
        self.assertIn("src/Evidgate.Api/Program.cs", scope.reason)
        self.assertEqual(scope.stages(), list(gate_scope.CANONICAL_STAGES))

    def test_an_uncommitted_source_edit_forces_full(self):
        self.change(("docs/a.md", "text\n"))
        self.repo.write("src/Evidgate.Api/Program.cs", "// uncommitted\n")
        self.assertEqual(self.scope().tier, "full")

    def test_an_uncommitted_documentation_edit_stays_docs(self):
        self.change(("docs/a.md", "text\n"))
        self.repo.write("docs/a.md", "more text\n")
        self.assertEqual(self.scope().tier, "docs")

    def test_an_untracked_file_counts_as_a_change(self):
        self.change(("docs/a.md", "text\n"))
        self.repo.write("src/Evidgate.Worker/New.cs", "// new\n")
        self.assertEqual(self.scope().tier, "full")

    def test_a_deleted_documentation_file_stays_docs(self):
        self.change(("docs/gone.md", "text\n"))
        base = self.repo.head()
        self.repo.remove("docs/gone.md")
        self.repo.commit()
        self.assertEqual(gate_scope.evaluate({"EVIDGATE_REVIEW_BASE": base}).tier, "docs")

    def test_the_develop_branch_is_the_default_base(self):
        self.change(("apps/web/src/x.ts", "export const x = 1;\n"))
        self.assertEqual(self.scope().tier, "no-dotnet")

    def test_an_unresolvable_base_is_a_full_gate(self):
        self.change(("docs/a.md", "text\n"))
        scope = gate_scope.evaluate({"EVIDGATE_REVIEW_BASE": "nosuchref"})
        self.assertEqual(scope.tier, "full")
        self.assertIn("does not resolve", scope.reason)
        self.assertEqual(scope.base, "unknown")

    def test_no_base_candidate_at_all_is_a_full_gate(self):
        git(self.repo.path, "branch", "-D", "develop")
        self.change(("docs/a.md", "text\n"))
        scope = self.scope()
        self.assertEqual(scope.tier, "full")
        self.assertIn("full history", scope.reason)

    def test_a_requested_docs_scope_cannot_hide_a_source_change(self):
        self.change(("src/Evidgate.Api/Program.cs", "// x\n"))
        scope = self.scope(EVIDGATE_GATE_SCOPE="docs")
        self.assertEqual(scope.tier, "full")
        self.assertTrue(any("refused" in note for note in scope.notes))

    def test_a_requested_full_scope_widens_a_docs_change(self):
        self.change(("docs/a.md", "text\n"))
        scope = self.scope(EVIDGATE_GATE_SCOPE="full")
        self.assertEqual(scope.tier, "full")
        self.assertTrue(any("widened" in note for note in scope.notes))

    def test_the_lock_stage_file_is_a_gate_file(self):
        self.change(("scripts/locked-run.sh", "#!/usr/bin/env bash\n"))
        self.assertEqual(self.scope().tier, "full")

    def test_the_report_carries_every_fact_verify_sh_parses(self):
        self.change(("docs/a.md", "text\n"))
        report = gate_scope.render(self.scope())
        keys = [line.split(":", 1)[0] for line in report.splitlines()]
        self.assertEqual(keys[:5], ["tier", "reason", "changed", "base", "stages"])
        self.assertIn("tier: docs", report)
        self.assertIn("skipped: restore", report)


class CommandLine(RepoTest):
    """The one entry point `scripts/verify.sh` calls: stdout lines, always exit 0."""

    def setUp(self):
        super().setUp()
        git(self.repo.path, "branch", "develop")
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
        self.assertIn("note: EVIDGATE_GATE_SCOPE=full widened", self.output)

    def test_outside_a_work_tree_the_report_is_a_full_gate(self):
        # A caller in a directory that is not a work tree gets a red-by-default
        # answer, not a traceback: git itself has nothing to read there.
        outside = tempfile.mkdtemp(prefix="gate-scope-outside-")
        self.addCleanup(shutil.rmtree, outside, ignore_errors=True)
        self.assertEqual(self.run_cli(cwd=outside), 0)
        self.assertIn("tier: full", self.output)
        self.assertIn("not inside a git work tree", self.output)


class MainFunction(unittest.TestCase):
    def test_help_prints_the_usage_and_exits_zero(self):
        out = io.StringIO()
        with redirect_stdout(out):
            self.assertEqual(gate_scope.main(["--help"]), 0)
        self.assertIn("usage: scripts/gate_scope.py", out.getvalue())


if __name__ == "__main__":
    unittest.main()
