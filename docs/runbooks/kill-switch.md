# Healing kill-switch runbook

Status: **10.7.1-T5.1 written; T5.2 staging drill open (AC6 open).**

No flip has been performed by a second person on staging: the drill (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md:126`) is unexecuted, so the "ingest, runs and gates continue" half of AC6 is unverified in a live environment (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md:27`). The document half of T5.1 — the flip order, the proof that `unavailable` is derived, and the rollback steps — is grounded in this tree.

Legend as in [`../../AGENTS.md`](../../AGENTS.md): ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`. Every route, permission, table, enum value and UI path below is cited to this worktree's `develop` (`68bde65d`), and the full mapping is the [evidence table](#11-evidence-table) in §11.

## 1. Scope, audience and rules

**Purpose.** Change the healing kill switch deliberately, at the right scope, and be able to say afterwards what the mode was, who set it and how to put it back. The switch is persisted in one table, one row per scope (`migrations/0052_healing_switch.sql:105-134`), and read at resolution time with "most restrictive wins" (`src/Modules/Evidgate.Modules.Healing.Contracts/HealingAvailability.cs:104-126`).

**Audience.** A second on-call operator, or an incident commander, who did not build 08.6.3. You need a member session holding `healing:configure` for the tenant/project scopes (§4), and the platform-operator credential for the system scope — which is not usable today (§4, §9).

**In scope.** The three scopes of `public.healing_switch` (`system`, `tenant`, `project`), the stored mode of each, the resolved posture, and how to read both back.

**Out of scope.** The healing apply path (V2, 08.6.1); incident triage and candidate review (`healing:triage` reads, 00 §8.7); the dependency health probes that will produce `unavailable` (08.6.3-T2); every other subsystem's kill switch (none exists).

**Prerequisites.**

- A checkout of the release tag the incident is about; every cite below is a line in that tree.
- For tenant/project flips: a member account holding `healing:configure` in the org (`src/Modules/Evidgate.Modules.Healing/Application/SetHealingSwitch.cs:76-77`, `:95-98`), and the project id when you flip a project.
- **The read and the write are separate permissions, and no single built-in role has both.** `tenant_admin` holds `healing:configure` only (`migrations/0010_identity_permission_catalogue.sql:302`); `qa_engineer` holds `healing:triage` only (`:299`). To flip and verify in one session, use a membership carrying both — a membership may hold several roles and its permission set is the union over them (`migrations/0011_identity_membership.sql:120`, `:129`; `src/Modules/Evidgate.Modules.Identity/Infrastructure/Persistence/IdentityAccessSql.cs:41-55`) — or a custom role, since both catalogue rows are tenant-assignable and non-reserved (`:329`, `:331`).
- For a system flip: the platform-operator credential (§4). It does not exist yet: the gate denies every caller (`src/Evidgate.Api/OperatorIngress/OperatorGate.cs:30-33`). ⚠️
- The org id — sent as `X-Evidgate-Org` on every member call, the read included (§5.1) — and (optionally) the project id; never paste a customer name or URL into a ticket.

**Rules.**

1. **Never write `public.healing_switch` by hand.** The tenant/project write path is the route (§5); the system row's only write path is the definer `evidgate.healing_switch_set_system` (`migrations/0052_healing_switch.sql:211-288`, called at `src/Modules/Evidgate.Modules.Healing/Application/HealingSystemSwitch.cs:43-46`). A hand `UPDATE` skips the mode gate, the reason bound and the audit row.
2. **Never try to set `unavailable`.** It is a derived observation; four layers refuse it (§3).
3. **Write a reason.** It is optional in the contract (`contracts/schemas/healing-switch-set.v1.json:27-32`) and in the UI (`apps/web/src/app/features/availability/healing-switch.copy.ts:43`), but the reason is the only human context stored with the change, and the audit row keeps only the mode.
4. **Record the before-state yourself for a system flip, and for any scope that has no row yet.** The system row has no durable audit record (A-F2, §8), so the value you overwrite cannot be recovered from an audit chain later; and a tenant/project `PUT` on a scope with no row **creates** the row, after which there is no earlier mode to restore (§7). Record the `X-Request-Id` the response **echoes** for every flip (send a safe token of your own when you want the value known in advance) — the echoed value is the only handle that ties the audit entry back to your own write (§5.1).
5. **One intent per attempt.** Reuse the same `Idempotency-Key` only to retry the same body; a different body under a reused key is `409 idempotency_conflict` (`contracts/openapi.yaml:6090-6094`).
6. The switch holds configuration only: no customer data, no evidence. Never treat a mode as a statement about a run.

## 2. The three scopes, and the order to flip them

| Scope | Row identity | Who writes it | Route |
|---|---|---|---|
| `system` | one row, `org_id` NULL (`migrations/0052_healing_switch.sql:123-125`, partial unique at `:139-141`) | platform operator, through the definer (`:211-288`) | `POST /internal/operator/healing/switch` (`src/Evidgate.Api/Endpoints/OperatorEndpoints.cs:49-54`) |
| `tenant` | one row per org (`migrations/0052_healing_switch.sql:142-144`) | a member holding `healing:configure` | `PUT /v1/healing/switch` with `scope: tenant` (`src/Modules/Evidgate.Modules.Healing/Endpoints/HealingEndpoints.cs:35-37`) |
| `project` | one row per project (`migrations/0052_healing_switch.sql:145-147`) | the same permission, project in scope | `PUT /v1/healing/switch` with `scope: project` and `project_id` |

The scope vocabulary is a closed CHECK (`migrations/0052_healing_switch.sql:121`) and the same three literals in code (`src/Modules/Evidgate.Modules.Healing/Application/SetHealingSwitch.cs:204-214`).

### 2.1 How the effective mode resolves

- The read is one statement over at most three rows: the platform row every org inherits, the org's tenant row, and the project row named by `project_id` (`src/Modules/Evidgate.Modules.Healing/Application/HealingSwitches.cs:187-193`, `:282-286`).
- The answer is the most restrictive stored mode, `enabled` < `shadow` < `disabled` < `unavailable` (`src/Modules/Evidgate.Modules.Healing.Contracts/HealingAvailability.cs:104-139`). This is the rule 00 §8.7 states as "resolved system → tenant → project (the most restrictive wins)" (`plans/00-overview.md:303`).
- A database with no row at any scope resolves to `shadow` (`src/Modules/Evidgate.Modules.Healing.Contracts/HealingAvailability.cs:101-102`), the V1 posture of healing that decides but never applies (`plans/00-overview.md:303`).
- The system row is read by every tenant through the catalogue-shaped read policy (`migrations/0052_healing_switch.sql:159-166`), but the tenant read redacts it to `mode`, `at` and `version`; `set_by` and `reason` come back null (`src/Modules/Evidgate.Modules.Healing/Application/GetHealingSwitch.cs:135-138`, `contracts/openapi.yaml:6061-6063`).

Two consequences to keep straight:

- **A narrow scope can only tighten, never loosen.** A tenant row set to `enabled` under a `shadow` system row has no effect; the resolution stays `shadow`. This is the whole answer to "why the order is system first": the system row is the platform-wide posture every org inherits (`plans/00-overview.md:303`), so it is the value that decides when nothing narrower is stricter.
- **An inner write is not refused by the API when an ancestor is stricter.** The handler checks the project, the scope shape and the mode, not the ancestor rows (`src/Modules/Evidgate.Modules.Healing/Application/SetHealingSwitch.cs:91-168`); the row is stored and simply does not change the resolution. The web UI is stricter than the API: it makes the tenant/project editor read-only while an ancestor is `disabled` (`apps/web/src/app/features/availability/healing-switch.store.ts:65-73`, `switch-scope-control.component.ts:75-77`).

### 2.2 The order to flip

T5.1 names the order **system → tenant → project** (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md:125`), and AC6 flips each scope through `enabled → shadow → disabled` (`:27`). Operationally:

