"""The ordered migration replay `scripts/check-rls.py` lints: tables, policies, privilege reach.

It lives beside the entry script (which stays at the 500-line limit) and is imported the way
`review_blockers` is: the entry script puts its own directory on `sys.path` first, so the
unittest that loads `check-rls.py` by path resolves this module too.

Two things are replayed in statement order — files by name, statements inside a file, last
statement wins:

  policies
    `create policy`, `alter policy` (its `to`, `rename to`, `using` and `with check` clauses)
    and `drop policy`. The lint judges the EFFECTIVE policy, and every statement that widens
    one (`alter … to public`, `alter … using (true)`) is reported itself, so a widening cannot
    hide behind the `create` that was safe.

  the SELECT reach of `evidgate_operator`
    table grants and revokes (one table or a list), `all tables in schema`, `to public`, role
    membership (`grant evidgate_app to evidgate_operator`, which hands the operator whatever
    the app role holds — and, because a policy's role list matches by membership, also makes a
    policy scoped to that role reach it) and `alter default privileges`, which reaches only the
    tables created after it.

Out of scope, by decision: statements inside a `DO $$ … $$` body (its text is not replayed) and
the quality of an operator policy's predicate — a policy that names `evidgate_operator` is
trusted to be the gate.
"""
from __future__ import annotations

import re
import sys
from dataclasses import dataclass, replace
from pathlib import Path

# The review-blocker gate already reads a policy's `TO` clause safely (quoted identifiers kept
# whole, parenthesised bodies blanked); the insert covers the unittest entry, which loads this
# module through the entry script.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from review_blockers.sql import ROLE_STOP, policy_tokens  # noqa: E402

APP = "evidgate_app"
OWNER = "evidgate_owner"  # the migration role: the owner of every table a migration creates
OPERATOR = "evidgate_operator"

# A `TO` clause naming one of these does not scope a policy to a role: PUBLIC matches every
# role, and the three session identifiers resolve to whichever role happens to be connected.
PSEUDO_ROLES = frozenset({"public", "current_user", "session_user", "current_role"})
SESSION_ROLES = frozenset({"current_user", "session_user", "current_role"})


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


NAME = r'"(?:[^"]|"")*"|\S+'
TABLE_NAME = r'[\w."]+'
TABLE = re.compile(r"^create\s+(?:unlogged\s+|temp(?:orary)?\s+)?table\s+(?:if\s+not\s+exists\s+)?([\w.\"]+)\s*\((.*)\)\s*(?:partition\s+by.*)?$", re.I | re.S)
POLICY = re.compile(rf"^create\s+policy\s+({NAME})\s+on\s+({TABLE_NAME})(.*)$", re.I | re.S)
ALTER_POLICY = re.compile(rf"^alter\s+policy\s+({NAME})\s+on\s+({TABLE_NAME})(.*)$", re.I | re.S)
DROP_POLICY = re.compile(rf"^drop\s+policy\s+(?:if\s+exists\s+)?({NAME})\s+on\s+({TABLE_NAME})$", re.I | re.S)
PRIVILEGE = re.compile(r"^(grant|revoke)\s+(?:grant\s+option\s+for\s+)?(.+?)\s+on\s+(.+?)\s+(?:to|from)\s+(.+)$", re.I | re.S)
MEMBERSHIP = re.compile(r"^(grant|revoke)\s+(.+?)\s+(?:to|from)\s+(.+)$", re.I | re.S)
DEFAULT_PRIVS = re.compile(
    r"^alter\s+default\s+privileges(?:\s+for\s+role\s+(\S+))?(?:\s+in\s+schema\s+([\w\"]+))?\s+"
    r"(grant|revoke)\s+(.+?)\s+on\s+(tables|sequences|functions)\s+(?:to|from)\s+(.+)$", re.I | re.S
)
ALTER_RLS = re.compile(
    r"^alter\s+table\s+(?:if\s+exists\s+)?(?:only\s+)?([\w.\"]+)\s+(enable|disable|no\s+force|force)\s+row\s+level\s+security$", re.I
)
NON_TABLE = re.compile(r"^(?:all\s+)?(?:schema|sequence|function|procedure|routine|database|domain|type|language|tablespace|foreign|large)\b", re.I)
HELPER = re.compile(r"current_org_id\s*\(\s*\)", re.I)
OPEN_USING = re.compile(r"using\s*\(\s*true\s*\)", re.I)
GRANTEE_STOP = frozenset({"with", "cascade"})


