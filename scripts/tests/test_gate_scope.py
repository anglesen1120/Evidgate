"""Path-scoped gate tiers: which verify.sh stages a change set can skip.

Two layers are tested apart. `tier_of_path`/`tier_of_paths` name the class of a
path and of a whole change set with no git involved, which is where the
allow-lists live; the `ChangeSet` cases then drive the real classifier over a
disposable repository so the git plumbing (committed range, working tree,
untracked files, deleted paths, base resolution) is exercised too. The
`ReverseCoverage` case closes the loop the other way: a path the .NET sources
name must never be narrowed away.

The fixture repository comes from `review_blockers_fixtures` (the
`check-review-blockers` gate's helper): one disposable repo per test, no
dependence on user git config.
"""
import io
import re
import subprocess
import sys
import unittest
from contextlib import redirect_stdout
from pathlib import Path

# `python3 -m unittest scripts.tests.test_gate_scope` from the repository root
# must find the classifier and the shared fixture helper: verify.sh's stage sets
# PYTHONPATH, a caller may not.
sys.path[:0] = [str(Path(__file__).resolve().parent.parent), str(Path(__file__).resolve().parent)]

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
        "scripts/check-rls.py",
        "scripts/tests/test_check_rls.py",
        "scripts/check-review-blockers.baseline.json",
        "package.json",
        "package-lock.json",
        "tsconfig.json",
        "tsconfig.spec.json",
        "eslint.config.mjs",
        ".prettierrc",
        ".prettierrc.json",
        ".prettierignore",
        "docs/adr/0002-compatibility-and-licence-matrix.md",
        "docs/adr/README.md",
    )
    FULL = (
        ".github/workflows/ci.yml",
        "scripts/review_blockers/gitio.py",
        "scripts/review_blockers/rules.py",
        "scripts/dev/down.sh",
        "Directory.Build.props",
        "Directory.Packages.props",
        "global.json",
        "nuget.config",
        "Evidgate.slnx",
        "Evidgate.slnf",
        "Legacy.sln",
        ".globalconfig",
        "style.ruleset",
        "tools/Legacy.fsproj",
        "tools/Legacy.vbproj",
        "src/Generated.cs",
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
        "docs/security/alert-rules.md",
        "docs/security/audit-and-deletion-rule.md",
        "docs/security/security-event-baseline.md",
        "fixtures/web-api/requests.json",
        ".omp/coordinator/prompt.md",
        "unknown-root-file",
        # The gate's own files: the sourced guard half, then the fixtures that
        # exercise it — a change to any of them is judged by the whole gate.
        "scripts/verify-guards.sh",
        "scripts/tests/test_verify_refusal.py",
        "scripts/tests/test_smoke_mktemp.py",
        "scripts/tests/test_gate_scope_reasons.py",
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

    def test_every_gate_file_needs_the_full_gate(self):
        # The gate files are full-tier even though they sit under scripts/.
        for path in sorted(gate_scope.GATE_PATHS):
            with self.subTest(path=path):
                self.assertEqual(gate_scope.tier_of_path(path)[0], "full")

    def test_untracked_harness_state_is_skipped(self):
        self.assertEqual(gate_scope.tier_of_path(".omp/x.md", untracked={".omp/x.md"})[0], "skip")

    def test_one_documentation_path_is_the_docs_tier(self):
        self.assertEqual(gate_scope.tier_of_paths(["docs/a.md"]), ("docs", gate_scope.DOCS_REASON))

    def test_a_web_path_makes_the_whole_set_no_dotnet(self):
        self.assertEqual(
            gate_scope.tier_of_paths(["docs/a.md", "apps/web/src/a.ts"]),
            ("no-dotnet", gate_scope.NO_DOTNET_REASON),
        )

    def test_review_paths_are_the_review_infra_tier(self):
        for path in (
            "infra/review/nginx/nginx.conf",
            "infra/review/check.sh",
            "infra/review/compose.review.yml",
            "scripts/review/deploy.sh",
            "scripts/review/tests/run.sh",
        ):
            with self.subTest(path=path):
                self.assertEqual(gate_scope.tier_of_path(path)[0], "review-infra")
        self.assertEqual(
            gate_scope.tier_of_paths(["infra/review/check.sh", "docs/a.md"]),
            ("review-infra", gate_scope.REVIEW_INFRA_REASON),
        )

    def test_review_dockerfiles_and_lookalikes_keep_their_own_tier(self):
        expected = {
            "infra/review/Dockerfile.web": "full",
            "infra/review/Dockerfile.web.dockerignore": "full",
            "infra/reviewer/x.sh": "full",
            "infra/README.md": "full",
            "scripts/review_blockers/rules.py": "full",
            "scripts/reviewer.sh": "no-dotnet",
        }
        for path, tier in expected.items():
            with self.subTest(path=path):
                self.assertEqual(gate_scope.tier_of_path(path)[0], tier)

    def test_review_plus_web_takes_the_stricter_tier_and_review_plus_source_is_full(self):
        self.assertEqual(gate_scope.tier_of_paths(["infra/review/check.sh", "apps/web/src/a.ts"])[0], "no-dotnet")
        self.assertEqual(gate_scope.tier_of_paths(["infra/review/check.sh", "src/Program.cs"])[0], "full")
        self.assertEqual(gate_scope.tier_of_paths(["infra/review/check.sh", "scripts/verify.sh"])[0], "full")

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

    def test_docs_is_the_formatter_plus_every_check_stage_but_the_review_one(self):
        stages = gate_scope.stages_for("docs")
        expected = [
            name
            for name in gate_scope.CANONICAL_STAGES
            if (name.startswith("check-") or name == "prettier")
            and name not in gate_scope.REVIEW_ONLY_STAGES
        ]
        self.assertEqual(stages, expected)
        self.assertIn("check-gate-scope", stages)
        self.assertNotIn("check-review-infra", stages)

    def test_review_infra_adds_its_checks_to_the_docs_tier_and_drops_the_build_stages(self):
        stages = gate_scope.stages_for("review-infra")
        for name in gate_scope.stages_for("docs") + ["check-review-infra", "eslint", "image-pins"]:
            self.assertIn(name, stages)
        for name in ("restore", "build", "format", "test", "isolation", "images", "tsc-build", "tsc-spec", "vitest"):
            self.assertNotIn(name, stages)
        self.assertEqual(stages, [n for n in gate_scope.CANONICAL_STAGES if n in stages])

    def test_no_dotnet_and_full_run_the_review_check(self):
        self.assertIn("check-review-infra", gate_scope.stages_for("no-dotnet"))
        self.assertIn("check-review-infra", gate_scope.stages_for("full"))

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
        self.assertIn("auto|docs|review-infra|no-dotnet|full", note)


class Repository(RepoTest):
    """A disposable repository on a `develop` base with the classifier helpers.

    It holds no test, so `test_gate_scope_reasons` can import it for the cases
    that read the reason strings and report lines: unittest collects it there
    with nothing to run.
    """

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

    def tier_of(self, *pairs):
        """Commit the pairs as one change set, then report its scope."""
        self.change(*pairs)
        return self.scope()


class ChangeSet(Repository):
    """The classifier over a disposable repository, one commit or file per case."""

    def test_each_new_full_rule_needs_the_full_gate(self):
        for path in (
            "tools/Legacy.fsproj", "Legacy.sln", ".globalconfig", "style.ruleset",
            "fixtures/web-api/requests.json", "docs/security/audit-and-deletion-rule.md",
            "docs/security/alert-rules.md", "docs/security/security-event-baseline.md",
        ):
            with self.subTest(path=path):
                self.assertEqual(self.tier_of((path, "// x\n")).tier, "full")

    def test_a_control_character_a_commit_carries_keeps_the_report_on_one_line(self):
        # Not `printable` alone: git's -z diff and status must not inject a newline.
        self.change(("src/Evidgate.Api/we\nird.cs", "// x\n"))
        report = gate_scope.render(self.scope())
        self.assertEqual(len(report.splitlines()), 5)
        self.assertIn("reason: src/Evidgate.Api/we\\nird.cs", report)

    def test_an_adr_commit_skips_the_dotnet_stages(self):
        for path in ("docs/adr/0002-compatibility-and-licence-matrix.md", "docs/adr/README.md"):
            with self.subTest(path=path):
                self.assertEqual(self.tier_of((path, "# adr\n")).tier, "no-dotnet")

    def test_a_deleted_story_file_needs_the_full_gate(self):
        story = "plans/features/01-foundation/01.2.1-x.md"
        self.change((story, "story\n"))
        base = self.repo.head()  # the commit that still holds the story
        self.repo.remove(story)
        uncommitted = gate_scope.evaluate({"EVIDGATE_REVIEW_BASE": base})
        self.repo.commit()
        committed = gate_scope.evaluate({"EVIDGATE_REVIEW_BASE": base})
        self.assertEqual((uncommitted.tier, committed.tier), ("full", "full"))
        self.assertIn("catalogue test", committed.reason)

    def test_a_story_file_renamed_away_needs_the_full_gate(self):
        story = "plans/features/01-foundation/01.2.1-x.md"
        self.change((story, "story\n"), ("docs/keep.md", "keep\n"))
        base = self.repo.head()
        git(self.repo.path, "mv", story, "docs/moved-story.md")
        self.repo.commit()
        scope = gate_scope.evaluate({"EVIDGATE_REVIEW_BASE": base})
        self.assertEqual(scope.tier, "full")
        self.assertIn("catalogue test", scope.reason)

    def test_a_story_file_edited_in_place_stays_docs(self):
        story = "plans/features/01-foundation/01.2.1-x.md"
        self.change((story, "story\n"))
        self.repo.write(story, "story, edited\n")
        self.repo.commit()
        self.assertEqual(self.scope().tier, "docs")

    def test_a_source_file_moved_into_docs_is_still_a_full_change(self):
        self.change(("src/Evidgate.Api/a.cs", "// x\n"), ("docs/keep.md", "keep\n"))
        base = self.repo.head()
        git(self.repo.path, "mv", "src/Evidgate.Api/a.cs", "docs/a.md")
        self.repo.commit()
        scope = gate_scope.evaluate({"EVIDGATE_REVIEW_BASE": base})
        self.assertEqual(scope.tier, "full")
        self.assertIn("src/Evidgate.Api/a.cs", scope.reason)

    def test_untracked_harness_state_is_not_counted(self):
        self.change(("docs/a.md", "text\n"))
        self.repo.write(".omp/coordinator/prompt.md", "x\n")
        scope = self.scope()
        self.assertEqual(scope.tier, "docs")
        self.assertEqual(scope.changed, 1)
        self.assertNotIn(".omp/coordinator/prompt.md", scope.paths)

    def test_a_review_base_widens_the_change_set_and_never_narrows_it(self):
        first = self.change(("src/Evidgate.Api/Program.cs", "// x\n"))
        git(self.repo.path, "branch", "-f", "develop", self.repo.head())
        self.change(("docs/a.md", "text\n"))
        self.assertEqual(self.scope().tier, "docs")
        widened = self.scope(EVIDGATE_REVIEW_BASE=first)
        self.assertEqual(widened.tier, "full")
        self.assertIn("src/Evidgate.Api/Program.cs", widened.paths)
        self.assertTrue(any("EVIDGATE_REVIEW_BASE" in note for note in widened.notes))
        # A base at HEAD adds nothing, and an all-zero base is not a base at all.
        self.assertEqual(self.scope(EVIDGATE_REVIEW_BASE=self.repo.head()).tier, "docs")
        self.assertEqual(self.scope(EVIDGATE_REVIEW_BASE="0" * 40).tier, "docs")

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

    def test_a_review_deployment_commit_is_the_review_infra_tier(self):
        self.change(
            ("infra/review/nginx/nginx.conf", "events {}\n"),
            ("infra/review/check-web-image.sh", "#!/usr/bin/env bash\n"),
        )
        scope = self.scope()
        self.assertEqual(scope.tier, "review-infra")
        self.assertEqual(scope.stages(), gate_scope.stages_for("review-infra"))

    def test_a_review_deployment_commit_with_its_runbook_stays_review_infra(self):
        self.change(("scripts/review/deploy.sh", "#!/usr/bin/env bash\n"), ("docs/runbooks/review-deploy.md", "x\n"))
        self.assertEqual(self.scope().tier, "review-infra")

    def test_a_review_deployment_commit_with_a_source_file_is_the_full_tier(self):
        self.change(("infra/review/nginx/nginx.conf", "events {}\n"), ("src/Evidgate.Api/Program.cs", "// x\n"))
        self.assertEqual(self.scope().tier, "full")

    def test_a_review_deployment_commit_with_a_gate_file_is_the_full_tier(self):
        for gate in ("scripts/verify.sh", "scripts/gate_scope.py", ".github/workflows/ci.yml"):
            with self.subTest(gate=gate):
                scope = self.tier_of(("infra/review/nginx/nginx.conf", f"# {gate}\n"), (gate, "# x\n"))
                self.assertEqual(scope.tier, "full")

    def test_a_review_dockerfile_commit_is_the_full_tier(self):
        self.assertEqual(self.tier_of(("infra/review/Dockerfile.web", "FROM scratch\n")).tier, "full")

    def test_a_review_deployment_and_web_commit_takes_the_stricter_no_dotnet_tier(self):
        self.change(("infra/review/compose.review.yml", "services: {}\n"), ("apps/web/src/x.ts", "export {};\n"))
        scope = self.scope()
        self.assertEqual(scope.tier, "no-dotnet")
        self.assertIn("check-review-infra", scope.stages())

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

    def test_the_lock_stage_file_is_a_gate_file(self):
        self.change(("scripts/locked-run.sh", "#!/usr/bin/env bash\n"))
        self.assertEqual(self.scope().tier, "full")


class ReverseCoverage(unittest.TestCase):
    """Every repository path the .NET sources name must survive narrowing.

    A test that opens `docs/…` or `fixtures/…` fails when that path changes, so
    the path cannot be prose-only: every such literal the repository's own C#
    sources name must classify `full`, or a new test reading one shrinks a lane.
    The literal form only: a path a source assembles from segments with
    `Path.Combine(root, "docs", …)` is invisible here, because no joined form is
    rebuilt from one call (`CatalogueTests` builds `plans/features` that way, and
    a removal under it is `full` on its own). A segmented path inside a tree that
    is already `full` loses nothing; one inside an allow-listed tree is the gap
    this test does not close.
    """

    TREES = ("src", "tests")
    LITERAL = re.compile(r'"((?:docs|plans|fixtures|apps|packages|scripts)/[^"\\\n]*)"')
    # A literal with whitespace or a glob is not a path: a citation
    # (`path:N — note`, which CatalogueTests uses) or a pattern. A citation is
    # prose here — only a deleted or renamed-away story file is judged full, so
    # an in-place edit above a cited line is a residual this test does not catch.
    NOT_A_PATH = set(" \t{}*?[")

    def path_literals(self):
        found = set()
        for tree in self.TREES:
            for source in (ROOT / tree).rglob("*.cs"):
                text = source.read_text(encoding="utf-8", errors="replace")
                for match in self.LITERAL.finditer(text):
                    found.add(match.group(1))
        return sorted(literal for literal in found if not set(literal) & self.NOT_A_PATH)

    def test_every_path_a_dotnet_source_names_needs_the_full_gate(self):
        literals = self.path_literals()
        # The audit rule page is the one the docs tier had to exempt: a scan that
        # cannot see it is looking in the wrong place.
        self.assertIn("docs/security/audit-and-deletion-rule.md", literals)
        for literal in literals:
            with self.subTest(literal=literal):
                tier, why = gate_scope.tier_of_path(literal)
                self.assertEqual(tier, "full", f"{literal} is named by a .NET source ({why})")


class MainFunction(unittest.TestCase):
    def test_help_prints_the_usage_and_exits_zero(self):
        out = io.StringIO()
        with redirect_stdout(out):
            self.assertEqual(gate_scope.main(["--help"]), 0)
        self.assertIn("usage: scripts/gate_scope.py", out.getvalue())


if __name__ == "__main__":
    unittest.main()
