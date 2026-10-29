"""The rules that read added lines and changed paths, and the patterns they name.

Rule ids and levels are documented in docs/engineering/agent-code-rules.md §8:
U8 (a gate disarmed with `--ignore-exit-code`), skip (a test that will not run),
ts-cast (`as unknown as`), U4 (scaffold DI, WARN), U1 (plan/task/review ids in
comments, WARN), gate-file (a change to the gate or a workflow, WARN).
"""
from __future__ import annotations

import re
from collections.abc import Iterable
from typing import NamedTuple

DOC_PATHS = re.compile(r"^(docs|plans)/|\.md$")
TEST_PATHS = re.compile(r"^tests/|\.(spec|test)\.tsx?$|/e2e/")
TS_CODE = re.compile(r"\.(ts|tsx|mts|cts)$")

GATE_FILES = re.compile(
    r"^(?:scripts/verify\.sh"
    r"|scripts/check[-_][^/]+"
    r"|scripts/review_blockers/[^/]+\.py"
    r"|scripts/tests/(?:test_)?(?:check_)?review_blockers[^/]*\.py"
    r"|\.github/workflows/[^/]+)$"
)
# The gate, its package, its fixtures and its baseline spell the forbidden
# patterns out by design, so the line rules skip them; a rule that fired there
# would make every change to the gate look like a violation of it. Edits stay
# visible to the gate-file rule, which is exactly what a reviewer must see.
LINE_EXEMPT = re.compile(
    r"^scripts/(?:check-review-blockers\.(?:py|baseline\.json)"
    r"|review_blockers/[^/]+\.py"
    r"|tests/(?:test_)?(?:check_)?review_blockers[^/]*\.py)$"
)

# U8: any mention of the flag in a code file, except inside a construct that
# refuses it — a grep/rg pattern argument, a case label, a `[ … ]`/`[[ … ]]`
# test expression or a `contains`/`includes` call. The construct itself must
# hold the flag: another command, or a trailing `# grep`, does not excuse it.
U8_FLAG = re.compile(r"--ignore-exit-code\b")
U8_REFUSALS = (
    re.compile(
        r"""(?:^|[;&|(]|\bif\s+!?\s*)\s*(?:grep|rg)\s+(?:-[\w-]+\s+)*(?:--\s+)?"""
        r"""(?:["'][^"'\n]*--ignore-exit-code|--ignore-exit-code\b)"""
    ),
    re.compile(r"""\[\[?[^\]\n]{0,1000}--ignore-exit-code[^\]\n]{0,1000}\]\]?"""),
    re.compile(
        r"""(?:^|[;|]|\bin\s)\s*(?:["']?[^|;\s)"']+["']?\s*\|\s*)*["']?--ignore-exit-code["']?(?:\s*\|\s*["']?[^|;\s)"']+["']?)*\s*\)"""
    ),
    re.compile(r"""\.(?:contains|includes|indexOf|Contains)\(\s*["'][^"'\n]*--ignore-exit-code[^"'\n]*["']\s*\)"""),
)
# Refusal patterns are matched in a bounded window around each flag occurrence,
# so a machine-generated 200 KB line costs milliseconds: an unbounded scan of
# `[`*100000 took 0.8 s per pattern.
REFUSAL_WINDOW = 1000

# A skip is a test that will not run. Attribute skips (a custom `*Fact` too),
# runtime skips and the vitest/playwright helpers are the shapes that exist
# here; `Skip = 5` on a paging option is not one of them, because only an
# attribute opens the line, or the line above it, and reaches `Skip…=`.
SKIP = re.compile(
    r"\b\w{0,40}(Fact|Theory)\w{0,40}\([^)]{0,1000}\bSkip(Unless|When)?\s*="
    r"|\bSkip\.If(Not)?\s*\(|\bAssert\.Skip(When|Unless)?\s*\("
    r"|\b(it|test|describe)\.(skip|skipIf|fixme)\b|\bx(it|describe|test)\s*\("
)
SKIP_ASSIGN = re.compile(r"\bSkip(Unless|When)?\s*=")
SKIP_ATTRIBUTE = re.compile(r"\[\w{0,40}(Fact|Theory)\w{0,40}\(")

U4_REG = re.compile(
    r"\b(Try)?Add(Singleton|Scoped|Transient|HostedService)\b[\s\S]{0,1000}?\b(InMemory|Mock|Fake|Stub|Slice|Demo|Noop)\w{0,80}"
)


class Finding(NamedTuple):
    level: str
    rule: str
    path: str
    line: int
    text: str

    def __str__(self) -> str:
        return f"{self.level} {self.rule} {self.path}:{self.line} {self.text[:200]}"