@dataclass(frozen=True)
class Policy:
    """A policy statement, or the policy the replay leaves in place.

    `to_roles` is empty both for a `create` without a `TO` clause and for an `alter` that does
    not restate one; `restates_roles` tells those apart, so a `rename to` alter is not judged
    as a statement that dropped its roles. `text` is the clause tail — `for select to … using
    (…)` — of the statement that last set the predicate, which is what the helper, the
    `for select` and the open-policy checks read.
    """

    file: str
    name: str
    table: str
    text: str
    to_roles: tuple[str, ...] = ()
    open_using: bool = False
    restates_roles: bool = True


def grantees(clause: str) -> list[str]:
    """The roles a `TO`/`FROM` clause names, cut at `with grant option` or `cascade`."""
    roles: list[str] = []
    for token in policy_tokens(clause):
        if token.lower() in GRANTEE_STOP:
            break
        roles.append(token.lower())
    return roles


def role_scoped(roles: tuple[str, ...]) -> bool:
    """True when the roles confine a policy to named roles, none of them PUBLIC or a session id."""
    return bool(roles) and not (PSEUDO_ROLES & set(roles))


def policy_clauses(tail: str) -> dict[str, object]:
    """The clauses a policy statement's tail restates: `rename` (or None), `roles` (or None when
    the statement does not name roles) and `using` (or None when it does not restate USING)."""
    tokens = policy_tokens(tail)
    clauses: dict[str, object] = {"rename": None, "roles": None, "using": None}
    i = 0
    while i < len(tokens):
        low = tokens[i].lower()
        if low == "rename" and i + 2 < len(tokens) and tokens[i + 1].lower() == "to":
            clauses["rename"] = tokens[i + 2]
            i += 3
        elif low == "to":
            roles: list[str] = []
            i += 1
            while i < len(tokens) and tokens[i].lower() not in ROLE_STOP and tokens[i].lower() != "rename":
                roles.append(tokens[i].lower())
                i += 1
            clauses["roles"] = roles
        elif low == "using":
            clauses["using"] = True  # the body is blanked by `policy_tokens`
            i += 1
        else:
            i += 1
    return clauses


def table_level_select(privileges: str) -> bool:
    """True when the privilege list grants table-wide SELECT (`all` included).

    A column list (`select (col)`) does not confer table SELECT, so it never counts.
    """
    for part in privileges.split(","):
        privilege = part.strip().lower()
        if "(" in privilege:
            continue
        if privilege in {"select", "all", "all privileges"}:
            return True
    return False


