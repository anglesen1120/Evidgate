"""The refusals in scripts/verify.sh: a disarmed gate, and a trait stage that tests nothing.

U8 (docs/engineering/agent-code-rules.md): the dotnet flag that reports success
for a run that executed nothing never reaches a stage. The script refuses it in
its own source, in its arguments and in the environment, and both trait stages
fail when their trait selects no test. Each case runs the real script, or a copy
of it with one line changed, so a refusal that stops working fails here.
"""
import os
import shutil
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path

from review_blockers.rules import U8_FLAG
from test_verify_scope import all_stages

ROOT = Path(__file__).resolve().parent.parent.parent
VERIFY = ROOT / "scripts" / "verify.sh"

# The flag is spelled once in the repository, by the U8 detector this suite
# exercises; spelling it again here would be the very mention that rule blocks.
FLAG = U8_FLAG.pattern.replace("\\b", "")

# The real `test` stage invocation, which a fixture puts the flag back onto.
INVOCATION = "    dotnet test --no-build -c Release --solution Evidgate.slnx\n"

# $1 the script to source, $2 the tree to stand in, $3 the trait to run.
TRAIT_DRIVER = textwrap.dedent("""\
    source "$1"
    cd "$2"
    dotnet() { printf 'DOTNET %s\\n' "$*"; }
    run_trait_stage "$3" "${4:-tests/Tenancy.IntegrationTests}"
    """)

# The same driver with a dotnet that reports "no test ran", the MTP exit code.
TRAIT_DRIVER_EMPTY = textwrap.dedent("""\
    source "$1"
    cd "$2"
    dotnet() { printf 'DOTNET %s\\n' "$*"; return 8; }
    run_trait_stage "$3" tests/Tenancy.IntegrationTests
    """)


def bash(*args, env=None, cwd=None):
    return subprocess.run(
        ["bash", *args], capture_output=True, text=True, env=env, cwd=cwd
    )


def run_verify(script=VERIFY, *args, env=None):
    """Run a verify.sh as a script: `$0` is the path, so `main` runs."""
    return bash(str(script), *args, env=env)


class SourceFlag(unittest.TestCase):
    """A script that hands the flag on never starts a stage."""

    def setUp(self):
        self.tree = Path(tempfile.mkdtemp(prefix="evgt-verify-flag-"))
        self.addCleanup(shutil.rmtree, self.tree, True)
        (self.tree / "scripts").mkdir()
        self.script = self.tree / "scripts" / "verify.sh"

    def write_copy(self, text):
        self.script.write_text(text)

    def assert_refused(self, done):
        self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
        self.assertIn("refusing to run", done.stderr)
        self.assertNotIn("fixtures-hostile\n", done.stdout)

    def test_the_real_tree_passes_the_scan(self):
        done = run_verify(VERIFY, "--list-stages")
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(done.stdout.splitlines(), all_stages())

    def test_a_stage_that_hands_on_the_flag_fails_the_script(self):
        text = VERIFY.read_text()
        self.assertIn(INVOCATION, text)
        self.write_copy(text.replace(INVOCATION, INVOCATION.rstrip("\n") + f" {FLAG} 8\n"))
        done = run_verify(self.script, "--list-stages")
        self.assert_refused(done)
        self.assertIn(FLAG, done.stderr)

    def test_a_flag_on_a_continued_line_is_the_same_command(self):
        text = VERIFY.read_text()
        self.assertIn(INVOCATION, text)
        self.write_copy(text.replace(INVOCATION, INVOCATION.rstrip("\n") + f" \\\n      {FLAG} 8\n"))
        done = run_verify(self.script, "--list-stages")
        self.assert_refused(done)

    def test_a_flag_the_environment_carries_is_refused(self):
        env = dict(os.environ, EVGT_LEAKED=FLAG)
        done = run_verify(VERIFY, "--list-stages", env=env)
        self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
        self.assertIn("from the environment", done.stderr)
        self.assertIn("EVGT_LEAKED", done.stderr)

    def test_a_flag_on_the_command_line_is_refused(self):
        done = run_verify(VERIFY, "--stage", "build", FLAG, "8")
        self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
        self.assertIn("in the arguments", done.stderr)


class PipedRun(unittest.TestCase):
    """`bash < scripts/verify.sh` runs the gate, not nothing."""

    def test_a_piped_run_reaches_main(self):
        done = subprocess.run(
            ["bash", "-s", "--", "--list-stages"],
            input=VERIFY.read_text(),
            capture_output=True,
            text=True,
            cwd=ROOT,
        )
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertIn("fixtures-hostile", done.stdout.splitlines())

    def test_a_piped_run_outside_a_repository_is_refused(self):
        tree = Path(tempfile.mkdtemp(prefix="evgt-verify-pipe-"))
        self.addCleanup(shutil.rmtree, tree, True)
        done = subprocess.run(
            ["bash", "-s", "--", "--list-stages"],
            input=VERIFY.read_text(),
            capture_output=True,
            text=True,
            cwd=tree,
        )
        self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
        self.assertIn("cannot find scripts/verify.sh", done.stderr)


