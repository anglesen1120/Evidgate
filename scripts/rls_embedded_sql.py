"""The statements a migration runs: the file's own SQL, plus what a `do` body or `execute` runs.

`rls_policy_lint` replays policies and grants to lint them, so it must see a statement written as
text inside `do $body$ … $body$` or `execute '…'` — PostgreSQL runs those too. This module yields
every statement in execution order, with dollar-quoted bodies returned beside their statement so
the definer lint still reads function bodies.

What cannot be read from the file comes back as a note instead of silence: an `execute` of a
variable, a `format(…)` template that builds DDL from its arguments. A lint that skipped those
would report a clean tree it never saw, so the caller turns each note into a finding.
"""
from __future__ import annotations

import re
from dataclasses import dataclass

from review_blockers.sql import quoted_end

DOLLAR = re.compile(r"\$(\w*)\$")
# The DDL the replay judges, and the one keyword PL/pgSQL runs SQL with. Both are matched at code
# positions only (`blank`): a `grant` inside a literal or an identifier is data, not a statement.
KEYWORD = re.compile(
    r"(?<![\w.\"$])(?:create\s+table|alter\s+table|create\s+policy|alter\s+policy|drop\s+policy"
    r"|alter\s+default\s+privileges|grant|revoke|execute)\b",
    re.I,
)
# A `do` body inside a `do` body is already unusual; beyond this depth the lint says so instead of
# recursing without a bound.
MAX_DEPTH = 3
ESCAPES = {"n": "\n", "t": "\t", "r": "\r"}


@dataclass(frozen=True)
class Statement:
    """One statement PostgreSQL runs: `body` is its dollar-quoted body, `embedded` marks the ones
    reached through a `do` body or an `execute` string rather than written at the top level."""

    text: str
    body: str = ""
    embedded: bool = False


def statements(sql: str) -> list[tuple[str, str]]:
    """Split comment-free SQL on `;` outside dollar-quoted bodies and single-quoted strings.

    Returns (statement, dollar bodies joined): each body is replaced by `$$` in the statement text,
    so a function body cannot end its statement, and kept aside for the caller. Generic dollar
    tags (`$fn$`, `$body$`) are honoured, so a body cannot hide behind its tag.
    """
    out: list[tuple[str, str]] = []
    head: list[str] = []
    bodies: list[str] = []
    i, n = 0, len(sql)
    while i < n:
        char = sql[i]
        if char == "'":
            j = i + 1
            while j < n:
                if sql[j] == "'" and sql[j + 1 : j + 2] == "'":
                    j += 2
                elif sql[j] == "'":
                    break
                else:
                    j += 1
            head.append(sql[i : j + 1])
            i = j + 1
        elif char == "$" and (tag := DOLLAR.match(sql, i)):
            end = sql.find(tag.group(0), tag.end())
            if end < 0:
                raise SystemExit(f"unterminated dollar quote {tag.group(0)}")
            bodies.append(sql[tag.end() : end])
            head.append("$$")
            i = end + len(tag.group(0))
        elif char == ";":
            out.append((" ".join("".join(head).split()), "\n".join(bodies)))
            head, bodies = [], []
            i += 1
        else:
            head.append(char)
            i += 1
    tail = " ".join("".join(head).split())
    if tail:
        out.append((tail, "\n".join(bodies)))
    return out


def blank(text: str) -> str:
    """`text` with quoted literals and identifiers replaced by spaces, at unchanged offsets.

    `KEYWORD` is searched on this copy, so a keyword that only appears inside a literal or a quoted
    identifier is not read as code; the offsets still address the original text."""
    out = list(text)
    i, n = 0, len(text)
    while i < n:
        char = text[i]
        if char in "'\"":
            escape = char == "'" and _escape_prefixed(text, i)
            end = quoted_end(text, i, char, escape)
            out[i:end] = " " * (end - i)
            i = end
        else:
            i += 1
    return "".join(out)


def execution_order(sql: str) -> tuple[list[Statement], list[str]]:
    """Every statement `sql` runs, in the order it runs them, and a note per statement not read."""
    ordered: list[Statement] = []
    notes: list[str] = []
    _walk(sql, 0, ordered, notes)
    return ordered, notes