class Privileges:
    """SELECT reach per role, replayed statement by statement in file order."""

    def __init__(self) -> None:
        self.select: dict[str, set[str]] = {}  # role -> tables it may SELECT
        self.members: dict[str, set[str]] = {}  # role -> roles whose privileges it inherits
        self.defaults: dict[tuple[str, str], bool] = {}  # (schema, role) -> SELECT on new tables
        self.tables: set[str] = set()

    def create_table(self, table: str) -> None:
        """`alter default privileges` reaches only the tables created after it, so the defaults
        in force are applied here, at creation, never retroactively to an earlier table."""
        self.tables.add(table)
        scope = table.rsplit(".", 1)[0]
        for (default_scope, role), granted in self.defaults.items():
            if default_scope == scope:
                self.hold(role, table, granted)

    def hold(self, role: str, table: str, granted: bool) -> None:
        tables = self.select.setdefault(role, set())
        if granted:
            tables.add(table)
        else:
            tables.discard(table)

    def scope(self, object_text: str) -> list[str]:
        """The tables a GRANT/REVOKE object clause names, empty for any other object kind."""
        text = re.sub(r"^\s*table\s+", "", object_text.strip(), flags=re.I)
        if m := re.match(r"all\s+tables\s+in\s+schema\s+(\S+)$", text, re.I):
            scope = ident(m.group(1))
            return [table for table in self.tables if table.rsplit(".", 1)[0] == scope]
        if "(" in text or NON_TABLE.match(text):
            return []
        return [qualified(part) for part in text.split(",") if part.strip()]

    def play(self, stmt: str) -> None:
        """One `grant`, `revoke`, role-membership or `alter default privileges` statement."""
        if m := DEFAULT_PRIVS.match(stmt):
            role, scope, verb, privileges, kind, roles = m.groups()
            if kind.lower() != "tables" or (role or OWNER).strip('"').lower() != OWNER:
                return
            if not table_level_select(privileges):
                return
            for grantee in grantees(roles):
                self.defaults[(ident(scope or "public"), grantee)] = verb.lower() == "grant"
            return
        if m := PRIVILEGE.match(stmt):
            verb, privileges, object_text, roles_text = m.groups()
            if not table_level_select(privileges) or re.match(r"^revoke\s+grant\s+option\s+for\b", stmt, re.I):
                return
            granted = verb.lower() == "grant"
            recipients = [role for role in grantees(roles_text) if role not in SESSION_ROLES]
            for table in self.scope(object_text):
                for role in recipients:
                    self.hold(role, table, granted)
            return
        if m := MEMBERSHIP.match(stmt):
            granted = m.group(1).lower() == "grant"
            granted_roles, members = grantees(m.group(2)), grantees(m.group(3))
            for member in members:
                if member in SESSION_ROLES:
                    continue
                for role in granted_roles:
                    if granted:
                        self.members.setdefault(member, set()).add(role)
                    else:
                        self.members.get(member, set()).discard(role)

    def reach(self, role: str) -> set[str]:
        """The roles a role's privileges arrive through: its own memberships, transitively, plus
        PUBLIC — a grant `to public` reaches every role."""
        seen: set[str] = set()
        todo = [role, "public"]
        while todo:
            current = todo.pop()
            if current in seen:
                continue
            seen.add(current)
            todo.extend(self.members.get(current, ()))
        return seen

    def operator_select(self) -> set[str]:
        """Every table `evidgate_operator` ends up able to SELECT after the replay."""
        return {table for role in self.reach(OPERATOR) for table in self.select.get(role, ())}


class Schema:
    """What the migrations, replayed in order, leave in place (last statement wins)."""

    def __init__(self) -> None:
        self.tables: dict[str, tuple[str, str]] = {}  # table -> (file, body)
        self.rls: dict[str, dict[str, bool]] = {}
        self.policies: dict[tuple[str, str], Policy] = {}  # (table, name) -> effective policy
        self.policy_statements: list[Policy] = []  # every create/alter policy, in replay order
        self.unique_org_id: set[str] = set()
        self.functions: list[tuple[str, str, str, str]] = []  # file, name, header, body
        self.grants: list[tuple[str, str]] = []  # file, statement: grants only, for the definer lint
        self.reach = Privileges()
        self.operator_select: set[str] = set()  # tables evidgate_operator ends up able to SELECT


def apply_create(schema: Schema, file: str, name: str, table: str, tail: str) -> None:
    table, name = qualified(table), ident(name)
    clause = policy_clauses(tail)
    policy = Policy(file, name, table, tail, tuple(clause["roles"] or ()), bool(OPEN_USING.search(tail)))
    schema.policies[(table, name)] = policy
    schema.policy_statements.append(policy)


def apply_alter(schema: Schema, file: str, name: str, table: str, tail: str) -> None:
    """Replay `alter policy`: roles, `rename to` and USING update the effective policy, and the
    statement is judged in its own right when it restates the roles or opens the predicate."""
    table, name = qualified(table), ident(name)
    clause = policy_clauses(tail)
    current = schema.policies.get((table, name)) or Policy(file, name, table, tail)
    restated = clause["using"] is not None
    roles = tuple(clause["roles"]) if clause["roles"] is not None else current.to_roles
    effective = replace(
        current,
        file=file if restated else current.file,
        name=ident(clause["rename"]) if clause["rename"] else name,
        table=table,
        text=tail if restated else current.text,
        to_roles=roles,
        open_using=bool(OPEN_USING.search(tail)) if restated else current.open_using,
        restates_roles=True,
    )
    schema.policies.pop((table, name), None)
    schema.policies[(table, effective.name)] = effective
    schema.policy_statements.append(
        Policy(file, name, table, tail, tuple(clause["roles"] or ()), bool(OPEN_USING.search(tail)),
               restates_roles=clause["roles"] is not None)
    )


