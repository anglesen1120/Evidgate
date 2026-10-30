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
                               tenant: it matches rows of every tenant or none.

Anything else counts as a gate, which is the limit of a text lint rather than a proof: a predicate
that compares `org_id` to a fixed value, a subquery or a function call is accepted, because
deciding whether that grant table is operator-writable, that function checks a scope, or that
constant names one tenant needs the database, not the file. A predicate a reviewer judges safe but
this module rejects is named in `OPERATOR_GATE_EXEMPT`.
"""
from __future__ import annotations

import re

from review_blockers.sql import quoted_end
from rls_embedded_sql import blank

WORD = re.compile(r"[A-Za-z_]\w*")
OPEN = re.compile(r"(?:true|1\s*=\s*1)", re.I)
SESSION = re.compile(
    r"(?<![\w.])(?:\w+\.)*current_setting\s*\("
    r"|(?<![\w.])(?:\w+\.)*current_org_ids?\b"
    r"|(?<![\w.])(?:\w+\.)*current_project_ids?\b",
    re.I,
)
TENANCY = re.compile(
    r"(?<![\w.])org_id\s*(?:::\s*\w+\s*)?"
    r"(?:=(?!=)|<>|!=|\bin\b|\bnot\s+in\b|\bis\s+(?:not\s+)?distinct\s+from\b)"
    r"|(?:=(?!=)|<>|!=)\s*org_id(?![\w])",
    re.I,
)


def confines_tenant(text: str) -> bool:
    """True when a policy's `USING` clause confines its rows by something the session cannot set."""
    body = using_body(text)
    return body is not None and not _open(body) and not _session_tenant(body) and _compares_tenant(body)


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

    Read on the masked copy: a `current_setting` inside a literal or a quoted identifier is data.
    The setting name is not inspected — any `current_setting(...)` in a `USING` clause is either
    the tenant itself or a session condition, and neither confines rows to a tenant."""
    return bool(SESSION.search(blank(body)))


def _compares_tenant(body: str) -> bool:
    """True when the predicate compares the tenant column, so it can separate tenants at all."""
    return bool(TENANCY.search(blank(body)))


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
