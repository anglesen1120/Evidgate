"""Diff-plumbing fixtures: transport, path decoding, added-line accounting, gate files.

The line rules have their own file; these fixtures prove the scan sees every
added line exactly once, under the name and the number it has in the head tree.
"""
import os
import time
import unittest

from review_blockers import gitio, ratchet, scan
from review_blockers_fixtures import RepoTest, blocks, git, reported


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

    def test_a_repository_gitattributes_cannot_hide_a_hunk(self):
        base = self.repo.head()
        self.repo.write(".gitattributes", "* -diff\n*.sh binary\n")
        self.repo.commit("attributes")
        self.repo.write("scripts/a.sh", "echo one\necho --ignore-exit-code two\n")
        self.assertEqual(reported(scan.scan(base, self.repo.commit()), "U8"), [("scripts/a.sh", 2)])

    def test_a_carriage_return_inside_a_line_cannot_hide_a_rule(self):
        self.assertEqual(
            reported(self.scan_change(("scripts/a.sh", "echo harmless\r dotnet test --ignore-exit-code 8\n")), "U8"),
            [("scripts/a.sh", 1)],
        )

    def test_a_carriage_return_after_a_comment_marker_cannot_hide_a_rule(self):
        self.assertEqual(
            reported(self.scan_change(("scripts/a.sh", "# harmless\rdotnet test --ignore-exit-code 8\n")), "U8"),
            [("scripts/a.sh", 1)],
        )

    def test_a_unicode_line_separator_is_not_a_line_break(self):
        text = 'const a = "x\u2028y";\ndotnet test --ignore-exit-code 8\n'
        self.assertEqual(reported(self.scan_change(("scripts/a.sh", text)), "U8"), [("scripts/a.sh", 2)])

    def test_paths_that_git_quotes_are_still_named_exactly(self):
        for path in ("scripts/with space.sh", "scripts/ünïcode\"name\".sh"):
            with self.subTest(path=path):
                self.assertEqual(reported(self.scan_change((path, "echo --ignore-exit-code 8\n")), "U8"), [(path, 1)])

    def test_an_external_diff_from_the_user_config_is_ignored(self):
        config = self.repo.path.parent / f"{self.repo.path.name}-global.gitconfig"
        config.write_text("[diff]\n\texternal = /bin/false\n", encoding="utf-8")
        self.addCleanup(config.unlink)
        self.addCleanup(os.environ.__setitem__, "GIT_CONFIG_GLOBAL", os.environ["GIT_CONFIG_GLOBAL"])
        os.environ["GIT_CONFIG_GLOBAL"] = str(config)
        self.assertEqual(blocks(self.scan_change(("scripts/a.sh", "echo --ignore-exit-code 8\n"))), {"U8"})

    def test_a_comment_id_on_a_u1_path_warns_in_the_diff_scan(self):
        found = self.scan_change(("src/Thing.cs", "// 01.4.2-T1.3\nclass Thing { }\n"))
        self.assertEqual([(finding.level, finding.rule) for finding in found], [("WARN", "U1")])

    def test_a_u1_path_holding_a_colon_still_counts(self):
        self.repo.write("src/with:colon.cs", "// 03.1.1-T2.2\nclass C { }\n")
        self.repo.commit("colon path")
        # One comment line, from a path whose name the plain `path:line:text`
        # format would have split apart.
        self.assertEqual(ratchet.u1_comment_lines("HEAD"), 1)

    def test_a_very_long_added_line_is_scanned_quickly(self):
        filler = "x" * 200_000
        started = time.monotonic()
        found = self.scan_change(("scripts/a.sh", f"echo {filler} --ignore-exit-code 8\n"))
        self.assertLess(time.monotonic() - started, 1.0)
        # Padding the line pushes nothing out of reach: the flag still blocks.
        self.assertEqual(blocks(found), {"U8"})