1. Flip `system` first: it is the posture every org inherits, so that is the step whose effect is platform-wide.
2. Flip `tenant`: it can only tighten the system posture.
3. Flip `project`: it can only tighten the tenant posture.

Because the resolution is "most restrictive wins", the **destination does not depend on the order** — the sequence exists so that each step is verified against a predictable table (§6) and so the platform-wide posture is set deliberately rather than inferred from a narrower scope. `[INFERENCE]` from the resolver (`src/Modules/Evidgate.Modules.Healing.Contracts/HealingAvailability.cs:104-126`), not a separate contract clause.

One UI caveat, verified in code: once the system row is `disabled`, the tenant and project editors are read-only in the web UI (`apps/web/src/app/features/availability/healing-switch.store.ts:65-73`). If the last step of a drill has to go through the UI, carry the system row to `disabled` last; the API path does not have that restriction.

## 3. `unavailable` is derived and cannot be set

The mode vocabulary has four values, but only three are storable: `enabled`, `shadow`, `disabled` (`src/Modules/Evidgate.Modules.Healing.Contracts/HealingAvailability.cs:29-52`). `unavailable` is an observation of a dependency, produced from health probes by 08.6.3-T2, never a configured value — and on this base nothing produces it at all (`:95-97`, `src/Modules/Evidgate.Modules.Healing/Application/HealingSwitches.cs:303-307`).

Four independent layers refuse it, so no client and no SQL session can widen the vocabulary:

| Layer | Refusal | Cite |
|---|---|---|
| Column CHECK | `mode in ('enabled','shadow','disabled')` — a 23514 check violation | `migrations/0052_healing_switch.sql:122` (reason at `:47-49`) |
| Route contract schema | `mode` is a free string on purpose, so the handler — not the schema — answers `422 invalid_switch_mode` | `contracts/schemas/healing-switch-set.v1.json:21-26` |
| Tenant/project handler | the shared mode gate refuses every token but the three | `src/Modules/Evidgate.Modules.Healing/Application/HealingSwitches.cs:64-72`, called at `src/Modules/Evidgate.Modules.Healing/Application/SetHealingSwitch.cs:146-149` |
| Operator definer | raises 22023 `invalid_parameter_value` for anything else, `unavailable` included | `migrations/0052_healing_switch.sql:242-247` |

How to verify it on a live deployment:

1. `GET /v1/healing/switch?project_id=…` with the session cookie and `X-Evidgate-Org` (§5.1), and read `resolved` (`contracts/openapi.yaml:6046-6085`). On this base it is always one of `enabled`, `shadow`, `disabled`, and `dependency_health` is `{"llm": null, "knowledge_store": null}` because no probe reports yet (`src/Modules/Evidgate.Modules.Healing/Application/GetHealingSwitch.cs:54-60`, `:151-156`). A null observation means "not observed", never "healthy" (`contracts/openapi.yaml:6057-6059`).
2. `PUT /v1/healing/switch` with `"mode": "unavailable"` answers `422 invalid_switch_mode` with `details[].path` on `mode` (`src/Modules/Evidgate.Modules.Healing/Application/HealingSwitches.cs:68-72`; contract wording `contracts/openapi.yaml:6100-6104`). Do this once in the drill and keep the response as evidence; it is the AC6 negative check.
3. If anyone claims the row holds `unavailable`, `SELECT mode FROM public.healing_switch` cannot show it: the CHECK makes the row unstorable (`migrations/0052_healing_switch.sql:122`).

## 4. Who may flip which scope

| Scope | Permission / credential | Where it is enforced | Who holds it | State |
|---|---|---|---|---|
| `tenant`, `project` | `healing:configure` | `[Permission(Permissions.Healing.Configure)]` on the handler (`src/Modules/Evidgate.Modules.Healing/Application/SetHealingSwitch.cs:76-77`; constant `src/BuildingBlocks/SharedKernel/Authorization/Permissions.g.cs:163`) | the `tenant_admin` role, and only it among the built-in roles (`migrations/0010_identity_permission_catalogue.sql:302`); the catalogue row is tenant-assignable and non-reserved (`:329`) | ✅ shipped |
| `tenant`, `project` (read) | `healing:triage` | `[Permission(Permissions.Healing.Triage)]` on the read (`src/Modules/Evidgate.Modules.Healing/Application/GetHealingSwitch.cs:90`; constant `Permissions.g.cs:169`) | the `qa_engineer` role, and only it among the built-in roles (`migrations/0010_identity_permission_catalogue.sql:299`); tenant-assignable, non-reserved (`:331`) | ✅ shipped |
| `system` | `platform_operator` + audited support grant | `RequireOperatorFilter` on the operator group (`src/Evidgate.Api/Endpoints/OperatorEndpoints.cs:49-54`; contract `plans/00-overview.md:333`) | the platform operator only | ⚠️ the default gate denies every caller (`src/Evidgate.Api/OperatorIngress/OperatorGate.cs:30-33`); the real operator identity is 02.8.1-T1 and is open |
| `system` (database) | `EXECUTE` on the definer, `evidgate_app` alone; `evidgate_operator` and `evidgate_worker_sched` are revoked | `migrations/0052_healing_switch.sql:290-295` | the API process | ✅ shipped |

