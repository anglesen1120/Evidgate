"""Fixtures for scripts/check-review-blockers.py: every rule fires on the shape it names and
stays quiet on the shape that only looks like it, a clean change set exits 0, and the U1
ratchet counts the head tree against a baseline it may not exceed."""
import importlib.util
import io
import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

SPEC = importlib.util.spec_from_file_location(
    "check_review_blockers", Path(__file__).resolve().parent.parent / "check-review-blockers.py"
)
check_review_blockers = importlib.util.module_from_spec(SPEC)
sys.modules["check_review_blockers"] = check_review_blockers
SPEC.loader.exec_module(check_review_blockers)


def git(cwd, *args):
    return subprocess.run(
        ["git", "-C", str(cwd), *args], capture_output=True, text=True, encoding="utf-8", check=True
    ).stdout.strip()


def rules(found):
    return {finding.rule for finding in found}


def blocks(found):
    return {finding.rule for finding in found if finding.level == "BLOCK"}


class Repo:
    """A throwaway repository, one commit per `commit()` call, with no dependence on user config."""

    def __init__(self):
        self.path = Path(tempfile.mkdtemp(prefix="review-blockers-"))
        git(self.path, "init", "-q", "-b", "trunk")
        git(self.path, "config", "user.email", "fixture@example.invalid")
        git(self.path, "config", "user.name", "fixture")
        git(self.path, "config", "commit.gpgsign", "false")

    def write(self, name, text):
        target = self.path / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8")

    def head(self):
        return git(self.path, "rev-parse", "HEAD")

    def commit(self, message="fixture"):
        git(self.path, "add", "-A")
        git(self.path, "commit", "-q", "-m", message)
        return self.head()

    def close(self):
        shutil.rmtree(self.path, ignore_errors=True)


class RepoTest(unittest.TestCase):
    """Points the scanner at a disposable repository for the duration of one test."""

    def setUp(self):
        self.repo = Repo()
        self.addCleanup(self.repo.close)
        self.addCleanup(setattr, check_review_blockers, "REPO", check_review_blockers.REPO)
        check_review_blockers.REPO = self.repo.path
        self.repo.write("README.md", "fixture\n")
        self.repo.commit("base")

    def scan_change(self, *pairs):
        """Write each (path, text) pair, commit once, and scan the change set from the previous tip."""
        base = self.repo.head()
        for path, text in pairs:
            self.repo.write(path, text)
        return check_review_blockers.scan(base, self.repo.commit())

    def run_main(self, argv):
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = check_review_blockers.main(argv)
        self.output = out.getvalue()
        self.errors = err.getvalue()
        return code


class PolicyRoles(RepoTest):
    MIGRATION = "migrations/0100_example.sql"

    def test_a_policy_without_a_role_clause_blocks(self):
        sql = "create policy p on public.widget using (org_id = 1);\n"
        self.assertEqual(blocks(self.scan_change((self.MIGRATION, sql))), {"D1"})

    def test_a_policy_to_public_blocks(self):
        sql = "create policy p on public.widget to public using (org_id = 1);\n"
        self.assertEqual(blocks(self.scan_change((self.MIGRATION, sql))), {"D1"})

    def test_a_quoted_public_role_blocks(self):
        for clause in ('to "public"', "to PUBLIC", 'to "PUBLIC"'):
            with self.subTest(clause=clause):
                sql = f"create policy p on public.widget {clause} using (org_id = 1);\n"
                self.assertEqual(blocks(self.scan_change((self.MIGRATION, sql))), {"D1"})

    def test_a_role_list_containing_public_blocks(self):
        sql = "create policy p on public.widget to evidgate_app, public using (org_id = 1);\n"
        self.assertEqual(blocks(self.scan_change((self.MIGRATION, sql))), {"D1"})

    def test_a_policy_that_names_its_roles_passes(self):
        sql = (
            "create policy p on public.widget for all to evidgate_app\n"
            "  using (org_id = evidgate.current_org_id())\n"
            "  with check (org_id = evidgate.current_org_id());\n"
            "create policy owner_all on public.widget for all to evidgate_owner using (true) with check (true);\n"
        )
        self.assertEqual(self.scan_change((self.MIGRATION, sql)), [])

    def test_comments_and_literals_are_not_role_clauses(self):
        sql = (
            "-- create policy commented on public.widget using (true);\n"
            "create policy ok on public.widget to evidgate_app\n"
            "  -- to public is not a clause either\n"
            "  using (note = 'a;b');\n"
        )
        self.assertEqual(self.scan_change((self.MIGRATION, sql)), [])

    def test_a_literal_holding_public_does_not_satisfy_the_rule(self):
        sql = "create policy p on public.widget using (note = 'to public');\n"
        self.assertEqual(blocks(self.scan_change((self.MIGRATION, sql))), {"D1"})

    def test_a_role_clause_inside_the_using_body_is_not_a_role(self):
        without_role = "create policy p on public.widget using (\"to\" > 0);\n"
        with_role = "create policy p on public.widget to evidgate_app using (\"to\" > 0);\n"
        self.assertEqual(blocks(self.scan_change((self.MIGRATION, without_role))), {"D1"})
        self.assertEqual(self.scan_change((self.MIGRATION, with_role)), [])

    def test_policy_debt_on_the_base_is_not_reported(self):
        self.scan_change((self.MIGRATION, "create policy old on public.widget using (true);\n"))
        self.assertEqual(rules(self.scan_change(("docs/note.md", "unrelated\n"))), set())


