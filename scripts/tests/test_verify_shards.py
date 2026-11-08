"""The parallel test stage in scripts/verify-guards.sh (`run_parallel_tests`).

The real function runs against a scratch tree and a fake `dotnet`: the shell
logic under test is the sharding, the exit codes and the count check, none of
which needs a container. The fake lists three Tenancy classes with two tests
each and "runs" whatever `--filter-class` names, failing the classes named in
FAIL_CLASS.
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
    classes=(Tenancy.IntegrationTests.AaTests Tenancy.IntegrationTests.BbTests Tenancy.IntegrationTests.CcTests)
    if [[ " $* " == *" --list-tests "* ]]; then
      [[ -n "${FAKE_LIST_EMPTY:-}" ]] && { echo "Discovered 0 tests in assembly - x"; exit 0; }
      echo "Discovered $(( ${#classes[@]} * 2 )) tests in assembly - x"
      for c in "${classes[@]}"; do echo "  $c.First"; echo "  $c.Second(value: 1.5)"; done
      exit 0
    fi
    ran=0 rc=0 picked=()
    if [[ " $* " == *" --filter-class "* ]]; then
      args=("$@"); take=0
      for a in "${args[@]}"; do
        if [[ "$a" == --filter-class ]]; then take=1; continue; fi
        [[ "$a" == --* ]] && take=0
        [[ $take -eq 1 ]] && picked+=("$a")
      done
      ran=$(( ${#picked[@]} * 2 - ${FAKE_DROP:-0} ))
      for c in "${picked[@]}"; do
        [[ "$c" == "${FAIL_CLASS:-}" ]] && { echo "failed $c.First (1ms)"; rc=2; }
      done
    else
      ran=3
    fi
    echo "  total: $ran"
    exit $rc
    """)


class ParallelTests(unittest.TestCase):
    def run_stage(self, **env):
        with tempfile.TemporaryDirectory() as tmp:
            tmp_path = Path(tmp)
            for project in ("Tenancy.IntegrationTests", "Other.Tests"):
                (tmp_path / "tests" / project).mkdir(parents=True)
                (tmp_path / "tests" / project / f"{project}.csproj").write_text("")
            bin_dir = tmp_path / "bin"
            bin_dir.mkdir()
            fake = bin_dir / "dotnet"
            fake.write_text(FAKE_DOTNET)
            fake.chmod(0o755)
            script = f'source "{GUARDS}"; run_parallel_tests'
            return subprocess.run(
                ["bash", "-c", script],
                cwd=tmp,
                env={**os.environ, "PATH": f"{bin_dir}:{os.environ['PATH']}", **env},
                capture_output=True,
                text=True,
            )

    def test_every_class_runs_in_exactly_one_shard(self):
        done = self.run_stage(EVIDGATE_TENANCY_SHARDS="2")
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertIn("tenancy: 6 tests run in 2 shard(s), 6 discovered", done.stdout)

    def test_a_failing_test_in_any_shard_fails_the_stage(self):
        for failing in ("AaTests", "BbTests", "CcTests"):
            with self.subTest(failing=failing):
                done = self.run_stage(
                    EVIDGATE_TENANCY_SHARDS="3",
                    FAIL_CLASS=f"Tenancy.IntegrationTests.{failing}",
                )
                self.assertNotEqual(done.returncode, 0, done.stdout)
                self.assertIn(f"failed Tenancy.IntegrationTests.{failing}.First", done.stdout)

    def test_a_shard_that_runs_fewer_tests_than_discovered_fails_the_stage(self):
        done = self.run_stage(EVIDGATE_TENANCY_SHARDS="2", FAKE_DROP="1")
        self.assertNotEqual(done.returncode, 0, done.stdout)
        self.assertIn("but discovery found 6", done.stderr)

    def test_discovery_without_tests_fails_the_stage(self):
        done = self.run_stage(FAKE_LIST_EMPTY="1")
        self.assertNotEqual(done.returncode, 0)
        self.assertIn("found no test", done.stderr)

    def test_a_bad_shard_count_is_refused(self):
        done = self.run_stage(EVIDGATE_TENANCY_SHARDS="0")
        self.assertEqual(done.returncode, 2, done.stdout + done.stderr)


if __name__ == "__main__":
    unittest.main()
