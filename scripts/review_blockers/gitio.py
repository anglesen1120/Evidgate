"""Git plumbing for the review-blocker gate: byte-exact transport, refs, added lines.

One helper runs every git command so the scan has a single failure path: any
non-zero exit (other than the ones a caller allows) becomes a `ScanError`, and
`text=None` keeps the output as bytes because text mode rewrites embedded `\\r`.
"""
from __future__ import annotations

import re
import subprocess
from collections.abc import Iterator
from pathlib import Path

HUNK = re.compile(r"@@ -\d+(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")
ZERO_REF = re.compile(r"^0+$")
# The base a change set is read against when the caller names none, in the
# order the other change-set stages try them: a clean clone has `develop` and
# no remote, a full checkout has the remote-tracking ref.
BASE_REFS = ("origin/develop", "develop", "origin/main", "main")

REPO = Path.cwd()


class ScanError(Exception):
    """The scan could not run: the caller must treat it as a failed hard check."""


def run_git(*args: str, allowed: tuple[int, ...] = (0,)) -> subprocess.CompletedProcess:
    """Run one git command in the repository and keep its output byte-exact."""
    try:
        result = subprocess.run(
            ["git", "-C", str(REPO), "-c", "core.quotePath=false", *args],
            capture_output=True,
            check=False,
        )
    except (OSError, ValueError) as error:
        raise ScanError(f"cannot run git: {error}") from error
    if result.returncode not in allowed:
        detail = (result.stderr or result.stdout).decode("utf-8", errors="replace").strip()
        raise ScanError(f"git {' '.join(args)}: {detail or 'failed'}")
    return result


def git(*args: str) -> str:
    """The decoded stdout of one git command; undecodable bytes are replaced, never fatal."""
    return run_git(*args).stdout.decode("utf-8", errors="replace")


def resolves(ref: str) -> bool:
    """True when `ref` names a commit in this clone."""
    return run_git("rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}", allowed=(0, 1)).returncode == 0


def resolve(ref: str, role: str) -> str:
    """The commit `ref` names, or a message that says what to do about it."""
    if not resolves(ref):
        raise ScanError(
            f"{role} {ref!r} does not resolve to a commit: fetch it (`git fetch origin`), "
            "pass another ref, or check the clone has full history (fetch-depth: 0)"
        )
    return git("rev-parse", "--verify", f"{ref}^{{commit}}").strip()


def effective_base(base: str) -> str:
    """The ref to diff against.

    An explicit base wins: CI passes the pull request's base sha, and a push
    passes the ref it started from — all zeros when the push created the
    branch. Without one, the develop tip is used, through the same fallback
    chain the other change-set stages use, because the clean-clone smoke build
    has a local `develop` and no remote.
    """
    if base and not ZERO_REF.match(base):
        return base
    for ref in BASE_REFS:
        if resolves(ref):
            return ref
    raise ScanError(
        f"no base ref ({', '.join(BASE_REFS)}) resolves: fetch it (`git fetch origin`), "
        "set EVIDGATE_REVIEW_BASE, or check the clone has full history (fetch-depth: 0)"
    )


def diff_path(header: str) -> str | None:
    """Decode Git's C-quoted header paths, including escaped quotes and tabs."""
    path = header.removesuffix("\t")
    if path.startswith('"'):
        escapes = {"a": 7, "b": 8, "t": 9, "n": 10, "v": 11, "f": 12, "r": 13, "\\": 92, '"': 34}
        body = path[1:-1]
        data = bytearray()
        cursor = 0
        for match in re.finditer(r"\\([0-7]{1,3}|.)", body):
            data.extend(body[cursor : match.start()].encode("utf-8"))
            token = match.group(1)
            data.append(int(token, 8) if token.isdigit() else escapes.get(token, ord(token)))
            cursor = match.end()
        data.extend(body[cursor:].encode("utf-8"))
        path = data.decode("utf-8", errors="replace")
    return None if path == "/dev/null" else path.removeprefix("b/")


def added_lines(base: str, head: str) -> Iterator[tuple[str, int, str]]:
    """Yield (path, head line number, text) for every added line of base...head.

    The hunk counters, not the prefixes, decide what a line is: `-U0` emits no
    context, and an added line whose own text starts with `++` arrives as
    `+++ …`, which a header-only parser would read as the next file. `--text`
    keeps a repository `.gitattributes` (`* -diff`, `binary`) from hiding a hunk,
    and the output is split on `\\n` only so a `\\r` or a U+2028 inside a line
    stays part of that line.
    """
    out = git(
        "diff",
        "-U0",
        "--no-color",
        "--text",
        "--no-textconv",
        "--no-ext-diff",
        "--src-prefix=a/",
        "--dst-prefix=b/",
        f"{base}...{head}",
    )
    path: str | None = None
    line_no = old_left = new_left = 0
    for raw in out.split("\n"):
        raw = raw.removesuffix("\r")
        if old_left <= 0 and new_left <= 0:
            if raw.startswith("+++ "):
                path = diff_path(raw[4:])
            elif match := HUNK.match(raw):
                old_left = int(match.group(1) or 1)
                line_no, new_left = int(match.group(2)), int(match.group(3) or 1)
            continue
        if raw.startswith("-"):
            old_left -= 1
        elif raw.startswith("+"):
            new_left -= 1
            if path:
                yield path, line_no, raw[1:]
            line_no += 1


def enter_repository() -> None:
    """Point every git call at the repository root, so a subdirectory works too."""
    global REPO
    REPO = Path(git("rev-parse", "--show-toplevel").strip())


def short(sha: str) -> str:
    """The abbreviated sha git shows, so a report names the commits it read."""
    return git("rev-parse", "--short", sha).strip()