Two boundaries worth stating explicitly:

- The definer **refuses to run inside a tenant transaction** (23514) even though the caller holds `EXECUTE`, so no tenant request can write the platform row through it (`migrations/0052_healing_switch.sql:228-240`, `:27-38`).
- `evidgate_operator` holds `SELECT` on the table but no policy, so an operator database login reads **zero rows** (`migrations/0052_healing_switch.sql:196-199`). Do not plan a system-scope read around an operator SQL session.

How a flip is attributed: the tenant/project write requires an authenticated member principal and uses its user id as `set_by` (`src/Modules/Evidgate.Modules.Healing/Application/SetHealingSwitch.cs:95-98`, `:151-152`); the system write requires an authenticated operator principal with a user id, 401/403 otherwise (`src/Evidgate.Api/Endpoints/OperatorEndpoints.cs:174-187`).

## 5. How to flip

### 5.1 The tenant/project route

`PUT /v1/healing/switch` (`src/Modules/Evidgate.Modules.Healing/Endpoints/HealingEndpoints.cs:35-37`, operation `setHealingSwitch` at `contracts/openapi.yaml:6086-6143`).

Headers:

- `Idempotency-Key` — mandatory, ≤ 128 chars, unique per `(org_id, project_id, operation)` for at least 24 h (`contracts/openapi.yaml:8948-8956`); the route answers 400 when the header is missing (`src/Modules/Evidgate.Modules.Healing/Endpoints/HealingEndpoints.cs:61-67`).
- `X-Evidgate-Org` — **required on every request of this route, by every member**, however many orgs the membership has: the route carries no `{o}`, so the org comes from this header alone, and a member request without it answers `422 request_invalid` with `details[].path` `X-Evidgate-Org`, `reason` `required`, before any lookup (`src/Evidgate.Api/Tenancy/TenantResolver.cs:57-62`; header constant `:22-23`, read at `src/Evidgate.Api/Tenancy/TenantResolutionFilter.cs:121-137`; `Error.Invalid` is 422 — `src/BuildingBlocks/SharedKernel/Results/Result.cs:112-113`; the healing group carries the filter — `src/Evidgate.Api/ApiHost.cs:231-232`; the same requirement and status are recorded independently for tenant reads at `docs/runbooks/pilot0-dogfood.md:172-177`).
- **The member cookie, not a bearer token.** `healing:configure` is a member permission and the handler refuses a principal with no user id (`src/Modules/Evidgate.Modules.Healing/Application/SetHealingSwitch.cs:95-98`). A session is never recognised when the request carries an `Authorization` header — the session authenticator answers 401 `unauthenticated` before it reads the cookie (`src/Modules/Evidgate.Modules.Identity/Sessions/Http/SessionRequestAuthenticator.cs:88-94`) — and the only other registered authenticator is the CI-producer bearer (`src/Modules/Evidgate.Modules.Identity/IdentityModule.cs:258-259`), which resolves a service principal, not the member this handler requires. The write therefore runs on the `__Host-evidgate_session` cookie (`src/Modules/Evidgate.Modules.Identity/Sessions/Http/SessionCookieNames.cs:28`).
- `X-XSRF-TOKEN` — mandatory on this write for that caller: the value of the script-readable `XSRF-TOKEN` cookie (`src/Modules/Evidgate.Modules.Identity/Sessions/Http/SessionCookieNames.cs:31`), paired with the HttpOnly `__Host-evidgate_af` cookie (`:34`); a cookie-authenticated mutation without the header is 403 `csrf_failed` before any handler runs (`src/Evidgate.Api/Middleware/SessionXsrfMiddleware.cs:12-15`; contract `contracts/openapi.yaml:8488-8489`, `:8973-8980`).
- `X-Request-Id` — optional, and worth sending: an inbound value is honoured only when it is a bounded token — at most 128 visible-ASCII characters, `0x21`–`0x7E`, so no spaces — and is otherwise replaced by a minted id (`src/Evidgate.Api/Middleware/RequestIdMiddleware.cs:37-45`; the rule is `src/BuildingBlocks/Observability/Correlation/CorrelationNames.cs:52-53`, `:70-88`); the id actually used is echoed in the response header (`RequestIdMiddleware.cs:49`) and stored on the audit row as `request_id` (`src/BuildingBlocks/Persistence/Audit/AuditTrail.cs:95-101`). Record the **echoed** value: it is how §7 finds the row id, and a replaced inbound value never appears in the chain. If you retry a lost response with the same `Idempotency-Key`, send the SAME `X-Request-Id` again: an idempotent replay returns the stored body and writes no second audit entry (`src/BuildingBlocks/Pipeline/Behaviours/IdempotencyBehaviour.cs:90-99`), so a different request id on the retry matches nothing; if it does, match on the original write's id or mark the row id `[OPEN]`.
- **No `If-Match`.** Concurrency is the row's `version`, which the response carries (`contracts/openapi.yaml:6093-6096`).

Body — the closed schema `healing-switch-set.v1` (`contracts/schemas/healing-switch-set.v1.json:1-34`): `scope` (`tenant` or `project`), `mode` (one of the three), `project_id` (nullable: a project body needs a value, a tenant body must omit it or send `null` — a non-null value on a tenant body is `422 project_id: not_allowed`, `src/Modules/Evidgate.Modules.Healing/Application/SetHealingSwitch.cs:116-121`), optional `reason` 1–2000 Unicode code points. The serialized body is capped at 4096 bytes (`contracts/schemas/healing-switch-set.v1.json:6`); a larger body is 413 before it is parsed (`src/Modules/Evidgate.Modules.Healing/Endpoints/HealingEndpoints.cs:20-21`, `:82-108`). Count the reason in code points, not UTF-16 units (`src/Modules/Evidgate.Modules.Healing/Application/HealingSwitches.cs:94-122`).

