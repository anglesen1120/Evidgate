#!/usr/bin/env python3
"""02.6.1-T2: the RLS and SECURITY DEFINER lint for `migrations/*.sql` (AC4, AC7).

Every table a migration creates must be tenant-isolated or be named on the exception
list (`contracts/rls-exceptions.txt`, the single source the boot self-check shares):

  missing_org_id        the table has no `org_id` column
  missing_unique_key    no `unique (org_id, id)` (TL T3: the composite-FK target)
  missing_rls           ENABLE or FORCE ROW LEVEL SECURITY is absent
  missing_policy        no policy using `evidgate.current_org_id()` (TL T2)

Exception kinds relax this: `platform` and `catalogue` tables still need ENABLE + FORCE
and a policy but have no `org_id` rule; `mechanism` tables are skipped entirely.

Every `SECURITY DEFINER` function (TL T80) must:

  definer_search_path   `set search_path = ''`
  definer_public_grant  never be granted to PUBLIC
  definer_unqualified   use schema-qualified relations only (`from public.x`, never `from x`)
  definer_audit         write a per-call audit row, unless named in DEFINER_AUDIT_EXEMPT
                        or the file carries `-- lint: audit-deferred <task>`

Usage: check-rls.py [--migrations DIR] [--exceptions FILE]. Exit 1 lists every finding.
"""
from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass
from pathlib import Path

# Definers whose audit trail is something other than a per-call audit row. Adding a name
# here is a review decision, so each entry carries its reason.
DEFINER_AUDIT_EXEMPT = {
    "evidgate.purge_expired_idempotency": "T80 sweep: bounded delete of expired rows, observed via the task row",
    "evidgate.purge_expired_ci_token_issue": "T7 sweep: bounded delete of expired replay-cache rows, observed via the task row",
    "evidgate.audit_org_ids": "T80 sweep: lists org ids for the chain-verification task, which records its own findings",
}

# Roles a definer function may be granted EXECUTE to.
ALLOWED_GRANTEES = {"evidgate_app", "evidgate_worker_sched", "evidgate_operator"}

DEFERRAL = re.compile(r"--\s*lint:\s*audit-deferred\s+(\S+)\s+([^\n]+)")


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


def load_exceptions(path: Path) -> dict[str, str]:
    kinds: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        entry = line.split("#", 1)[0].strip()
        if not entry:
            continue
        parts = entry.split()
        if len(parts) != 2 or "." not in parts[0]:
            raise SystemExit(f"{path}: malformed exception entry: {line!r}")
        kinds[parts[0].lower()] = parts[1]
    return kinds


DOLLAR = re.compile(r"\$(\w*)\$")


def statements(sql: str) -> list[tuple[str, str]]:
    """Split comment-free SQL on `;` outside dollar-quoted bodies and single-quoted strings.

    Returns (statement with each dollar body replaced by `$$`, concatenated bodies). Generic
    dollar tags (`$fn$`, `$body$`) are honoured, so a function cannot hide behind its tag.
    """
    out: list[tuple[str, str]] = []
    head: list[str] = []
    bodies: list[str] = []
    i, n = 0, len(sql)
    while i < n:
        ch = sql[i]
        if ch == "'":
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
        elif ch == "$" and (m := DOLLAR.match(sql, i)):
            end = sql.find(m.group(0), m.end())
            if end < 0:
                raise SystemExit(f"unterminated dollar quote {m.group(0)}")
            bodies.append(sql[m.end() : end])
            head.append("$$")
            i = end + len(m.group(0))
        elif ch == ";":
            out.append((" ".join("".join(head).split()), "\n".join(bodies)))
            head, bodies = [], []
            i += 1
        else:
            head.append(ch)
            i += 1
    tail = " ".join("".join(head).split())
    if tail:
        out.append((tail, "\n".join(bodies)))
    return out


def qualified(name: str) -> str:
    name = name.strip().strip('"').lower()
    return name if "." in name else f"public.{name}"


