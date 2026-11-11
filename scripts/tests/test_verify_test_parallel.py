"""Cap, build-server shutdown and duration lines of `run_parallel_tests`.

The fake `dotnet` records how many test processes are alive at once and the
order of its calls, so the shell logic is checked without a container.
"""
import os
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
# An inherited cap would change what these fixtures measure.
BASE_ENV = {k: v for k, v in os.environ.items() if k != "EVIDGATE_TEST_PARALLEL"}
GUARDS = ROOT / "scripts" / "verify-parallel-tests.sh"
PROJECTS = ("Tenancy.IntegrationTests", "A.Tests", "B.Tests", "C.Tests", "D.Tests")

FAKE_DOTNET = textwrap.dedent("""\
    #!/usr/bin/env bash
    if [[ "${1:-}" == build-server ]]; then
      echo SHUTDOWN >>"$FAKE_DIR/calls"
      exit "${FAKE_SHUTDOWN_RC:-0}"
    fi
    if [[ " $* " == *" --list-tests "* ]]; then
      echo "Discovered 2 tests in assembly - x"; echo "  Tenancy.IntegrationTests.AaTests.First"
      echo "  Tenancy.IntegrationTests.AaTests.Second"; exit 0
    fi
    echo START >>"$FAKE_DIR/calls"
    touch "$FAKE_DIR/alive.$$"
    ls "$FAKE_DIR" | grep -c '^alive\\.' >>"$FAKE_DIR/concurrency"
    sleep "${FAKE_SLEEP:-0.6}"
    rm -f "$FAKE_DIR/alive.$$"
    if [[ " $* " == *" --project tests/B.Tests/B.Tests.csproj "* ]]; then
      echo "boom from B"; echo "  total: 3"; exit 2
    fi
    if [[ " $* " == *" --filter-class "* ]]; then echo "  total: 2"; else echo "  total: 3"; fi
    """)


class TestParallelCap(unittest.TestCase):
    def run_stage(self, **env):
        with tempfile.TemporaryDirectory() as tmp:
            tmp_path = Path(tmp)
            for project in PROJECTS:
                (tmp_path / "tests" / project).mkdir(parents=True)
                (tmp_path / "tests" / project / f"{project}.csproj").write_text("")
            (tmp_path / "bin").mkdir()
            (tmp_path / "state").mkdir()
            fake = tmp_path / "bin" / "dotnet"
            fake.write_text(FAKE_DOTNET)
            fake.chmod(0o755)
            done = subprocess.run(
                ["bash", "-c", f'set -euo pipefail; source "{GUARDS}"; run_parallel_tests'],
                cwd=tmp,
                env={
                    **BASE_ENV,
                    "PATH": f"{tmp_path / 'bin'}:{os.environ['PATH']}",
                    "FAKE_DIR": str(tmp_path / "state"),
                    "EVIDGATE_TENANCY_SHARDS": "1",
                    **env,
                },
                capture_output=True,
                text=True,
            )
            state = tmp_path / "state"
            calls = (state / "calls").read_text().split() if (state / "calls").exists() else []
            conc = (state / "concurrency").read_text().split() if (state / "concurrency").exists() else []
            return done, calls, [int(c) for c in conc]

    def test_the_cap_bounds_concurrent_processes(self):
        done, calls, conc = self.run_stage(EVIDGATE_TEST_PARALLEL="2", FAKE_SHUTDOWN_RC="0")
        self.assertEqual(calls.count("START"), 5, done.stdout + done.stderr)
        self.assertLessEqual(max(conc), 2, conc)
        self.assertEqual(max(conc), 2, conc)

    def test_unset_starts_every_process_before_the_first_finishes(self):
        done, calls, conc = self.run_stage(FAKE_SLEEP="1.5")
        self.assertEqual(calls.count("START"), 5, done.stdout + done.stderr)
        self.assertEqual(max(conc), 5, conc)

    def test_a_cap_of_one_runs_the_tenancy_shard_first_and_serially(self):
        done, calls, conc = self.run_stage(EVIDGATE_TEST_PARALLEL="1", FAKE_SLEEP="0.3")
        self.assertEqual(max(conc), 1, conc)
        lines = [l for l in done.stdout.splitlines() if l.startswith("== ")]
        self.assertTrue(lines[0].startswith("== tenancy-0: passed"), lines)

    def test_bad_values_are_refused(self):
        for value in ("0", "17", "08", "abc", "-1", "2 3"):
            with self.subTest(value=value):
                done, calls, _ = self.run_stage(EVIDGATE_TEST_PARALLEL=value)
                self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
                self.assertIn("EVIDGATE_TEST_PARALLEL must be an integer from 1 to 16", done.stderr)
                self.assertEqual(calls, [])

    def test_an_empty_cap_means_unset(self):
        done, calls, conc = self.run_stage(EVIDGATE_TEST_PARALLEL="", FAKE_SLEEP="1.5")
        self.assertEqual(max(conc), 5, done.stdout + done.stderr)

    def test_build_servers_stop_once_before_the_first_test_process(self):
        done, calls, _ = self.run_stage()
        self.assertEqual(calls[0], "SHUTDOWN", calls)
        self.assertEqual(calls.count("SHUTDOWN"), 1)
        self.assertIn("build servers stopped before the tests", done.stdout)

    def test_a_failed_shutdown_is_noted_and_ignored(self):
        done, calls, _ = self.run_stage(FAKE_SHUTDOWN_RC="1")
        self.assertIn("build-server shutdown failed; continuing", done.stdout)
        self.assertEqual(calls.count("START"), 5)

    def test_a_failing_project_is_reported_with_its_log_and_duration(self):
        done, _, _ = self.run_stage(EVIDGATE_TEST_PARALLEL="2")
        self.assertEqual(done.returncode, 1, done.stdout)
        self.assertRegex(done.stdout, r"== B\.Tests: FAILED \(exit 2, \d+m\d\ds\)")
        self.assertIn("boom from B", done.stdout)
        self.assertRegex(done.stdout, r"== A\.Tests: passed \(3 tests, \d+m\d\ds\)")


if __name__ == "__main__":
    unittest.main()