class GateFiles(RepoTest):
    def gate_warnings(self, found):
        return [finding.path for finding in found if finding.rule == "gate-file"]

    def test_the_gate_patterns_warn(self):
        found = self.scan_change(
            ("scripts/check-something.py", "print(1)\n"),
            ("scripts/verify.sh", "echo ok\n"),
            ("scripts/review_blockers/rules.py", "x = 1\n"),
            (".github/workflows/build.yml", "name: build\n"),
            ("scripts/tests/test_check_review_blockers.py", "x = 1\n"),
            ("scripts/tests/review_blockers_fixtures.py", "x = 1\n"),
        )
        self.assertEqual(
            sorted(self.gate_warnings(found)),
            sorted(
                [
                    "scripts/check-something.py",
                    "scripts/verify.sh",
                    "scripts/review_blockers/rules.py",
                    ".github/workflows/build.yml",
                    "scripts/tests/test_check_review_blockers.py",
                    "scripts/tests/review_blockers_fixtures.py",
                ]
            ),
        )

    def test_a_gate_file_moved_away_still_warns(self):
        # Without rename detection the deleted old path is listed, so a gate file
        # cannot leave the checked set by moving.
        self.repo.write("scripts/verify.sh", "echo ok\n")
        self.repo.commit("gate file")
        base = self.repo.head()
        git(self.repo.path, "mv", "scripts/verify.sh", "scripts/verify-two.sh")
        found = scan.scan(base, self.repo.commit())
        self.assertEqual(self.gate_warnings(found), ["scripts/verify.sh"])

    def test_a_file_moved_into_a_gate_name_warns(self):
        self.repo.write("scripts/helper.sh", "echo ok\n")
        self.repo.commit("helper")
        base = self.repo.head()
        git(self.repo.path, "mv", "scripts/helper.sh", "scripts/check-helper.py")
        found = scan.scan(base, self.repo.commit())
        self.assertEqual(self.gate_warnings(found), ["scripts/check-helper.py"])

    def test_a_file_moved_into_a_scanned_path_is_line_scanned(self):
        self.repo.write("docs/x.md", "run --ignore-exit-code 8\n")
        self.repo.write("scripts/keep.sh", "echo ok\n")
        self.repo.commit("doc")
        base = self.repo.head()
        git(self.repo.path, "mv", "docs/x.md", "scripts/x.sh")
        self.assertEqual(blocks(scan.scan(base, self.repo.commit())), {"U8"})

    def test_a_deleted_gate_file_warns_and_its_migration_is_not_read(self):
        self.repo.write("scripts/verify.sh", "echo ok\n")
        self.repo.write("migrations/0100_gone.sql", "create policy gone on public.widget using (true);\n")
        self.repo.commit("debt")
        base = self.repo.head()
        self.repo.remove("scripts/verify.sh")
        self.repo.remove("migrations/0100_gone.sql")
        found = scan.scan(base, self.repo.commit())
        self.assertEqual(self.gate_warnings(found), ["scripts/verify.sh"])
        self.assertEqual(blocks(found), set())


class SelfExemption(RepoTest):
    """The gate, its package, its fixtures and its baseline spell the patterns out by design,
    so the line rules skip them — while every edit to them stays a gate-file warning."""

    def test_the_gate_sources_are_not_judged_by_their_line_rules(self):
        found = self.scan_change(("scripts/check-review-blockers.py", 'U8_FLAG_BYTES = b"--ignore-exit-code"\n'))
        self.assertEqual([(finding.level, finding.rule) for finding in found], [("WARN", "gate-file")])
        found = self.scan_change(("scripts/review_blockers/rules.py", 'U8_FLAG = re.compile(r"--ignore-exit-code\\b")\n'))
        self.assertEqual([(finding.level, finding.rule) for finding in found], [("WARN", "gate-file")])

    def test_the_fixtures_and_the_baseline_are_gate_files_too(self):
        # The fixtures spell the patterns out, and editing them is a gate-file warning in its own right.
        found = self.scan_change(("scripts/tests/test_check_review_blockers.py", 'x = "--ignore-exit-code"\n'))
        self.assertEqual([(finding.level, finding.rule) for finding in found], [("WARN", "gate-file")])
        found = self.scan_change(("scripts/check-review-blockers.baseline.json", '{"u1_comment_lines": 1548}\n'))
        self.assertEqual([(finding.level, finding.rule) for finding in found], [("WARN", "gate-file")])


if __name__ == "__main__":
    unittest.main()
