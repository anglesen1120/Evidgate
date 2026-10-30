"""Names and findings shared by the RLS lint: identifiers, dotted qualification, a found problem.

Split out of `rls_policy_lint` to keep each module under the 500-line budget that script lives
under, since the replay and the policy rules together reached it. The helpers here are lexical
only: they normalise PostgreSQL identifiers, separate comments from data, and print a finding, and
they hold no rule.
"""
from __future__ import annotations

import re
from dataclasses import dataclass

from review_blockers.sql import quoted_end
from rls_embedded_sql import DOLLAR, _escape_prefixed

NAME = r'"(?:[^"]|"")*"|\S+'
TABLE_NAME = r'[\w."]+'
WORD = re.compile(r"[A-Za-z_]\w*")


@dataclass(frozen=True)
class Finding:
    file: str
    subject: str
    rule: str
    message: str

    def __str__(self) -> str:
        return f"{self.file}: {self.subject}: {self.rule}: {self.message}"


def strip_comments(sql: str) -> str:
    """`sql` with its comments removed and every other span kept verbatim.

    PostgreSQL ends a comment only at a token boundary, so the sweep walks the text: a `--` or
    `/*` inside a literal or a quoted identifier is data, and a block comment nests. A dollar-quoted
    body keeps its tag and is swept in turn — its own `--` comments the SQL or PL/pgSQL inside it,
    which the replay reads as statements next."""
    out: list[str] = []
    i, n = 0, len(sql)
    while i < n:
        char = sql[i]
        if sql.startswith("--", i):
            end = sql.find("\n", i)
            out.append(" ")
            i = n if end < 0 else end
        elif sql.startswith("/*", i):
            out.append(" ")
            i = _past_block(sql, i)
        elif char in "'\"":
            end = quoted_end(sql, i, char, char == "'" and _escape_prefixed(sql, i))
            out.append(sql[i:end])
            i = end
        elif char == "$" and (tag := DOLLAR.match(sql, i)):
            close = sql.find(tag.group(0), tag.end())
            if close < 0:
                out.append(sql[i:])
                i = n
            else:
                out.append(tag.group(0) + strip_comments(sql[tag.end() : close]) + tag.group(0))
                i = close + len(tag.group(0))
        else:
            out.append(char)
            i += 1
    return "".join(out)


def code_copy(text: str, literal: str = " ") -> str:
    """`text` with string literals replaced by `literal` and quoted identifiers unquoted.

    A rule that looks for keywords or comparisons reads this copy: a word inside a literal is data,
    while a quoted identifier names the same object as its bare spelling (`"current_setting"(...)`
    is still a call). `blank` leaves quoted identifiers blank because a keyword search must not
    read one as code; a comparison or a call name must."""
    out: list[str] = []
    i, n = 0, len(text)
    while i < n:
        char = text[i]
        if char in "'\"":
            end = quoted_end(text, i, char, char == "'" and _escape_prefixed(text, i))
            inner = text[i + 1 : end - 1]
            if char == '"' and WORD.fullmatch(inner):
                out.append(inner)
            else:
                out.append(literal if char == "'" else " ")
            i = end
        else:
            out.append(char)
            i += 1
    return "".join(out)


def _past_block(sql: str, start: int) -> int:
    """Index just past the block comment opening at `start`, counting the ones it nests."""
    depth, i, n = 0, start, len(sql)
    while i < n:
        if sql.startswith("/*", i):
            depth += 1
            i += 2
        elif sql.startswith("*/", i):
            depth -= 1
            i += 2
            if depth == 0:
                return i
        else:
            i += 1
    return n


def ident(token: str) -> str:
    """One (possibly quoted) identifier: quotes removed, doubled quotes undone, lowercased."""
    token = token.strip()
    if len(token) >= 2 and token[0] == '"' and token[-1] == '"':
        return token[1:-1].replace('""', '"').lower()
    return token.lower()


def split_dotted(name: str) -> list[str]:
    """Split `schema.table` on the dots outside quoted identifiers."""
    parts: list[str] = []
    buf: list[str] = []
    quoted = False
    for char in name.strip():
        if char == '"':
            quoted = not quoted
            buf.append(char)
        elif char == "." and not quoted:
            parts.append("".join(buf))
            buf = []
        else:
            buf.append(char)
    parts.append("".join(buf))
    return [part for part in parts if part]


def qualified(name: str) -> str:
    parts = [ident(part) for part in split_dotted(name)] or [""]
    return ".".join(parts) if len(parts) > 1 else f"public.{parts[0]}"
