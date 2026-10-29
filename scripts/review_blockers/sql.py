"""D1: the static SQL a migration is checked for, with literals and identifiers read safely.

A `;` inside a string, a quoted identifier or a dollar-quoted body does not end
a statement, and a `TO public` inside a literal or an expression is data, not a
role clause, so the splitter keeps identifiers whole, blanks literals and drops
parenthesised bodies before the policy rule reads the statement.
"""
from __future__ import annotations

import re
from collections.abc import Iterator

from review_blockers.gitio import git

DOLLAR = re.compile(r"\$\w*\$")
POLICY_STATEMENT = re.compile(r"\s*(create|alter)\s+policy\b", re.I)
# A quoted identifier is one token, so a name that holds a space, a comma or
# the word `to` is not read as a clause.
POLICY_TOKEN = re.compile(r'"((?:[^"]|"")*)"|([^\s,]+)')
ROLE_STOP = frozenset({"using", "with", "for", "as"})


def quoted_end(text: str, start: int, mark: str, backslash: bool) -> int:
    """Index just past the closing `mark` of the quoted run opening at `start`.

    A doubled `mark` is an escaped one and, in an `E'…'` string, a backslash
    escapes the next character, so neither ends the run: a `;` or a `--` inside
    a literal or an identifier is data, not statement structure.
    """
    i, size = start + len(mark), len(text)
    while i < size:
        if backslash and text[i] == "\\":
            i += 2
            continue
        if text.startswith(mark, i):
            if text.startswith(mark + mark, i):
                i += 2 * len(mark)
                continue
            return i + len(mark)
        i += 1
    return size


def sql_statements(text: str) -> list[tuple[int, str]]:
    """Split SQL into (first line, statement), dropping comments and literal bodies.

    Quoted identifiers and dollar-quoted bodies are kept whole so their contents
    cannot end a statement or open a comment; string literals are blanked because
    a `to public` inside one is data, not a role clause.
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
        if quote:
            end = quoted_end(text, i, quote.group(0), False)
            line += text.count("\n", i, end)
            buffer.append(" '' ")
            i = end
            continue
        if char == '"':
            end = quoted_end(text, i, '"', False)
            line += text.count("\n", i, end)
            buffer.append(text[i:end])
            i = end
            continue
        if char == "'":
            # Only an `E'…'` literal unescapes with a backslash; in a plain string
            # a backslash is an ordinary character.
            prefixed = i > 0 and text[i - 1] in "Ee" and (i < 2 or not (text[i - 2].isalnum() or text[i - 2] == "_"))
            end = quoted_end(text, i, "'", prefixed)
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
    """Blank parenthesised bodies so a `to` inside an expression is not a role clause.

    Quoted identifiers are copied through untouched, so a parenthesis inside one
    does not open a body.
    """
    kept: list[str] = []
    depth, i, size = 0, 0, len(statement)
    while i < size:
        char = statement[i]
        if char == '"':
            end = quoted_end(statement, i, '"', False)
            if depth == 0:
                kept.append(statement[i:end])
            i = end
            continue
        if char == "(":
            depth += 1
        elif char == ")":
            depth = max(0, depth - 1)
        elif depth == 0:
            kept.append(char)
        i += 1
    return "".join(kept)


def policy_tokens(statement: str) -> list[str]:
    """The top-level words of a statement, each quoted identifier read as one word."""
    tokens: list[str] = []
    for match in POLICY_TOKEN.finditer(without_parens(statement)):
        quoted = match.group(1)
        tokens.append(quoted.replace('""', '"') if quoted is not None else match.group(2))
    return tokens


def policy_roles(statement: str) -> list[str] | None:
    """The roles a policy statement applies to, or None when it has no `TO` clause.

    The clause is read only at top level and only after `ON <table>`, so a `to`
    inside an expression, a literal, an identifier or the policy name is not one.
    """
    tokens = policy_tokens(statement)
    lowered = [token.lower() for token in tokens]
    if "on" not in lowered:
        return None
    after_table = lowered.index("on") + 2  # the `on` keyword and the table name
    if "to" not in lowered[after_table:]:
        return None
    to = lowered.index("to", after_table)
    roles: list[str] = []
    for token, low in zip(tokens[to + 1 :], lowered[to + 1 :]):
        if low in ROLE_STOP:
            break
        roles.append(token)
    return roles


def policy_roles_ok(statement: str) -> bool:
    """True when the policy names its roles and none of them is PUBLIC.

    Without a `TO` clause PostgreSQL applies the policy to every role, and
    `TO public` says the same thing out loud; both are the same violation.
    """
    roles = policy_roles(statement)
    return bool(roles) and all(role and role.lower() != "public" for role in roles)


def policies_without_roles(head: str, path: str) -> Iterator[tuple[int, str]]:
    """Every CREATE POLICY, and every ALTER POLICY that restates its roles, that is too open."""
    for line, statement in sql_statements(git("show", f"{head}:{path}")):
        command = POLICY_STATEMENT.match(statement)
        if not command:
            continue
        roles = policy_roles(statement)
        # `ALTER POLICY … USING (…)` with no `TO` changes an expression, not the role set.
        if roles is None and command.group(1).lower() == "alter":
            continue
        if not policy_roles_ok(statement):
            yield line, " ".join(statement.split())[:120]