```bash
# $JAR holds the member session's cookies: __Host-evidgate_session plus the antiforgery pair.
# How to obtain them: the dev sign-in writes them to a jar (docs/dev-setup.md:406-418, a
# Development host only); a deployed environment signs in through the web app, which sets the
# same three cookies in the browser.
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

`-b`/`-c` keep the jar because a rotating session writes a refreshed `XSRF-TOKEN` back; `-D` keeps the response headers because the `X-Request-Id` there is the handle §7 uses to find the row.

A project flip is the same call — same cookie jar, `X-Evidgate-Org`, `X-XSRF-TOKEN`, `Idempotency-Key` and a fresh `X-Request-Id` — with `"scope":"project"` and `"project_id":"$PROJECT"`.

Answers: 200 with the stored row (`HealingSwitchView`: scope, project, mode, reason, set_by, at, version — `src/Modules/Evidgate.Modules.Healing/Application/GetHealingSwitch.cs:23-30`); 403 `forbidden_scope` for `scope: system`, refused before any lookup (`src/Modules/Evidgate.Modules.Healing/Application/SetHealingSwitch.cs:107-114`); 422 `project_id: not_allowed` / `required` (`:116-131`); 403 `project_scope_mismatch` for a project outside the credential or request scope (`src/Modules/Evidgate.Modules.Healing/Application/HealingSwitches.cs:136-149`); 404 for an unknown, soft-deleted or other-tenant project (`src/Modules/Evidgate.Modules.Healing/Application/SetHealingSwitch.cs:138-141`); 409 `idempotency_conflict`; 413 `payload_too_large`.

### 5.2 The system route

`POST /internal/operator/healing/switch` (`src/Evidgate.Api/Endpoints/OperatorEndpoints.cs:49-54`, handler `:170-210`), excluded from the served OpenAPI document (`:38-42`).

- Body: `{"mode": "...", "reason": "..."}` — `set_by` is never in the body, it is the authenticated operator's own id (`src/Evidgate.Api/Endpoints/OperatorEndpoints.cs:220-226`).
- **No `Idempotency-Key`** and therefore no idempotency semantics on this route; a retry writes the same mode again and bumps `version` (`:164-168`). **No `If-Match`.**
- The same mode gate applies, so `unavailable` and any other token is 422 `invalid_switch_mode`, and `enabled` is 422 `mode_not_available` while `Healing:Apply` is off (`src/Modules/Evidgate.Modules.Healing/Application/HealingSystemSwitch.cs:59-62`).
- The write calls the definer on a connection outside any tenant transaction (`:64-71`); the definer validates again and returns the stored row (`migrations/0052_healing_switch.sql:242-286`).
- The change is recorded by log event `8603` (mode, operator, version — never the reason) and by the row itself (`src/Modules/Evidgate.Modules.Healing/Application/HealingSystemSwitch.cs:87-95`, `:21-29`).

Today every caller of this route is denied by the fail-closed gate (`src/Evidgate.Api/OperatorIngress/OperatorGate.cs:30-33`), so a human cannot perform a system flip on this base. ⚠️

### 5.3 The web UI

Path: the project nav entry **Availability** — id `availability`, `scope: project`, permission `healing:triage`, segment `availability` (`apps/web/src/app/shell/nav/nav-registry.ts:210-221`) — mounted lazily through `apps/web/src/app/app.routes.ts:68` and the area's one route table (`apps/web/src/app/features/availability/availability.routes.ts:13-14`).

The page shows the resolved posture, the dependency observations and one card per scope (`apps/web/src/app/features/availability/healing-switch.page.html:34-116`):

- **System scope** is read-only there: only the platform operator can change it (`apps/web/src/app/features/availability/healing-switch.copy.ts:29`), and the page only ever sends writes for `tenant`/`project` (`apps/web/src/app/features/availability/healing-switch.store.ts:29-30`, `:76-86`).
- **Tenant/project** cards carry the mode selector (`enabled`, `shadow`, `disabled` only — `switch-scope-control.component.ts:24-29`), a reason field and a save button.
- **Both permissions are needed on the page**: `healing:triage` to load it (`apps/web/src/app/features/availability/healing-switch.page.ts:38-40`; the nav entry carries the same permission, `apps/web/src/app/shell/nav/nav-registry.ts:218`) and `healing:configure` to save (`apps/web/src/app/features/availability/healing-switch.store.ts:138`). A `tenant_admin` alone therefore reaches the page's forbidden state, and a `qa_engineer` alone sees it read-only (§1).
- The reason is measured locally against both limits — 2000 code points and the 4096-byte serialized body — before a save is offered (`apps/web/src/app/features/availability/reason-limit.ts:5-39`, `:35-39`).
- A card is read-only when the caller lacks `healing:configure` (`healing-switch.store.ts:138`), when `resolved` is `unavailable` (`healing-switch.page.ts:33-36`), or when an ancestor is `disabled` (`healing-switch.store.ts:65-73`).
- A save reloads the resolution from the server, so the posture you read after a save is the server's, not the form's (`healing-switch.store.ts:205-210`).
- With `Healing:Apply` off — the default on this base (`src/Modules/Evidgate.Modules.Healing/Application/HealingSwitches.cs:16-29`) — the selector still offers `enabled` but the server answers 422 `mode_not_available`; the practical settable modes today are `shadow` and `disabled`.

## 6. Procedure, with verification after each flip

Record the before-state first, with a session holding `healing:triage` (the read's permission — `src/Modules/Evidgate.Modules.Healing/Application/GetHealingSwitch.cs:90`), sending the `__Host-evidgate_session` cookie and `X-Evidgate-Org` on **every** call in this section, reads included: the read resolves its tenant the same way, so a GET without the header is the same `422` `request_invalid` naming `X-Evidgate-Org` (`src/Evidgate.Api/Tenancy/TenantResolver.cs:57-62`), and the cookie is the only member credential: with an `Authorization` header in place of it the session authenticator refuses before reading the cookie (`src/Modules/Evidgate.Modules.Identity/Sessions/Http/SessionRequestAuthenticator.cs:88-94`). `GET /v1/healing/switch?project_id=…` is enough for the tenant answer plus the redacted system row (`src/Modules/Evidgate.Modules.Healing/Application/GetHealingSwitch.cs:98-157`); note `resolved`, each scope's `mode` and `version`, and — for the system row — the mode and the time you observed it, because no durable audit exists (§8, A-F2 `[OPEN]`).

Then, in the order of §2.2:

1. **System.** `POST /internal/operator/healing/switch` with the new mode and a reason (§5.2). ⚠️ blocked today by the operator gate (§4); until 02.8.1-T1 lands this step can only be exercised by a test host that substitutes the gate.
   Verify: `GET /v1/healing/switch` → `system.mode` is the new value, `system.version` increased (`migrations/0052_healing_switch.sql:149-154`), and `resolved` equals the most restrictive mode across the rows that carry one.
2. **Tenant.** `PUT /v1/healing/switch` with `scope: tenant` (§5.1).
   Verify: `GET /v1/healing/switch` → `tenant.mode` is the new value, `version` increased, `resolved` recomputed. A tenant mode weaker than the system row changes `tenant.mode` but not `resolved` — that is the resolver working, not a failed write.
3. **Project.** `PUT /v1/healing/switch` with `scope: project` and the project id (§5.1).
   Verify: `GET /v1/healing/switch?project_id=…` → `project.mode` is the new value, `version` increased, `resolved` recomputed for that project.
4. **`unavailable` check.** `PUT` with `"mode": "unavailable"` and expect 422 `invalid_switch_mode` (§3). Nothing in storage changes.

What is unaffected, and how that is known:

- **Ingest, runs and gates do not consult the switch on this base.** The resolver interface (`src/Modules/Evidgate.Modules.Healing.Contracts/HealingAvailability.cs:142-157`) is declared, implemented (`src/Modules/Evidgate.Modules.Healing/Application/HealingSwitches.cs:308-328`) and registered (`src/Modules/Evidgate.Modules.Healing/HealingModule.cs:38`), and a source search on this tree finds no other caller: no ingest, orchestration, gate or worker code reads it. So a flip cannot stop a run or an ingest request here.
- **The change emits one outbox event, and nothing consumes it yet.** `healing.switch_changed` is enqueued per stored version (`src/Modules/Evidgate.Modules.Healing/Application/SetHealingSwitch.cs:228-269`), and its own doc comment says no handler registers it yet (`:230-231`).
- **The apply capability is V2.** Healing acts on a validated candidate only through 08.6.1, which is not in this tree; the V1 clamp also refuses `enabled` unless the deployment sets `Healing:Apply` (`src/Modules/Evidgate.Modules.Healing/Application/HealingSwitches.cs:16-29`). The locked rule behind that is DECISIONS §5.9 item 9: kill switches exist at all three scopes **before** any apply capability ships, and if the model or knowledge store fails, tests and gates run normally while healing becomes `unavailable` (`DECISIONS.md:112`). The code half of that reading (nothing gates on the switch) is the bullet above; the "becomes `unavailable`" half is still 08.6.3-T2's.
- ⚠️ The staging drill (T5.2) is what proves "ingest, runs and gates continue" as an observation rather than a code reading (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md:126`). Until it runs, treat that half of AC6 as unproven.

