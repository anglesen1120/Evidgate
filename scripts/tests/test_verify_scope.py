"""Stage selection in scripts/verify.sh: which stages a bare run executes.

`verify.sh` defines the selection in functions and runs nothing when it is
sourced, so the cases drive the real `read_scope`/`plan_selection` with report
text written out by hand, and `main` end to end against a copied script whose
classifier and `run_stage` are stubs. Nothing here needs git, a change set or a
built tree.
"""
import os
import shutil
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
VERIFY = ROOT / "scripts" / "verify.sh"

_ALL_STAGES = None


def all_stages():
    """The canonical list, read from the script itself."""
    global _ALL_STAGES
    if _ALL_STAGES is None:
        listed = subprocess.run(
            ["bash", str(VERIFY), "--list-stages"],
            capture_output=True,
            text=True,
            check=True,
        )
        _ALL_STAGES = listed.stdout.split()
    return _ALL_STAGES


# $1 is the script to source, $2.. its own arguments: $0 stays a driver name, so
# BASH_SOURCE differs from $0 and the end-of-file guard does not run `main`.
SOURCE = textwrap.dedent("""\
    source "$1"
    shift
    """)

SELECT = SOURCE + textwrap.dedent("""\
    read_scope "$1"
    plan_selection
    printf 'tier=%s\\n' "$scope_tier"
    for name in ${selected[@]+"${selected[@]}"}; do printf 'stage=%s\\n' "$name"; done
    """)

MAIN = SOURCE + textwrap.dedent("""\
    run_stage() { printf 'RAN %s\\n' "$1"; }
    main "$@"
    """)

STUB_CLASSIFIER = textwrap.dedent("""\
    import os
    import pathlib
    import sys

    marker = os.environ.get("STUB_MARKER")
    if marker:
        pathlib.Path(marker).write_text("called\\n")
    sys.stdout.write(os.environ.get("STUB_REPORT", ""))
    sys.exit(int(os.environ.get("STUB_EXIT", "0")))
    """)


def bash(script, *args, env=None):
    """Run `script` with $1 = the script to source and the rest as its args."""
    return subprocess.run(
        ["bash", "-c", script, "verify-scope-driver", *args],
        capture_output=True,
        text=True,
        env=env,
    )


def select(report):
    """Run `read_scope` + `plan_selection` over one classifier report."""
    done = bash(SELECT, str(VERIFY), report)
    if done.returncode != 0:
        raise AssertionError(f"selection failed: {done.stderr}")
    lines = done.stdout.splitlines()
    tier = [line[len("tier=") :] for line in lines if line.startswith("tier=")]
    stages = [line[len("stage=") :] for line in lines if line.startswith("stage=")]
    return tier[0], stages, done.stdout


