"""U1 ratchet and process fixtures: the count, the baseline, and the entry's exit codes.

The count reads comment lines only, on the three U1 trees, from the repository
root; the ratchet BLOCKs above the committed ceiling and only advises below it.
"""
import io
import json
import os
import unittest
from contextlib import redirect_stdout

from review_blockers import gitio, ratchet
from review_blockers_fixtures import Repo, RepoTest, git


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
        head = self.repo.commit()
        self.assertEqual(self.run_main(["0" * 40, "HEAD"]), 0)
        self.assertIn(f"(develop {gitio.short(base)}...HEAD {gitio.short(head)})", self.output)

    def test_an_explicit_ref_wins_over_the_fallback(self):
        git(self.repo.path, "branch", "develop")
        base = self.repo.head()
        self.assertEqual(gitio.effective_base(base), base)
        self.assertEqual(gitio.effective_base(""), "develop")
        self.assertEqual(gitio.effective_base("0" * 40), "develop")

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

    def test_the_summary_names_the_resolved_commits_and_the_line_count(self):
        base = self.repo.head()
        self.repo.write("docs/note.md", "text\n")
        self.repo.write("docs/more.md", "more\n")
        head = self.repo.commit()
        self.assertEqual(self.run_main([base, "HEAD"]), 0)
        self.assertIn(f"{gitio.short(base)}...HEAD {gitio.short(head)}", self.output)
        self.assertIn("2 added line(s)", self.output)

    def test_a_missing_git_binary_exits_2(self):
        path = os.environ.get("PATH", "")
        self.addCleanup(os.environ.__setitem__, "PATH", path)
        os.environ["PATH"] = os.devnull.rsplit("/", 1)[0] + "/no-such-bin"
        self.assertEqual(self.run_main(["HEAD~1", "HEAD"]), 2)
        self.assertIn("cannot run git", self.errors)

    def test_an_unexpected_crash_exits_2(self):
        from review_blockers import scan

        self.addCleanup(setattr, scan, "scan_change_set", scan.scan_change_set)
        scan.scan_change_set = lambda base, head: 1 / 0
        self.assertEqual(self.run_main(["HEAD", "HEAD"]), 2)
        self.assertIn("scan crashed", self.errors)


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
        self.assertEqual(ratchet.u1_comment_lines("HEAD"), 2)

    def test_equal_to_the_baseline_passes(self):
        with redirect_stdout(io.StringIO()):
            self.assertEqual(ratchet.ratchet("HEAD", self.baseline(2)), 0)

    def test_a_new_comment_id_fails(self):
        self.repo.write("src/A.cs", "// first 01.4.2-T1.3\n// added CR-07\nclass A { }\n// second D-S68-Q1\n")
        self.repo.commit("one more id")
        with redirect_stdout(io.StringIO()):
            self.assertEqual(ratchet.ratchet("HEAD", self.baseline(2)), 1)

    def test_below_the_baseline_passes_and_asks_for_the_lower_number(self):
        out = io.StringIO()
        with redirect_stdout(out):
            self.assertEqual(ratchet.ratchet("HEAD", self.baseline(3)), 0)
        self.assertIn("lower the baseline", out.getvalue())

    def test_a_baseline_that_cannot_be_read_exits_2(self):
        broken = self.repo.path / "broken.json"
        broken.write_text("{", encoding="utf-8")
        with self.assertRaises(gitio.ScanError):
            ratchet.ratchet("HEAD", broken)
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

    def test_a_comment_marker_inside_a_string_literal_is_not_a_comment(self):
        self.repo.write("src/C.cs", 'var url = "http://x // 01.4.2-T1.3";\nvar id = "CR-99";\n')
        self.repo.commit("strings")
        self.assertEqual(ratchet.u1_comment_lines("HEAD"), 2)

    def test_a_package_source_file_counts(self):
        self.repo.write("packages/design-system/src/tokens.ts", "// 05.2.1-T3.4\n")
        self.repo.commit("package comment")
        self.assertEqual(ratchet.u1_comment_lines("HEAD"), 3)

    def test_an_empty_u1_scope_is_a_scan_error(self):
        empty = Repo()
        self.addCleanup(empty.close)
        empty.write("docs/note.md", "// 01.4.2-T1.3\n")
        empty.commit("docs only")
        self.addCleanup(setattr, gitio, "REPO", gitio.REPO)
        gitio.REPO = empty.path
        with self.assertRaises(gitio.ScanError):
            ratchet.u1_comment_lines("HEAD")
        self.assertEqual(self.run_main(["--u1-count", "HEAD"]), 2)
        self.assertIn("no tracked files match the U1 paths", self.errors)

    def test_the_count_works_from_a_subdirectory(self):
        subdirectory = self.repo.path / "src"
        cwd = os.getcwd()
        self.addCleanup(os.chdir, cwd)
        os.chdir(subdirectory)
        gitio.REPO = os.getcwd()
        self.assertEqual(self.run_main(["--u1-count", "HEAD"]), 0)
        self.assertEqual(self.output.strip(), "2")

    def test_the_count_option_runs_from_main(self):
        self.assertEqual(self.run_main(["--u1-count", "HEAD"]), 0)
        self.assertEqual(self.output.strip(), "2")


if __name__ == "__main__":
    unittest.main()