TABLE = re.compile(r"^create\s+(?:unlogged\s+|temp(?:orary)?\s+)?table\s+(?:if\s+not\s+exists\s+)?([\w.\"]+)\s*\((.*)\)\s*(?:partition\s+by.*)?$", re.I | re.S)
POLICY = re.compile(r"^create\s+policy\s+\S+\s+on\s+([\w.\"]+)(.*)$", re.I | re.S)
ALTER_RLS = re.compile(
    r"^alter\s+table\s+(?:if\s+exists\s+)?(?:only\s+)?([\w.\"]+)\s+(enable|disable|no\s+force|force)\s+row\s+level\s+security$", re.I
)
HELPER = re.compile(r"current_org_id\s*\(\s*\)", re.I)
OPEN_USING = re.compile(r"using\s*\(\s*true\s*\)", re.I)


class Schema:
    """What the migrations, replayed in order, leave in place (last statement wins)."""

    def __init__(self) -> None:
        self.tables: dict[str, tuple[str, str]] = {}  # table -> (file, body)
        self.rls: dict[str, dict[str, bool]] = {}
        self.policies: dict[str, list[tuple[str, str]]] = {}  # table -> [(file, text)]
        self.unique_org_id: set[str] = set()
        self.functions: list[tuple[str, str, str, str]] = []  # file, name, header, body
        self.grants: list[tuple[str, str]] = []  # file, statement


def collect(files: dict[str, str]) -> Schema:
    schema = Schema()
    for name in sorted(files):
        for stmt, body in statements(strip_comments(files[name])):
            low = stmt.lower()
            if m := TABLE.match(stmt):
                schema.tables[qualified(m.group(1))] = (name, m.group(2))
            elif m := ALTER_RLS.match(stmt):
                state = schema.rls.setdefault(qualified(m.group(1)), {"enable": False, "force": False})
                verb = re.sub(r"\s+", " ", m.group(2).lower())
                if verb == "enable":
                    state["enable"] = True
                elif verb == "disable":
                    state["enable"] = False
                elif verb == "force":
                    state["force"] = True
                else:
                    state["force"] = False
            elif m := POLICY.match(stmt):
                schema.policies.setdefault(qualified(m.group(1)), []).append((name, m.group(2)))
            elif m := re.match(
                r"^create\s+unique\s+index\s+(?:if\s+not\s+exists\s+)?\S+\s+on\s+([\w.\"]+)\s*\(\s*org_id\s*,\s*id\s*\)", stmt, re.I
            ):
                schema.unique_org_id.add(qualified(m.group(1)))
            elif m := re.match(
                r"^alter\s+table\s+(?:if\s+exists\s+)?([\w.\"]+)\s+add\s+constraint\s+\S+\s+(?:unique|primary\s+key)\s*\(\s*org_id\s*,\s*id\s*\)", stmt, re.I
            ):
                schema.unique_org_id.add(qualified(m.group(1)))
            elif m := re.match(r"^create\s+(?:or\s+replace\s+)?function\s+([\w.\"]+)\s*\(", stmt, re.I):
                schema.functions.append((name, m.group(1).lower().strip('"'), stmt, body))
            elif low.startswith(("grant ", "alter default privileges")):
                schema.grants.append((name, stmt))
    return schema


def lint_tables(schema: Schema, exceptions: dict[str, str]) -> list[Finding]:
    findings: list[Finding] = []
    for table, (name, body) in schema.tables.items():
        kind = exceptions.get(table)
        if kind == "mechanism":
            continue
        if kind is None:
            if not re.search(r"(^|[\s,(])org_id\s+uuid", body, re.I):
                findings.append(Finding(name, table, "missing_org_id", "a business table carries `org_id uuid` (00 §5.4.1)"))
            inline = re.search(r"(?:unique|primary\s+key)\s*\(\s*org_id\s*,\s*id\s*\)", body, re.I)
            if not (inline or table in schema.unique_org_id):
                findings.append(Finding(name, table, "missing_unique_key", "declare `unique (org_id, id)` as the composite-FK target (TL T3)"))

        state = schema.rls.get(table, {})
        if not (state.get("enable") and state.get("force")):
            findings.append(Finding(name, table, "missing_rls", "ENABLE and FORCE ROW LEVEL SECURITY must both be in force after the last migration"))

        policies = schema.policies.get(table, [])
        if kind is None and not any(HELPER.search(text) for _, text in policies):
            findings.append(Finding(name, table, "missing_policy", "no policy over `evidgate.current_org_id()` (TL T2)"))
        if kind is not None and not policies:
            findings.append(Finding(name, table, "missing_policy", "an excepted table still needs at least one policy"))
        for pfile, text in policies:
            if kind is not None and re.match(r"\s*for\s+select\b", text, re.I):
                continue  # a read-only platform/catalogue policy may be open (e.g. the permission catalogue)
            owner_only = re.search(r"\bto\s+evidgate_owner\b", text, re.I) and not re.search(r"\bto\s+[^;]*\b(evidgate_app|public)\b", text, re.I)
            if OPEN_USING.search(text) and not owner_only:
                findings.append(Finding(pfile, table, "open_policy", "`using (true)` is allowed only `to evidgate_owner`"))
    return findings


