#!/usr/bin/env python3
"""Review-blocker gate: the REVIEW.md "Blocking" patterns a grep can see.

  scripts/check-review-blockers.py <base> <head>     scan only the lines base...head adds
  scripts/check-review-blockers.py --u1-ratchet [<head>] [--baseline <file>]
  scripts/check-review-blockers.py --u1-count [<head>]

The diff scan reads `git diff base...head`, so debt already on the base never
fails a change set; it also reads the whole head file of every migration the
change set touches, because a policy's missing `TO` clause is a property of the
statement, not of a line. The U1 half is a ratchet instead: plan, task and
review ids in source comments predate this gate, so the committed baseline is
the ceiling and the count may only fall (`--u1-count` prints it for an update).

Prints `BLOCK <rule> <file>:<line> <text>` and `WARN ...`; rule ids are in
docs/engineering/agent-code-rules.md. With no base argument the scan reads the
first of origin/develop, develop, origin/main, main that resolves. Exit 1 on any
BLOCK (or over the U1 baseline), 0 otherwise, 2 when the scan cannot run — a
shallow clone or a missing ref is a failed hard check, never a clean one.

On a pull request CI runs the BASE revision's copy of this file and of its
baseline, materialised with `git show`, so the rules and the ceiling a change
set is judged by are the ones on its base: a pull request can edit a rule or
raise the number in its own copy and the trusted run still uses the base's. The
first pull request to carry the gate has no base copy yet, falls back to its own
and says so. The coordinator's merge check does the same with `origin/develop`.
"""
from __future__ import annotations

import json
import re
import subprocess
import sys
from collections.abc import Iterable, Iterator
from pathlib import Path
from typing import NamedTuple

GATE_FILES = re.compile(r"^(scripts/verify\.sh|scripts/check[-_][^/]+|\.github/workflows/[^/]+)$")
DOC_PATHS = re.compile(r"^(docs|plans)/|\.md$")
TEST_PATHS = re.compile(r"^tests/|\.(spec|test)\.tsx?$|/e2e/")
TS_CODE = re.compile(r"\.(ts|tsx|mts|cts)$")
U1_PATHS = re.compile(r"^(src/.*\.cs|apps/web/src/.*\.ts|packages/[^/]+/src/.*\.ts)$")
U1_TREE_PATHS = ("src", "apps/web/src", "packages/*/src")

# The gate, its fixtures and its baseline spell the forbidden patterns out by
# design, so the line rules skip them; a rule that fired there would make every
# change to the gate look like a violation of it. Edits stay visible to the
# gate-file rule, which is exactly what a reviewer must see.
LINE_EXEMPT = frozenset(
    {
        "scripts/check-review-blockers.baseline.json",
        "scripts/check-review-blockers.py",
        "scripts/tests/test_check_review_blockers.py",
    }
)

# U8: any mention of the flag in a code file, except on a line that tests for
# it — a grep, a case arm, a `[[ ]]` match, an `in <name>:` case list or a
# `contains`/`includes` call.
U8_FLAG = re.compile(r"--ignore-exit-code\b")
U8_REFUSAL = re.compile(r"\b(grep|rg|case)\b|\[\[|\bin\s+[\w.\[\]'\"]+\s*:|\.(contains|includes|indexOf|Contains)\(")

# A skip is a test that will not run. Attribute skips (a custom `*Fact` too),
# runtime skips and the vitest/playwright helpers are the shapes that exist
# here; `Skip = 5` on a paging option is not one of them, because only an
# attribute opening the line reaches `Skip…=`.
SKIP = re.compile(
    r"\[\w*(Fact|Theory)\w*\([^\]]*\bSkip(Unless|When)?\s*="
    r"|\bSkip\.If(Not)?\s*\(|\bAssert\.Skip(When|Unless)?\s*\("
    r"|\b(it|test|describe)\.(skip|skipIf|fixme)\b|\bx(it|describe|test)\s*\("
)

U1_IDS = re.compile(r"\b\d{2}\.\d+\.\d+-T\d+|\bCR-\d{2}\b|\bD-S\d+")
# Cheap superset for `git grep`; the comment check below decides, so a hit the
# Python regex rejects (an id inside a string, say) never reaches the count.
U1_GREP = r"([0-9]{2}\.[0-9]+\.[0-9]+-T[0-9]+|CR-[0-9]{2}|D-S[0-9]+)"
COMMENT = re.compile(r"(//|/\*|^\s*\*)(.*)$")
U4_REG = re.compile(
    r"\b(Try)?Add(Singleton|Scoped|Transient|HostedService)\b[\s\S]*\b(InMemory|Mock|Fake|Stub|Slice|Demo|Noop)\w*"
)
POLICY_ROLES = re.compile(r"\bto\s+(.*?)(?=\busing\b|\bwith\s+check\b|$)", re.I | re.S)
DOLLAR = re.compile(r"\$\w*\$")
HUNK = re.compile(r"@@ -\d+(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")
ZERO_REF = re.compile(r"^0+$")
# The base a change set is read against when the caller names none, in the
# order the other change-set stages try them: a clean clone has `develop` and
# no remote, a full checkout has the remote-tracking ref.
BASE_REFS = ("origin/develop", "develop", "origin/main", "main")

