# Security alert rules (V1)

Owner: 11.5.1-T3.1 (task [11.5.1](../../plans/features/11-security-compliance/11.5.1-who-saw-what-provably-acceptance-audit.md) §10, AC5). One rule row per type of the closed V1 `security_event` set fixed in [security-event-baseline](security-event-baseline.md) §2. This page decides signal source, threshold or pattern, severity and the on-call routing decision; [10.3.1](../../plans/features/10-operations/10.3.1-telemetry-and-alerting.md)-T5 turns each row into a rule under `infra/alerts/<env>/` (numeric threshold, severity and owner: T5.1; runbook link: T5.2; channel routing and grouping: T5.3; synthetic drill: T5.4).

Version: v1 · Last review: 2026-11-05 (S247, develop `446beee`)
Legend: ✅ verified with a file:line or link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`

## 1. Scope and status

- **Rules, not alerts.** Nothing here is deployed. `infra/alerts/` does not exist yet and no emitter writes a `security_event` row (§3), so every rule below is a specification handed to 10.3.1-T5, not a live control.
- **Types are the baseline's, and only those.** The set is the five `type` values of [security-event-baseline](security-event-baseline.md) §2, enforced by the closed array in `src/Modules/Evidgate.Modules.Identity/Endpoints/SecurityEventEndpoints.cs:21-25` ✅. `tests/Architecture.Tests/SecurityEventEmitterCoverageTests.cs` fails when a type appears in the baseline or the endpoint without a row here.
- **Severity is the baseline's** (closed `info | warning | critical`, baseline §1). A rule row may raise a pattern to `critical`; it never invents a fourth level.
- **Thresholds are starting points.** No vendor or standard fixes a threshold for these Evidgate-specific signals, so every number below is `[INFERENCE]` ⚠️ derived from the baseline's impact reasoning, to be tuned by 10.3.1-T5.4 drill data and the first pilot traffic. Do not cite them as an external benchmark.
- **Tenant visibility is separate from paging.** A `security_event` row is always readable by the tenant's `tenant_admin` (`security_event:read`, baseline §4); "route" below is only the Evidgate-side on-call decision.
- **No secret in a signal.** Rules count rows by `type`, `severity`, principal `{kind, id}` and a closed `details` enum; they never read or quote a credential, a matched secret value or a foreign org/project id (baseline §1 metadata rule).

## 2. Rules

Routing vocabulary (`[DECISION]` for V1, consumed by 10.3.1-T5.3): `page` = wake the on-call engineer now; `ticket` = business-hours queue, reviewed daily; `digest` = weekly security review only, no alert. Runbooks that exist today: [breach](../runbooks/breach.md); a rule whose runbook does not exist yet takes a stub from 10.7.1 (10.3.1-T5.2).

| Rule id | Baseline row | Signal source | Threshold / pattern `[INFERENCE]` ⚠️ | Severity | On-call route |
|---|---|---|---|---|---|
| `AR-auth-failure-burst` | [baseline §2](security-event-baseline.md) `auth_failure` · §3.1 | `security_event` rows with `type=auth_failure`, grouped by `details.surface` (`login`, `session`, `ci_exchange`, `ingest_token`) and by `actor.id`. Until an emitter exists the only live signals are the login and session counters `evidgate_login_attempts_total` and `evidgate_session_auth_total` (`src/BuildingBlocks/Observability/EvidgateTelemetry.cs:65,73`) ✅; the CI and ingest-token surfaces have none (baseline §5 row 4) | Per rule evaluation window of 5 min: ≥ 20 rows for one `surface` across the platform, or ≥ 10 rows for one `actor.id` on one org, whichever first. A single row never alerts | `warning`; `critical` when the same `actor.id` also produced one accepted exchange in the window (a guessed credential that worked) | `ticket` for `warning`; `page` for `critical`; grouped per org and surface |
| `AR-cross-tenant-probe-pattern` | [baseline §2](security-event-baseline.md) `cross_tenant_probe` · §3.2 | `security_event` rows with `type=cross_tenant_probe`, grouped by `actor.id` and `details.surface` (`member_org`, `service_org`, `foreign_id`). No live signal today: a denied read writes no row (`audit-taxonomy.md:62`) | One foreign-id answer is indistinguishable from a stale bookmark, so no alert on a row. Per 10 min: ≥ 5 rows for one `actor.id` across ≥ 3 distinct routes, or ≥ 25 rows for one `actor.id` on one route | `warning` per row (stored, not alerted); `critical` once the pattern matches (baseline §3.2) | `page` on `critical` (an enumeration attempt, handled under [breach](../runbooks/breach.md)); the `warning` rows are `digest` |
| `AR-secret-scanner-flagged` | [baseline §2](security-event-baseline.md) `secret_scanner_alert` · §3.3 | `security_event` rows with `type=secret_scanner_alert`, one per flagged artifact (`details.artifact_id`, `pattern_class`, `hit_count`). State-only precursor today: `artifact.scan_state=flagged` in `migrations/0028_evidence_artifact.sql:59,72` ✅, which nothing writes yet | Every row alerts: a secret is already stored in tenant-visible evidence and the disclosure clock has started (baseline §3.3). Grouped per org so one noisy run raises one page, not one per artifact | `critical` | `page`; first action is the viewer quarantine of the artifact, then the [breach](../runbooks/breach.md) disclosure decision |
| `AR-kill-switch-used` | [baseline §2](security-event-baseline.md) `kill_switch_used` · §3.4 | `security_event` rows with `type=kill_switch_used`, `details.scope` (`system`, `tenant`, `project`) and `details.automatic`. No signal today: healing is an empty contract (`src/Modules/Evidgate.Modules.Healing.Contracts/IHealing.cs:8-10`) ✅ | Any row alerts: a deliberate switch write is rare and its interest is visibility. `scope=system` or `automatic=true` is distinguished by severity, not by a higher count | `warning`; `critical` when `scope=system` or `automatic=true` (a platform-wide or unattended stop) | `ticket` for `warning`; `page` for `critical` |
| `AR-support-access-grant` | [baseline §2](security-event-baseline.md) `support_access` · §3.5 | `security_event` rows with `type=support_access`, `details.action` (`grant_requested`, `approved`, `revoked`, `read`, `write`) and `details.grant_id`. No signal today: no grant table exists and the operator principal carries no permissions (`src/BuildingBlocks/Pipeline/Authorization/IRequestPrincipal.cs:145-146`) ✅ | Every grant row and every grant-derived read or write is stored and visible to the tenant. Alert on a row whose grant is expired or revoked, or whose object is outside the grant scope (any count), and on ≥ 1 grant created outside the on-call's open support ticket window (matching rule input comes with 02.8.1-T2.2) | `warning`; `critical` for an expired or revoked grant or an out-of-scope attempt (baseline §2) | `ticket` for `warning` (the tenant also sees it); `page` for `critical` |

## 3. Emitter state

No baseline type has a production emitter on develop `446beee`. The coverage test lists the five as known gaps and fails the moment one gains an emitter without being removed from the list, and the moment a new type appears with neither an emitter nor a gap entry.

| Type | Emitter state | Planned producer (task id) | Receiver of the gap |
|---|---|---|---|
| `auth_failure` | none | none assigned; candidates 02.3.1/02.3.2 login and session, 02.4.1-T1 and 02.4.2-T1.2 exchange (baseline §5 rows 2-3) | 02.4.1-T1 owner with OI-02-31 |
| `cross_tenant_probe` | none | none assigned; OI-02-31 recommends the row | 02.4.1-T1 owner with OI-02-31 |
| `secret_scanner_alert` | none | detection 06.2.3-T1; no sub-task writes the event | 06.2.3 owner |
| `kill_switch_used` | none | detection 08.6.3-T1.2; no sub-task writes the event | 08.6.3 owner |
| `support_access` | none | 02.8.1-T2.2 | 02.8.1 owner |

The gap is also recorded as a carried row, "Security event emitters missing (11.5.1-T3.2)" `[OPEN]`, appended by the close-out of this change.

## 4. Hand-over to 10.3.1-T5

| 10.3.1 sub-task | What this page gives it |
|---|---|
| T5.1 | The five rule ids, their pattern, severity and route; the numeric thresholds in §2 are the proposals to finalise |
| T5.2 | Runbook: [breach](../runbooks/breach.md) for `critical` rules; stubs for the rest are 10.7.1's |
| T5.3 | The `page` / `ticket` / `digest` split and the grouping keys (org, surface, `actor.id`) |
| T5.4 | One synthetic breach per rule id; a rule has no drill until its emitter exists (§3), so the drill injects rows through the same table |

## 5. Known gaps

| # | Gap | Receiver | State |
|---|---|---|---|
| 1 | Every rule depends on an emitter that does not exist (§3); until then a rule can only be drilled with injected rows | the receivers in §3 | `[OPEN]` |
| 2 | Numeric thresholds are `[INFERENCE]`; no baseline traffic exists to tune them | 10.3.1-T5.4 drill data and the first pilot | `[OPEN]` |
| 3 | `AR-support-access-grant` names the on-call ticket window, which has no data source before 02.8.1-T2.2 | 02.8.1-T2.2 | `[OPEN]` |