def _walk(sql: str, depth: int, ordered: list[Statement], notes: list[str]) -> None:
    for text, body in statements(sql):
        if depth > 0:
            _fragment(text, body, depth, ordered, notes)
            continue
        ordered.append(Statement(text, body))
        if body and re.match(r"^do\b", text, re.I):
            _do(body, depth, ordered, notes)


def _do(body: str, depth: int, ordered: list[Statement], notes: list[str]) -> None:
    if depth + 1 > MAX_DEPTH:
        notes.append("a `do` body nested past the replay depth is not read")
        return
    _walk(body, depth + 1, ordered, notes)


def _fragment(
    text: str, body: str, depth: int, ordered: list[Statement], notes: list[str]
) -> None:
    """One PL/pgSQL fragment of a `do` body: the DDL it writes, and the SQL its `execute` runs.

    PL/pgSQL wraps a statement in its own keywords (`begin`, `then`, `if`), so a statement is found
    where it starts: the fragment is `;`-split already, so the text from there to the end is the
    statement PostgreSQL runs. `body` is the fragment's dollar-quoted text, which an `execute`
    written with `$tag$…$tag$` runs as its SQL."""
    for match in KEYWORD.finditer(blank(text)):
        if match.group(0).lower() == "execute":
            inner = _executed(text, body, match.end(), notes)
            if inner is not None:
                _walk(inner, depth, ordered, notes)
        else:
            ordered.append(Statement(text[match.start() :].strip(), "", True))


def _executed(text: str, body: str, start: int, notes: list[str]) -> str | None:
    """The SQL text an `execute` at `start` runs, or None when the file does not spell it out.

    A `format(…)` call is reported when its template shows DDL: the arguments it substitutes can
    change the table or the role, so replaying the template would judge a statement that never
    runs, and trusting the call would hide one that does."""
    i = _space(text, start)
    after = _space(text, i + 6)
    if text[i : i + 6].lower() == "format" and text[after : after + 1] == "(":
        template = _literal(text, _space(text, after + 1))
        if template is None:
            notes.append("`execute format(…)` with a template the replay cannot read")
            return None
        if KEYWORD.search(blank(template)):
            notes.append(
                f"`execute format(…)` builds SQL from its arguments ({_snippet(template)}); "
                "write that DDL as a literal so the replay can read it"
            )
        return None
    if text[i : i + 2] == "$$" and body:
        return body  # a `$tag$…$tag$` argument, split out with the dollar-quoted body
    sql = _literal(text, i)
    if sql is None:
        notes.append(
            f"`execute` of an expression the replay cannot read ({_snippet(text[start:])}); "
            "write the SQL as a literal or a dollar-quoted string"
        )
    return sql


def _literal(text: str, i: int) -> str | None:
    """The value of the string literal at `i` — `'…'`, `E'…'` or `$tag$…$tag$` — or None."""
    if i < len(text) and text[i] in "Ee" and text[i + 1 : i + 2] == "'":
        i += 1  # the escaped form; `_escape_prefixed` then reads the `E`
    if i >= len(text):
        return None
    if text[i] == "$":
        tag = DOLLAR.match(text, i)
        end = text.find(tag.group(0), tag.end()) if tag else -1
        return text[tag.end() : end] if tag and end >= 0 else None
    if text[i] != "'":
        return None
    escape = _escape_prefixed(text, i)
    end = quoted_end(text, i, "'", escape)
    if text[end - 1 : end] != "'" or end - 1 <= i:
        return None
    raw = text[i + 1 : end - 1]
    if escape:
        raw = _unescape(raw)
    return raw.replace("''", "'")


def _unescape(raw: str) -> str:
    out: list[str] = []
    i = 0
    while i < len(raw):
        if raw[i] == "\\" and i + 1 < len(raw):
            out.append(ESCAPES.get(raw[i + 1], raw[i + 1]))
            i += 2
        else:
            out.append(raw[i])
            i += 1
    return "".join(out)


def _escape_prefixed(text: str, i: int) -> bool:
    """True when the quote at `i` opens an `E'…'` literal, the only form where `\\` escapes."""
    return i > 0 and text[i - 1] in "Ee" and (i < 2 or not (text[i - 2].isalnum() or text[i - 2] == "_"))


def _space(text: str, i: int) -> int:
    while i < len(text) and text[i].isspace():
        i += 1
    return i


def _snippet(text: str) -> str:
    return " ".join(text.split())[:60]
