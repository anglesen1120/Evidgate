"""What a policy's predicate says about the tenant, read from the policy text.

`evidgate_operator` may `set evidgate.org_id` itself, so a policy confining the operator by
comparing `org_id` to that setting hands it every tenant: the role chooses the rows it reads
instead of the policy choosing them. Such a policy is not a gate, and naming the operator in its
`TO` clause does not make it one. `confines_tenant` answers that one question:

  no `USING` clause            PostgreSQL applies `true`, so nothing confines the rows;
  `using (true)`               the same, written out;
  a session-read predicate     `current_setting('evidgate.org_id')`, `evidgate.current_org_id()`
                               and the `project` sibling read a custom GUC any role may set;
  no tenant comparison         a predicate that never compares `org_id` (`using (org_id is not
                               null)`, `using (current_setting('role') = 'x')`) separates no
                               tenant: it matches rows of every tenant or none;
  a tautology                  `using (org_id = org_id)` compares the column with itself, and an
                               `or` arm that is simply true (`… or 1 = 1`) admits every row, so
                               neither confines anything.

Anything else counts as a gate, which is the limit of a text lint rather than a proof: a predicate
that compares `org_id` to a fixed value, a subquery or a function call is accepted, because
deciding whether that grant table is operator-writable, that function checks a scope, or that
constant names one tenant needs the database, not the file. A predicate a reviewer judges safe but
this module rejects is named in `OPERATOR_GATE_EXEMPT`.
"""
from __future__ import annotations

import re

from review_blockers.sql import quoted_end
from rls_names import code_copy

WORD = re.compile(r"[A-Za-z_]\w*")
OPEN = re.compile(r"(?:true|1\s*=\s*1)", re.I)
SESSION = re.compile(
    r"(?<![\w.])(?:\w+\.)*current_setting\s*\("
    r"|(?<![\w.])(?:\w+\.)*current_org_ids?\b"
    r"|(?<![\w.])(?:\w+\.)*current_project_ids?\b",
    re.I,
)
# The tenant column, bare or table-qualified (`org_id`, `widget.org_id`).
COLUMN = re.compile(r"(?:\w+\.)*org_id", re.I)
CAST = re.compile(r"(?:\s*::\s*\w+(?:\s*\(\s*\d+\s*\))?)+")
COMPARE = re.compile(
    r"(?<![\w<>=!])(?:=(?!=)|<>|!=|is\s+(?:not\s+)?distinct\s+from|not\s+in\b|\bin\b)",
    re.I,
)


def confines_tenant(text: str) -> bool:
    """True when a policy's `USING` clause confines its rows by something the session cannot set."""
    body = using_body(text)
    if body is None or _open(body) or _session_tenant(body):
        return False
    return _confines(code_copy(body, "0"))


def using_body(text: str) -> str | None:
    """The expression inside a policy's `USING (…)`, or None when it has no `USING` clause.

    Only the top level is read: a quoted identifier or literal is skipped whole and a parenthesised
    expression is stepped over, so a `using` inside one opens nothing."""
    i, n = 0, len(text)
    while i < n:
        char = text[i]
        if char in "'\"":
            i = quoted_end(text, i, char, False)
        elif char == "(":
            i = _past_parens(text, i)
        elif (match := WORD.match(text, i)) and (i == 0 or text[i - 1] not in '_."' and not text[i - 1].isalnum()):
            if match.group(0).lower() == "using":
                start = _space(text, match.end())
                if text[start : start + 1] != "(":
                    return None
                end = _past_parens(text, start)
                return text[start + 1 : end - 1]
            i = match.end()
        else:
            i += 1
    return None


def _session_tenant(body: str) -> bool:
    """True when the predicate reads the session at all, so the session can choose the rows.

    Read on the lexical copy: a `current_setting` inside a literal is data, while a quoted
    identifier names the same function as its bare spelling, so `"current_setting"(…)` still
    counts. The setting name is not inspected — any `current_setting(...)` in a `USING` clause is
    either the tenant itself or a session condition, and neither confines rows to a tenant."""
    return bool(SESSION.search(code_copy(body)))


def _confines(expr: str) -> bool:
    """True when the boolean `expr` confines the tenant.

    A disjunction confines only when every arm does — `org_id = x or true` admits every row — and
    a conjunction confines as soon as one conjunct does, since `org_id = x and id is not null`
    still picks one tenant. Wrapping parentheses come off before the split, so a grouped
    `(org_id = org_id or true)` is decomposed here instead of being read as one comparison whose
    right side happens to hold `or true`."""
    expr = _unparen(expr)
    arms = [arm for arm in _split(expr, "or") if arm.strip()]
    if len(arms) > 1:
        return all(_confines(arm) for arm in arms)
    conjuncts = [part for part in _split(expr, "and") if part.strip()]
    if len(conjuncts) > 1:
        return any(_confines(part) for part in conjuncts)
    return _tenant_comparison(expr)


def _tenant_comparison(term: str) -> bool:
    """True when `term` compares the tenant column with something other than itself."""
    parts = _compare(term)
    if parts is None:
        return False
    left, _operator, right = parts
    left, right = _unparen(left), _unparen(right)
    return (_column(left) and not _column(right)) or (_column(right) and not _column(left))


def _compare(term: str) -> tuple[str, str, str] | None:
    """`term` as (left, operator, right) around its top-level comparison, or None."""
    depth = 0
    for i, char in enumerate(term):
        if char == "(":
            depth += 1
        elif char == ")":
            depth = max(0, depth - 1)
        elif depth == 0 and (match := COMPARE.match(term, i)):
            return term[:i].strip(), match.group(0), term[match.end() :].strip()
    return None


def _column(side: str) -> bool:
    """True when `side` is the tenant column itself, bare, qualified or cast."""
    return bool(COLUMN.fullmatch(CAST.sub("", side).strip()))


def _unparen(term: str) -> str:
    """`term` without the parentheses that wrap the whole expression."""
    term = term.strip()
    while term.startswith("(") and _past_parens(term, 0) == len(term):
        term = term[1:-1].strip()
    return term


def _split(text: str, keyword: str) -> list[str]:
    """`text` split on the top-level `keyword` (`and`, `or`), stepping over nested parentheses."""
    parts: list[str] = []
    depth, start, i, n = 0, 0, 0, len(text)
    while i < n:
        char = text[i]
        if char == "(":
            depth += 1
        elif char == ")":
            depth = max(0, depth - 1)
        elif depth == 0 and char.isalpha() and (i == 0 or not (text[i - 1].isalnum() or text[i - 1] == "_")):
            match = WORD.match(text, i)
            if match and match.group(0).lower() == keyword:
                parts.append(text[start:i])
                start = i = match.end()
                continue
            i = match.end() if match else i + 1
            continue
        i += 1
    parts.append(text[start:])
    return parts


def _open(body: str) -> bool:
    return bool(OPEN.fullmatch(body.strip()))


def _past_parens(text: str, start: int) -> int:
    """Index just past the parenthesis matching the one at `start` (or the end of the text)."""
    depth, i, n = 0, start, len(text)
    while i < n:
        char = text[i]
        if char in "'\"":
            i = quoted_end(text, i, char, False)
            continue
        if char == "(":
            depth += 1
        elif char == ")":
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    return n


def _space(text: str, i: int) -> int:
    while i < len(text) and text[i].isspace():
        i += 1
    return i