REPO = Path.cwd()


class Finding(NamedTuple):
    level: str
    rule: str
    path: str
    line: int
    text: str

    def __str__(self) -> str:
        return f"{self.level} {self.rule} {self.path}:{self.line} {self.text[:200]}"


class ScanError(Exception):
    """The scan could not run: the caller must treat it as a failed hard check."""


def git(*args: str) -> str:
    result = subprocess.run(
        ["git", "-C", str(REPO), *args],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    if result.returncode != 0:
        raise ScanError(f"git {' '.join(args)}: {(result.stderr or result.stdout).strip()}")
    return result.stdout


def resolves(ref: str) -> bool:
    result = subprocess.run(
        ["git", "-C", str(REPO), "rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}"],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    return result.returncode == 0


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


def added_lines(base: str, head: str) -> Iterator[tuple[str, int, str]]:
    """Yield (path, head line number, text) for every added line of base...head.

    The hunk counters, not the prefixes, decide what a line is: `-U0` emits no
    context, and an added line whose own text starts with `++` arrives as
    `+++ …`, which a header-only parser would read as the next file.
    """
    out = git(
        "diff", "-U0", "--no-color", "--no-ext-diff", "--src-prefix=a/", "--dst-prefix=b/", f"{base}...{head}"
    )
    path: str | None = None
    line_no = old_left = new_left = 0
    for raw in out.splitlines():
        if old_left <= 0 and new_left <= 0:
            if raw.startswith("+++ "):
                path = None if raw == "+++ /dev/null" else raw[6:]
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


def sql_statements(text: str) -> list[tuple[int, str]]:
    """Split SQL into (first line, statement), dropping comments and literal bodies.

    A `;` inside a string or a dollar-quoted body does not end a statement and a
    `TO public` inside one is data, not a role clause; both are removed here so
    the statement text the policy rule reads is DDL only.
    """
    statements: list[tuple[int, str]] = []
    buffer: list[str] = []
    start, started, line, i, size = 1, False, 1, 0, len(text)
    while i < size:
        char = text[i]
        if char == "\n":
            line += 1
        if text.startswith("--", i):
            end = text.find("\n", i)
            i = size if end < 0 else end
            continue
        if text.startswith("/*", i):
            end = text.find("*/", i + 2)
            end = size if end < 0 else end + 2
            line += text.count("\n", i, end)
            i = end
            buffer.append(" ")
            continue
        quote = DOLLAR.match(text, i) if char == "$" else None
        if char == "'" or quote:
            tag = "'" if char == "'" else quote.group(0)
            end = text.find(tag, i + len(tag))
            end = size if end < 0 else end + len(tag)
            line += text.count("\n", i, end)
            buffer.append(" '' ")
            i = end
            continue
        if char == ";":
            statements.append((start, "".join(buffer)))
            buffer, started = [], False
        else:
            if not started and not char.isspace():
                start, started = line, True
            buffer.append(char)
        i += 1
    if started:
        statements.append((start, "".join(buffer)))
    return statements


def without_parens(statement: str) -> str:
    """Drop parenthesised bodies so a `to` inside an expression is not read as a role."""
    previous = None
    while previous != statement:
        previous, statement = statement, re.sub(r"\([^()]*\)", " ", statement)
    return statement


def policy_roles_ok(statement: str) -> bool:
    """True when the policy names its roles and none of them is PUBLIC.

    Without a `TO` clause PostgreSQL applies the policy to every role, and
    `TO public` says the same thing out loud; both are the same violation.
    """
    match = POLICY_ROLES.search(without_parens(statement))
    roles = [role.strip().strip('"').lower() for role in match.group(1).split(",")] if match else []
    return bool(roles) and all(role and role != "public" for role in roles)


def policies_without_roles(head: str, path: str) -> Iterator[tuple[int, str]]:
    for line, statement in sql_statements(git("show", f"{head}:{path}")):
        if re.match(r"\s*create\s+policy\b", statement, re.I) and not policy_roles_ok(statement):
            yield line, " ".join(statement.split())[:120]


def scaffolding_use(previous: tuple[int, str] | None, line_no: int, text: str) -> bool:
    """True when this line, or this line with the one directly above, registers a test double.

    The pair is reported once, on its lower line, so a builder call whose type
    argument sits on the next line is not counted twice.
    """
    above_no, above = previous if previous else (None, "")
    window = f"{above}\n{text}" if above_no == line_no - 1 else text
    return bool(U4_REG.search(window)) and bool(U4_REG.search(text) or not U4_REG.search(above))


def line_findings(path: str, line_no: int, text: str, previous: tuple[int, str] | None) -> list[Finding]:
    """The rules that read one added line; the path decides which of them apply."""
    found: list[Finding] = []
    if (
        not DOC_PATHS.search(path)
        and not text.lstrip().startswith("#")
        and U8_FLAG.search(text)
        and not U8_REFUSAL.search(text)
    ):
        found.append(Finding("BLOCK", "U8", path, line_no, text.strip()))
    if TEST_PATHS.search(path) and SKIP.search(text):
        found.append(Finding("BLOCK", "skip", path, line_no, text.strip()))
    if TS_CODE.search(path) and not DOC_PATHS.search(path) and "as unknown as" in text:
        found.append(Finding("BLOCK", "ts-cast", path, line_no, text.strip()))
    if path.startswith("src/") and scaffolding_use(previous, line_no, text):
        found.append(Finding("WARN", "U4", path, line_no, text.strip() + "  (allowed only inside IsDevelopment())"))
    return found


def added_line_findings(lines: Iterable[tuple[str, int, str]]) -> list[Finding]:
    found: list[Finding] = []
    comment_ids: list[tuple[str, int]] = []
    previous: dict[str, tuple[int, str]] = {}
    for path, line_no, text in lines:
        if path in LINE_EXEMPT:
            continue
        found.extend(line_findings(path, line_no, text, previous.get(path)))
        comment = COMMENT.search(text)
        if U1_PATHS.match(path) and comment and U1_IDS.search(comment.group(2)):
            comment_ids.append((path, line_no))
        previous[path] = (line_no, text)
    if comment_ids:  # a nit per change set, with its count, not one finding per line
        path, line_no = comment_ids[0]
        found.append(Finding("WARN", "U1", path, line_no, f"{len(comment_ids)} added comment lines carry plan/task/review ids (first shown)"))
    return found


def scan(base: str, head: str) -> list[Finding]:
    """Every finding of base...head: the added-line rules plus the migration policy rule."""
    found: list[Finding] = []
    names = [path for path in git("diff", "--name-only", "-z", f"{base}...{head}").split("\0") if path]
    in_head = {path for path in git("ls-tree", "-r", "-z", "--name-only", head).split("\0") if path}
    for path in names:
        if GATE_FILES.match(path):
            found.append(Finding("WARN", "gate-file", path, 0, "changed: allowed only if the task file names it"))
        if path.startswith("migrations/") and path.endswith(".sql") and path in in_head:
            for line_no, statement in policies_without_roles(head, path):
                found.append(Finding("BLOCK", "D1", path, line_no, statement))
    return found + added_line_findings(added_lines(base, head))


def u1_comment_lines(head: str) -> int:
    """Count the comment lines in the head tree that carry plan/task/review ids."""
    result = subprocess.run(
        ["git", "-C", str(REPO), "grep", "-I", "-n", "-E", "--no-color", "-e", U1_GREP, head, "--", *U1_TREE_PATHS],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    if result.returncode not in (0, 1):  # 1 is "no hit", which is a count of zero
        raise ScanError(f"git grep: {(result.stderr or result.stdout).strip() or 'failed'}")
    prefix = f"{head}:"
    count = 0
    for row in result.stdout.splitlines():
        if not row.startswith(prefix):
            continue
        path, _, text = row[len(prefix) :].split(":", 2)
        comment = COMMENT.search(text)
        if U1_PATHS.match(path) and comment and U1_IDS.search(comment.group(2)):
            count += 1
    return count


def baseline_path() -> Path:
    return REPO / "scripts" / "check-review-blockers.baseline.json"


def ratchet(head: str, baseline_file: Path) -> int:
    """Fail above the committed U1 ceiling; a lower count asks for a lower baseline."""
    try:
        baseline = int(json.loads(baseline_file.read_text(encoding="utf-8"))["u1_comment_lines"])
    except (OSError, ValueError, KeyError, TypeError) as error:
        raise ScanError(f"cannot read the U1 baseline {baseline_file}: {error}") from error
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
        raise ScanError(f"{name} needs a file path")
    return argv[:index] + argv[index + 2 :], argv[index + 1]


def main(argv: list[str]) -> int:
    try:
        argv, baseline = take_option(argv, "--baseline")
        if argv[:1] == ["--u1-ratchet"]:
            head = resolve(argv[1] if len(argv) > 1 else "HEAD", "head")
            return ratchet(head, Path(baseline) if baseline else baseline_path())
        if argv[:1] == ["--u1-count"]:
            print(u1_comment_lines(resolve(argv[1] if len(argv) > 1 else "HEAD", "head")))
            return 0
        if len(argv) != 2:
            print(__doc__, file=sys.stderr)
            return 2
        base_ref, head_ref = argv
        base_ref = effective_base(base_ref)
        found = scan(resolve(base_ref, "base"), resolve(head_ref, "head"))
    except ScanError as error:
        print(f"check-review-blockers: scan did not run: {error}", file=sys.stderr)
        return 2
    for finding in found:
        print(finding)
    blocks = sum(1 for finding in found if finding.level == "BLOCK")
    print(f"check-review-blockers: {blocks} block, {len(found) - blocks} warn ({base_ref}...{head_ref})")
    return 1 if blocks else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
