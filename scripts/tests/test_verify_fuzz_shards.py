"""Shard count, budget split, seeds and iteration sum of `run_fuzz_shards`.

The fake `dotnet` records the seed and budget each shard was given and writes the
run.json the real harness writes, so the shell logic runs without the test host.
"""
import os
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
GUARDS = ROOT / "scripts" / "verify-guards.sh"

FAKE_DOTNET = textwrap.dedent("""\
    #!/usr/bin/env bash
    echo "$EVIDGATE_FUZZ_SEED $EVIDGATE_FUZZ_BUDGET_SECONDS $EVIDGATE_FUZZ_FINDINGS_DIR" >>"$FAKE_DIR/calls"
    mkdir -p "$EVIDGATE_FUZZ_FINDINGS_DIR"
    if [[ "${FAKE_NO_RUN_JSON:-}" != "$EVIDGATE_FUZZ_SEED" ]]; then
      printf '{\\n  "Iterations": 1000,\\n  "Findings": 0\\n}\\n' >"$EVIDGATE_FUZZ_FINDINGS_DIR/run.json"
    fi
    echo "  total: 4"
    [[ "${FAKE_FAIL_SEED:-}" != "$EVIDGATE_FUZZ_SEED" ]] || { echo "finding"; exit 2; }
    """)


def run_stage(**env):
    with tempfile.TemporaryDirectory() as tmp:
        tmp_path = Path(tmp)
        project = tmp_path / "tests" / "Tenancy.IntegrationTests"
        project.mkdir(parents=True)
        (project / "Tenancy.IntegrationTests.csproj").write_text("")
        (tmp_path / "bin").mkdir()
        (tmp_path / "state").mkdir()
        fake = tmp_path / "bin" / "dotnet"
        fake.write_text(FAKE_DOTNET)
        fake.chmod(0o755)
        done = subprocess.run(
            [
                "bash",
                "-c",
                f'set -euo pipefail; ROOT="{ROOT}"; source "{GUARDS}"; '
                "run_fuzz_shards tests/Tenancy.IntegrationTests",
            ],
            cwd=tmp,
            env={
                **{k: v for k, v in os.environ.items() if not k.startswith("EVIDGATE_FUZZ")},
                "PATH": f"{tmp_path / 'bin'}:{os.environ['PATH']}",
                "FAKE_DIR": str(tmp_path / "state"),
                "EVIDGATE_FUZZ_BUDGET_SECONDS": "600",
                "EVIDGATE_FUZZ_SEED": "5000",
                "EVIDGATE_FUZZ_FINDINGS_DIR": str(tmp_path / "findings"),
                **env,
            },
            capture_output=True,
            text=True,
        )
        log = tmp_path / "state" / "calls"
        calls = log.read_text().splitlines() if log.exists() else []
        return done, [c.split() for c in calls]


def run_stage_expect_refusal(value):
    # No fake call is made, so the calls file does not exist: run the shell directly.
    return subprocess.run(
        [
            "bash",
            "-c",
            f'set -euo pipefail; ROOT="{ROOT}"; source "{GUARDS}"; '
            "run_fuzz_shards tests/Tenancy.IntegrationTests",
        ],
        env={
            **os.environ,
            "EVIDGATE_FUZZ_SHARDS": value,
            "EVIDGATE_FUZZ_BUDGET_SECONDS": "600",
            "EVIDGATE_FUZZ_SEED": "1",
            "EVIDGATE_FUZZ_FINDINGS_DIR": "/nonexistent",
        },
        capture_output=True,
        text=True,
    )


