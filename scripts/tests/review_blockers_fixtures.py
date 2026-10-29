"""Shared fixtures for the review-blocker gate: a disposable repository, the gate's modules.

Not a test module (no `test_` prefix), so `unittest discover` skips it while the
three `test_check_review_blockers*.py` files import it.
"""
import importlib.util
import io
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent.parent
if str(SCRIPTS) not in sys.path:
    sys.path.insert(0, str(SCRIPTS))

from review_blockers import gitio, ratchet, rules, scan  # noqa: E402


def load_entry():
    """The `scripts/check-review-blockers.py` module, loaded by path like a caller would."""
    spec = importlib.util.spec_from_file_location("check_review_blockers_entry", SCRIPTS / "check-review-blockers.py")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


entry = load_entry()


def git(cwd, *args):
    return subprocess.run(
        ["git", "-C", str(cwd), *args], capture_output=True, text=True, encoding="utf-8", check=True
    ).stdout.strip()


def isolate_git(case):
    """Run every git call in these fixtures without the user's or the system's config."""
    for key, value in (("GIT_CONFIG_GLOBAL", os.devnull), ("GIT_CONFIG_NOSYSTEM", "1")):
        previous = os.environ.get(key)

        def restore(key=key, previous=previous):
            if previous is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = previous

        case.addCleanup(restore)
        os.environ[key] = value


def rules_of(found):
    return {finding.rule for finding in found}


def blocks(found):
    return {finding.rule for finding in found if finding.level == "BLOCK"}


def reported(found, rule):
    return [(finding.path, finding.line) for finding in found if finding.rule == rule]


class Repo:
    """A throwaway repository, one commit per `commit()` call, with no dependence on user config."""

    def __init__(self):
        self.path = Path(tempfile.mkdtemp(prefix="review-blockers-"))
        git(self.path, "init", "-q", "-b", "trunk")
        git(self.path, "config", "user.email", "fixture@example.invalid")
        git(self.path, "config", "user.name", "fixture")
        git(self.path, "config", "commit.gpgsign", "false")

    def write(self, name, text):
        target = self.path / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8")

    def remove(self, name):
        (self.path / name).unlink()

    def head(self):
        return git(self.path, "rev-parse", "HEAD")

    def commit(self, message="fixture"):
        git(self.path, "add", "-A")
        git(self.path, "commit", "-q", "-m", message)
        return self.head()

    def close(self):
        shutil.rmtree(self.path, ignore_errors=True)


class RepoTest(unittest.TestCase):
    """Points the gate at a disposable repository for the duration of one test."""

    def setUp(self):
        isolate_git(self)
        self.repo = Repo()
        self.addCleanup(self.repo.close)
        self.addCleanup(setattr, gitio, "REPO", gitio.REPO)
        gitio.REPO = self.repo.path
        self.repo.write("README.md", "fixture\n")
        self.repo.commit("base")

    def scan_change(self, *pairs):
        """Write each (path, text) pair, commit once, and scan the change set from the previous tip."""
        base = self.repo.head()
        for path, text in pairs:
            self.repo.write(path, text)
        return scan.scan(base, self.repo.commit())

    def run_main(self, argv):
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = entry.main(argv)
        self.output = out.getvalue()
        self.errors = err.getvalue()
        return code
