"""U1: the ratchet over plan/task/review ids in source comments.

Those ids predate this gate, so the committed baseline is the ceiling: a change
set may not raise the count, and a lower count only prints an advisory asking
for the lower number — a forced update would conflict with every parallel
change set. `--u1-count` prints the number for that update.
"""
from __future__ import annotations

import json
import re
from pathlib import Path

from review_blockers import gitio
from review_blockers.rules import comment_tail

U1_PATHS = re.compile(r"^(src/.*\.cs|apps/web/src/.*\.ts|packages/[^/]+/src/.*\.ts)$")
# Git pathspec globs, so `packages/*/src` really reaches package sources: a
# bare `packages/*/src` path matches the directory entry only, never a file.
U1_TREE_PATHS = (":(top,glob)src/**/*.cs", ":(top,glob)apps/web/src/**/*.ts", ":(top,glob)packages/*/src/**/*.ts")
U1_IDS = re.compile(r"\b\d{2}\.\d+\.\d+-T\d+|\bCR-\d{2}\b|\bD-S\d+")
# Cheap superset for `git grep`; the comment check below decides, so a hit the
# Python regex rejects (an id inside a string, say) never reaches the count.
U1_GREP = r"([0-9]{2}\.[0-9]+\.[0-9]+-T[0-9]+|CR-[0-9]{2}|D-S[0-9]+)"
BASELINE_NAME = "check-review-blockers.baseline.json"


def u1_comment_lines(head: str) -> int:
    """Count the comment lines in the head tree that carry plan/task/review ids.

    `git grep -z` NUL-terminates the path field, so a path holding a colon still
    parses; the line number and the matched text follow, one record per `\\n`.
    `-a` reads every tracked file rather than trusting attributes to decide what
    is binary.
    """
    if not gitio.run_git("ls-files", "-z", "--", *U1_TREE_PATHS).stdout.strip(b"\0"):
        raise gitio.ScanError(f"no tracked files match the U1 paths {', '.join(U1_TREE_PATHS)}")
    result = gitio.run_git(
        "grep", "-a", "-n", "-z", "-E", "--no-color", "-e", U1_GREP, head, "--", *U1_TREE_PATHS, allowed=(0, 1)
    )
    prefix = head + ":"
    count = 0
    for record in result.stdout.split(b"\n"):
        path_field, separator, rest = record.partition(b"\0")
        if not separator or not path_field.startswith(prefix.encode()):
            continue
        _number, separator, text = rest.partition(b"\0")
        if not separator:
            continue
        path = path_field.decode("utf-8", errors="replace")[len(prefix) :]
        tail = comment_tail(text.decode("utf-8", errors="replace"))
        if U1_PATHS.match(path) and tail and U1_IDS.search(tail):
            count += 1
    return count


def baseline_path() -> Path:
    """The committed baseline in the scanned repository, not beside the script."""
    return gitio.REPO / "scripts" / BASELINE_NAME


def ratchet(head: str, baseline_file: Path) -> int:
    """Fail above the committed U1 ceiling; a lower count asks for a lower baseline."""
    try:
        baseline = int(json.loads(baseline_file.read_text(encoding="utf-8"))["u1_comment_lines"])
    except (OSError, ValueError, KeyError, TypeError) as error:
        raise gitio.ScanError(f"cannot read the U1 baseline {baseline_file}: {error}") from error
    count = u1_comment_lines(head)
    if count > baseline:
        print(
            f"BLOCK U1 ratchet {count} > {baseline}: {count - baseline} more comment line(s) on {head} carry "
            "plan/task/review ids; keep them in the commit and the plan, not in the code"
        )
        return 1
    if count < baseline:
        print(f"check-review-blockers: U1 count fell to {count}; lower the baseline in {baseline_file.name} to {count}")
    print(f"check-review-blockers: U1 ratchet {count} <= baseline {baseline} ({head})")
    return 0
