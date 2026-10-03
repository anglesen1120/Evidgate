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
}

# Roles a definer function may be granted EXECUTE to.
ALLOWED_GRANTEES = {"evidgate_app", "evidgate_worker_sched", "evidgate_operator"}

DEFERRAL = re.compile(r"--\s*lint:\s*audit-deferred\s+(\S+)")


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


def tables(sql: str) -> dict[str, str]:
    """name -> column/constraint body of each CREATE TABLE (balanced parentheses)."""
    found: dict[str, str] = {}
    for m in re.finditer(r"create\s+table\s+(?:if\s+not\s+exists\s+)?([a-z_][\w]*\.[a-z_][\w]*)\s*\(", sql, re.I):
        depth, i = 1, m.end()
        while i < len(sql) and depth:
            depth += {"(": 1, ")": -1}.get(sql[i], 0)
            i += 1
        found[m.group(1).lower()] = sql[m.end() : i - 1]
    return found


def lint_tables(name: str, sql: str, exceptions: dict[str, str], all_sql: str) -> list[Finding]:
    findings: list[Finding] = []
    for table, body in tables(sql).items():
        kind = exceptions.get(table)
        if kind == "mechanism":
            continue
        short = table.split(".", 1)[1]
        esc = re.escape(table)

        if kind is None:
            if not re.search(r"(^|[\s,(])org_id\s+uuid", body, re.I):
                findings.append(Finding(name, table, "missing_org_id", "a business table carries `org_id uuid` (00 §5.4.1)"))
            has_unique = re.search(r"(?:unique|primary\s+key)\s*\(\s*org_id\s*,\s*id\s*\)", body, re.I) or re.search(
                rf"alter\s+table\s+{esc}\s+add\s+constraint\s+\S+\s+unique\s*\(\s*org_id\s*,\s*id\s*\)", all_sql, re.I
            ) or re.search(
                rf"create\s+unique\s+index\s+(?:if\s+not\s+exists\s+)?\S+\s+on\s+{esc}\s*\(\s*org_id\s*,\s*id\s*\)", all_sql, re.I
            )
            if not has_unique:
                findings.append(Finding(name, table, "missing_unique_key", "declare `unique (org_id, id)` as the composite-FK target (TL T3)"))

        enable = re.search(rf"alter\s+table\s+(?:if\s+exists\s+)?{esc}\s+enable\s+row\s+level\s+security", all_sql, re.I)
        force = re.search(rf"alter\s+table\s+(?:if\s+exists\s+)?{esc}\s+force\s+row\s+level\s+security", all_sql, re.I)
        if not (enable and force):
            findings.append(Finding(name, table, "missing_rls", "ENABLE and FORCE ROW LEVEL SECURITY are both required"))

        policy = re.search(
            rf"create\s+policy\s+\S+\s+on\s+{esc}\b.*?current_org_id\s*\(\s*\)", all_sql, re.I | re.S
        )
        if not policy:
            findings.append(Finding(name, table, "missing_policy", "no policy over `evidgate.current_org_id()` (TL T2)"))
        _ = short
    return findings


FUNCTION = re.compile(
    r"create\s+(?:or\s+replace\s+)?function\s+([a-z_][\w]*\.[a-z_][\w]*)\s*\((.*?)\$\$(.*?)\$\$", re.I | re.S
)
RELATION = re.compile(r"\b(?:from|join|into|update)\s+(?!only\b)([a-z_][\w]*)(?=\s|;|\)|$)", re.I)
SQL_WORDS = {"skip", "lateral", "unnest", "generate_series", "select", "values", "batch", "removed", "removed_rows"}


def lint_definers(name: str, raw: str, sql: str, all_sql: str) -> list[Finding]:
    findings: list[Finding] = []
    deferred = DEFERRAL.search(raw)
    for m in FUNCTION.finditer(sql):
        func, header_and_args, body = m.group(1).lower(), m.group(0), m.group(3)
        if not re.search(r"security\s+definer", header_and_args, re.I):
            continue
        if not re.search(r"set\s+search_path\s*=\s*''", header_and_args, re.I):
            findings.append(Finding(name, func, "definer_search_path", "a SECURITY DEFINER function pins `set search_path = ''`"))
        if re.search(rf"grant\s+execute\s+on\s+function\s+{re.escape(func)}\s*\([^)]*\)\s+to\s+public\b", all_sql, re.I):
            findings.append(Finding(name, func, "definer_public_grant", "never grant EXECUTE to PUBLIC"))
        for grantee in re.findall(
            rf"grant\s+execute\s+on\s+function\s+{re.escape(func)}\s*\([^)]*\)\s+to\s+([a-z_]\w*)", all_sql, re.I
        ):
            if grantee.lower() not in ALLOWED_GRANTEES and grantee.lower() != "public":
                findings.append(Finding(name, func, "definer_grantee", f"EXECUTE granted to unexpected role {grantee}"))
        for relation in RELATION.findall(body):
            if relation.lower() not in SQL_WORDS and not relation.lower().startswith(("batch", "removed")):
                findings.append(Finding(name, func, "definer_unqualified", f"relation `{relation}` is not schema-qualified"))
        audited = re.search(r"insert\s+into\s+\S*audit", body, re.I)
        if not audited and func not in DEFINER_AUDIT_EXEMPT and not deferred:
            findings.append(Finding(name, func, "definer_audit", "no per-call audit row (TL T80); add one or `-- lint: audit-deferred <task>`"))
    return findings


def lint(migrations: Path, exceptions_path: Path) -> list[Finding]:
    exceptions = load_exceptions(exceptions_path)
    files = sorted(migrations.glob("*.sql"))
    raws = {f.name: f.read_text(encoding="utf-8") for f in files}
    stripped = {n: strip_comments(r) for n, r in raws.items()}
    all_sql = "\n".join(stripped.values())
    findings: list[Finding] = []
    for n in raws:
        findings += lint_tables(n, stripped[n], exceptions, all_sql)
        findings += lint_definers(n, raws[n], stripped[n], all_sql)
    return findings


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