class TraitStage(unittest.TestCase):
    """A trait stage fails when its trait selects nothing, and only runs the named assemblies."""

    def setUp(self):
        self.tree = Path(tempfile.mkdtemp(prefix="evgt-verify-trait-"))
        self.addCleanup(shutil.rmtree, self.tree, True)
        self.assembly = self.tree / "tests" / "Tenancy.IntegrationTests"
        self.assembly.mkdir(parents=True)

    def run_trait(self, trait="Hostile", driver=TRAIT_DRIVER, env=None):
        return bash(
            "-c",
            driver,
            "verify-trait-driver",
            str(VERIFY),
            str(self.tree),
            trait,
            env=env,
        )

    def test_the_named_assembly_runs_with_the_trait_filter_and_a_minimum(self):
        (self.assembly / "HostileFixtureTests.cs").write_text(
            '[Trait("Category", "Hostile")]\n'
        )
        done = self.run_trait()
        self.assertEqual(done.returncode, 0, done.stderr)
        calls = [l for l in done.stdout.splitlines() if l.startswith("DOTNET ")]
        self.assertEqual(len(calls), 1)
        self.assertIn("--filter-trait Category=Hostile", calls[0])
        self.assertIn("--minimum-expected-tests 1", calls[0])
        self.assertIn("--project tests/Tenancy.IntegrationTests", calls[0])
        self.assertNotIn("--solution", calls[0])

    def test_a_trait_file_outside_the_named_assembly_fails_the_stage(self):
        other = self.tree / "tests" / "Other.Tests"
        other.mkdir()
        (other / "MovedHostileTests.cs").write_text(
            '[Trait("Category", "Fuzz")]\n'
        )
        done = self.run_trait("Fuzz")
        self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
        self.assertIn("carries the Fuzz trait", done.stderr)
        self.assertIn("tests/Other.Tests", done.stderr)
        self.assertNotIn("DOTNET ", done.stdout)

    def test_a_trait_that_selects_nothing_fails_the_stage(self):
        (self.assembly / "Unrelated.cs").write_text("// no trait here\n")
        done = self.run_trait(driver=TRAIT_DRIVER_EMPTY)
        self.assertEqual(done.returncode, 8, done.stdout + done.stderr)
        self.assertIn("DOTNET ", done.stdout)


class DockerSkippedTestStage(unittest.TestCase):
    """The skip path tolerates an empty assembly, never an empty stage.

    `EVIDGATE_VERIFY_SKIP_DOCKER=1` runs one assembly per call, so exit code 8
    ("no test ran") belongs to one assembly; the stage still fails when no
    assembly ran a test, and any other failure is that assembly's own verdict.
    """

    DRIVER = textwrap.dedent("""\
        source "$1"
        cd "$2"
        dotnet() {
          case "$*" in
          %s
          *) return 0 ;;
          esac
        }
        EVIDGATE_VERIFY_SKIP_DOCKER=1 run_stage test
        """)

    def setUp(self):
        self.tree = Path(tempfile.mkdtemp(prefix="evgt-verify-skip-"))
        self.addCleanup(shutil.rmtree, self.tree, True)
        for name in ("Unit.Tests", "Docker.Tests"):
            project = self.tree / "tests" / name
            project.mkdir(parents=True)
            (project / f"{name}.csproj").write_text("<Project />\n")

    def run_skip(self, rules):
        return bash("-c", self.DRIVER % rules, "verify-skip-driver", str(VERIFY), str(self.tree))

    def test_an_empty_assembly_is_not_a_failure(self):
        done = self.run_skip("*tests/Docker.Tests/*) return 8 ;;")
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertIn("no test runs with Docker skipped", done.stdout)

    def test_a_stage_where_no_assembly_ran_a_test_fails(self):
        done = self.run_skip("*) return 8 ;;")
        self.assertEqual(done.returncode, 1, done.stdout + done.stderr)
        self.assertIn("ran no test in any assembly", done.stderr)

    def test_an_assembly_failure_is_that_assemblys_verdict(self):
        done = self.run_skip("*tests/Unit.Tests/*) return 1 ;;")
        self.assertEqual(done.returncode, 1, done.stdout + done.stderr)
        self.assertNotIn("ran no test in any assembly", done.stderr)


if __name__ == "__main__":
    unittest.main()