## 7. Rolling a flip back

The modes are `enabled`, `shadow`, `disabled` (`migrations/0052_healing_switch.sql:122`); "rollback" is always **another flip to the previous mode**, never a row deletion — the app role holds no `DELETE` on the table (`migrations/0052_healing_switch.sql:191`, `:75-76`), so there is no supported way to remove a row and fall back to "no row" behaviour. The one case with no previous mode is a scope's **first** write, handled in step 1 below.

Steps:

1. **Find the previous mode.**
   - Tenant/project: the audit chain, which needs `audit:read` (`contracts/openapi.yaml:4644`). `GET /v1/orgs/{o}/audit?object_type=healing_switch&action=healing.switch.set` (`:4635-4654`) returns the entries newest first (`src/Modules/Evidgate.Modules.Identity.Contracts/AuditDtos.cs:36-39`), each with `metadata.mode` — the mode that entry set — plus `id`, `object_id`, `actor_id`, `request_id` and `created_at` (`:19-34`). The page mixes every `healing_switch` object of the org (the tenant row and each project row) and the filter set has **no `object_id` parameter** (`:4646-4654`), so match it yourself on the request id: walk the pages with `cursor` (the response's `next_cursor`) and `limit` 1–200 (`contracts/openapi.yaml:8748-8756`, `:8787-8795`) and keep the entry whose `request_id` equals the `X-Request-Id` the response echoed for the flip (§5.1; an inbound id the middleware will not accept is replaced, `src/Evidgate.Api/Middleware/RequestIdMiddleware.cs:37-45`). **That entry is how you obtain the row id**: its `object_id` is the switch row id the handler passed as the audit object (`src/Modules/Evidgate.Modules.Healing/Application/SetHealingSwitch.cs:159-165`). Nothing else hands you that id — neither the `PUT` answer nor `GET` returns it (`HealingSwitchView` is scope, project, mode, reason, set_by, at and version only — `src/Modules/Evidgate.Modules.Healing/Application/GetHealingSwitch.cs:23-30`; the `GET` answer holds three of those views and no id — `:71-76`) — and it is the value the next older entry's `object_id` must equal. If a flip was made without a chosen `X-Request-Id`, fall back to the newest entry whose `actor_id` is yours and whose `created_at` matches the flip you recorded, say so in the record, and treat the preceding-mode conclusion as `[OPEN]` when more than one entry matches. The **next older entry with that same `object_id`** carries the mode to restore.
   - **First write on a scope leaves no previous stored mode.** When the `PUT` created the row (`insert … on conflict … do update` — `src/Modules/Evidgate.Modules.Healing/Application/HealingSwitches.cs:196-209`) its audit entry has no predecessor for that `object_id`; when the entry you matched carries your own `X-Request-Id` that absence does establish the creation, and when the correlation was ambiguous (no chosen request id) it does **not** — record that as `[OPEN]` instead of inferring it. "No row at this scope" is also not the same as the posture `shadow`: the resolution falls back to `shadow` only when **no** scope carries a row (`src/Modules/Evidgate.Modules.Healing.Contracts/HealingAvailability.cs:104-126`), so the posture before the flip is whatever `resolved` the §6 `GET` returned — keep that value, and if it is missing record the posture as unknown `[OPEN]`. That posture is not re-submittable either: `mode` accepts only the three stored values (`migrations/0052_healing_switch.sql:122`) and the row cannot be removed (the app role holds no `DELETE` — `migrations/0052_healing_switch.sql:191`; no route removes one — §5, §11), so there is no literal undo. The **conservative** recovery is a flip of that scope to `disabled`, the strictest settable mode (`migrations/0052_healing_switch.sql:122`, `src/Modules/Evidgate.Modules.Healing.Contracts/HealingAvailability.cs:104-139`) — chosen, not forced, since `shadow` is equally accepted and `enabled` answers `mode_not_available` while `Healing:Apply` is off (§9). The record must state that the scope held no row before and what `resolved` was then. An approved procedure for this case is `[OPEN]`.
   - System: ⚠️ **there is no durable record** (A-F2, §8). Use the `system.mode` you recorded before the change (§6), or the operator host's log event `8603` (`src/Modules/Evidgate.Modules.Healing/Application/HealingSystemSwitch.cs:87-95`). Do not claim an audit trail for a system flip. `[OPEN]`
2. **Re-flip that scope to the previous mode**, with a reason naming the incident and the rollback (§5.1 / §5.2). The reason is optional in the contract and the UI but expected in practice (§1 rule 3). This is a new change and takes a **new** `Idempotency-Key` for a tenant/project body; reusing the delivery key of the original would be a different body under a used key, which is `409 idempotency_conflict` (`contracts/openapi.yaml:6090-6094`).
3. **Verify the resolved posture**, not only the row: `GET /v1/healing/switch?project_id=…` and compare `resolved` with the value you recorded before the first flip (§6). Rolling back the innermost row while an ancestor is still stricter will not move `resolved` — that is expected, and the reason the rollback walks the same scopes in the same order (§2.2).
4. **Check the version moved.** Each write bumps the row's `version` through the shared trigger (`migrations/0052_healing_switch.sql:149-154`); an unchanged `version` in the 200 body means the row you read is not the row you just wrote.

`[INFERENCE]` The rollback order is the flip order of §2.2, for the same reason: the destination is order-independent under "most restrictive wins", and one order keeps the verification table predictable.

## 8. Audit trail

**Tenant and project flips are audited in the org's chain.** The handler is annotated `[AuditedAction("healing.switch.set")]` and files one audit object per flip (`src/Modules/Evidgate.Modules.Healing/Application/SetHealingSwitch.cs:76-77`, `:159-165`):

| Recorded | Value | Cite |
|---|---|---|
| `action` | `healing.switch.set` | `src/Modules/Evidgate.Modules.Healing/Application/SetHealingSwitch.cs:76` |
| `object_type` / `object_id` | `healing_switch` / the switch row's id (stable across upserts: `id` is not in the conflict update) | `src/Modules/Evidgate.Modules.Healing/Application/SetHealingSwitch.cs:159-165`; upsert `src/Modules/Evidgate.Modules.Healing/Application/HealingSwitches.cs:196-209` |
| `metadata` | `mode` (the new mode) only — `scope`, `project_id` and `version` are deliberately not on the metadata allowlist | `src/Modules/Evidgate.Modules.Healing/Application/SetHealingSwitch.cs:156-165`; allowlisted key `mode` at `src/BuildingBlocks/Persistence/Audit/AuditMetadata.cs:52` |
| actor, time, chain | `actor_type`/`actor_id`, `created_at`, `seq`/`prev_hash`/`entry_hash` | `migrations/0015_identity_audit_log.sql:25-45`; read shape `src/Modules/Evidgate.Modules.Identity.Contracts/AuditDtos.cs:19-34` |

The **old** mode is not stored in the new entry; it is the `metadata.mode` of the preceding entry for the same `object_id`. The outbox event `healing.switch_changed` carries ids and the mode only, never the reason (`src/Modules/Evidgate.Modules.Healing/Application/SetHealingSwitch.cs:223-227`).

**A system flip is not audited in `audit_log`.** That table's `org_id` is `NOT NULL` and its hash chain is per org (`migrations/0015_identity_audit_log.sql:27`, `:39-45`), and the system row belongs to no org, so there is no chain to append to; the writer says so and stands in a log line instead (`src/Modules/Evidgate.Modules.Healing/Application/HealingSystemSwitch.cs:21-29`, `:87-95`). `migrations/0088_platform_sweep_audit.sql` is the platform **sweep** audit table — it covers owner sweeps, not the switch (`:1-20`, table at `:54-72`), so it does not close this gap.

The gap is carried as row **"A-F2 healing_switch_set_system has no durable audit record (S301)"** (`docs/status-carried.md:872`, detail `docs/security/resolver-review.md:92-97`: the only record is the mutable row plus an information log line; P2; receiver is the "Platform security event table (D-S137-OWNER)" queue row). `[OPEN]`

## 9. Known gaps

- **The system flip has no durable audit record.** A-F2, as above (`docs/security/resolver-review.md:92-97`). `[OPEN]`
- **The system route admits nobody today.** The default operator gate denies every caller; the real credential is 02.8.1-T1 (`src/Evidgate.Api/OperatorIngress/OperatorGate.cs:30-33`, `plans/00-overview.md:333`). A system-scope drill cannot be executed by a human until it lands. `[OPEN]`
- **`enabled` cannot be set on this base.** `Healing:Apply` defaults to false and no infra configuration file sets it (`src/Modules/Evidgate.Modules.Healing/Application/HealingSwitches.cs:16-29`; environment spelling `EVIDGATE__HEALING__APPLY`, `:16-19`), so the `enabled` leg of AC6's `enabled → shadow → disabled` answers 422 `mode_not_available` (`:74-80`). That is the V1 posture ruling T33 records: only `shadow` and `disabled` are settable until the V2 apply path is switched on (`plans/features/README.md:88`). ⚠️ The drill's first leg is therefore blocked by configuration, not by the switch. `[OPEN]`
- **Nothing consumes the resolved mode yet.** The resolver has no caller outside its module (§6), so a flip changes stored configuration and the `GET` answer, not any behaviour. The apply path is V2. `[OPEN]`
- **`unavailable` is never produced.** The health probes and `dependency_health` values are 08.6.3-T2's; until then both fields are null (`src/Modules/Evidgate.Modules.Healing/Application/GetHealingSwitch.cs:54-60`). The runbook can prove `unavailable` is not settable; it cannot yet observe the platform producing it. `[OPEN]`
- **The healing-false-positive alert does not exist, so this file is not linked from it.** T5.1's done-when is "runbook committed and linked from the healing-false-positive alert" (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md:125`); the rules are 10.3.1-T5.1 under `infra/alerts/<env>/` and the coverage lint is `scripts/check-alerts.sh` (`plans/features/10-operations/10.3.1-telemetry-and-alerting.md:135-136`). On this base `infra/` holds no alert rule and `scripts/` holds no `check-alerts.sh` (source search), so the link target for that rule is **this file**, to be wired when 10.3.1-T5.1 lands. `[OPEN]`
- **No second-person drill.** T5.2 is open (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md:126`). `[OPEN]`

## 10. Related runbooks

- [dead-letter](dead-letter.md) — the queue's failed work; its §5.4 records the operator-ingress audit gap this file's A-F2 shares.
- [stuck run](stuck-run.md) — a run past its collection deadline.
- [incident process](incident-process.md) — severity ladder, on-call expectation and postmortem (10.7.1-T6).
- [breach](breach.md) — security incidents; a kill-switch flip during one belongs in its timeline.
- [environments](environments.md) — how to obtain a session against the environment you are in.

## 11. Evidence table

Every route, permission, table, column and UI path this runbook uses, and where it is defined.

| Identifier | Kind | Cite |
|---|---|---|
| `public.healing_switch` | table | `migrations/0052_healing_switch.sql:105-134` |
| `healing_switch.scope` (`system`, `tenant`, `project`) | column + CHECK | `migrations/0052_healing_switch.sql:107`, `:121` |
| `healing_switch.mode` (`enabled`, `shadow`, `disabled`; `unavailable` refused) | column + CHECK | `migrations/0052_healing_switch.sql:110`, `:122`, reason `:47-49` |
| `healing_switch.reason` (≤ 2000 chars) | column + CHECK | `migrations/0052_healing_switch.sql:111`, `:127-132` |
| `healing_switch.set_by`, `.at`, `.version`, `.updated_at` | columns (+ trigger) | `migrations/0052_healing_switch.sql:112-116`, `:149-154` |
| `healing_switch_scope_org_check`, `healing_switch_scope_project_check` | CHECKs | `migrations/0052_healing_switch.sql:123-126` |
| `healing_switch_system_key`, `_tenant_key`, `_project_key` | partial unique indexes | `migrations/0052_healing_switch.sql:139-147` |
| `healing_switch_app_read` (`org_id is null or org_id = current_org_id()`), `healing_switch_app_write`, `healing_switch_owner_backfill` | RLS policies | `migrations/0052_healing_switch.sql:162-183` |
| `healing_switch` grants: app `select` + column-scoped `insert`/`update`, no `delete`; operator `select` (no policy); worker_sched none | grants | `migrations/0052_healing_switch.sql:191-200` |
| `evidgate.healing_switch_set_system(text, text, uuid)` — security definer, `search_path = ''`, tenant-transaction refusal, mode validation, reason bound | function | `migrations/0052_healing_switch.sql:211-288` (refusal `:237-240`, mode `:244-247`, reason `:251-254`) |
| definer ACL: PUBLIC revoked, `EXECUTE` to `evidgate_app` alone | grants | `migrations/0052_healing_switch.sql:290-295` |
| `PUT /v1/healing/switch` | route | `src/Modules/Evidgate.Modules.Healing/Endpoints/HealingEndpoints.cs:35-37`; contract `contracts/openapi.yaml:6086-6143` |
| `GET /v1/healing/switch` | route | `src/Modules/Evidgate.Modules.Healing/Endpoints/HealingEndpoints.cs:31-33`; contract `contracts/openapi.yaml:6046-6085` |
| `POST /internal/operator/healing/switch` | route | `src/Evidgate.Api/Endpoints/OperatorEndpoints.cs:49-54` (handler `:170-210`) |
| `healing:configure` | permission (route) + catalogue row | `src/BuildingBlocks/SharedKernel/Authorization/Permissions.g.cs:163`; `migrations/0010_identity_permission_catalogue.sql:329`; held by `tenant_admin` `:302` |
| `healing:triage` | permission (route) + catalogue row | `src/BuildingBlocks/SharedKernel/Authorization/Permissions.g.cs:169`; `migrations/0010_identity_permission_catalogue.sql:331`; held by `qa_engineer` `:299` |
| `membership_role` (a membership may hold several roles) | table + unique | `migrations/0011_identity_membership.sql:120`, `:129` |
| member permission set = union over the membership's roles | authz SQL | `src/Modules/Evidgate.Modules.Identity/Infrastructure/Persistence/IdentityAccessSql.cs:41-55` |
| `healing-switch-set.v1` (closed, `x-max-bytes` 4096, `mode` free string) | contract schema | `contracts/schemas/healing-switch-set.v1.json:1-34` |
| `Idempotency-Key` (required, ≤ 128 chars, 24 h) | header | `contracts/openapi.yaml:8948-8956`; enforcement `src/Modules/Evidgate.Modules.Healing/Endpoints/HealingEndpoints.cs:61-67` |
| `X-Evidgate-Org` (mandatory for every member request on routes without `{o}`) | header | `src/Evidgate.Api/Tenancy/TenantResolver.cs:22-23`, `:57-62`; read at `src/Evidgate.Api/Tenancy/TenantResolutionFilter.cs:121-137` |
| `__Host-evidgate_session` (the member credential; the session authenticator answers 401 when an `Authorization` header is present) | auth | `src/Modules/Evidgate.Modules.Identity/Sessions/Http/SessionCookieNames.cs:28`; refusal `src/Modules/Evidgate.Modules.Identity/Sessions/Http/SessionRequestAuthenticator.cs:88-94`; other authenticator is CI-producer `src/Modules/Evidgate.Modules.Identity/IdentityModule.cs:258-259` |
| `XSRF-TOKEN` / `__Host-evidgate_af` cookies + `X-XSRF-TOKEN` header (403 `csrf_failed` without it) | antiforgery pair | `src/Modules/Evidgate.Modules.Identity/Sessions/Http/SessionCookieNames.cs:31`, `:34`, `:37`; enforcement `src/Evidgate.Api/Middleware/SessionXsrfMiddleware.cs:12-15`; contract `contracts/openapi.yaml:8488-8489`, `:8973-8980`; `plans/00-overview.md:329` |
| `X-Request-Id` (honoured only as a bounded printable token, then echoed; carried on the audit row as `request_id`) | header | `src/Evidgate.Api/Middleware/RequestIdMiddleware.cs:37-45`, `:49`; bound `src/BuildingBlocks/Observability/Correlation/CorrelationNames.cs:52-53`, `:70-88`; consumption `src/BuildingBlocks/Persistence/Audit/AuditTrail.cs:45-46`, `:95-101` |
| no `If-Match` on the switch write; `version` is the concurrency token | contract note | `contracts/openapi.yaml:6093-6096` |
| `HealingAvailability` / `HealingAvailabilityModes` | enum + wire vocabulary | `src/Modules/Evidgate.Modules.Healing.Contracts/HealingAvailability.cs:8-91` |
| `HealingAvailabilityResolution.Resolve` / `.Rank` / `NoRowDefault` | resolution rule | `src/Modules/Evidgate.Modules.Healing.Contracts/HealingAvailability.cs:99-140` |
| `IHealingAvailabilityResolver` (+ registration) | interface / seam | `src/Modules/Evidgate.Modules.Healing.Contracts/HealingAvailability.cs:142-157`; `src/Modules/Evidgate.Modules.Healing/HealingModule.cs:38` |
| `HealingModeGate` (mode + reason validation, `mode_not_available`) | validation | `src/Modules/Evidgate.Modules.Healing/Application/HealingSwitches.cs:55-123` |
| `HealingScopeGuard.Project` | validation | `src/Modules/Evidgate.Modules.Healing/Application/HealingSwitches.cs:130-150` |
| resolution / upsert SQL (`Resolution`, `UpsertTenant`, `UpsertProject`) | statements | `src/Modules/Evidgate.Modules.Healing/Application/HealingSwitches.cs:187-209` |
| `HealingSwitchStore.ReadAsync` / `UpsertAsync` | data path | `src/Modules/Evidgate.Modules.Healing/Application/HealingSwitches.cs:253-286` |
| `SetHealingSwitchCommandHandler` (`system` refused, scope shape, project checks, audit, outbox) | handler | `src/Modules/Evidgate.Modules.Healing/Application/SetHealingSwitch.cs:76-201` |
| `healing.switch_changed` (task type, aggregate `healing_switch`, per-version key, no handler) | outbox event | `src/Modules/Evidgate.Modules.Healing/Application/SetHealingSwitch.cs:223-269` |
| `HealingSystemSwitchWriter` (definer call, log 8603, no audit/outbox) | writer | `src/Modules/Evidgate.Modules.Healing/Application/HealingSystemSwitch.cs:34-104` |
| `HealingOptions` / `Healing:Apply` (`EVIDGATE__HEALING__APPLY`, default false) | configuration | `src/Modules/Evidgate.Modules.Healing/Application/HealingSwitches.cs:15-29` |
| `public.audit_log` (per-org chain, `org_id NOT NULL`) | table | `migrations/0015_identity_audit_log.sql:25-45` |
| `GET /v1/orgs/{o}/audit` (`audit:read`; `object_type`, `action`, `from`, `to` filters, **no `object_id` filter**) | route | `contracts/openapi.yaml:4635-4654`; parameters `:8655-8679` |
| `AuditEntryDto` (`metadata`, `object_id`, `request_id`, `created_at`, hashes) | wire shape | `src/Modules/Evidgate.Modules.Identity.Contracts/AuditDtos.cs:19-34` |
| `mode` on the audit metadata allowlist | allowlist | `src/BuildingBlocks/Persistence/Audit/AuditMetadata.cs:52` |
| `public.platform_sweep_audit` (sweep audit, not the switch) | table + function | `migrations/0088_platform_sweep_audit.sql:54-72` (table), `:138-159` (`evidgate.record_sweep_audit`) |
| A-F2 carried row | status record | `docs/status-carried.md:872`; detail `docs/security/resolver-review.md:92-97` |
| T33 — only `enabled`, `shadow` and `disabled` are settable; `unavailable` is derived; V1 allows `shadow`/`disabled` | ruling | `plans/features/README.md:88` |
| DECISIONS §5.9 item 9 — switches at every scope before apply; failure leaves tests and gates running | locked rule | `DECISIONS.md:112` |
| 00 §8.7 — healing availability vocabulary and `system → tenant → project` resolution | contract | `plans/00-overview.md:303` |
| AC6, T5.1, T5.2 — drill, runbook, flip order | story | `plans/features/10-operations/10.7.1-operable-by-a-second-person.md:27`, `:125-126` |
| Availability nav entry (`availability`, project scope, `healing:triage`) | UI nav | `apps/web/src/app/shell/nav/nav-registry.ts:210-221`; mount `apps/web/src/app/app.routes.ts:68` |
| Availability route table | UI route | `apps/web/src/app/features/availability/availability.routes.ts:13-14` |
| `HealingSwitchPage` (read gate, write gate) | UI page | `apps/web/src/app/features/availability/healing-switch.page.ts:33-45`; template `healing-switch.page.html:34-116` |
| `HealingSwitchStore` (write targets, ancestor blocker, save + reload) | UI store | `apps/web/src/app/features/availability/healing-switch.store.ts:23-30`, `:65-73`, `:176-232` |
| `SwitchScopeControl` (three modes, optional reason, read-only rules) | UI component | `apps/web/src/app/features/availability/switch-scope-control.component.ts:24-29`, `:43-77`, `:160-164` |
| `reason-limit.ts` (2000 code points + 4096-byte body) | UI guard | `apps/web/src/app/features/availability/reason-limit.ts:5-39` |
| Availability copy (system read-only, reason optional, blocked-ancestor notes) | UI copy | `apps/web/src/app/features/availability/healing-switch.copy.ts:29`, `:43-45`, `:52-53` |
| alert rules `infra/alerts/<env>/` + `scripts/check-alerts.sh` (absent on this base) | planned | `plans/features/10-operations/10.3.1-telemetry-and-alerting.md:135-136` |

Aliases in the examples (`API`, `JAR`, `CSRF`, `ORG`, `REQUEST_ID`, `KEY`, `PROJECT`) are shell variables, not wire fields (`X-Evidgate-Org`, `X-XSRF-TOKEN`, `X-Request-Id`, `Idempotency-Key` are the wire names).
