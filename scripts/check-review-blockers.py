#!/usr/bin/env python3
"""Review-blocker gate: the REVIEW.md "Blocking" patterns a grep can see.

  scripts/check-review-blockers.py <base> <head>     scan only the lines base...head adds
  scripts/check-review-blockers.py --u1-ratchet [<head>] [--baseline <file>]
  scripts/check-review-blockers.py --u1-count [<head>]

This file is the entry point only: argv, exit codes and the report. The rules
live in `review_blockers/` beside it — `gitio` (git transport and diff parsing),
`sql` (D1's policy statements), `rules` (added-line patterns), `ratchet` (U1)
and `scan` (one change set) — which is imported from the script's own directory
so an extracted copy runs anywhere.

The diff scan reads `git diff base...head`, so debt already on the base never
fails a change set; it also reads the whole head file of every migration the
change set touches, because a policy's missing `TO` clause is a property of the
statement, not of a line. Dynamic SQL inside DO/dollar-quoted bodies (including
EXECUTE 'create policy …') is not inspected: interpreting procedural SQL would
require executing or parsing another language. The U1 half is a ratchet instead:
plan, task and review ids in source comments predate this gate, so the committed
baseline is the ceiling and the count may only fall (`--u1-count` prints it).

Prints `BLOCK <rule> <file>:<line> <text>` and `WARN ...`; rule ids are in
docs/engineering/agent-code-rules.md. With no base argument the scan reads the
first of origin/develop, develop, origin/main, main that resolves. Exit 1 on any
BLOCK (or over the U1 baseline), 0 otherwise, 2 when the scan cannot run — a
shallow clone or a missing ref is a failed hard check, never a clean one.

On a pull request CI runs the BASE revision's copy of this file, its package and
its baseline, materialised with `git archive`, so the rules and the ceiling a
change set is judged by are the ones on its base: a pull request can edit a rule
or raise the number in its own copy and the trusted run still uses the base's.
The first pull request to carry the gate has no base copy yet, falls back to its
own and says so.
"""
from __future__ import annotations

import sys
from pathlib import Path
from typing import NamedTuple

sys.path.insert(0, str(Path(__file__).resolve().parent))

from review_blockers import gitio, ratchet, scan  # noqa: E402  (the path insert must come first)


def take_option(argv: list[str], name: str) -> tuple[list[str], str | None]:
    """Remove `name <value>` from argv and return the value.

    CI materialises the base revision's baseline and points the ratchet at that
    copy, so the ceiling a pull request is judged by is the one on its base and
    not the number the pull request itself brought.
    """
    if name not in argv:
        return argv, None
    index = argv.index(name)
    if index + 1 >= len(argv):
        raise gitio.ScanError(f"{name} needs a file path")
    return argv[:index] + argv[index + 2 :], argv[index + 1]


class Report(NamedTuple):
    """What one diff scan produced, so the summary can name the commits it read."""

    findings: list
    scanned: int
    base_ref: str
    base: str
    head_ref: str
    head: str


def report(argv: list[str]) -> Report:
    """Run one diff scan: its findings, the added-line count and the resolved commits."""
    base_ref, head_ref = argv
    base_ref = gitio.effective_base(base_ref)
    base, head = gitio.resolve(base_ref, "base"), gitio.resolve(head_ref, "head")
    found, scanned = scan.scan_change_set(base, head)
    return Report(found, scanned, base_ref, base, head_ref, head)


def main(argv: list[str]) -> int:
    try:
        gitio.enter_repository()
        argv, baseline = take_option(argv, "--baseline")
        if argv[:1] == ["--u1-ratchet"]:
            head = gitio.resolve(argv[1] if len(argv) > 1 else "HEAD", "head")
            return ratchet.ratchet(head, Path(baseline) if baseline else ratchet.baseline_path())
        if argv[:1] == ["--u1-count"]:
            print(ratchet.u1_comment_lines(gitio.resolve(argv[1] if len(argv) > 1 else "HEAD", "head")))
            return 0
        if len(argv) != 2:
            print(__doc__, file=sys.stderr)
            return 2
        scanned = report(argv)
    except gitio.ScanError as error:
        print(f"check-review-blockers: scan did not run: {error}", file=sys.stderr)
        return 2
    except Exception as error:  # a crash must fail the gate, never pass it
        print(f"check-review-blockers: scan crashed: {type(error).__name__}: {error}", file=sys.stderr)
        return 2
    for finding in scanned.findings:
        print(finding)
    blocks = sum(1 for finding in scanned.findings if finding.level == "BLOCK")
    print(
        f"check-review-blockers: {blocks} block, {len(scanned.findings) - blocks} warn, {scanned.scanned} added line(s) "
        f"({scanned.base_ref} {gitio.short(scanned.base)}...{scanned.head_ref} {gitio.short(scanned.head)})"
    )
    return 1 if blocks else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
