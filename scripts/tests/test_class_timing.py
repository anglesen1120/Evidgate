"""scripts/class-timing.mjs: the per-class timing summary must never fail a stage."""

import subprocess
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "class-timing.mjs"


def junit(*cases: tuple[str, str]) -> str:
    body = "".join(f'<testcase name="{c}.t{i}" time="{t}" classname="{c}"/>' for i, (c, t) in enumerate(cases))
    return f'<testsuites><testsuite name="a.dll">{body}</testsuite></testsuites>'


class ClassTimingTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self._tmp.name)
        self.addCleanup(self._tmp.cleanup)

    def run_script(self, *args: str) -> subprocess.CompletedProcess:
        return subprocess.run(["node", str(SCRIPT), *args], capture_output=True, text=True, check=False)

    def test_sums_per_class_across_reports_and_orders_slowest_first(self) -> None:
        (self.dir / "a.junit.xml").write_text(junit(("N.Slow", "1.5"), ("N.Fast", "0.1"), ("N.Slow", "2.0")))
        (self.dir / "b.junit.xml").write_text(junit(("N.Fast", "0.2"), ("M.Mid", "1.0")))
        tsv = self.dir / "out" / "class-timing.tsv"
        r = self.run_script("--out", str(tsv), str(self.dir))
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(
            tsv.read_text().splitlines(),
            ["class\ttests\tseconds", "N.Slow\t2\t3.500", "M.Mid\t1\t1.000", "N.Fast\t2\t0.300"],
        )
        self.assertLess(r.stdout.index("N.Slow"), r.stdout.index("M.Mid"))

    def test_top_limits_the_printed_table_but_not_the_tsv(self) -> None:
        (self.dir / "a.junit.xml").write_text(junit(*[(f"C{i}", str(i)) for i in range(1, 6)]))
        tsv = self.dir / "t.tsv"
        r = self.run_script("--top", "2", "--out", str(tsv), str(self.dir))
        self.assertEqual(r.returncode, 0)
        self.assertIn("C5", r.stdout)
        self.assertIn("C4", r.stdout)
        self.assertNotIn("C3", r.stdout)
        self.assertEqual(len(tsv.read_text().splitlines()), 6)

    def test_default_table_is_bounded_to_twenty_classes(self) -> None:
        (self.dir / "a.junit.xml").write_text(junit(*[(f"C{i:02}", str(i)) for i in range(1, 41)]))
        r = self.run_script(str(self.dir))
        self.assertEqual(r.returncode, 0)
        self.assertEqual(len(r.stdout.splitlines()), 22)  # headline, column header, 20 classes

    def test_missing_report_only_warns(self) -> None:
        r = self.run_script("--out", str(self.dir / "x.tsv"), str(self.dir / "absent"))
        self.assertEqual(r.returncode, 0)
        self.assertIn("not found", r.stderr)
        self.assertFalse((self.dir / "x.tsv").exists())

    def test_empty_directory_only_warns(self) -> None:
        r = self.run_script(str(self.dir))
        self.assertEqual(r.returncode, 0)
        self.assertIn("no *.junit.xml", r.stderr)

    def test_malformed_input_is_skipped_with_a_warning(self) -> None:
        (self.dir / "bad.junit.xml").write_text('<testsuites><testcase classname="A" time="oops"/><testcase time="1"/>')
        (self.dir / "junk.junit.xml").write_bytes(b"\x00\xff not xml at all")
        (self.dir / "good.junit.xml").write_text(junit(("G.Ok", "0.5")))
        r = self.run_script("--out", str(self.dir / "t.tsv"), str(self.dir))
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("2 testcase(s) without a usable", r.stderr)
        self.assertIn("no testcase found", r.stderr)
        self.assertEqual((self.dir / "t.tsv").read_text().splitlines()[1:], ["G.Ok\t1\t0.500"])

    def test_negative_time_is_unusable(self) -> None:
        (self.dir / "a.junit.xml").write_text('<testcase classname="A" time="-1"/>')
        r = self.run_script(str(self.dir))
        self.assertEqual(r.returncode, 0)
        self.assertIn("without a usable", r.stderr)

    def test_unwritable_tsv_only_warns(self) -> None:
        (self.dir / "a.junit.xml").write_text(junit(("A", "1")))
        blocker = self.dir / "file"
        blocker.write_text("x")
        r = self.run_script("--out", str(blocker / "t.tsv"), str(self.dir))
        self.assertEqual(r.returncode, 0)
        self.assertIn("cannot write", r.stderr)

    def test_bad_command_line_is_the_only_failure(self) -> None:
        for args in ([], ["--top", "0", str(self.dir)], ["--bogus", str(self.dir)], ["--out"]):
            with self.subTest(args=args):
                self.assertEqual(self.run_script(*args).returncode, 2)


if __name__ == "__main__":
    unittest.main()
