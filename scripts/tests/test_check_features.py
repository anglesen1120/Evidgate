"""scripts/check_features.py sub-task rules: numeric ids, letter suffixes (a, b, c contiguous), sizes,
and the [OPEN] + `unsized` row exception. Each case rewrites the sub-task rows of 04.1.3-T2 (an M task,
1.0 d) in a scratch copy of plans/ and scripts/ and runs the real checker on it.
"""
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
FEATURE = "plans/features/04-ingestion-adapters/04.1.3-large-report-caps-and-streaming.md"
TID = "04.1.3-T2"


def row(sub, size, note=""):
    return f"| {TID}.{sub} | Work {sub}{note} | {size} | Done when {sub} |"


class CheckFeaturesSubtasks(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = Path(tempfile.mkdtemp(prefix="check-features-"))
        shutil.copytree(ROOT / "plans", cls.tmp / "plans")
        shutil.copytree(ROOT / "scripts", cls.tmp / "scripts", ignore=shutil.ignore_patterns("tests", "__pycache__"))
        cls.original = (cls.tmp / FEATURE).read_text(encoding="utf-8")

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def check(self, *rows):
        lines, out, placed = self.original.split("\n"), [], False
        for ln in lines:
            if ln.startswith(f"| {TID}."):
                if not placed:
                    out.extend(rows)
                    placed = True
                continue
            out.append(ln)
        self.assertTrue(placed, "fixture rows of 04.1.3-T2 not found")
        (self.tmp / FEATURE).write_text("\n".join(out), encoding="utf-8")
        done = subprocess.run([sys.executable, "scripts/check_features.py", "04"], cwd=self.tmp,
                              capture_output=True, text=True, env={"PYTHONDONTWRITEBYTECODE": "1", "PATH": "/usr/bin:/bin"})
        mine = [l.strip() for l in done.stdout.splitlines() if TID in l or "04.1.3" in l]
        errors = [l for l in mine if l.startswith("E ")]
        warns = [l for l in mine if l.startswith("W ")]
        return done.returncode, errors, warns

    def test_numeric_ids_unchanged(self):
        rc, errors, warns = self.check(row("1", "S"), row("2", "S"))
        self.assertEqual((errors, warns), ([], []))
        self.assertEqual(rc, 0)

    def test_numeric_gap_is_an_error(self):
        rc, errors, _ = self.check(row("1", "S"), row("3", "S"))
        self.assertEqual(rc, 1)
        self.assertRegex(errors[0], r"sub-task ids not 1\.\.n: \[1, 3\]")

    def test_letter_suffixes_summing_right(self):
        rc, errors, warns = self.check(row("1", "XS"), row("2a", "XS"), row("2c", "XS"), row("2b", "XS"))
        self.assertEqual((rc, errors, warns), (0, [], []))

    def test_letter_suffixes_summing_wrong(self):
        rc, errors, _ = self.check(row("1", "S"), row("2a", "S"), row("2b", "XS"))
        self.assertEqual(rc, 1)
        self.assertRegex(errors[0], r"sub-tasks sum 1\.25 != task size 1 \(M\)")

    def test_gap_in_letters_is_an_error(self):
        rc, errors, _ = self.check(row("1", "S"), row("2a", "XS"), row("2c", "XS"))
        self.assertEqual(rc, 1)
        self.assertRegex(errors[0], r"T2\.2 letter suffixes not contiguous from a: \['a', 'c'\]")

    def test_letters_must_start_at_a(self):
        rc, errors, _ = self.check(row("1", "S"), row("2b", "S"))
        self.assertEqual(rc, 1)
        self.assertRegex(errors[0], r"not contiguous from a: \['b'\]")

    def test_bare_id_mixed_with_letters_is_an_error(self):
        rc, errors, _ = self.check(row("1", "S"), row("1a", "XS"), row("1b", "XS"), row("2", "XS"), row("2b", "XS"))
        self.assertEqual(rc, 1)
        self.assertTrue(any("T2.2 mixes a bare id with letter suffixes" in e for e in errors), errors)

    def test_open_unsized_row_is_a_warning_and_excluded(self):
        rc, errors, warns = self.check(row("1", "XS"), row("2a", "S"), row("2c", "unsized (not counted)", " [OPEN] indicative M"),
                                       row("2b", "XS"))
        self.assertEqual((rc, errors), (0, []))
        self.assertEqual(len(warns), 1)
        self.assertRegex(warns[0], r"04\.1\.3-T2\.2c is \[OPEN\] and unsized: left out of the sum")

    def test_unsized_without_open_is_an_error(self):
        rc, errors, _ = self.check(row("1", "S"), row("2", "unsized (not counted)"))
        self.assertEqual(rc, 1)
        self.assertRegex(errors[0], r"04\.1\.3-T2\.2 has no size cell")

    def test_open_without_unsized_size_is_an_error(self):
        rc, errors, _ = self.check(row("1", "S"), row("2", "TBD", " [OPEN]"))
        self.assertEqual(rc, 1)
        self.assertRegex(errors[0], r"04\.1\.3-T2\.2 has no size cell")

    def test_unsized_rows_do_not_hide_a_wrong_sum(self):
        rc, errors, warns = self.check(row("1", "XS"), row("2a", "unsized", " [OPEN]"))
        self.assertEqual(rc, 1)
        self.assertEqual(len(warns), 1)
        self.assertRegex(errors[0], r"sub-tasks sum 0\.25 != task size 1 \(M\)")

    def test_sub_task_larger_than_s_is_an_error(self):
        rc, errors, _ = self.check(row("1", "M"))
        self.assertEqual(rc, 1)
        self.assertRegex(errors[0], r"sub-tasks > S: \['04\.1\.3-T2\.1=M'\]")

    def test_suffixed_sub_task_larger_than_s_is_an_error(self):
        rc, errors, _ = self.check(row("1a", "M"), row("1b", "S"))
        self.assertEqual(rc, 1)
        self.assertTrue(any(re.search(r"sub-tasks > S: \['04\.1\.3-T2\.1a=M'\]", e) for e in errors), errors)


if __name__ == "__main__":
    unittest.main()
