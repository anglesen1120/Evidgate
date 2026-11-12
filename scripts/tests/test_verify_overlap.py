"""The opt-in side lane of scripts/verify.sh (EVIDGATE_VERIFY_OVERLAP=1).

verify.sh is sourced with `run_stage` replaced by a stub, so the cases drive the real
`main`, `run_selected_stages` and the lane without building or testing anything.
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
      test) sleep "${TEST_SLEEP:-2}"; echo "ran test"; [[ -z "${TEST_FAIL:-}" ]] || exit 7 ;;
      tsc-build) echo "side one a"; sleep 1; echo "side one b" ;;
      prettier) echo "side two a"; sleep 1; echo "side two b"; [[ -z "${SIDE_FAIL:-}" ]] || exit 5 ;;
      *) echo "ran $1" ;;
      esac
    }
    main "$@"
    """)


def run(stages, **env):
    args = [a for s in stages for a in ("--stage", s)]
    return subprocess.run(
        ["bash", "-c", DRIVER, "driver", str(VERIFY), *args],
        capture_output=True,
        text=True,
        cwd=ROOT,
        env={**os.environ, "EVIDGATE_VERIFY_OVERLAP": "0", **env},
    )


STAGES = ["restore", "test", "tsc-build", "prettier", "isolation"]