class IgnoreExitCode(RepoTest):
    def test_the_flag_as_an_argument_blocks(self):
        found = self.scan_change(("scripts/verify.sh", 'dotnet test --no-build --ignore-exit-code 8\n'))
        self.assertEqual(blocks(found), {"U8"})

    def test_a_quoted_argument_blocks_too(self):
        found = self.scan_change(("scripts/verify.sh", 'dotnet test "--ignore-exit-code"\n'))
        self.assertEqual(blocks(found), {"U8"})

    def test_a_line_that_tests_for_the_flag_passes(self):
        for line in (
            'grep -q -- "--ignore-exit-code" scripts/verify.sh\n',
            'case "$flag" in --ignore-exit-code) fail=1 ;;\n',
            'if [[ "$args" == *--ignore-exit-code* ]]; then fail=1; fi\n',
            'self.assertTrue(log.contains("--ignore-exit-code"))\n',
            'expect(helpText.includes("--ignore-exit-code")).toBe(false);\n',
        ):
            with self.subTest(line=line):
                # scripts/verify.sh is a gate file, so a WARN gate-file is expected; the rule under test must not fire.
                self.assertEqual(blocks(self.scan_change(("scripts/verify.sh", line))), set())

    def test_docs_and_shell_comments_are_out_of_scope(self):
        self.assertEqual(rules(self.scan_change(("docs/engineering/gates.md", "never pass `--ignore-exit-code`\n"))), set())
        self.assertEqual(blocks(self.scan_change(("scripts/verify.sh", "# never pass --ignore-exit-code\n"))), set())


class TestSkips(RepoTest):
    def test_attribute_and_runtime_skips_block(self):
        for path, line in (
            ("tests/ExampleTests.cs", '[Fact(Skip = "flaky")]\n'),
            ("tests/DockerTests.cs", '[DockerFact(Skip = "no daemon")]\n'),
            ("tests/EnvTests.cs", '[Fact(SkipUnless = "EVIDGATE_DB")]\n'),
            ("apps/web/src/app/x.test.ts", 'it.skip("later", () => {});\n'),
            ("apps/web/src/app/x.test.ts", 'xit("later", () => {});\n'),
            ("apps/web/src/app/x.spec.ts", 'test.fixme("later", () => {});\n'),
        ):
            with self.subTest(line=line):
                self.assertEqual(blocks(self.scan_change((path, line))), {"skip"})

    def test_runtime_skip_helpers_block(self):
        for line in ('Skip.If(true, "not here");\n', 'Assert.Skip("no docker");\n'):
            with self.subTest(line=line):
                self.assertEqual(blocks(self.scan_change(("tests/ExampleTests.cs", line))), {"skip"})

    def test_paging_skip_is_not_a_test_skip(self):
        self.assertEqual(self.scan_change(("tests/PagingTests.cs", "options.Skip = 5;\n")), [])

    def test_a_skip_outside_a_test_file_is_not_scanned(self):
        self.assertEqual(self.scan_change(("src/Thing.cs", '[Fact(Skip = "x")]\n')), [])