class Selection(unittest.TestCase):
    """The parsed report in, the run list out: only two tiers may narrow."""

    def test_a_docs_tier_runs_exactly_its_stages(self):
        tier, stages, _ = select("tier: docs\nstages: prettier check-gate-scope\n")
        self.assertEqual(tier, "docs")
        self.assertEqual(stages, ["prettier", "check-gate-scope"])

    def test_a_no_dotnet_tier_runs_exactly_its_stages(self):
        tier, stages, _ = select("tier: no-dotnet\nstages: tsc-build vitest image-pins\n")
        self.assertEqual(tier, "no-dotnet")
        self.assertEqual(stages, ["tsc-build", "vitest", "image-pins"])

    def test_a_full_tier_runs_every_stage_whatever_its_list_says(self):
        # The tier decides, not the list: a `full` tier that listed two stages
        # would otherwise run a gate nobody asked it to narrow.
        tier, stages, _ = select("tier: full\nstages: build test\n")
        self.assertEqual(tier, "full")
        self.assertEqual(stages, all_stages())

    def test_an_unknown_tier_runs_every_stage(self):
        tier, stages, _ = select("tier: quick\nstages: build\n")
        self.assertEqual(tier, "full")
        self.assertEqual(stages, all_stages())

    def test_an_unknown_stage_name_runs_every_stage(self):
        tier, stages, _ = select("tier: docs\nstages: prettier nosuch-stage\n")
        self.assertEqual(tier, "full")
        self.assertEqual(stages, all_stages())

    def test_an_empty_or_blank_stage_list_runs_every_stage(self):
        for report in ("tier: docs\nstages: \n", "tier: docs\nstages:    \n", "tier: docs\n"):
            with self.subTest(report=report):
                tier, stages, out = select(report)
                self.assertEqual(tier, "full")
                self.assertEqual(stages, all_stages())
                self.assertIn("no usable tier/stage list (full gate)", out)

    def test_no_report_at_all_runs_every_stage(self):
        tier, stages, out = select("")
        self.assertEqual(tier, "full")
        self.assertEqual(stages, all_stages())
        self.assertIn("no usable tier/stage list (full gate)", out)

    def test_the_first_value_of_a_key_wins(self):
        # A later duplicate may repeat a fact; it must not overwrite the value
        # the decision was already made on.
        tier, stages, _ = select("tier: full\ntier: docs\nstages: build\n")
        self.assertEqual(tier, "full")
        self.assertEqual(stages, all_stages())
        tier, stages, _ = select("tier: docs\nstages: build\nstages: restore\n")
        self.assertEqual(stages, ["build"])

    def test_the_scope_line_reports_the_change_set(self):
        _, _, out = select(
            "tier: docs\nreason: markdown only\nchanged: 3\nbase: abc123\n"
            "stages: prettier\nskipped: build\nnote: untracked .omp ignored\n"
        )
        self.assertIn("== gate scope: docs (markdown only, 3 changed path(s), base abc123)", out)
        self.assertIn("skipped by scope: build", out)
        self.assertIn("note: untracked .omp ignored", out)

    def test_a_full_tier_reports_why_it_is_full(self):
        # A `full` verdict the classifier made is a verdict with a reason, not a
        # missing stage list: the line names the change set the gate ran for.
        _, stages, out = select(
            "tier: full\nreason: outside the narrowed allow-lists\nchanged: 4\n"
            "base: def456\nstages: build test\n"
        )
        self.assertEqual(stages, all_stages())
        self.assertIn(
            "== gate scope: full (outside the narrowed allow-lists, 4 changed path(s), base def456)",
            out,
        )
        self.assertNotIn("no usable tier/stage list", out)

    def test_a_report_without_facts_keeps_the_missing_list_line(self):
        _, stages, out = select("tier: full\n")
        self.assertEqual(stages, all_stages())
        self.assertIn("no usable tier/stage list (full gate)", out)

    def test_a_narrow_tier_without_a_usable_stage_list_keeps_the_missing_list_line(self):
        # The rich line is a `full` verdict's, not a stand-in: a docs report whose
        # stage list is empty or invented names no runnable list, so it falls back
        # to the line that says so.
        for report in (
            "tier: docs\nreason: markdown only\nchanged: 1\nbase: abc\nstages: \n",
            "tier: docs\nreason: markdown only\nchanged: 1\nbase: abc\nstages: nosuch\n",
            "tier: quick\nreason: an unknown tier\nchanged: 1\nbase: abc\nstages: build\n",
        ):
            with self.subTest(report=report):
                tier, stages, out = select(report)
                self.assertEqual(tier, "full")
                self.assertEqual(stages, all_stages())
                self.assertIn("no usable tier/stage list (full gate)", out)
                self.assertNotIn("== gate scope: full (", out)