class OverlapTests(unittest.TestCase):
    def test_off_by_default_runs_every_stage_in_order_with_no_lane(self):
        done = run(STAGES)
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertNotIn("side lane", done.stdout)
        order = [l for l in done.stdout.splitlines() if l.startswith("== stage ") and l.endswith("s") and ": " in l]
        self.assertEqual([l.split()[2].rstrip(":") for l in order], STAGES)

    def test_on_overlaps_the_side_stages_with_test_and_keeps_every_line(self):
        started = time.monotonic()
        done = run(STAGES, EVIDGATE_VERIFY_OVERLAP="1", TEST_SLEEP="3")
        wall = time.monotonic() - started
        self.assertEqual(done.returncode, 0, done.stderr)
        # Side stages take 2 s in total and test 3 s: serial would be 5 s plus bash start-up.
        self.assertLess(wall, 4.8, done.stdout)
        for stage in STAGES:
            self.assertRegex(done.stdout, rf"== stage {stage}: 0 \d+s")
        self.assertIn("== verify: selected stages passed", done.stdout)
        self.assertRegex(done.stdout, r"wall +- +\d+s")

    def test_a_side_stage_log_is_printed_whole_and_not_interleaved(self):
        done = run(STAGES, EVIDGATE_VERIFY_OVERLAP="1")
        out = done.stdout
        self.assertIn("side one a\nside one b\n", out)
        self.assertIn("side two a\nside two b\n", out)
        self.assertLess(out.index("side one b"), out.index("side two a"))

    def test_a_failing_side_stage_fails_the_gate_and_keeps_its_status(self):
        done = run(STAGES, EVIDGATE_VERIFY_OVERLAP="1", SIDE_FAIL="1")
        self.assertNotEqual(done.returncode, 0)
        self.assertRegex(done.stdout, r"== stage prettier: 5 \d+s")
        self.assertIn("side two b", done.stdout)
        self.assertNotIn("selected stages passed", done.stdout)
        self.assertIn("stage timings", done.stdout)

    def test_a_failing_main_stage_stops_the_lane_and_prints_the_table(self):
        done = run(STAGES, EVIDGATE_VERIFY_OVERLAP="1", TEST_FAIL="1")
        self.assertEqual(done.returncode, 7, done.stdout)
        self.assertNotIn("== stage isolation", done.stdout)
        self.assertIn("stage timings", done.stdout)

    def test_stages_before_test_are_not_moved_into_the_lane(self):
        done = run(["tsc-build", "test"], EVIDGATE_VERIFY_OVERLAP="1")
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertNotIn("side lane", done.stdout)

    def test_a_repeated_stage_is_refused_before_anything_runs(self):
        for stages in (["tsc-build", "test", "tsc-build"], ["test", "tsc-build", "test"]):
            with self.subTest(stages=stages):
                done = run(stages, EVIDGATE_VERIFY_OVERLAP="1")
                self.assertEqual(done.returncode, 2)
                self.assertIn("refuses a repeated stage", done.stderr)
                self.assertNotIn("ran ", done.stdout)

    def test_a_repeated_stage_is_fine_with_the_switch_off(self):
        self.assertEqual(run(["tsc-build", "test", "tsc-build"]).returncode, 0)

    def test_the_lane_is_joined_before_the_stage_after_test(self):
        script = DRIVER.replace('*) echo "ran $1" ;;', 'isolation) echo "isolation saw: $(cat "$D/side-done" 2>/dev/null)" ;;\n      *) echo "ran $1" ;;')
        script = script.replace('prettier) echo "side two a"; sleep 1;', 'prettier) echo "side two a"; sleep 3; echo yes >"$D/side-done";')
        with tempfile.TemporaryDirectory() as tmp:
            done = subprocess.run(
                ["bash", "-c", script, "driver", str(VERIFY), *[a for s in STAGES for a in ("--stage", s)]],
                capture_output=True, text=True, cwd=ROOT,
                env={**os.environ, "D": tmp, "EVIDGATE_VERIFY_OVERLAP": "1", "TEST_SLEEP": "1"},
            )
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertIn("isolation saw: yes", done.stdout)

    def test_a_lane_that_dies_without_results_fails_the_gate(self):
        script = DRIVER.replace('tsc-build) echo "side one a"; sleep 1; echo "side one b" ;;', 'tsc-build) kill -KILL $(ps -o ppid= -p "$(sh -c "echo \\$PPID")"); sleep 5 ;;')
        done = subprocess.run(
            ["bash", "-c", script, "driver", str(VERIFY), *[a for s in STAGES for a in ("--stage", s)]],
            capture_output=True, text=True, cwd=ROOT,
            env={**os.environ, "EVIDGATE_VERIFY_OVERLAP": "1", "TEST_SLEEP": "1"},
        )
        self.assertNotEqual(done.returncode, 0, done.stdout)
        self.assertNotIn("selected stages passed", done.stdout)

    def test_a_bad_switch_value_is_refused(self):
        done = run(STAGES, EVIDGATE_VERIFY_OVERLAP="yes")
        self.assertEqual(done.returncode, 2)
        self.assertIn("EVIDGATE_VERIFY_OVERLAP must be 0 or 1", done.stderr)

    def test_a_term_sent_to_the_gate_stops_the_side_lane_stage_and_its_children(self):
        with tempfile.TemporaryDirectory() as tmp:
            script = DRIVER.replace(
                'tsc-build) echo "side one a"; sleep 1; echo "side one b" ;;',
                'tsc-build) sleep 60 & echo $! >"$D/child"; wait ;;',
            ).replace('test) sleep "${TEST_SLEEP:-2}"', "test) sleep 30")
            proc = subprocess.Popen(
                ["bash", "-c", script, "driver", str(VERIFY), "--stage", "test", "--stage", "tsc-build"],
                env={**os.environ, "D": tmp, "EVIDGATE_VERIFY_OVERLAP": "1"},
                cwd=ROOT,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            for _ in range(100):
                if (Path(tmp) / "child").exists():
                    break
                time.sleep(0.1)
            child = int((Path(tmp) / "child").read_text())
            proc.send_signal(signal.SIGTERM)
            self.assertEqual(proc.wait(timeout=20), 143)
            for _ in range(50):
                try:
                    os.kill(child, 0)
                except ProcessLookupError:
                    break
                time.sleep(0.1)
            else:
                os.kill(child, signal.SIGKILL)
                self.fail("the side stage's child outlived the gate")


if __name__ == "__main__":
    unittest.main()