def refused(segment: str) -> bool:
    """True when each flag in this text sits inside a construct that names it.

    Every occurrence is checked, not just the first: a line that refuses the
    flag in one command and passes it to another is still a violation.
    """
    for flag in U8_FLAG.finditer(segment):
        start = max(0, flag.start() - REFUSAL_WINDOW)
        window = segment[start : flag.end() + REFUSAL_WINDOW]
        offset = flag.start() - start
        end = offset + len(flag.group(0))
        inside = any(hit.start() <= offset and end <= hit.end() for pattern in U8_REFUSALS for hit in pattern.finditer(window))
        if not inside:
            return False
    return True


def skip_hit(segment: str, above: str) -> bool:
    """True when this line skips a test, including an attribute split across lines.

    A `Skip = …` on its own line belongs to the attribute opened on the line
    above when that attribute is still unclosed; without one it is a property
    assignment such as a paging option and says nothing about a test.
    """
    if SKIP.search(segment):
        return True
    if not SKIP_ASSIGN.search(segment):
        return False
    return bool(SKIP_ATTRIBUTE.search(above)) and above.count("(") > above.count(")")


def comment_tail(text: str) -> str | None:
    """The text after the comment marker on this line, or None when it opens no comment.

    A marker inside a string literal is not a comment, and a `http://` in a
    literal is the common shape of one: a marker only opens a comment when the
    text before it holds an even number of unescaped double quotes.
    """
    quotes, i, size = 0, 0, len(text)
    while i < size:
        if text[i] == "\\":
            i += 2
            continue
        if text[i] == '"':
            quotes += 1
        elif quotes % 2 == 0 and text.startswith(("//", "/*"), i):
            return text[i + 2 :]
        i += 1
    continuation = re.match(r"\s*\*(?!/)", text)
    return text[continuation.end() :] if continuation else None


def scaffolding_use(previous: tuple[int, str] | None, line_no: int, text: str) -> bool:
    """True when this line, or this line with the one directly above, registers a test double.

    The pair is reported once, on its lower line, so a builder call whose type
    argument sits on the next line is not counted twice.
    """
    above_no, above = previous if previous else (None, "")
    window = f"{above}\n{text}" if above_no == line_no - 1 else text
    return bool(U4_REG.search(window)) and bool(U4_REG.search(text) or not U4_REG.search(above))


def line_findings(path: str, line_no: int, text: str, previous: tuple[int, str] | None) -> list[Finding]:
    """The rules that read one added line; the path decides which of them apply.

    A carriage return inside an added line does not hide a rule: a terminal
    overwrites what precedes it, so each segment is judged on its own. Every
    rule reports at most once per line.
    """
    found: list[Finding] = []
    seen: set[str] = set()

    def add(level: str, rule: str, note: str) -> None:
        if rule not in seen:
            seen.add(rule)
            found.append(Finding(level, rule, path, line_no, note))

    above = previous[1] if previous else ""
    for segment in text.split("\r"):
        if (
            not DOC_PATHS.search(path)
            and not segment.lstrip().startswith("#")
            and U8_FLAG.search(segment)
            and not refused(segment)
        ):
            add("BLOCK", "U8", segment.strip())
        if TEST_PATHS.search(path) and skip_hit(segment, above):
            add("BLOCK", "skip", segment.strip())
        if TS_CODE.search(path) and not DOC_PATHS.search(path) and "as unknown as" in segment:
            add("BLOCK", "ts-cast", segment.strip())
    if path.startswith("src/") and scaffolding_use(previous, line_no, text):
        add("WARN", "U4", text.strip() + "  (allowed only inside IsDevelopment())")
    return found


def added_line_findings(lines: Iterable[tuple[str, int, str]], u1_paths: re.Pattern[str], u1_ids: re.Pattern[str]) -> list[Finding]:
    """Every line rule over the added lines, plus one U1 nit for the change set."""
    found: list[Finding] = []
    comment_ids: list[tuple[str, int]] = []
    previous: dict[str, tuple[int, str]] = {}
    for path, line_no, text in lines:
        if LINE_EXEMPT.match(path):
            continue
        found.extend(line_findings(path, line_no, text, previous.get(path)))
        tail = comment_tail(text)
        if u1_paths.match(path) and tail and u1_ids.search(tail):
            comment_ids.append((path, line_no))
        previous[path] = (line_no, text)
    if comment_ids:  # a nit per change set, with its count, not one finding per line
        path, line_no = comment_ids[0]
        found.append(Finding("WARN", "U1", path, line_no, f"{len(comment_ids)} added comment lines carry plan/task/review ids (first shown)"))
    return found


def gate_findings(names: Iterable[str]) -> list[Finding]:
    """One WARN per changed path the gate itself owns, so a reviewer always sees it."""
    return [
        Finding("WARN", "gate-file", path, 0, "changed: allowed only if the task file names it")
        for path in names
        if GATE_FILES.match(path)
    ]
