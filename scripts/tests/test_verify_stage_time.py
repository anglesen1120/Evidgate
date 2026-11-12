"""Per-stage wall time in scripts/verify.sh: the line after each stage and the table.

The script is sourced with `run_stage` replaced by a stub, so the cases drive the
real `main` and `run_selected_stages` without building or testing anything.
"""
import os
import signal
import subprocess
import tempfile
import textwrap
import time
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
VERIFY = ROOT / "scripts" / "verify.sh"

DRIVER = textwrap.dedent("""\
    source "$1"
    shift
    run_stage() {
      case "$1" in
      isolation) sleep 2 ;;
      format) echo "boom ran"; exit 3 ;;
      *) echo "ran $1" ;;
      esac
    }
    main "$@"
    """)


def run(*stages):
    args = [a for s in stages for a in ("--stage", s)]
    return subprocess.run(
        ["bash", "-c", DRIVER, "driver", str(VERIFY), *args],
        capture_output=True,
        text=True,
        cwd=ROOT,
    )


class StageTimeTests(unittest.TestCase):
    def test_every_stage_gets_a_line_and_a_table_row(self):
        done = run("restore", "isolation")
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertIn("== stage restore: 0 0s", done.stdout)
        self.assertRegex(done.stdout, r"== stage isolation: 0 [2-9]s")
        table = done.stdout.split("== stage timings", 1)[1]
        self.assertRegex(table, r"restore +0 +0s")
        self.assertRegex(table, r"isolation +0 +[2-9]s")
        self.assertRegex(table, r"total +- +[2-9]s")

    def test_failed_stage_keeps_its_status_prints_the_table_and_stops(self):
        done = run("restore", "format", "build")
        self.assertEqual(done.returncode, 3)
        self.assertIn("== stage format: 3 ", done.stdout)
        self.assertRegex(done.stdout.split("== stage timings", 1)[1], r"format +3 ")
        self.assertNotIn("ran build", done.stdout)
        self.assertNotIn("all stages passed", done.stdout)
        self.assertNotIn("selected stages passed", done.stdout)

    def test_errexit_inside_a_stage_still_fails_the_stage(self):
        script = DRIVER.replace(
            'format) echo "boom ran"; exit 3 ;;',
            'format) false; echo "after false" ;;',
        )
        done = subprocess.run(
            ["bash", "-c", script, "driver", str(VERIFY), "--stage", "format"],
            capture_output=True,
            text=True,
            cwd=ROOT,
        )
        self.assertNotEqual(done.returncode, 0)
        self.assertNotIn("after false", done.stdout)

    def test_a_term_sent_only_to_the_gate_process_reaches_the_stage_cleanup(self):
        with tempfile.TemporaryDirectory() as tmp:
            script = DRIVER.replace(
                'format) echo "boom ran"; exit 3 ;;',
                'format) trap \'touch "$D/cleaned"; exit 143\' TERM; touch "$D/started"; '
                'sleep 30 & wait $! ;;',
            )
            proc = subprocess.Popen(
                ["bash", "-c", script, "driver", str(VERIFY), "--stage", "format"],
                env={**os.environ, "D": tmp},
                cwd=ROOT,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            for _ in range(100):
                if (Path(tmp) / "started").exists():
                    break
                time.sleep(0.1)
            proc.send_signal(signal.SIGTERM)
            self.assertEqual(proc.wait(timeout=20), 143)
            self.assertTrue((Path(tmp) / "cleaned").exists())

    def test_a_stage_still_reads_the_gate_stdin(self):
        script = DRIVER.replace('format) echo "boom ran"; exit 3 ;;', 'format) read -r word; echo "got $word" ;;')
        done = subprocess.run(
            ["bash", "-c", script, "driver", str(VERIFY), "--stage", "format"],
            input="hello\n",
            capture_output=True,
            text=True,
            cwd=ROOT,
        )
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertIn("got hello", done.stdout)


if __name__ == "__main__":
    unittest.main()
