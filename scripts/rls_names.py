"""Names and findings shared by the RLS lint: identifiers, dotted qualification, a found problem.

Split out of `rls_policy_lint` to keep each module under the 500-line budget that script lives
under, since the replay and the policy rules together reached it. The helpers here are lexical
only: they normalise PostgreSQL identifiers and print a finding, and they hold no rule.
"""
from __future__ import annotations

import re
from dataclasses import dataclass

NAME = r'"(?:[^"]|"")*"|\S+'
TABLE_NAME = r'[\w."]+'


@dataclass(frozen=True)
class Finding:
    file: str
    subject: str
    rule: str
    message: str

    def __str__(self) -> str:
        return f"{self.file}: {self.subject}: {self.rule}: {self.message}"


def strip_comments(sql: str) -> str:
    sql = re.sub(r"/\*.*?\*/", " ", sql, flags=re.S)
    return re.sub(r"--[^\n]*", " ", sql)


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