class BareRun(unittest.TestCase):
    """`main` end to end: the copied script, a stub classifier, stub stages."""

    def setUp(self):
        self.tree = Path(tempfile.mkdtemp(prefix="evgt-verify-scope-"))
        self.addCleanup(shutil.rmtree, self.tree, True)
        (self.tree / "scripts").mkdir()
        self.script = self.tree / "scripts" / "verify.sh"
        shutil.copy(VERIFY, self.script)
        (self.tree / "scripts" / "gate_scope.py").write_text(STUB_CLASSIFIER)
        self.marker = self.tree / "classifier-ran"

    def run_main(self, *args, report="", exit_code=0, marker=False):
        env = dict(
            os.environ,
            STUB_REPORT=report,
            STUB_EXIT=str(exit_code),
            PYTHONDONTWRITEBYTECODE="1",
        )
        if marker:
            env["STUB_MARKER"] = str(self.marker)
        return bash(MAIN, str(self.script), *args, env=env)

    @staticmethod
    def ran(done):
        return [line[len("RAN ") :] for line in done.stdout.splitlines() if line.startswith("RAN ")]

    def test_a_narrowed_tier_runs_only_its_stages(self):
        done = self.run_main(
            report="tier: docs\nreason: markdown only\nchanged: 2\nbase: abc\n"
            "stages: prettier check-gate-scope\n"
        )
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(self.ran(done), ["prettier", "check-gate-scope"])
        self.assertIn("== verify: selected stages passed: prettier check-gate-scope", done.stdout)

    def test_a_crashing_classifier_is_the_full_gate(self):
        done = self.run_main(exit_code=1, report="")
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(self.ran(done), all_stages())
        self.assertIn("no usable tier/stage list (full gate)", done.stdout)
        self.assertIn("== verify: all stages passed", done.stdout)

    def test_a_silent_classifier_is_the_full_gate(self):
        done = self.run_main(report="")
        self.assertEqual(self.ran(done), all_stages())
        self.assertIn("no usable tier/stage list (full gate)", done.stdout)

    def test_a_full_tier_never_claims_a_narrowed_run(self):
        done = self.run_main(
            report="tier: full\nreason: outside the narrowed allow-lists\nchanged: 1\n"
            "base: abc\nstages: build\n"
        )
        self.assertEqual(self.ran(done), all_stages())
        self.assertIn(
            "== gate scope: full (outside the narrowed allow-lists, 1 changed path(s), base abc)",
            done.stdout,
        )
        self.assertIn("== verify: all stages passed", done.stdout)

    def test_all_stages_passed_is_a_coverage_claim_not_a_tier_claim(self):
        done = self.run_main(report="tier: docs\nstages: " + " ".join(all_stages()) + "\n")
        self.assertEqual(self.ran(done), all_stages())
        self.assertIn("== verify: all stages passed", done.stdout)

    def test_an_explicit_stage_list_never_consults_the_classifier(self):
        done = self.run_main(
            "--stage", "build", marker=True, report="tier: docs\nstages: prettier\n"
        )
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(self.ran(done), ["build"])
        self.assertFalse(self.marker.exists(), "a --stage run must not ask the classifier")
        self.assertIn("== gate scope: explicit", done.stdout)
        self.assertIn("== verify: selected stages passed: build", done.stdout)

    def test_an_unknown_explicit_stage_is_refused(self):
        done = self.run_main("--stage", "nosuch-stage")
        self.assertEqual(done.returncode, 2)
        self.assertIn("verify: unknown stage 'nosuch-stage'", done.stderr)
        self.assertEqual(self.ran(done), [])

    def test_sourcing_defines_the_selection_and_runs_nothing(self):
        done = bash(SOURCE + 'cd /; printf "cwd=%s\\n" "$PWD"\n', str(VERIFY))
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertNotIn("==", done.stdout)
        self.assertEqual(done.stdout.strip(), "cwd=/")


class StageWiring(unittest.TestCase):
    def test_check_gate_scope_runs_every_scope_module(self):
        text = VERIFY.read_text()
        body = text.split("  check-gate-scope)", 1)[1].split(";;", 1)[0]
        for module in (
            "scripts/tests/test_gate_scope.py",
            "scripts/tests/test_verify_scope.py",
            "scripts/tests/test_verify_refusal.py",
            "scripts/tests/test_smoke_mktemp.py",
        ):
            self.assertIn(module, body)


if __name__ == "__main__":
    unittest.main()