class FuzzShardTests(unittest.TestCase):
    def test_budget_is_split_and_every_shard_gets_its_own_seed_and_directory(self):
        done, calls = run_stage(EVIDGATE_FUZZ_SHARDS="4")
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertEqual(sorted(c[0] for c in calls), ["5000", "5001", "5002", "5003"])
        self.assertEqual({c[1] for c in calls}, {"150"})
        self.assertEqual(len({c[2] for c in calls}), 4)
        self.assertTrue(all("/shard-" in c[2] for c in calls))
        self.assertIn("fuzz: 4000 inputs in total over 4 shard(s)", done.stdout)

    def test_the_budget_rounds_up_so_core_seconds_are_never_lost(self):
        done, calls = run_stage(EVIDGATE_FUZZ_SHARDS="3", EVIDGATE_FUZZ_BUDGET_SECONDS="100")
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertEqual({c[1] for c in calls}, {"34"})

    def test_one_shard_keeps_the_whole_budget_and_the_base_seed(self):
        done, calls = run_stage(EVIDGATE_FUZZ_SHARDS="1")
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertEqual([c[:2] for c in calls], [["5000", "600"]])

    def test_a_failing_shard_fails_the_stage(self):
        done, _ = run_stage(EVIDGATE_FUZZ_SHARDS="2", FAKE_FAIL_SEED="5001")
        self.assertNotEqual(done.returncode, 0)
        self.assertIn("fuzz-1: FAILED", done.stdout)

    def test_a_shard_without_an_iteration_count_fails_the_stage(self):
        done, _ = run_stage(EVIDGATE_FUZZ_SHARDS="2", FAKE_NO_RUN_JSON="5000")
        self.assertNotEqual(done.returncode, 0)
        self.assertIn("shard 0 wrote no run.json", done.stderr)

    def test_the_default_count_never_exceeds_four_or_the_core_count(self):
        done, calls = run_stage()
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertEqual(len(calls), min(4, os.cpu_count() or 1))

    def test_a_bad_shard_count_is_refused(self):
        for value in ("0", "17", "x"):
            with self.subTest(value=value):
                done = run_stage_expect_refusal(value)
                self.assertEqual(done.returncode, 2)
                self.assertIn("EVIDGATE_FUZZ_SHARDS must be an integer from 1 to 16", done.stderr)

    def test_a_seed_above_int64_is_passed_verbatim_to_a_single_shard(self):
        done, calls = run_stage(EVIDGATE_FUZZ_SHARDS="1", EVIDGATE_FUZZ_SEED="18446744073709551615")
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertEqual(calls[0][0], "18446744073709551615")

    def test_a_seed_that_is_not_a_decimal_is_refused_before_any_run(self):
        for seed in ("abc", "a[$(touch pwned)]", "1 2"):
            done, calls = run_stage(EVIDGATE_FUZZ_SHARDS="2", EVIDGATE_FUZZ_SEED=seed)
            self.assertEqual(done.returncode, 2, seed)
            self.assertEqual(calls, [], seed)

    def test_a_multi_shard_seed_that_bash_cannot_add_to_is_refused(self):
        done, calls = run_stage(EVIDGATE_FUZZ_SHARDS="2", EVIDGATE_FUZZ_SEED="18446744073709551615")
        self.assertEqual(done.returncode, 2)
        self.assertEqual(calls, [])

    def test_leading_zero_budget_and_seed_are_decimal_not_octal(self):
        done, calls = run_stage(
            EVIDGATE_FUZZ_SHARDS="4", EVIDGATE_FUZZ_BUDGET_SECONDS="0600", EVIDGATE_FUZZ_SEED="0008"
        )
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertEqual({c[1] for c in calls}, {"150"})
        self.assertIn("11", [c[0] for c in calls])

    def test_a_zero_budget_is_refused(self):
        done, calls = run_stage(EVIDGATE_FUZZ_BUDGET_SECONDS="0")
        self.assertEqual(done.returncode, 2)
        self.assertEqual(calls, [])

    def test_a_directory_the_stage_did_not_create_is_never_emptied(self):
        with tempfile.TemporaryDirectory() as other:
            keep = Path(other) / "shard-0"
            keep.mkdir()
            (keep / "precious").write_text("x")
            done, calls = run_stage(EVIDGATE_FUZZ_FINDINGS_DIR=other)
            self.assertEqual(done.returncode, 2)
            self.assertEqual(calls, [])
            self.assertTrue((keep / "precious").exists())


if __name__ == "__main__":
    unittest.main()
