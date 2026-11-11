"""The refusals in scripts/verify.sh and the half it sources: a disarmed gate, and a trait stage that tests nothing.

U8 (docs/engineering/agent-code-rules.md): the dotnet flag that reports success
for a run that executed nothing never reaches a stage. The script refuses it in
its own source — both files that make up the gate, `verify.sh` and the
`verify-guards.sh` it sources — in its arguments and in the environment, and the
trait stages fail when their trait selects no test in a named assembly. Each case
runs the real script, or a copy of it with one line changed, so a refusal that
stops working fails here.
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
INVOCATION = '        dotnet test --no-build -c Release --project "$test_project"'

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
    """A script that hands the flag on never starts a stage.

    Every run gets a `dotnet` stub first on PATH: a refusal that stops working
    must fail these cases quickly instead of starting a real build, and the stub
    proves nothing was executed before the scan said no.
    """

    def setUp(self):
        self.tree = Path(tempfile.mkdtemp(prefix="evgt-verify-flag-"))
        self.addCleanup(shutil.rmtree, self.tree, True)
        (self.tree / "scripts").mkdir()
        shutil.copy(VERIFY.with_name("verify-guards.sh"), self.tree / "scripts")
        shutil.copy(VERIFY.with_name("verify-parallel-tests.sh"), self.tree / "scripts")
        self.script = self.tree / "scripts" / "verify.sh"
        self.bin = self.tree / "bin"
        self.bin.mkdir()
        self.ran = self.tree / "dotnet-ran"
        stub = self.bin / "dotnet"
        stub.write_text('#!/usr/bin/env bash\nprintf "%s\\n" "$*" >>"$EVGT_DOTNET_MARKER"\nexit 1\n')
        stub.chmod(0o755)

    def write_copy(self, text):
        self.script.write_text(text)

    def invoke(self, script=VERIFY, *args, **env):
        environment = dict(
            os.environ,
            PATH=f"{self.bin}:{os.environ['PATH']}",
            EVGT_DOTNET_MARKER=str(self.ran),
            **env,
        )
        return run_verify(script, *args, env=environment)

    def assert_refused(self, done):
        self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
        self.assertIn("refusing to run", done.stderr)
        self.assertNotIn("fixtures-hostile\n", done.stdout)
        if self.ran.exists():
            self.fail(f"dotnet ran before the scan refused: {self.ran.read_text()}")

    def test_the_real_tree_passes_the_scan(self):
        done = self.invoke(VERIFY, "--list-stages")
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(done.stdout.splitlines(), all_stages())

    def test_a_stage_that_hands_on_the_flag_fails_the_script(self):
        text = VERIFY.read_text()
        self.assertIn(INVOCATION, text)
        self.write_copy(text.replace(INVOCATION, INVOCATION + f" {FLAG} 8"))
        done = self.invoke(self.script, "--list-stages")
        self.assert_refused(done)
        self.assertIn(FLAG, done.stderr)

    def test_a_flag_on_a_continued_line_is_the_same_command(self):
        text = VERIFY.read_text()
        self.assertIn(INVOCATION, text)
        self.write_copy(text.replace(INVOCATION, INVOCATION + f" \\\n      {FLAG} 8"))
        self.assert_refused(self.invoke(self.script, "--list-stages"))

    def test_a_flag_the_environment_carries_is_refused(self):
        done = self.invoke(VERIFY, "--list-stages", EVGT_LEAKED=FLAG)
        self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
        self.assertIn("from the environment", done.stderr)
        self.assertIn("EVGT_LEAKED", done.stderr)

    def test_a_flag_on_the_command_line_is_refused(self):
        done = self.invoke(VERIFY, "--stage", "build", FLAG, "8")
        self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
        self.assertIn("in the arguments", done.stderr)

    def test_grep_pattern_does_not_exempt_a_chained_test_command(self):
        for separator in ("&&", ";"):
            with self.subTest(separator=separator):
                self.write_copy(VERIFY.read_text() + (
                    f"\ngrep -q x /dev/null {separator} dotnet test {FLAG} 8\n"
                ))
                self.assert_refused(self.invoke(self.script, "--list-stages"))

    def test_a_comparison_does_not_exempt_a_command_substitution(self):
        self.write_copy(VERIFY.read_text() + (
            f'\nif [[ -n "$(dotnet test {FLAG} 8)" ]]; then :; fi\n'
        ))
        self.assert_refused(self.invoke(self.script, "--list-stages"))

    def test_safe_occurrences_do_not_hide_a_second_occurrence(self):
        lines = (
            f"grep -q -- '{FLAG}' /dev/null && dotnet test {FLAG} 8",
            f"rg '{FLAG}' /dev/null; dotnet test {FLAG} 8",
            f'[[ "$value" == *{FLAG}* ]] && dotnet test {FLAG} 8',
            f'[[ "$(dotnet test {FLAG} 8)" == *{FLAG}* ]]',
        )
        for line in lines:
            with self.subTest(line=line):
                self.write_copy(VERIFY.read_text() + "\n" + line + "\n")
                self.assert_refused(self.invoke(self.script, "--list-stages"))

    def test_a_search_operand_that_is_not_the_pattern_is_refused(self):
        # Only the pattern operand is a place where a search string may sit: a
        # flag elsewhere in the same command is an argument of the search that
        # hands it on, or a file it would never read as a pattern.
        lines = (
            f"grep -q x /dev/null {FLAG}",
            f"grep -q x {FLAG} /dev/null",
            f"rg x /dev/null {FLAG}",
            f"grep -q x /dev/null -- {FLAG}",
        )
        for line in lines:
            with self.subTest(line=line):
                self.write_copy(VERIFY.read_text() + "\n" + line + "\n")
                self.assert_refused(self.invoke(self.script, "--list-stages"))

    def test_no_substitution_form_is_exempt(self):
        # `$( … )`, `<( … )` and backticks run a command of their own; a search
        # around them never covers that command's arguments, and a double-quoted
        # backtick substitution still runs.
        lines = (
            f"grep -q x <(dotnet test {FLAG} 8)",
            f"grep -q x >(dotnet test {FLAG} 8)",
            f"grep -q x `dotnet test {FLAG} 8`",
            f'grep -q "$(dotnet test {FLAG} 8)"',
            f'grep -e "`dotnet test {FLAG} 8`" /dev/null',
            f'[[ "$value" == *"`dotnet test {FLAG} 8`"* ]]',
        )
        for line in lines:
            with self.subTest(line=line):
                self.write_copy(VERIFY.read_text() + "\n" + line + "\n")
                self.assert_refused(self.invoke(self.script, "--list-stages"))

    def test_a_search_string_that_looks_like_an_option_is_refused(self):
        # The shell strips the quotes, so the search reads this word as an
        # option; the scan refuses rather than guessing at the intent, and a
        # literal stays reachable through `-e` or after `--`.
        lines = (
            f"grep -q '{FLAG}' /dev/null",
            f"rg '{FLAG}' /dev/null",
        )
        for line in lines:
            with self.subTest(line=line):
                self.write_copy(VERIFY.read_text() + "\n" + line + "\n")
                self.assert_refused(self.invoke(self.script, "--list-stages"))

    def test_only_the_grep_pattern_and_comparison_pattern_are_exempt(self):
        # The exempt lines are defined, never executed: `--list-stages` exits
        # before the end of the file, and this asserts the scan, not the run.
        lines = (
            f"grep -q -- '{FLAG}' /dev/null",
            f"grep -q -e '{FLAG}' /dev/null",
            f"grep -m 1 -e '{FLAG}' /dev/null",
            f"grep -A 2 -- '{FLAG}' /dev/null",
            f"rg -e '{FLAG}' /dev/null",
            f"rg --regexp='{FLAG}' /dev/null",
            f'[[ "$value" == *{FLAG}* ]]',
        )
        for line in lines:
            with self.subTest(line=line):
                self.write_copy(VERIFY.read_text() + "\n" + line + "\n")
                done = self.invoke(self.script, "--list-stages")
                self.assertEqual(done.returncode, 0, done.stderr)

    def test_a_flag_in_the_sourced_helper_is_refused(self):
        self.write_copy(VERIFY.read_text())
        helper = self.tree / "scripts" / "verify-guards.sh"
        # A definition, not a call: the helper is sourced before the scan runs,
        # so a top-level line here would execute before anything could refuse it.
        helper.write_text(
            helper.read_text() + f"\nunused_trait_stage() {{\n  dotnet test {FLAG} 8\n}}\n"
        )
        self.assert_refused(self.invoke(self.script, "--list-stages"))

    def test_multiline_environment_values_are_scanned_as_values(self):
        for name in ("EVGT_LEAKED", "TESTINGPLATFORM_TESTOPTIONS"):
            with self.subTest(name=name):
                done = self.invoke(VERIFY, "--list-stages", **{name: f"first line\n{FLAG} 8\nlast line"})
                self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
                self.assertIn("from the environment", done.stderr)
                self.assertIn(name, done.stderr)

    def test_testingplatform_exitcode_override_is_refused_even_when_empty(self):
        for value in ("8", ""):
            with self.subTest(value=value):
                done = self.invoke(VERIFY, "--list-stages", TESTINGPLATFORM_EXITCODE_IGNORE=value)
                self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
                self.assertIn("TESTINGPLATFORM_EXITCODE_IGNORE", done.stderr)

    def test_other_testingplatform_settings_remain_allowed(self):
        done = self.invoke(VERIFY, "--list-stages", TESTINGPLATFORM_TELEMETRY_OPTOUT="1")
        self.assertEqual(done.returncode, 0, done.stderr)


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
        (self.assembly / "Tenancy.IntegrationTests.csproj").write_text("<Project />\n")

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
        (other / "Other.Tests.csproj").write_text("<Project />\n")
        (other / "MovedHostileTests.cs").write_text(
            '[Trait("Category", "Fuzz")]\n'
        )
        done = self.run_trait("Fuzz")
        self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
        self.assertIn("carries the Fuzz trait", done.stderr)
        self.assertIn("tests/Other.Tests", done.stderr)
        self.assertNotIn("DOTNET ", done.stdout)

    def test_nested_trait_file_belongs_to_its_owning_project(self):
        nested = self.assembly / "Parsing" / "Fixtures"
        nested.mkdir(parents=True)
        (nested / "HostileTests.cs").write_text('[Trait("Category", "Hostile")]\n')
        done = self.run_trait()
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertIn("--project tests/Tenancy.IntegrationTests", done.stdout)

    def test_reformatted_or_nonliteral_moved_traits_are_refused(self):
        other = self.tree / "tests" / "Other.Tests"
        other.mkdir()
        (other / "Other.Tests.csproj").write_text("<Project />\n")
        source = other / "Nested" / "MovedTests.cs"
        source.parent.mkdir()
        attributes = (
            '[Trait ( "Category" , "Hostile" )]',
            '[Trait(\n "Category",\n "Hostile"\n)]',
            '[Trait(Categories.Category, Categories.Hostile)]',
            '[Trait(nameof(Category), nameof(Hostile))]',
            '[Trait(Categories.Category, Categories.Fuzz)]',
        )
        for attribute in attributes:
            with self.subTest(attribute=attribute):
                source.write_text(attribute + "\n")
                trait = "Fuzz" if "Fuzz" in attribute else "Hostile"
                done = self.run_trait(trait)
                self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
                self.assertIn(f"carries the {trait} trait", done.stderr)
                self.assertIn("tests/Other.Tests", done.stderr)
                self.assertNotIn("DOTNET ", done.stdout)

    def test_a_trait_file_without_an_owning_project_is_refused(self):
        (self.assembly / "Tenancy.IntegrationTests.csproj").unlink()
        (self.assembly / "HostileTests.cs").write_text('[Trait("Category", "Hostile")]\n')
        done = self.run_trait()
        self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
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