def collect(files: dict[str, str]) -> Schema:
    schema = Schema()
    for name in sorted(files):
        for stmt, body in statements(strip_comments(files[name])):
            low = stmt.lower()
            if m := TABLE.match(stmt):
                table = qualified(m.group(1))
                schema.tables[table] = (name, m.group(2))
                schema.reach.create_table(table)
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
                apply_create(schema, name, m.group(1), m.group(2), m.group(3))
            elif m := ALTER_POLICY.match(stmt):
                apply_alter(schema, name, m.group(1), m.group(2), m.group(3))
            elif m := DROP_POLICY.match(stmt):
                schema.policies.pop((qualified(m.group(2)), ident(m.group(1))), None)
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
            elif low.startswith(("grant ", "revoke ", "alter default privileges")):
                schema.reach.play(stmt)
                if low.startswith(("grant ", "alter default privileges")):
                    schema.grants.append((name, stmt))
    schema.operator_select = schema.reach.operator_select()
    return schema


def effective_policies(schema: Schema, table: str) -> list[Policy]:
    return [policy for (owner, _), policy in schema.policies.items() if owner == table]


def exempt_superseded(schema: Schema, index: int, policy: Policy, exempt: dict[tuple[str, str, str], str]) -> bool:
    """The exemption counts only while a LATER statement re-creates the policy `to evidgate_app`.

    It records a replacement: a replacement that names another role, PUBLIC or a session
    pseudo-role does not absolve the open statement.
    """
    if (policy.file, policy.name, policy.table) not in exempt:
        return False
    later = [
        statement
        for statement in schema.policy_statements[index + 1 :]
        if statement.table == policy.table and statement.name == policy.name and statement.restates_roles
    ]
    return bool(later) and role_scoped(later[-1].to_roles) and APP in later[-1].to_roles


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

        policies = effective_policies(schema, table)
        if kind is None and not any(HELPER.search(policy.text) for policy in policies):
            findings.append(Finding(name, table, "missing_policy", "no policy over `evidgate.current_org_id()` (TL T2)"))
        if kind is not None and not policies:
            findings.append(Finding(name, table, "missing_policy", "an excepted table still needs at least one policy"))
        for policy in policies:
            if kind is not None and re.match(r"\s*for\s+select\b", policy.text, re.I):
                continue  # a read-only platform/catalogue policy may be open (e.g. the permission catalogue)
            if policy.open_using and set(policy.to_roles) != {OWNER}:
                findings.append(Finding(policy.file, table, "open_policy", "`using (true)` is allowed only `to evidgate_owner`"))
    return findings


def lint_policies(schema: Schema, exceptions: dict[str, str], exempt: dict[tuple[str, str, str], str]) -> list[Finding]:
    """Every policy names its roles, unless a later migration replaced it; and no policy may
    admit `evidgate_operator` to a table where it holds SELECT with no operator policy."""
    findings: list[Finding] = []
    for index, policy in enumerate(schema.policy_statements):
        if not policy.restates_roles or role_scoped(policy.to_roles):
            continue
        if exempt_superseded(schema, index, policy, exempt):
            continue
        findings.append(Finding(policy.file, policy.table, "policy_no_role",
            "name the roles a tenant policy applies to (`to evidgate_app`); without a clause it filters PUBLIC too, the operator included"))
    # A policy's role list matches by membership, so a policy scoped to a role the operator is a
    # member of reaches the operator too; one scoped to any other role cannot, and RLS then yields
    # zero rows for it.
    reach = schema.reach.reach(OPERATOR) - {OPERATOR}
    for table in schema.tables:
        if table not in schema.operator_select or exceptions.get(table) is not None:
            continue
        policies = effective_policies(schema, table)
        if any(OPERATOR in policy.to_roles for policy in policies):
            continue  # an operator policy is the gate: it decides which rows the operator reads
        for policy in policies:
            if role_scoped(policy.to_roles) and not (set(policy.to_roles) & reach):
                continue
            findings.append(Finding(policy.file, table, "operator_select_no_policy",
                "evidgate_operator holds SELECT here and this policy admits it; scope the policy or revoke SELECT from evidgate_operator"))
    return findings
