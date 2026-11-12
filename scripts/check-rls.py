#!/usr/bin/env python3
"""02.6.1-T2: the RLS and SECURITY DEFINER lint for `migrations/*.sql` (AC4, AC7).

Every table a migration creates must be tenant-isolated or be named on the exception
list (`contracts/rls-exceptions.txt`, the single source the boot self-check shares):

  missing_org_id        the table has no `org_id` column
  missing_unique_key    no `unique (org_id, id)` (TL T3: the composite-FK target)
  missing_rls           ENABLE or FORCE ROW LEVEL SECURITY is absent
  missing_policy        no policy using `evidgate.current_org_id()` (TL T2)
  policy_no_role        a policy without a `TO <role>` clause, or one naming `to public` or a
                        session pseudo-role (`current_user`, `session_user`, `current_role`):
                        it filters every role that holds a privilege, `evidgate_operator`
                        included. `alter policy` is replayed, so a later widening is reported
  operator_select_no_policy   `evidgate_operator` ends up holding SELECT (after the replay)
                              and a policy admits it (no `TO` clause, `to public`, a session
                              pseudo-role, or a role the operator is a member of), with nothing
                              gating it. A policy `to evidgate_operator` gates it only while its
                              `USING` predicate compares `org_id` to something the session cannot
                              choose: the role may `set evidgate.org_id` itself, so a
                              `current_setting('evidgate.org_id')` comparison — or the
                              `current_org_id()`/`current_project_id()` helper, which is that
                              comparison — is a finding, not a gate, and a predicate that never
                              compares `org_id` (`using (org_id is not null)`) separates no tenant.
                              Narrowed on purpose: a policy
                              scoped to a role that cannot reach the operator leaves it zero rows
                              through RLS, so that shape is not reported; `0055` revokes the
                              operator's SELECT on the three tables it repairs anyway
  embedded_dynamic      a `do` body or an `execute` runs SQL the replay cannot read statically
                        (an `execute` of a variable, an `execute format(…)` that builds DDL from
                        its arguments): reported rather than trusted, so an unreadable statement
                        cannot pass as clean

Exception kinds relax this: `platform` and `catalogue` tables still need ENABLE + FORCE
and a policy but have no `org_id` rule; `mechanism` tables are skipped entirely.

`POLICY_TO_EXEMPT` excuses a `TO`-less policy only while a later statement re-creates that
policy `to evidgate_app`; a replacement naming another role, PUBLIC or a pseudo-role does not
count. `OPERATOR_GATE_EXEMPT` accepts an operator predicate the text lint cannot judge, keyed by
the exact `(file, policy, table)` the replay leaves in place.

`0001` hands `evidgate_operator` SELECT on every `public` table, so the operator rule fires only
where a policy admits it on a business table; a policy naming only `evidgate_app` matches nothing.

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
from pathlib import Path

# The ordered replay of tables, policies and privilege reach lives in `rls_policy_lint.py`: this
# script is at the 500-line limit and the entry stays readable. The path insert covers the
# unittest entry, which loads this file by path rather than as a package member.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from rls_names import code_copy  # noqa: E402
from rls_policy_lint import Finding, collect, lint_embedded, lint_policies, lint_tables  # noqa: E402

# Definers whose audit trail is something other than a per-call audit row. Adding a name
# here is a review decision, so each entry carries its reason. Keys are EXACT qualified
# names (`schema.function`), never patterns: `lint_definers` compares the name the migration
# defines, so a function in another schema with the same spelling stays linted.
#
# The session family below is the re-point of the 02.7.1-T2.2 deferral that 0042/0044/0045/
# 0048 carried (F28, migration 0051). Those markers named a task that is now merged, which is
# no longer a reason; the real reasons are recorded here instead. The one member that DOES owe
# a per-call row — `evidgate_resolve.session_revoke_one`, the administrative revoke — is
# deliberately absent: 0050 has it append its own chain row, and 0051 keeps that.
DEFINER_AUDIT_EXEMPT = {
    "evidgate.purge_expired_idempotency": "T80 sweep: bounded cross-tenant delete of expired rows; the function writes no audit row itself, the caller IdempotencyPurgeTaskHandler records the removed total of each task fire through evidgate.record_sweep_audit",
    "evidgate.purge_expired_ci_token_issue": "T7 sweep: bounded delete of expired replay-cache rows, observed via the task row",
    "evidgate.purge_expired_authoring_changes": (
        "created 0067_authoring_authoring_change.sql (integer): tenancy-free retention sweep — it deletes "
        "discarded authoring proposals after their window across every org, in bounded batches, and the "
        "scheduled task row is where the fire and its row count are observed, exactly like "
        "purge_expired_idempotency; an audit row per call would have to pick one org for a cross-tenant delete"
    ),
    "evidgate.expire_pending_invites": "T80 sweep: bounded expiry of past-due pending invites, observed via the task row",
    "evidgate.audit_org_ids": "T80 sweep: lists org ids for the chain-verification task, which records its own findings",
    "evidgate.reconcile_overdue_runs": (
        "re-created 0058_reconcile_overdue_running.sql:65 (uuid, uuid, integer; first created "
        "0056_reconcile_missing_result.sql:132): read-only sweep scan — the "
        "collection-deadline sweep reads which runs are overdue and writes nothing here, and the attempt it "
        "queues is the observable outcome (the run's state, its evidence_revision row, the task row); a per-call "
        "audit row written by a read-only projection would be a write amplification on every ten-minute fire"
    ),
    "evidgate_resolve.login_upsert_identity": (
        "created 0042_identity_login_resolvers.sql:95 (text, text, text, text, uuid, uuid): pre-tenant, no org chain to "
        "write into — a federated login resolves a person before any tenant scope exists; the org-scoped `auth.login` "
        "row is written after it by `IdentityAuditWriter` (src/Modules/Evidgate.Modules.Identity/Login/Audit/"
        "IdentityAuditWriter.cs:59-95, called from Login/Completion/CompleteLoginCommandHandler.cs:137), and "
        "pre-tenant refusals are security_event (11.5.1-T5)"
    ),
    "evidgate_resolve.session_create": (
        "current body created 0065_identity_session_user_lock.sql:52 (uuid, uuid, bytea, integer, text, text, timestamptz, "
        "timestamptz, bytea[] default null, timestamptz default null; 0065 re-created the 0045:253 body around the per-user "
        "advisory lock, and the 8- and 9-argument forms were dropped at 0045:250-251): pre-tenant, no org "
        "chain to write into — a session is the credential of a person, not of an org; called by "
        "Sessions/SessionStore.cs:115 for the login completion (Login/Completion/CompleteLoginCommandHandler.cs:199); the "
        "`auth.login` row that names the sign-in is written by `IdentityAuditWriter` (Login/Audit/IdentityAuditWriter.cs:59-95, "
        "Login/Completion/CompleteLoginCommandHandler.cs:188-193)"
    ),
    "evidgate_resolve.session_user": (
        "created 0044_identity_login_linking.sql:198 (bytea, integer) and dropped again by 0045:729: pre-tenant, no "
        "org chain to write into — a read-only lookup whose first column is the acting person, not a tenant; the "
        "entry exists only because the lint replays `create` statements without their drops"
    ),
    "evidgate_resolve.login_link_identity": (
        "created 0045_identity_session_hardening.sql:603 (bytea[], timestamptz, text, text, text, uuid; the 5-argument "
        "0044:222 was dropped at 0045:601): pre-tenant, no org chain to write into — linking moves identities between "
        "users before any tenant scope exists; the org-scoped `auth.link` rows are written in the same "
        "transaction by its only caller, `evidgate_resolve.login_link_identity_audited` (0101)"
    ),
    "evidgate_resolve.session_authenticate": (
        "created 0045_identity_session_hardening.sql:338 (bytea[], bytea, integer, timestamptz, integer): pre-tenant, "
        "no org chain to write into — the authenticator runs before a tenant is known and answers a rotation "
        "result, not an organisational act"
    ),
    "evidgate_resolve.session_rotate": (
        "created 0045_identity_session_hardening.sql:456 (uuid, uuid, bytea, integer, timestamptz, integer, text, "
        "text): pre-tenant, no org chain to write into — rotating one's own credential is the session lifecycle, not "
        "an act inside an org"
    ),
    "evidgate_resolve.session_revoke_all_for_user": (
        "current body created 0065_identity_session_user_lock.sql:231 (uuid, timestamptz; first created "
        "0045_identity_session_hardening.sql:548, unchanged by 0090): the membership-lifecycle seam "
        "`ISessionRevocation` (src/Modules/Evidgate.Modules.Identity.Contracts/ISessionRevocation.cs:3-20) ends "
        "ANOTHER member's live sessions when an org-scoped membership command removes or disables them, so the "
        "calling command's own audit row — `[AuditedAction]` membership.remove / membership.update — covers the "
        "effect (Sessions/SessionRevocationStore.cs:30-42); its callers are Application/Commands/RemoveMember.cs:139 "
        "and UpdateMembership.cs:156, each inside its audited transaction, hence no per-call row of its own"
    ),
    "evidgate_resolve.session_require_rotation": (
        "created 0045_identity_session_hardening.sql:572 (uuid, timestamptz): a side effect of a membership or role "
        "change, run inside the org-scoped command's own transaction through `ISessionPrivilegeChange` "
        "(Sessions/SessionRevocationStore.cs:45-53), called from RemoveMember.cs:137, UpdateMembership.cs:160, "
        "RevokeRole.cs:146 and GrantRole.cs:170 — the calling command's own `[AuditedAction]` row "
        "(membership.remove / membership.update / membership.role_revoke / membership.role_grant) covers the effect"
    ),
    "evidgate_resolve.session_list": (
        "created 0048_identity_session_routes.sql:138 (bytea[], timestamptz): pre-tenant and read-only, no org chain "
        "to write into — the caller's own sign-in history, projected from the presented cookie hash"
    ),
    "evidgate_resolve.user_profile_read": (
        "created 0061_identity_user_profile.sql:123 (bytea[], timestamptz): pre-tenant and read-only, no org chain to "
        "write into — the caller's own profile, projected from the presented cookie hash"
    ),
    "evidgate_resolve.user_profile_update": (
        "created 0061_identity_user_profile.sql:178 (bytea[], text, integer, timestamptz): pre-tenant, no org chain to "
        "write into — a person's language choice belongs to no org; the org-scoped `user.locale_changed` row is "
        "written after it by `IdentityAuditWriter` (src/Modules/Evidgate.Modules.Identity/Login/Audit/"
        "IdentityAuditWriter.cs), called from Profile/UpdateMyProfileCommand.cs"
    ),
    "evidgate_resolve.session_revoke_current": (
        "created 0048_identity_session_routes.sql:206 (bytea[], timestamptz): no per-call audit row by owner decision "
        "D-S102-Q3 — a self revoke is every caller's own logout, and logout / self revoke / revoke-all will emit a "
        "security_event from inside the 0048 definers once the session-lifecycle emitter lands: `security_event` "
        "exists since 0053 and is tenant-scoped, so that emitter is a new migration, not a route-side writer "
        "(11.5.1-T5 handed it over; open receiver)"
    ),
    "evidgate_resolve.session_revoke_others": (
        "created 0048_identity_session_routes.sql:398 (bytea[], boolean, timestamptz): no per-call audit row by owner "
        "decision D-S102-Q3 — a self-service revoke of one's own other sessions, never an act on another member; a "
        "security_event from inside the 0048 definer will cover it once the session-lifecycle emitter lands "
        "(`security_event` exists since 0053; 11.5.1-T5 handed the emitter over, and the closed V1 type set has no "
        "session type yet — 11.1.1-T4.1; open receiver)"
    ),
    "evidgate.session_cleanup": (
        "created 0048_identity_session_routes.sql:479 (timestamptz, integer): T80 sweep — a bounded bulk delete of "
        "expired and past-retention sessions, observed via the task row, exactly like `purge_expired_idempotency`"
    ),
    "evidgate.healing_switch_set_system": (
        "created 0052_healing_switch.sql:211 (text, text, uuid): the platform-wide healing kill switch "
        "belongs to no tenant, so there is no org chain to append to — `public.audit_log.org_id` is NOT "
        "NULL and every row of it is chained per org (0015). The action is attested outside the audit "
        "chain: its only caller is `POST /internal/operator/healing/switch` "
        "(src/Evidgate.Api/Endpoints/OperatorEndpoints.cs, `healing.MapPost(\"/switch\", ...)`, behind "
        "02.8.1's fail-closed `RequireOperatorFilter`), which writes a structured operator log event on "
        "every system write — mode, set_by and version, never the reason text — through "
        "`HealingSystemSwitchWriter` (src/Modules/Evidgate.Modules.Healing/Application/HealingSystemSwitch.cs), "
        "and a security_event row would be the durable record but does not fit it yet: 11.5.1-T5 "
        "shipped `security_event` (0053) and handed the platform-scope record over, because its org_id is "
        "NOT NULL, so a no-tenant write has no row; D-S137-OWNER (DECISIONS §8.2, decided 2026-10-29) "
        "puts such an event in the separate `platform_security_event` table, never a nullable org_id, "
        "and that table plus its emitter are still owed — an open receiver, "
        "exactly like the D-S102-Q3 entries above. No per-call audit row is possible here, which is why "
        "the exemption is recorded rather than the row"
    ),
    "evidgate.usage_price_insert_global": (
        "created 0073_usage_price.sql:215 (uuid, text, numeric, text, timestamptz, timestamptz, numeric, text): the "
        "rate-card loader stands in for the migration role, because a global price belongs to no tenant and the "
        "app role is refused a global row by row level security. The configuration act has no org chain to append "
        "to: the durable record is the immutable `usage_price` row it writes (scope, metric, amount, window and "
        "card version), and the tenant boundary is the function's own guard, which refuses a call made inside a "
        "tenant transaction"
    ),
}

# Roles a definer function may be granted EXECUTE to: the four runtime roles of the matrix
# 0104 and 0001 establish (tenant traffic, support reads, platform sweeps, the outbox and task
# queue machinery). Membership here says the role is one the system grants to on purpose; it
# does not say a particular function should hand it anything.
ALLOWED_GRANTEES = {"evidgate_app", "evidgate_worker_sched", "evidgate_operator", "evidgate_worker"}

# Tenant policies that shipped without a `TO` clause and were replaced later. The key is the
# EXACT statement (file, policy, table) and it counts only while a later statement re-creates the
# policy `to evidgate_app`: an unsuperseded or differently-scoped one keeps failing.
POLICY_TO_EXEMPT = {
    (file, "tenant_isolation", table): "superseded by 0055_tenant_policy_roles.sql, which recreates the policy `to evidgate_app`"
    for file, table in (
        ("0053_security_security_event.sql", "public.security_event"),
        ("0054_healing_llm_call.sql", "public.healing_llm_call"),
        ("0054_healing_llm_call.sql", "public.healing_llm_budget"),
    )
}

# An operator policy whose predicate a reviewer accepted, where the text lint cannot judge it. The
# key is the EXACT effective policy (file, policy, table), so a later statement under the same name
# is judged again, and the accepted statement must not be an open `using (true)`.
OPERATOR_GATE_EXEMPT = {
    ("0003_foundation_rls_probe.sql", "rls_probe_operator_read", "public.rls_probe"): (
        "0003:61-79, the foundation RLS canary: `rls_probe` is the probe table Isolation.Tests "
        "asserts the policy contract on (a uuid org, id, label and timestamps — no tenant business "
        "data), and its operator read is the deliberate audited read path the table exists to "
        "demonstrate. The predicate is the tenant shape on purpose: it shows that RLS filters a "
        "role that sets its own `evidgate.org_id`, which is the exposure 0055 removed from the "
        "three business tables. The file is applied, so its text cannot change."
    )
}

DEFERRAL = re.compile(r"--\s*lint:\s*audit-deferred\s+(\S+)\s+([^\n]+)")


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


def lint_definers(schema, raws: dict[str, str]) -> list[Finding]:
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
            if relation.lower() not in SQL_WORDS and relation.lower() not in ctes and not relation.lower().startswith(("batch", "removed", "v_")):
                findings.append(Finding(fname, short, "definer_unqualified", f"relation `{relation}` is not schema-qualified"))
        executable = code_copy(body, "''")  # a quoted mention is not a call
        audited = re.search(r"insert\s+into\s+\S*audit|perform\s+evidgate\.(?:session_revoke_audit_append|audit_append_user_event)\s*\(", executable, re.I)
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
    return (lint_tables(schema, exceptions)
            + lint_policies(schema, exceptions, POLICY_TO_EXEMPT, OPERATOR_GATE_EXEMPT)
            + lint_embedded(schema)
            + lint_definers(schema, raws))


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