def lint_definers(schema: Schema, raws: dict[str, str]) -> list[Finding]:
    findings: list[Finding] = []
    deferrals: dict[str, set[str]] = {}
    for fname, raw in raws.items():
        for m in DEFERRAL.finditer(raw):
            deferrals.setdefault(fname, set()).update(
                part.strip().lower() for part in m.group(2).split(",") if part.strip()
            )
    for fname, func, header, body in schema.functions:
        if not re.search(r"security\s+definer", header, re.I):
            continue
        short = func if "." in func else f"public.{func}"
        if not re.search(r"set\s+search_path\s*(?:=|to)\s*''", header, re.I):
            findings.append(Finding(fname, short, "definer_search_path", "a SECURITY DEFINER function pins `set search_path = ''`"))
        for gfile, grant in schema.grants:
            if re.search(r"\bto\s+public\b", grant, re.I) and (
                func in grant.lower() or "alter default privileges" in grant.lower()
            ) and re.search(r"\b(execute|all)\b", grant, re.I):
                findings.append(Finding(gfile, short, "definer_public_grant", "never grant EXECUTE to PUBLIC"))
            m = re.match(rf"^grant\s+(?:execute|all(?:\s+privileges)?)\s+on\s+function\s+{re.escape(func)}\s*\([^)]*\)\s+to\s+(.+)$", grant, re.I)
            if m:
                for grantee in re.split(r"\s*,\s*", m.group(1).strip()):
                    g = grantee.split()[0].lower()
                    if g not in ALLOWED_GRANTEES and g != "public":
                        findings.append(Finding(gfile, short, "definer_grantee", f"EXECUTE granted to unexpected role {g}"))
        ctes = {name.lower() for name in re.findall(r"\b([a-z_]\w*)\s+as\s+(?:not\s+)?(?:materialized\s*)?\(", body, re.I)}
        for relation in RELATION.findall(body):
            if relation.lower() not in SQL_WORDS and relation.lower() not in ctes and not relation.lower().startswith(("batch", "removed")):
                findings.append(Finding(fname, short, "definer_unqualified", f"relation `{relation}` is not schema-qualified"))
        audited = re.search(r"insert\s+into\s+\S*audit", body, re.I)
        if not audited and func not in DEFINER_AUDIT_EXEMPT and func not in deferrals.get(fname, set()):
            findings.append(
                Finding(fname, short, "definer_audit", "no per-call audit row (TL T80); add one or name it in `-- lint: audit-deferred <task> <schema.function>[, ...]`")
            )
    return findings


RELATION = re.compile(r"\b(?:from|join|into|update)\s+(?!only\b)([a-z_][\w]*)(?=\s|;|\)|$)", re.I)
SQL_WORDS = {"skip", "lateral", "unnest", "generate_series", "select", "values", "batch", "removed", "removed_rows"}


def lint(migrations: Path, exceptions_path: Path) -> list[Finding]:
    exceptions = load_exceptions(exceptions_path)
    raws = {f.name: f.read_text(encoding="utf-8") for f in sorted(migrations.glob("*.sql"))}
    schema = collect(raws)
    return lint_tables(schema, exceptions) + lint_definers(schema, raws)


def main(argv: list[str]) -> int:
    root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser()
    parser.add_argument("--migrations", type=Path, default=root / "migrations")
    parser.add_argument("--exceptions", type=Path, default=root / "contracts" / "rls-exceptions.txt")
    args = parser.parse_args(argv)
    findings = lint(args.migrations, args.exceptions)
    for finding in findings:
        print(finding, file=sys.stderr)
    count = len(list(args.migrations.glob("*.sql")))
    if findings:
        print(f"check-rls: {len(findings)} finding(s) in {count} migration(s)", file=sys.stderr)
        return 1
    print(f"check-rls: {count} migration(s) clean")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