class TypeCasts(RepoTest):
    def test_a_double_cast_blocks(self):
        found = self.scan_change(("apps/web/src/app/x.ts", "const v = f() as unknown as Signal<number>;\n"))
        self.assertEqual(blocks(found), {"ts-cast"})

    def test_a_single_cast_passes(self):
        self.assertEqual(self.scan_change(("apps/web/src/app/x.ts", "const v = f() as Signal<number>;\n")), [])


class Scaffolding(RepoTest):
    PATH = "src/Evidgate.Api/Composition.cs"

    def test_a_registration_on_one_line_warns(self):
        found = self.scan_change((self.PATH, "services.AddSingleton<IHealingLlmProvider, MockHealingLlmProvider>();\n"))
        self.assertEqual([(finding.level, finding.rule) for finding in found], [("WARN", "U4")])

    def test_a_registration_across_two_lines_warns_once(self):
        text = "services.AddSingleton<IHealingLlmProvider>(\n    new MockHealingLlmProvider());\n"
        found = self.scan_change((self.PATH, text))
        self.assertEqual([finding.rule for finding in found], ["U4"])
        self.assertEqual(found[0].line, 2)

    def test_a_real_registration_passes(self):
        self.assertEqual(self.scan_change((self.PATH, "services.AddSingleton<IClock, SystemClock>();\n")), [])


class DiffParsing(RepoTest):
    def test_an_added_line_starting_with_two_pluses_is_content(self):
        text = "const a = 1;\n++ as unknown as Foo;\n"
        found = self.scan_change(("apps/web/src/app/x.ts", text))
        self.assertEqual([(finding.rule, finding.path, finding.line) for finding in found], [("ts-cast", "apps/web/src/app/x.ts", 2)])

    def test_line_numbers_follow_each_head_file(self):
        found = self.scan_change(
            ("scripts/a.sh", "echo one\necho --ignore-exit-code two\n"),
            ("scripts/b.sh", "echo b\n"),
        )
        self.assertEqual([(finding.rule, finding.path, finding.line) for finding in found], [("U8", "scripts/a.sh", 2)])


class SelfExemption(RepoTest):
    """The gate, its fixtures and its baseline spell the patterns out by design, so the line
    rules skip them — while every edit to them stays a gate-file warning."""

    def test_the_gate_sources_are_not_judged_by_their_line_rules(self):
        found = self.scan_change(("scripts/check-review-blockers.py", 'U8_FLAG = re.compile(r"--ignore-exit-code\\b")\n'))
        self.assertEqual([(finding.level, finding.rule) for finding in found], [("WARN", "gate-file")])
        self.assertEqual(self.scan_change(("scripts/tests/test_check_review_blockers.py", 'x = "--ignore-exit-code"\n')), [])

    def test_a_baseline_edit_is_a_gate_file_warning(self):
        found = self.scan_change(("scripts/check-review-blockers.baseline.json", '{"u1_comment_lines": 1548}\n'))
        self.assertEqual([(finding.level, finding.rule) for finding in found], [("WARN", "gate-file")])


class ExitCodes(RepoTest):
    def test_an_unresolvable_base_exits_2(self):
        self.assertEqual(self.run_main(["nosuchref", "HEAD"]), 2)
        self.assertIn("does not resolve", self.errors)

    def test_no_base_ref_at_all_exits_2(self):
        # The fixture repo has no remote and no develop/main branch: no change set can be read.
        self.assertEqual(self.run_main(["", "HEAD"]), 2)
        self.assertIn("fetch-depth: 0", self.errors)

    def test_a_local_develop_is_the_fallback_base(self):
        git(self.repo.path, "branch", "develop")
        base = self.repo.head()
        self.repo.write("docs/note.md", "text\n")
        self.repo.commit()
        self.assertEqual(self.run_main(["0" * 40, "HEAD"]), 0)
        self.assertIn("(develop...HEAD)", self.output)
        self.assertNotEqual(base, self.repo.head())

    def test_an_explicit_ref_wins_over_the_fallback(self):
        git(self.repo.path, "branch", "develop")
        base = self.repo.head()
        self.assertNotEqual(check_review_blockers.effective_base(base), "develop")
        self.assertEqual(check_review_blockers.effective_base(""), "develop")
        self.assertEqual(check_review_blockers.effective_base("0" * 40), "develop")

    def test_a_clean_change_set_exits_0(self):
        base = self.repo.head()
        self.repo.write("docs/note.md", "text\n")
        self.repo.commit()
        self.assertEqual(self.run_main([base, "HEAD"]), 0)

    def test_a_blocking_change_set_exits_1(self):
        base = self.repo.head()
        self.repo.write("scripts/verify.sh", "echo --ignore-exit-code\n")
        self.repo.commit()
        self.assertEqual(self.run_main([base, "HEAD"]), 1)


