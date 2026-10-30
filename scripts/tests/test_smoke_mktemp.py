"""scripts/smoke-clean-build.sh builds its two temp paths with mktemp.

Lanes on one machine run the smoke at the same time, and BSD mktemp substitutes
only a *trailing* run of Xs: a template such as ``....XXXXXX.sh`` is a literal
path, so the second concurrent (or stale) run dies with ``File exists``. The
cases run the script's real ``mktemp`` command lines in child bash processes,
so the child sees the system mktemp rather than any shell builtin of the parent.
"""
import os
import re
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
SMOKE = ROOT / "scripts" / "smoke-clean-build.sh"

# NAME="$(mktemp [-d] "template")" — the only temp-path shape the script uses.
ASSIGNMENT = re.compile(
    r'^(?P<name>[A-Za-z_][A-Za-z0-9_]*)="\$\((?P<command>mktemp\s+(?:-d\s+)?"[^"]+")\)"$',
    re.M,
)
TEMPLATE_ARG = re.compile(r'"([^"]*)"')
TRAP = re.compile(r"^trap '.*' EXIT$", re.M)

# The template this file guards against: Xs followed by a suffix.
OLD_INNER_TEMPLATE = "$HOME/.evidgate-smoke-inner.XXXXXX.sh"

# Hold every child here until the gate file appears, so concurrent children
# reach mktemp together instead of one after the other.
GATE_WAIT = 'while [ ! -e "$SMOKE_TEST_GATE" ]; do sleep 0.005; done\n'


def assignments():
    """{'NAME': (command, template)} for every mktemp temp path in the script."""
    found = {}
    for name, command in ASSIGNMENT.findall(SMOKE.read_text(encoding="utf-8")):
        template = TEMPLATE_ARG.findall(command)
        if len(template) != 1:
            raise AssertionError(f"{name}: expected one template in {command!r}")
        found[name] = (command, template[0])
    return found


def start(command, home, gate):
    """Start one child that waits for `gate` and then runs `mktemp`."""
    env = dict(os.environ, HOME=str(home), SMOKE_TEST_GATE=str(gate))
    return subprocess.Popen(
        ["bash", "-c", GATE_WAIT + command],
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )


def finish(proc):
    """Wait for a started child; return its exit code, path and stderr."""
    out, err = proc.communicate(timeout=60)
    return proc.returncode, out.strip(), err.strip()


class SmokeCleanBuildTempPaths(unittest.TestCase):
    """The smoke's temp paths: unique per run, under $HOME, removed on exit."""

    def test_mktemp_templates_end_with_their_x_run(self):
        found = assignments()
        # X4: assert the discovery itself is meaningful before trusting it.
        self.assertGreaterEqual(len(found), 2, f"no mktemp templates found in {SMOKE}")
        for name, (_, template) in found.items():
            with self.subTest(name=name, template=template):
                self.assertRegex(
                    template,
                    r"X{6,}$",
                    "the template must end with its X run: BSD mktemp treats "
                    "anything after the Xs as literal, so parallel runs collide",
                )

    def test_parallel_runs_create_distinct_paths(self):
        found = assignments()
        self.assertIn("INNER_SCRIPT", found)
        with tempfile.TemporaryDirectory(prefix="smoke-mktemp-") as sandbox:
            home = Path(sandbox, "home")
            home.mkdir()
            gate = Path(sandbox, "go")
            paths = []
            for name, (command, _) in found.items():
                with self.subTest(name=name):
                    procs = [start(command, home, gate) for _ in range(2)]
                    gate.touch()
                    for proc in procs:
                        code, out, err = finish(proc)
                        self.assertEqual(code, 0, f"{name}: mktemp failed: {err}")
                        path = Path(out)
                        self.assertEqual(path.parent, home, f"{name}: {path} not under $HOME")
                        self.assertTrue(path.exists(), f"{name}: {path} missing")
                        wants_dir = "-d " in command
                        self.assertEqual(path.is_dir(), wants_dir, f"{name}: {path} kind")
                        if not wants_dir:
                            self.assertTrue(os.access(path, os.W_OK), f"{name}: {path} unwritable")
                        paths.append(path)
            self.assertEqual(len(paths), len(set(paths)), f"temp paths repeated: {paths}")
            self.assertEqual({path.parent for path in paths}, {home})

    def test_trap_removes_both_temp_paths(self):
        text = SMOKE.read_text(encoding="utf-8")
        traps = TRAP.findall(text)
        self.assertTrue(traps, "the smoke must remove its temp paths on exit")
        joined = " ".join(traps)
        for name in ("WORKDIR", "INNER_SCRIPT"):
            with self.subTest(name=name):
                self.assertIn(f'"${name}"', joined)

    def test_old_suffix_template_collides_on_second_creation(self):
        # The regression this file exists for: two lanes creating the rejected
        # template at the same moment, one of which has to lose.
        with tempfile.TemporaryDirectory(prefix="smoke-mktemp-old-") as sandbox:
            home = Path(sandbox, "home")
            home.mkdir()
            command = f'mktemp "{OLD_INNER_TEMPLATE}"'
            gate = Path(sandbox, "go")
            procs = [start(command, home, gate) for _ in range(2)]
            gate.touch()
            results = [finish(proc) for proc in procs]
            made = [result for result in results if result[0] == 0]
            lost = [result for result in results if result[0] != 0]
            if lost:
                # mktemp substitutes only a trailing X run, so both runs name
                # the same literal path and the loser hits the existing file.
                self.assertEqual(len(made), 1, f"unexpected results: {results}")
                self.assertIn("File exists", lost[0][2])
                self.assertEqual(Path(made[0][1]).name, Path(OLD_INNER_TEMPLATE).name)
                self.assertTrue(Path(made[0][1]).exists())
            else:
                # GNU mktemp substitutes Xs anywhere, which hides the defect on
                # this host; the same template is still a fixed name for BSD, so
                # prove the fixed name cannot be created twice.
                self.assertNotRegex(OLD_INNER_TEMPLATE, r"X{6,}$")
                literal = os.path.join(sandbox, "literal")
                os.close(os.open(literal, os.O_CREAT | os.O_EXCL | os.O_WRONLY))
                with self.assertRaises(FileExistsError):
                    os.open(literal, os.O_CREAT | os.O_EXCL | os.O_WRONLY)


if __name__ == "__main__":
    unittest.main()
