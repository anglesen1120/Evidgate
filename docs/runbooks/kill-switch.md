# Healing kill-switch runbook

Status: **10.7.1-T5.1 written; T5.2 staging drill open (AC6 open).**

⚠️ **Drill pending.** The repository records 10.7.1-T5.2 and AC6 as open (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md`, Acceptance criteria and Tasks). No staging drill evidence was inspected in this desk check, and no drill is claimed here. A live executor must verify database connectivity, deployed migrations and role grants, owner/platform credentials, member permissions and current cookies, the effective Apply setting, and uninterrupted ingest/runs/gates. The flip order, stored-mode checks and rollback guidance below are grounded in this checkout.

Legend as in [`../../AGENTS.md`](../../AGENTS.md): ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`. References name the route, symbol or migration in this checkout rather than unstable source line numbers; the full mapping is the [evidence table](#11-evidence-table) in §11.

## 1. Scope, audience and rules

**Purpose.** Change the healing kill switch deliberately, at the right scope, and be able to say afterwards what the mode was, who set it and how to put it back. `public.healing_switch` persists one row per scope identity (`0052_healing_switch.sql`). `HealingAvailabilityResolution.Resolve` selects the most restrictive stored mode; `HealingApplyClamp.Apply` then clamps an otherwise `enabled` answer to `shadow` when Apply is off.

**Audience.** A second on-call operator, or an incident commander, who did not build 08.6.3. You need a member session holding `healing:configure` for the tenant/project scopes (§4), and for the system scope a `psql` session as `evidgate_platform_operator`, the role whose password is `EVIDGATE_PLATFORM_OPERATOR_PASSWORD` (§5.2).

**In scope.** The three scopes of `public.healing_switch` (`system`, `tenant`, `project`), the stored mode of each, the resolved posture, and how to read both back.

**Out of scope.** The healing apply path (V2, 08.6.1); incident triage and candidate review (`healing:triage` reads, 00 §8.7); the dependency health probes that will produce `unavailable` (08.6.3-T2); every other subsystem's kill switch (none exists).

**Prerequisites.**

- A checkout of the release tag the incident is about; resolve each named symbol and migration against that release.
- For tenant/project flips: a member account holding `healing:configure` in the org (`SetHealingSwitchCommandHandler.Handle`), and the project id when you flip a project.
- **The read and the write are separate permissions, and no single built-in role has both.** `tenant_admin` holds `healing:configure` only; `qa_engineer` holds `healing:triage` only (`0010_identity_permission_catalogue.sql`, `contracts/permissions.json`). To flip and verify in one session, use a membership carrying both — `membership_role` permits several roles (`0011_identity_membership.sql`) and `IdentityAccessSql.MemberPermissionSet` unions their permissions — or a custom role, since both permission rows are tenant-assignable and non-reserved.
- For a system flip: a host with `psql` and network access to the environment's database, and the `evidgate_platform_operator` password (§5.2). No Evidgate host runs as that role. The before/after SQL reads additionally need authorized emergency access as `evidgate_owner`: runtime reader roles cannot see the platform audit rows under the current policies (§5.2).
- The org id — sent as `X-Evidgate-Org` on every member call, the read included (§5.1) — and (optionally) the project id; never paste a customer name or URL into a ticket.

**Rules.**

1. **Never write `public.healing_switch` by hand.** The supported tenant/project path is the route (§5.1); the system path is the definer `evidgate.healing_switch_set_system`, introduced by `0052_healing_switch.sql` and rewritten with its final ACL and audit insert by `0105_platform_operator_role.sql`. Its dedicated runtime grantee is `evidgate_platform_operator`, reached through `psql` (§5.2). A hand `UPDATE` bypasses the API Apply clamp, minimum-reason validation and audit/outbox path; it does **not** bypass the column CHECKs on stored modes and the 2000-character maximum.
2. **Never try to set `unavailable`.** It is a derived observation; three enforcing layers refuse to store it (§3).
3. **Write a reason.** It is optional in `healing-switch-set.v1` and in `HEALING_SWITCH_COPY.reasonLabel`, but it is the human context stored with the change. Tenant/project audit metadata records the new mode; platform audit records old/new modes, not the reason.
4. **A first write on a scope has no earlier mode to restore.** A system flip leaves `old_mode` in `public.platform_operator_audit` — `null` when that call created the row — while a tenant/project audit entry stores only the new mode, and a `PUT` on a scope with no row **creates** it (§7). For a scope with no row, record the posture you replaced. Record the `X-Request-Id` the response **echoes** for every member flip (send a safe token of your own when you want the value known in advance) — the echoed value is the only handle that ties the audit entry back to your own write (§5.1).
5. **One intent per attempt.** Reuse the same `Idempotency-Key` only to retry the same canonical request; a different request under a reused key is `409 idempotency_conflict` (`SetHealingSwitchCommand.BodyHash`, `IdempotencyBehaviour.Handle`, OpenAPI `setHealingSwitch`).
6. The switch stores configuration plus a free-text reason, which may contain customer or incident details. Never treat a mode as a statement about a run.

## 2. The three scopes, and the order to flip them

| Scope | Row identity | Who writes it | Route |
|---|---|---|---|
| `system` | one row, `org_id` NULL; partial unique `healing_switch_system_key` (`0052_healing_switch.sql`) | a human in `psql` as `evidgate_platform_operator`, through the definer; final grant in `0105_platform_operator_role.sql` | `select * from evidgate.healing_switch_set_system(…)` (§5.2); the operator POST remains fail-closed |
| `tenant` | one row per org; `healing_switch_tenant_key` (`0052_healing_switch.sql`) | a member holding `healing:configure` | `PUT /v1/healing/switch` with `scope: tenant` (`HealingEndpoints.MapHealingEndpoints`) |
| `project` | one row per org/project; `healing_switch_project_key` (`0052_healing_switch.sql`) | the same permission, project in scope | `PUT /v1/healing/switch` with `scope: project` and `project_id` |

The scope vocabulary is a closed CHECK, `healing_switch_scope_check` in `0052_healing_switch.sql`, and the same three literals in `HealingSwitchScopes`.

### 2.1 How the effective mode resolves

- `HealingSwitchStore.ReadAsync` runs `HealingSwitchSql.Resolution`: one statement over at most three rows, the platform row every org inherits, the org's tenant row, and the project row named by `project_id`.
- `HealingAvailabilityResolution.Resolve` ranks the modes `enabled` < `shadow` < `disabled` < `unavailable`. The GET and resolver then call `HealingApplyClamp.Apply`: if Apply is off, an otherwise `enabled` resolution is reported as `shadow`. Each scope view still shows its stored mode (`GetHealingSwitchQueryHandler.Handle`).
- With no row at any scope, `HealingAvailabilityResolution.NoRowDefault` is `shadow`, the V1 posture that decides but never applies (`plans/00-overview.md`, Healing availability).
- `healing_switch_app_read` in `0052_healing_switch.sql` lets every tenant read the system row. `HealingSwitchView.Redacted` exposes its stored mode, time and version but returns null `set_by` and `reason` (OpenAPI `getHealingSwitch`).

Two consequences to keep straight:

- **A narrow scope can only tighten an existing ancestor's stored posture, never loosen it.** A tenant row set to `enabled` under a `shadow` system row cannot change the resolution to `enabled`. The system row is inherited by every org; the final Apply clamp is separate (`HealingAvailabilityResolution.Resolve`, `HealingApplyClamp.Apply`).
- **An inner write is not refused by the API when an ancestor is stricter.** `SetHealingSwitchCommandHandler.Handle` checks project scope, body shape and mode, not ancestor rows; it stores a row that may not change the resolution. The web UI is stricter: `HealingSwitchStore.blocker` makes an editor read-only beneath a `disabled` ancestor, enforced by `SwitchScopeControl.writable`.

### 2.2 The order to flip

10.7.1-T5.1 names the order **system → tenant → project**, and AC6 flips each scope through `enabled → shadow → disabled` (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md`, Acceptance criteria and Tasks). Operationally:

1. Flip `system` first: it is the posture every org inherits, so that is the step whose effect is platform-wide.
2. Flip `tenant`: it can only tighten the system posture.
3. Flip `project`: it can only tighten the tenant posture.

Because stored-mode resolution is "most restrictive wins", the **destination does not depend on the order** — the sequence exists so that each step is verified deliberately, especially the platform-wide row. The final Apply clamp does not change that order independence. `[INFERENCE]` from `HealingAvailabilityResolution.Resolve` and `HealingApplyClamp.Apply`, not a separate contract clause.

One UI caveat: once the system row is `disabled`, the tenant and project editors are read-only (`HealingSwitchStore.blocker`). If a drill must use the UI for its inner writes, carry the system row to `disabled` last; the API does not impose that ancestor restriction.

## 3. `unavailable` is derived and cannot be set

The mode vocabulary has four values, but `HealingAvailabilityModes.Storable` contains only `enabled`, `shadow`, `disabled`. `unavailable` is a dependency observation, never a configured value. `GetHealingSwitchQueryHandler.Handle` currently returns null observations and neither switch resolver consults a health probe.

Three enforcing layers refuse to store it. The route schema is a separate routing choice, **not** a fourth refusing layer:

| Layer | Refusal / behavior | Definition |
|---|---|---|
| Column CHECK | `mode in ('enabled','shadow','disabled')`; violation is `23514` | `healing_switch_mode_check`, `0052_healing_switch.sql` |
| Route contract schema | A bounded string deliberately accepts `unavailable`, so the handler can return its specific error; malformed/overlong schema values can instead be `request_invalid` | `contracts/schemas/healing-switch-set.v1.json`, `mode` |
| Tenant/project handler | Anything outside the three stored tokens is `422 invalid_switch_mode` once schema validation succeeds | `HealingModeGate.Check`, called by `SetHealingSwitchCommandHandler.Handle` |
| Operator definer | Other modes raise `22023 invalid_parameter_value` | `evidgate.healing_switch_set_system`, `0105_platform_operator_role.sql` |

How to verify it on a live deployment:

1. `GET /v1/healing/switch?project_id=…` with the session cookie and `X-Evidgate-Org` (§5.1), and read `resolved` (OpenAPI `getHealingSwitch`). It is currently one of `enabled`, `shadow`, `disabled`, subject to the Apply clamp, and `dependency_health` is `{"llm": null, "knowledge_store": null}` (`GetHealingSwitchQueryHandler.Handle`). Null means "not observed", never "healthy".
2. `PUT /v1/healing/switch` with a valid body containing `"mode": "unavailable"` answers `422 invalid_switch_mode` with a detail on `mode` (`HealingModeGate.Check`). Do this once in the pending drill and keep the response as the AC6 negative-check evidence.
3. `SELECT mode FROM public.healing_switch` cannot return a stored `unavailable` under `healing_switch_mode_check` (`0052_healing_switch.sql`). A member reads through GET; direct SQL needs `evidgate_owner`'s emergency read path (§5.2), since `evidgate_operator` sees zero rows.

## 4. Who may flip which scope

| Scope | Permission / credential | Where it is enforced | Who holds it | State |
|---|---|---|---|---|
| `tenant`, `project` | `healing:configure` | `[Permission(Permissions.Healing.Configure)]`, `SetHealingSwitchCommandHandler` | built-in `tenant_admin` only; tenant-assignable/non-reserved (`0010_identity_permission_catalogue.sql`, `contracts/permissions.json`) | ✅ shipped |
| `tenant`, `project` (read) | `healing:triage` | `[Permission(Permissions.Healing.Triage)]`, `GetHealingSwitchQueryHandler` | built-in `qa_engineer` only; tenant-assignable/non-reserved (same catalogue) | ✅ shipped |
| `system` (HTTP) | planned `platform_operator` identity and audited support grant | `RequireOperatorFilter` on the operator route | planned platform operator | ⚠️ `DenyAllOperatorGate` denies every caller; current app-role writer also lacks definer EXECUTE |
| `system` (database) | dedicated runtime EXECUTE grant to `evidgate_platform_operator`; `PUBLIC`, app, operator, worker and scheduler revoked | definer ACL in `0105_platform_operator_role.sql` | authorized person at `psql`, never the Api process | ✅ shipped migration; deployment verification pending |

Two boundaries worth stating explicitly:

- The definer **refuses a tenant context** with `23514`, even if its caller holds EXECUTE (`evidgate.healing_switch_set_system`, `0105_platform_operator_role.sql`).
- `evidgate_operator` has SELECT grants on `public.healing_switch` and `public.platform_operator_audit` but no matching RLS policies, so those reads return **zero rows** (`0052_healing_switch.sql`, `0105_platform_operator_role.sql`). `evidgate_platform_operator` has neither table privilege; `evidgate_app` has no platform-audit grant. The reads in §5.2 therefore require authorized emergency `evidgate_owner` access, whose owner policies allow them; do not substitute an operator login or a generic privileged account.

Tenant/project attribution comes from the authenticated member user id (`SetHealingSwitchCommandHandler.Handle`). A system write accepts the caller's asserted `p_set_by` UUID, stored as switch `set_by` and platform-audit `actor` (`evidgate.healing_switch_set_system`).

## 5. How to flip

### 5.1 The tenant/project route

`PUT /v1/healing/switch` (`HealingEndpoints.MapHealingEndpoints`, OpenAPI operation `setHealingSwitch`).

Headers:

- `Idempotency-Key` — mandatory, 1–128 characters; the OpenAPI `IdempotencyKey` parameter defines uniqueness per `(org_id, project_id, operation)` for at least 24 h. `HealingEndpoints.SetHealingSwitchAsync` answers 400 when missing; `IdempotencyBehaviour.Handle` enforces the length.
- `X-Evidgate-Org` — **required on every member request to this route**. There is no `{o}` route segment, so `TenantResolutionFilter` obtains the org from this header. `TenantResolver.ResolveMemberAsync` refuses its absence before membership lookup with `422 request_invalid`, detail `X-Evidgate-Org: required`; `ApiHost` attaches that filter to the healing group.
- **The member cookie, not a bearer token.** `SetHealingSwitchCommandHandler.Handle` requires a member user id. `SessionRequestAuthenticator.ResolveAsync` refuses session authentication whenever an `Authorization` header is present, before reading a cookie. `IdentityModule` registers that session path and `CiProducerAuthenticator`, whose bearer credential is a service principal, not this member. Use the `__Host-evidgate_session` cookie (`SessionCookieNames.Session`).
- `X-XSRF-TOKEN` — required for the cookie-authenticated write: echo the script-readable `XSRF-TOKEN` cookie, paired with HttpOnly `__Host-evidgate_af` (`SessionCookieNames`). `SessionXsrfMiddleware` refuses a missing/mismatched pair with 403 `csrf_failed` before the handler; see OpenAPI `XsrfToken`.
- `X-Request-Id` — optional but worth choosing: `CorrelationNames.IsSafeRequestId` accepts only 1–128 visible-ASCII characters (`0x21`–`0x7E`, no spaces). `RequestIdMiddleware.Invoke` replaces an unsafe value and **echoes the value actually used**; `AuditTrail.FlushAsync` stores it as `request_id`. Record that echoed value for §7. Retry a lost response with the SAME request id and idempotency key: `IdempotencyBehaviour.Handle` replays the original body and suppresses a second audit entry, so a fresh retry request id does not identify the original write.
- **No `If-Match`.** The response includes `version`, but this route accepts no expected-version precondition; the upsert is not a compare-and-swap (`HealingSwitchSql.UpsertTenant` / `UpsertProject`, OpenAPI `setHealingSwitch`).

Body — the closed `healing-switch-set.v1` schema: `scope` (use `tenant` or `project`; `system` is parsed then refused), `mode` (one of the three stored tokens), nullable `project_id`, and optional/null `reason` of 1–2000 Unicode code points when supplied as text. `SetHealingSwitchCommandHandler.Handle` requires a project id for `project` and rejects a non-null one for `tenant`. The serialized body is capped at 4096 bytes before JSON parsing (`HealingEndpoints.ReadBodyAsync`); larger input is `413 payload_too_large`. `HealingModeGate` counts code points rather than UTF-16 units.

```bash
# $JAR holds the member session's cookies: __Host-evidgate_session plus the antiforgery pair.
# How to obtain them: see docs/dev-setup.md, "Signing in to the web app against the real API",
# for the Development-only POST /v1/dev/session cookie-jar example. A deployed environment
# uses its configured web sign-in; verify the actual session and permissions before this drill.
CSRF=$(awk '$6 == "XSRF-TOKEN" { print $7 }' "$JAR")

curl -sS -X PUT "$API/v1/healing/switch" \
  -b "$JAR" -c "$JAR" \
  -H "X-Evidgate-Org: $ORG" \
  -H "X-XSRF-TOKEN: $CSRF" \
  -H "X-Request-Id: $REQUEST_ID" \
  -H "Idempotency-Key: $KEY" \
  -H 'Content-Type: application/json' \
  -D /tmp/healing-put.headers \
  -d '{"scope":"tenant","mode":"disabled","reason":"INC-… healing off while the model endpoint is degraded"}'
```

`-b`/`-c` preserve refreshed cookies, and `-D` captures the response headers whose echoed `X-Request-Id` §7 uses. Re-read `CSRF` from the updated jar before each new PUT; a rotating session refreshes its antiforgery pair (`SessionRequestAuthenticator.ResolveAsync`).

A project flip uses the same route and header names with `"scope":"project"` and the actual project UUID in `project_id`, plus a fresh intent key/request id. Expand `$PROJECT` before sending the JSON; a literal `"$PROJECT"` is not a UUID.

Answers: 200 with `HealingSwitchView` (scope, project_id, mode, reason, set_by, at, version); 403 `forbidden_scope` for `system` before a lookup; 422 for missing/not-allowed project id; 403 `project_scope_mismatch` for a mismatched credential/request project; 404 for unknown, soft-deleted or other-tenant project; 409 `idempotency_conflict`; 413 `payload_too_large` (`SetHealingSwitchCommandHandler.Handle`, `HealingScopeGuard.Project`, `HealingEndpoints.ReadBodyAsync`, OpenAPI `setHealingSwitch`).

### 5.2 The system flip: `psql` as `evidgate_platform_operator`

The supported platform write is `evidgate.healing_switch_set_system(p_mode text, p_reason text, p_set_by uuid)`, introduced in `0052_healing_switch.sql` and rewritten in `0105_platform_operator_role.sql` to append durable audit. The latter revokes EXECUTE from `PUBLIC`, `evidgate_app`, `evidgate_operator`, `evidgate_worker` and `evidgate_worker_sched`, granting it to `evidgate_platform_operator` as the dedicated runtime caller. No Evidgate host uses that login.

**Step 1 — record the current row and audit tail.** Both reads below are an authorized **emergency owner path**, not a normal operator read. `evidgate_platform_operator` has no table privilege; `evidgate_operator` has SELECT grants but no matching FORCE-RLS policies; `evidgate_app` cannot read platform audit. `evidgate_owner` owns the migrated tables and has `healing_switch_owner_backfill` (`0052_healing_switch.sql`) plus `platform_operator_audit_owner_read` (`0105_platform_operator_role.sql`). Use that explicitly authorized credential, never an unspecified “DBA”.

```bash
PGPASSWORD="$EVIDGATE_OWNER_PASSWORD" psql -h "$DB_HOST" -p "$DB_PORT" -U evidgate_owner -d "$DB_NAME" \
  -c "select mode, set_by, at, version from public.healing_switch where scope = 'system'" \
  -c "select actor, action, old_mode, new_mode, version, created_at from public.platform_operator_audit order by created_at desc limit 5"
```

**Step 2 — call the definer as the platform operator.** The password goes into `PGPASSWORD` only: never on the command line, never inside `psql -c` text, never echoed.

```bash
# The same value as EVIDGATE_PLATFORM_OPERATOR_PASSWORD in the environment's
# secret delivery: read it into the environment, do not paste it into history.
read -rsp 'evidgate_platform_operator password: ' PGPASSWORD; echo
export PGPASSWORD
psql -h "$DB_HOST" -p "$DB_PORT" -U evidgate_platform_operator -d "$DB_NAME" -v ON_ERROR_STOP=1 \
  -c "select * from evidgate.healing_switch_set_system('disabled', 'INC-… healing off while the model endpoint is degraded', '$OPERATOR_ID')"
unset PGPASSWORD
```

- `$OPERATOR_ID` is the flipping person's user UUID, asserted by the caller and not verified against a user table. The shared login proves possession of the password, not per-person identity; `p_set_by` becomes both `actor` and `set_by` (`evidgate.healing_switch_set_system`). Do not supply a tenant id.
- `$DB_HOST`, `$DB_PORT`, `$DB_NAME` correspond to the bootstrap's `EVIDGATE_DB_HOST`, `EVIDGATE_DB_PORT`, `EVIDGATE_DB_NAME` (`scripts/env/bootstrap-db.sh`). Actual coordinates, credentials and deployed ACLs remain drill prerequisites, not desk-check observations.
- Stored modes are `enabled`, `shadow`, `disabled`. Invalid modes and reasons over 2000 characters raise `22023`; a non-null tenant context raises `23514` (`0105_platform_operator_role.sql`). Use a fresh plain `psql` connection with no tenant GUC.
- The function returns `mode`, `reason`, `set_by`, `at`, `version`. Creation starts at version 1; an update increments it. Record the **returned** version. Reason stays on the switch row and is not copied into platform audit because it can contain customer details.

**Step 3 — identify this call's audit row.** Read as the same authorized emergency `evidgate_owner` role from step 1. Find `action = 'healing.switch.system_set'`, `actor = $OPERATOR_ID` and `version` equal to the function's returned version. Do not assume the newest row or the first five rows still contain your call: another operator can write after it. Its `old_mode` is the value actually replaced, or null on creation, and `new_mode`/`version` must match the returned row (`0105_platform_operator_role.sql`). If `old_mode` differs from the before-state you recorded, reconcile the concurrent change before rollback; there is no expected-version precondition.

The Apply write gate is Api-side, not in the definer: `HealingSystemSwitchWriter.SetAsync` rejects `enabled` while Apply is off, whereas SQL accepts it. On read, `HealingApplyClamp.Apply` additionally turns an otherwise `enabled` resolution into `shadow` while Apply is off; scope views still show stored `enabled`. Storing it through SQL does not demonstrate that healing apply is on.

**Not possible yet:** `POST /internal/operator/healing/switch` cannot write on the shipped host. `DenyAllOperatorGate` refuses every caller; even with a gate override, the app-role writer lacks definer EXECUTE and returns a forbidden result. The operator-access receiver is 02.8.1-T2.1 (`plans/features/02-identity-tenancy/02.8.1-time-boxed-operator-access.md`), alongside the support-grant work in T1. Its operator-role access plan does not itself specify a credential authorized to call this platform-only definer: that system-write wiring remains an owner decision. Keep using the dedicated SQL path until both boundaries are explicitly resolved.

### 5.3 The web UI

Path: project navigation **Availability**, id/segment `availability`, permission `healing:triage` (`NAV_ENTRIES` in `nav-registry.ts`). `AREA_ROUTES.availability` loads `availability.routes.ts`, whose empty route mounts `HealingSwitchPage`.

The page shows resolved posture and dependency observations, a three-scope hierarchy table (`SwitchScopeHierarchy`), and two configuration editors for tenant/project (`HealingSwitchPage` template):

- **System scope** is read-only in the hierarchy. `HealingSwitchStore` only constructs tenant/project write bodies.
- **Tenant/project** editors offer `enabled`, `shadow`, `disabled`, an optional reason and a save control (`SwitchScopeControl`, `HEALING_SWITCH_COPY`).
- **Both permissions are needed:** `HealingSwitchPage.canRead` requires `healing:triage`; `HealingSwitchStore.canConfigure` gates save with `healing:configure`. A lone `tenant_admin` cannot load the page, while a lone `qa_engineer` reads without saving.
- `reasonExceedsLimit` checks 2000 code points and the 4096-byte serialized body before saving.
- `HealingSwitchPage.canEdit`, `blocker` and `SwitchScopeControl.writable` refuse edits without configure permission, with `unavailable`, or below a disabled ancestor. Narrow-viewport mutation policy also applies (`NarrowMutationPolicy`).
- `HealingSwitchStore.save` reloads the server resolution after success rather than treating the form's value as the new resolved posture.
- With Apply off — the code/shipped-configuration default, not an observation of your deployment — the selector still offers `enabled` but `HealingModeGate.Check` returns 422 `mode_not_available`; practical API modes are `shadow` and `disabled`.

## 6. Procedure, with verification after each flip

Record the before-state first with `healing:triage`. Send the `__Host-evidgate_session` cookie and `X-Evidgate-Org` on every healing GET/PUT: `TenantResolver.ResolveMemberAsync` rejects a missing org, and `SessionRequestAuthenticator.ResolveAsync` does not authenticate the cookie when an Authorization header is present. `GET /v1/healing/switch?project_id=…` returns system/tenant/project views (`GetHealingSwitchQueryHandler.Handle`). Record `resolved`, each stored mode/version, the request ids and observation times. For a system flip, also retain the durable platform-audit entry matched by returned version and actor (§5.2); migration `0105_platform_operator_role.sql` supplies that record.

Then, in the order of §2.2:

1. **System.** `psql` as `evidgate_platform_operator`, calling `evidgate.healing_switch_set_system(...)` with the new mode and a reason (§5.2).
   Verify: the returned mode is correct and version is 1 on creation or incremented on update. Identify the platform audit row by returned version and actor (§5.2), recording its actual `old_mode`, `new_mode` and `version` (`0105_platform_operator_role.sql`). The member GET reports the stored `system.mode`; `resolved` is the most restrictive stored mode **after** the Apply clamp (`GetHealingSwitchQueryHandler.Handle`).
2. **Tenant.** `PUT /v1/healing/switch` with `scope: tenant` (§5.1).
   Verify: GET reports the new `tenant.mode`; a fresh update increments version. Resolution is recomputed through `HealingAvailabilityResolution.Resolve` and `HealingApplyClamp.Apply`. A weaker tenant row may leave `resolved` unchanged; an idempotent replay returns the original version rather than updating again.
3. **Project.** `PUT /v1/healing/switch` with `scope: project` and the project id (§5.1).
   Verify: `GET /v1/healing/switch?project_id=…` reports the new `project.mode` and fresh-write version, with the project's resolution recomputed through the same Apply clamp.
4. **`unavailable` check.** `PUT` with `"mode": "unavailable"` and expect 422 `invalid_switch_mode` (§3). Nothing in storage changes.

What is unaffected, and how that is known:

- **No direct ingest/run/gate reader was found, but the resolver does have a consumer.** `HealingLlmGateway.AdmissionRefusalAsync` calls `IHealingAvailabilityResolver.ResolveAsync`: enabled/shadow permit LLM admission; disabled/unavailable refuse it. `Refuse` records `evidgate_healing_llm_refused_total` through `EvidgateTelemetry.RecordHealingLlmRefused`. A flip is therefore not behaviorless; uninterrupted core operation remains a live-drill assertion.
- **Tenant/project changes emit one outbox event per stored version.** `HealingSwitchEvents.EnqueueAsync` produces `healing.switch_changed`; no handler registers that task type yet. The SQL system function instead appends platform audit and emits no such outbox event (`0105_platform_operator_role.sql`).
- **Apply remains V2.** 08.6.1-T1.5 owns flag-gated apply/approve registration and switch checks (`plans/features/08-healing-learning/08.6.1-validation-pr-and-approval-binding.md`). `DECISIONS.md`, Safe-healing rule 9, requires switches before apply and uninterrupted tests/gates on dependency failure; `HealingModeGate` and `HealingApplyClamp` enforce the current default-off posture.
- ⚠️ **Drill pending:** 10.7.1-T5.2 must observe uninterrupted ingest/runs/gates. No such observation is claimed here. **Not possible yet:** dependency-derived `unavailable` needs 08.6.3-T2.1. In-flight re-checks, including between gateway retries, belong to 08.6.3-T2.2 (`plans/features/08-healing-learning/08.6.3-kill-switches-and-degradation.md`); current gateway admission is not a physical cancellation channel for an already-started provider call, and that task defines no such channel.

## 7. Rolling a flip back

Stored modes remain `enabled`, `shadow`, `disabled` (`healing_switch_mode_check`, `0052_healing_switch.sql`). Supported rollback is another flip, not deletion: the app role has no DELETE grant and `HealingEndpoints.MapHealingEndpoints` exposes no delete route. A first write has no previous stored row; distinguish restoring its former **effective mode** from recreating the absent-row storage state.

Steps:

1. **Find the previous mode.**
   - Tenant/project: use `GET /v1/orgs/{o}/audit?object_type=healing_switch&action=healing.switch.set`, requiring `audit:read` (OpenAPI `listAudit`). `AuditListResponse` is newest-first, paged with `cursor = next_cursor` and `limit` 1–200. There is no `object_id` filter: match the original flip's echoed `X-Request-Id` to `AuditEntryDto.RequestId`, then retain that entry's `ObjectId`. The GET/PUT `HealingSwitchView` deliberately omits the switch row id. Find the next older entry with the same object id; its `metadata.mode` is the previous stored mode (`SetHealingSwitchCommandHandler.Handle`, `AuditEntryDto`). If request-id correlation is unavailable, use the recorded actor/time only as a fallback and leave the conclusion `[OPEN]` when ambiguous.
   - **First write leaves no previous stored mode.** After finding the correct audit object, absence of a predecessor in the complete chain identifies creation; do not infer it from an incomplete page or ambiguous match. Preserve the pre-flip GET's `resolved`. That effective mode may be submitted again when accepted by the current Apply setting, but doing so does not recreate the absent row. **Not possible yet:** there is no supported absent-row undo and no DELETE-enabling task in the checked switch plan. 10.7.1-T5.1's rollback guidance needs an owner-approved first-write recovery procedure; 10.7.1-T5.2 must exercise it. `[OPEN]` A deliberately conservative flip to `disabled` is accepted, but is not an exact rollback to an earlier shadow/enabled posture.
   - System: read `public.platform_operator_audit` through the authorized emergency `evidgate_owner` path in §5.2. Match action, actor and the version returned by your original call; its `old_mode` is the actual previous stored mode (`0105_platform_operator_role.sql`). Null means **no system row**, not necessarily a shadow effective posture: narrower rows and the Apply clamp determined the pre-flip GET. Only no rows at any scope invokes `HealingAvailabilityResolution.NoRowDefault`. Api log event `8603` (`HealingSystemSwitchWriter.LogSystemSwitchSet`) is a legacy operational trace, not the SQL path's durable record.
2. **Re-flip that scope to the previous mode**, with an incident/rollback reason (§5.1 / §5.2). Use a **new** `Idempotency-Key` for this new tenant/project intent; changing the body under the old key is `409 idempotency_conflict` (`IdempotencyBehaviour.Handle`, OpenAPI `setHealingSwitch`).
3. **Verify resolved posture**, not only the stored row: GET the same project and compare against the recorded before-state. A stricter ancestor or the Apply clamp can keep `resolved` unchanged (`GetHealingSwitchQueryHandler.Handle`). First-write recovery must explicitly state when the original posture was not restored.
4. **Interpret version correctly.** A fresh update increments it through `healing_switch_set_updated_at_and_version` (`0052_healing_switch.sql`); creation starts at 1. An idempotent replay legitimately returns the original 200 body/version without a write (`IdempotencyBehaviour.Handle`). Distinguish that case before treating an unchanged version as a failed fresh update.

`[INFERENCE]` Rollback follows the scope order of §2.2 for predictable verification; destination resolution is order-independent (`HealingAvailabilityResolution.Resolve`, `HealingApplyClamp.Apply`). Reconcile concurrent writes before choosing a rollback mode.

## 8. Audit trail

**Tenant/project flips are audited in the org chain.** `SetHealingSwitchCommandHandler` carries `[AuditedAction("healing.switch.set")]` and describes one audit object for each successful fresh write; idempotent replay suppresses another audit entry (`IdempotencyBehaviour.Handle`).

| Recorded | Value | Cite |
|---|---|---|
| `action` | `healing.switch.set` | `SetHealingSwitchCommandHandler` audit attribute |
| `object_type` / `object_id` | `healing_switch` / stable switch row id | `SetHealingSwitchCommandHandler.Handle`; `HealingSwitchSql.UpsertTenant` / `UpsertProject` do not update id |
| `metadata` | new `mode` only; no scope/project/version metadata | `SetHealingSwitchCommandHandler.Handle`; `AuditMetadata` allowlist |
| actor, time, chain | `actor_type`/`actor_id`, `created_at`, `seq`/`prev_hash`/`entry_hash` | `0015_identity_audit_log.sql`; `AuditEntryDto` |

The old mode is not in the new tenant/project entry; find the preceding entry for that object. `HealingSwitchEvents.Payload` contains scope, project id, mode and version, never reason.

**System flips use platform audit, not the tenant chain.** `public.audit_log.org_id` is NOT NULL and its chain is per org (`0015_identity_audit_log.sql`), while system scope has no org. `0105_platform_operator_role.sql` makes `evidgate.healing_switch_set_system` insert one `public.platform_operator_audit` row in the same transaction as the switch write:

| Column | Value |
|---|---|
| `actor` | the `p_set_by` uuid passed to the definer — the operator's `user` id |
| `action` | `healing.switch.system_set` |
| `old_mode` | the mode the row held before the call, `null` when the call created it |
| `new_mode` | the mode just stored |
| `version` | the switch row's version after the call |
| `created_at` | when the call ran |

Reason is omitted because it can contain customer details. `platform_operator_audit` follows `0088_platform_sweep_audit.sql`: no `org_id`, FORCE RLS, owner-only insert/read policies, and an append-only UPDATE/DELETE guard raising `23514` when a row reaches it. `0105_platform_operator_role.sql` grants `evidgate_operator` SELECT but no policy, so it sees zero rows; app/worker/scheduler/platform-operator table privileges are revoked. The `platform` exception is in `contracts/rls-exceptions.txt`. Use only the authorized emergency `evidgate_owner` reader in §5.2.

This implements the switch half of carried row **"A-F2 healing_switch_set_system has no durable audit record (S301)"** (`docs/status-carried.md`, same row title; `docs/security/resolver-review.md`, A-F2). Those historical tracker descriptions do not override the migration. Other org-less security events still await the separate platform security-event receiver (D-S137-OWNER); tracker close-out belongs to its status owner.

## 9. Known gaps

- **Durable system audit exists in the migration.** `0105_platform_operator_role.sql` implements A-F2's switch half (§8). ✅ Actual deployment remains drill-pending.
- **Not possible yet: system flip through operator HTTP.** `DenyAllOperatorGate` and the definer ACL both block it (§5.2). Receiver: 02.8.1-T2.1 plus support-grant work in 02.8.1-T1; the dedicated platform-write credential wiring is not specified by that task and remains an owner decision. `[OPEN]`
- **Not possible yet: the full API/UI enabled leg with shipped defaults.** Apply defaults false (`HealingOptions`, `infra/config-matrix.json`), so `HealingModeGate.Check` returns 422 `mode_not_available`. 08.6.1-T1.5 owns the flag-gated apply path, and 08.6.3-T3 owns its prerequisite drill (`plans/features/08-healing-learning/08.6.1-validation-pr-and-approval-binding.md`, Rollout). The definer accepts stored `enabled`, but `HealingApplyClamp` reports `shadow` while Apply is off; SQL storage alone does not verify enablement. Check the actual deployment flag in the pending drill. `[OPEN]`
- **The gateway consumes resolution; in-flight control remains incomplete.** `HealingLlmGateway.AdmissionRefusalAsync` refuses disabled/unavailable before admission. **Not possible yet:** its retry-loop re-check needs 08.6.3-T2.2. No physical cancellation channel for an already-started provider call is implemented or specified by that task; do not claim one. `[OPEN]`
- **Not possible yet: observe dependency-produced unavailable.** `GetHealingSwitchQueryHandler.Handle` returns null dependency fields until 08.6.3-T2.1 implements `IHealingDependencyHealth` (`plans/features/08-healing-learning/08.6.3-kill-switches-and-degradation.md`). This runbook can check non-settability, not observe that future degradation path. `[OPEN]`
- **Not possible yet: link the healing-false-positive deployment alert or run its planned lint.** 10.3.1-T5.1 owns `infra/alerts/<env>/` and `scripts/check-alerts.sh` (`plans/features/10-operations/10.3.1-telemetry-and-alerting.md`, Tasks). Neither exists in this checkout; the planned alert's runbook target remains **this file**. The security-event catalogue in `docs/security/alert-rules.md` is a different surface, not proof that this deployment alert exists. `[OPEN]`
- **Drill pending:** 10.7.1-T5.2 and AC6 remain open in the repository. The live executor must verify tooling/connectivity, deployed schema/ACLs and credentials, member permissions/current cookies, effective Apply configuration, and uninterrupted ingest/runs/gates. No second-person staging execution is claimed. `[OPEN]`

## 10. Related runbooks

- [dead-letter](dead-letter.md) — failed queue work; its §5.4 describes durable replay audit and the remaining fail-closed ingress limitation, not a shared missing-audit gap.
- [stuck run](stuck-run.md) — a run past its collection deadline.
- [incident process](incident-process.md) — severity ladder, on-call expectation and postmortem (10.7.1-T6).
- [breach](breach.md) — security incidents; a kill-switch flip during one belongs in its timeline.
- [environments](environments.md) — deployment configuration, credentials and database bootstrap. For Development-only session/cookie-jar acquisition, see `docs/dev-setup.md`, "Signing in to the web app against the real API"; deployed environments use their configured web sign-in.

## 11. Evidence table

Every route, permission, table, column and UI path this runbook uses, and where it is defined.

| Identifier | Kind | Cite |
|---|---|---|
| `public.healing_switch` | table/columns | `0052_healing_switch.sql`, table definition |
| `healing_switch.scope` (`system`, `tenant`, `project`) | column + CHECK | `healing_switch_scope_check`, `0052_healing_switch.sql` |
| `healing_switch.mode` (`enabled`, `shadow`, `disabled`; no `unavailable`) | column + CHECK | `healing_switch_mode_check`, `0052_healing_switch.sql` |
| `healing_switch.reason` (nullable, ≤ 2000 chars) | column + CHECK | `healing_switch_reason_length_check`, `0052_healing_switch.sql` |
| `healing_switch.set_by`, `.at`, `.version`, `.updated_at` | columns/trigger | `0052_healing_switch.sql`, `healing_switch_set_updated_at_and_version` |
| `healing_switch_scope_org_check`, `healing_switch_scope_project_check` | CHECKs | `0052_healing_switch.sql` |
| `healing_switch_system_key`, `_tenant_key`, `_project_key` | partial uniques | `0052_healing_switch.sql` |
| `healing_switch_app_read`, `_app_write`, `_owner_backfill` | FORCE-RLS policies | `0052_healing_switch.sql` |
| switch table grants: app SELECT + column INSERT/UPDATE, no DELETE; operator SELECT/no policy; scheduler none | grants | `0052_healing_switch.sql`; emergency owner reader explained in §5.2 |
| `evidgate.healing_switch_set_system(text, text, uuid)` | definer | `0052_healing_switch.sql`; final body/audit/ACL in `0105_platform_operator_role.sql` |
| definer EXECUTE: dedicated platform operator, listed runtime roles/PUBLIC revoked | ACL | `0105_platform_operator_role.sql` |
| `public.platform_operator_audit` (`actor`, `action`, `old_mode`, `new_mode`, `version`, `created_at`) | table/append-only/RLS | `0105_platform_operator_role.sql`; `contracts/rls-exceptions.txt` |
| `evidgate_platform_operator` (no table privileges; schema usage + definer EXECUTE) | role | `0105_platform_operator_role.sql`; `scripts/env/bootstrap-db.sh` credentials mode |
| `PUT /v1/healing/switch` | route | `HealingEndpoints.MapHealingEndpoints`; OpenAPI `setHealingSwitch` |
| `GET /v1/healing/switch` | route | `HealingEndpoints.MapHealingEndpoints`; OpenAPI `getHealingSwitch` |
| `POST /internal/operator/healing/switch` (fail-closed) | route | `OperatorEndpoints.MapOperatorEndpoints`, `SetHealingSwitchSystemAsync`; `DenyAllOperatorGate` |
| `healing:configure` | permission/catalogue | `Permissions.Healing.Configure`; `contracts/permissions.json`; `0010_identity_permission_catalogue.sql` |
| `healing:triage` | permission/catalogue | `Permissions.Healing.Triage`; same catalogue sources |
| `membership_role` supports several roles | table/unique | `0011_identity_membership.sql`, `membership_role_membership_role_key` |
| permission union over membership roles | authorization | `IdentityAccessSql.MemberPermissionSet` / `MemberGrantJoin` |
| `healing-switch-set.v1` (closed, 4096 bytes, bounded-string mode) | schema | `contracts/schemas/healing-switch-set.v1.json` |
| `Idempotency-Key` (required, ≤ 128 chars, ≥ 24 h contract) | header | OpenAPI `IdempotencyKey`; `HealingEndpoints.SetHealingSwitchAsync`; `IdempotencyBehaviour.Handle` |
| `X-Evidgate-Org` (required on healing member calls) | header | `TenantResolver.OrgHeader` / `ResolveMemberAsync`; `TenantResolutionFilter` |
| `__Host-evidgate_session`; Authorization excludes cookie auth | member authentication | `SessionCookieNames.Session`; `SessionRequestAuthenticator.ResolveAsync`; `IdentityModule` |
| `XSRF-TOKEN`, `__Host-evidgate_af`, `X-XSRF-TOKEN` | antiforgery | `SessionCookieNames`; `SessionXsrfMiddleware`; OpenAPI `XsrfToken` |
| `X-Request-Id` (bounded, echoed and carried in audit) | correlation | `CorrelationNames.IsSafeRequestId`; `RequestIdMiddleware.Invoke`; `AuditTrail.FlushAsync` |
| no `If-Match`; response version is not an expected-version precondition | write contract | OpenAPI `setHealingSwitch`; `HealingSwitchSql.UpsertTenant` / `UpsertProject` |
| `HealingAvailability` / `HealingAvailabilityModes` | enum/wire vocabulary | `src/Modules/Evidgate.Modules.Healing.Contracts/HealingAvailability.cs` |
| `HealingAvailabilityResolution.Resolve` / `Rank` / `NoRowDefault` | pure ranking/default | same Contracts file |
| `HealingApplyClamp.Apply` | effective-resolution clamp | `src/Modules/Evidgate.Modules.Healing/Application/HealingSwitches.cs`; `GetHealingSwitchQueryHandler.Handle` |
| `IHealingAvailabilityResolver` + gateway consumer | contract/consumer | `HealingAvailabilityResolver.ResolveAsync`; `HealingLlmGateway.AdmissionRefusalAsync`; `HealingModule` registration |
| `HealingModeGate.Check` | mode/reason validation | `src/Modules/Evidgate.Modules.Healing/Application/HealingSwitches.cs` |
| `HealingScopeGuard.Project` | project boundary | same application file |
| `HealingSwitchSql.Resolution`, `UpsertTenant`, `UpsertProject` | SQL | same application file |
| `HealingSwitchStore.ReadAsync` / `UpsertAsync` | data path | same application file |
| `SetHealingSwitchCommandHandler.Handle` | scope/project validation, audit/outbox | `src/Modules/Evidgate.Modules.Healing/Application/SetHealingSwitch.cs` |
| `healing.switch_changed` (tenant/project task, per-version key, no handler yet) | outbox | `HealingSwitchEvents.TaskType` / `AggregateType` / `IdempotencyKey` / `Payload` |
| `HealingSystemSwitchWriter.SetAsync` (app role cannot execute definer) | legacy operator writer | `src/Modules/Evidgate.Modules.Healing/Application/HealingSystemSwitch.cs`; `0105_platform_operator_role.sql` |
| `HealingSystemSwitchWriter.LogSystemSwitchSet` / event `8603` | legacy successful-write log | same writer file; SQL flips instead use platform audit |
| `HealingOptions`, `EVIDGATE__HEALING__APPLY` (default false) | configuration | `HealingOptions.SectionName` / `Apply`; `HealingModule`; `infra/config-matrix.json` |
| `evidgate_healing_llm_refused_total` | current refusal metric | `EvidgateTelemetry.HealingLlmRefusedMetricName` / `RecordHealingLlmRefused` |
| `public.audit_log` (org NOT NULL, per-org hash chain) | table | `0015_identity_audit_log.sql` |
| `GET /v1/orgs/{o}/audit` (`audit:read`; no object_id filter) | read route | OpenAPI `listAudit`, audit parameters, `Cursor` / `Limit` |
| `AuditEntryDto` / `AuditListResponse` | audit read shape | `src/Modules/Evidgate.Modules.Identity.Contracts/AuditDtos.cs` |
| `mode` audit metadata allowlist | metadata | `AuditMetadata`, `src/BuildingBlocks/Persistence/Audit/AuditMetadata.cs` |
| `public.platform_sweep_audit` / `evidgate.record_sweep_audit` | predecessor audit model | `0088_platform_sweep_audit.sql` |
| A-F2 switch half implemented by migration | historical tracker | `docs/status-carried.md`, A-F2 row title; `docs/security/resolver-review.md`, A-F2; `0105_platform_operator_role.sql` is authoritative code |
| T33 stored/derived vocabulary and V1 gate | ruling | `plans/features/README.md`, T33 |
| Safe-healing rule 9: switches before apply, tests/gates continue | locked requirement | `DECISIONS.md`, Safe-healing rules |
| three-scope resolution vocabulary | contract | `plans/00-overview.md`, Healing availability |
| AC6 / 10.7.1-T5.1 / T5.2 | runbook and pending drill | `plans/features/10-operations/10.7.1-operable-by-a-second-person.md`, Acceptance criteria / Tasks |
| Availability nav/lazy route | UI route | `NAV_ENTRIES`; `AREA_ROUTES.availability`; `features/availability/availability.routes.ts` |
| `HealingSwitchPage` / `SwitchScopeHierarchy` | posture, table, read/write gates | `features/availability/healing-switch.page.ts` / `.html`; `switch-scope-hierarchy.component.ts` |
| `HealingSwitchStore` | targets, ancestor blocker, save/reload | `features/availability/healing-switch.store.ts` |
| `SwitchScopeControl` | selector/reason/save/read-only rules | `features/availability/switch-scope-control.component.ts` / `.html` |
| reason code-point and body-byte limits | UI guard | `features/availability/reason-limit.ts`, `reasonExceedsLimit` |
| availability labels and optional reason | UI copy | `features/availability/healing-switch.copy.ts`, `HEALING_SWITCH_COPY` |
| planned alert rules / `scripts/check-alerts.sh` (absent) | not possible yet | 10.3.1-T5.1, `plans/features/10-operations/10.3.1-telemetry-and-alerting.md` |

Aliases in the examples (`API`, `JAR`, `CSRF`, `ORG`, `REQUEST_ID`, `KEY`, `PROJECT`) are shell variables, not wire fields (`X-Evidgate-Org`, `X-XSRF-TOKEN`, `X-Request-Id`, `Idempotency-Key` are the wire names).

Desk check: 2026-11-21, wave fd69e03b; not checkable without a live environment: staging drill history, psql/network connectivity, deployed migrations/ACLs and credentials, member permissions/current cookies, effective Apply configuration, uninterrupted ingest/runs/gates.