class Ratchet(RepoTest):
    def setUp(self):
        super().setUp()
        self.repo.write("src/A.cs", "// first 01.4.2-T1.3\nclass A { }\n// second D-S68-Q1\n")
        self.repo.commit("u1 lines")

    def baseline(self, count):
        path = self.repo.path / "baseline.json"
        path.write_text(json.dumps({"u1_comment_lines": count}), encoding="utf-8")
        return path

    def test_the_count_reads_comments_on_the_u1_paths_only(self):
        self.repo.write("src/B.cs", 'var id = "01.4.2-T1.3";\n')
        self.repo.write("docs/note.md", "// 01.4.2-T1.3\n")
        self.repo.commit("not comments on a u1 path")
        self.assertEqual(check_review_blockers.u1_comment_lines("HEAD"), 2)

    def test_equal_to_the_baseline_passes(self):
        with redirect_stdout(io.StringIO()):
            self.assertEqual(check_review_blockers.ratchet("HEAD", self.baseline(2)), 0)

    def test_a_new_comment_id_fails(self):
        self.repo.write("src/A.cs", "// first 01.4.2-T1.3\n// added CR-07\nclass A { }\n// second D-S68-Q1\n")
        self.repo.commit("one more id")
        with redirect_stdout(io.StringIO()):
            self.assertEqual(check_review_blockers.ratchet("HEAD", self.baseline(2)), 1)

    def test_below_the_baseline_passes_and_asks_for_the_lower_number(self):
        out = io.StringIO()
        with redirect_stdout(out):
            self.assertEqual(check_review_blockers.ratchet("HEAD", self.baseline(3)), 0)
        self.assertIn("lower the baseline", out.getvalue())

    def test_a_baseline_that_cannot_be_read_exits_2(self):
        broken = self.repo.path / "broken.json"
        broken.write_text("{", encoding="utf-8")
        with self.assertRaises(check_review_blockers.ScanError):
            check_review_blockers.ratchet("HEAD", broken)
        self.assertEqual(self.run_main(["--u1-ratchet", "HEAD"]), 2)

    def test_the_baseline_option_reads_the_file_it_names(self):
        self.repo.write("src/A.cs", "// first 01.4.2-T1.3\n// added CR-07\nclass A { }\n// second D-S68-Q1\n")
        self.repo.commit("one more id")
        # CI points the ratchet at the base revision's number: the change set's own copy is ignored.
        self.assertEqual(self.run_main(["--baseline", str(self.baseline(2)), "--u1-ratchet", "HEAD"]), 1)
        self.assertIn("BLOCK U1 ratchet 3 > 2", self.output)
        self.assertEqual(self.run_main(["--u1-ratchet", "--baseline", str(self.baseline(3))]), 0)
        self.assertIn("U1 ratchet 3 <= baseline 3", self.output)

    def test_the_baseline_option_needs_a_path(self):
        self.assertEqual(self.run_main(["--u1-ratchet", "--baseline"]), 2)
        self.assertIn("--baseline needs a file path", self.errors)


class RealRepository(unittest.TestCase):
    def test_this_tree_is_at_or_below_the_committed_baseline(self):
        previous = check_review_blockers.REPO
        self.addCleanup(setattr, check_review_blockers, "REPO", previous)
        check_review_blockers.REPO = Path(__file__).resolve().parent.parent.parent
        with redirect_stdout(io.StringIO()):
            self.assertEqual(check_review_blockers.ratchet("HEAD", check_review_blockers.baseline_path()), 0)


if __name__ == "__main__":
    unittest.main()
