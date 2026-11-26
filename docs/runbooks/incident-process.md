# Incident process

Status: **10.7.1-T6.1 written; T6.2 (postmortem template, staged exercise, check-alert-runbooks wiring) open.**

This page is the process, not a tool: no paging route, no rule file under `infra/alerts/` and no coverage lint exists on this tree yet, so every step that depends on one says so. The severity ladder (§2) and the pilot on-call answer (§3) are usable today; the customer-facing half of §4 and the incident-record location in §5 are `[OPEN]`.

Legend as in [`../../AGENTS.md`](../../AGENTS.md): ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`. Sources use document headings, task ids, symbols and migration names rather than line pins. The [evidence table](#7-evidence-table) includes the secret-shape-timeout sources; the desk check below is against wave `fd69e03b`, not evidence of a staging drill.

## 1. Scope, audience and rules

**Purpose.** Give one person a decision procedure for an operational incident on the Evidgate pilot: how bad it is (§2), who responds (§3), what is said and to whom (§4), the order of steps (§5), and which existing runbook does the work (§6). The story's acceptance for this half is only "severity ladder covers at minimum P1–P3 with entry criteria" (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md (“10.7.1-T6 — Incident process (M, M3)”)`).

**Audience.** The operator on duty — today the owner alone (DECISIONS §8 O2, `DECISIONS.md (“8. Open decisions (owner)”)`; `AGENTS.md (“Authority and editing rules”)`). Written so a second person can use it without tribal knowledge (`plans/10-operations.md (“Story 10.7.1 Operable by a second person — acceptance: each runbook is executed once by someone who did not write it, in staging, and its steps are correct without tribal knowledge”)`), which is also the area plan's mitigation for the bus-factor risk (`plans/10-operations.md (“8. Risks”)`).

**In scope.** Any event that breaks one of the product invariants (`AGENTS.md (“Invariants code must keep”)`), one of the measured targets (`plans/00-overview.md (“11. Non-functional requirements (all plans)”)`), or the trustworthiness of a decision (`plans/10-operations.md (“1. Purpose and scope”)`), in dev, staging or prod (`plans/10-operations.md (“2. Requirements”)`).

**Out of scope.** The content of each technical runbook (§6 links them); the paging tool, its routing table and its escalation timeout (10.7.2, `plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md (“1. Goal and non-goals”)`); the breach runbook's notice template and legal mapping ([breach](breach.md)); customer support severities and response expectations (12.2.1-T5, `plans/12-pilot-commercial.md (“Story 12.2.1 A pilot that can be measured and paid for - acceptance: agreement + DPA signed before any upload, baseline captured for two sprints, weekly reviews held, support hours metered.”)`); DR failover ([`plans/features/10-operations/10.7.1-operable-by-a-second-person.md (“1. Goal and non-goals”)`](../../plans/features/10-operations/10.7.1-operable-by-a-second-person.md)).

**Rules.**

- **The invariant, not the metric, decides the severity.** Overview §11 labels its NFR targets "pilot, to be measured; not an SLA" (`plans/00-overview.md`, “Non-functional requirements”). A threshold breach is an alert; an invariant breach is an incident.
- **A missing runbook is not permission to improvise.** If the class has no runbook, stop at "contain and record" and escalate; never hand-write a database change (`docs/runbooks/dead-letter.md (“1. Purpose, audience and rules”)`, `docs/runbooks/key-rotation.md (“1. Scope, roles and rules”)`).
- **Ids, closed enums, counts, timestamps and hashes only.** No secret values, payloads, tenant names, repository names, URLs, hostnames or e-mail addresses in tickets, records or chat (`docs/runbooks/breach.md (“1. Scope, roles and rules”)`, `docs/runbooks/dead-letter.md (“1. Purpose, audience and rules”)`).
- **One record per incident, written while it happens**, with the field names in §5. A late record reconstructed from memory is a finding, not a record.
- **Never close an incident by deleting evidence** (`docs/runbooks/breach.md (“1. Scope, roles and rules”)`); a deletion request that arrives mid-incident goes to the deletion procedure, not to this one.
- **Severity is a property of the incident, not of the alert.** The planned paging-rule label contract is `severity ∈ {P1,P2,P3}` (`plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md`, “Contract changes”); re-declaring is expected (§5) and must be recorded with a reason.

## 2. Severity ladder

Three incident levels match the planned paging-rule label contract (`severity ∈ {P1,P2,P3}`, `plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md`, “Contract changes”). There is no P4: anything below P3 is a ticket, not an incident. When two triggers match, take the more severe one.

Entry criteria are tests over repo invariants and states. **No deployed numeric alert-rule files exist on this tree.** Numeric proposals do exist in `docs/security/alert-rules.md` (“Rules”, “Operational counter rules”), including `AR-secret-shape-timeout`; they are not deployed controls. Rule deployment remains 10.3.1-T5.1 (`plans/features/10-operations/10.3.1-telemetry-and-alerting.md`, task 10.3.1-T5.1).

### P1 — invariant broken; stop everything else

| # | Entry criterion (any one) | Why this is P1 | Runbook |
|---|---|---|---|
| P1-a | A tenant's row, artifact, presigned URL, audit row or decision is readable or writable by a principal outside that tenant (confirmed or plausible) | Tenant isolation is the first invariant (`AGENTS.md (“Invariants code must keep”)`); a cross-tenant path is the red-team failure (`plans/11-security-compliance.md (“5. Failure modes and edge cases”)`) | [breach](breach.md) §3 |
| P1-b | Evidence or a decision is not what it was: a manifest/revision/evaluation row was edited or deleted (`AGENTS.md (“Invariants code must keep”)`), a decision is PASS while a required shard or artifact was missing (`AGENTS.md (“Invariants code must keep”)`, `docs/decisions/O1-pilot-profile.md (“3. Pilot acceptance sheet (what the QA lead signs)”)`), or a decision changed without an approved shadow diff (`plans/10-operations.md (“5. Failure modes and edge cases”)`) | Completeness and immutability are the product; a wrong PASS is released to customers (`plans/10-operations.md (“1. Purpose and scope”)`) | [breach](breach.md) §3 if tenant data is involved, else §5 record + engineering fix |
| P1-c | An acknowledged event is lost: the producer was told "committed" and the row is absent, or a restore lands before the last acknowledged commit | "Acknowledged only after commit" (`AGENTS.md (“Invariants code must keep”)`); the crash invariant is "no lost acknowledged event" (`plans/10-operations.md (“5. Failure modes and edge cases”)`, `plans/10-operations.md, “5. Failure modes and edge cases”`) | [dead-letter](dead-letter.md) §2/§7, [stuck-run](stuck-run.md) §5; restore/DR runbook `[OPEN]` 10.5.1-T4 |
| P1-d | A secret reached tenant data or a tenant credential (in a log, an artifact, an error envelope, a screenshot) | Secrets never in logs or labels (`plans/11-security-compliance.md (“5. Failure modes and edge cases”)`); containment is purge-then-rotate, with the rotation procedure in the key-rotation runbook (`docs/runbooks/key-rotation.md (“1. Scope, roles and rules”)`) | [breach](breach.md) §3.2 row 1, then [key-rotation](key-rotation.md) |
| P1-e | The audit hash chain is broken, a gap or a mismatch in one org's chain | Treated as possible tampering; the product loses its reconstruction claim (`docs/runbooks/breach.md (“3.2 Signal-to-action matrix”)`, `plans/10-operations.md (“2. Requirements”)`) | [breach](breach.md) §5.2 |
| P1-f | The service is unusable for every tenant: readiness red on every API replica, or every ingest batch fails | Total loss of the core loop; availability target 99.5 %/month is measured, not promised (`plans/00-overview.md (“11. Non-functional requirements (all plans)”)`, health endpoints at `plans/10-operations.md (“4. Design”)`) | restore/DR runbook `[OPEN]` 10.5.1-T4; release runbook `[OPEN]` 10.2.2-T4 |

**P1 behaviour.** Drop everything else. Start the record before the first fix. If P1-a or P1-d holds, the breach clock starts at once — record `signal_seen_at` and the notice fields as they stand — but **containment still comes first**, exactly as §5 step 4 orders it and as the breach matrix's ordered first actions do: record ids, times and a hash, restrict access, then purge/redact and rotate, and notify on the facts that gives you (`docs/runbooks/breach.md (“1. Scope, roles and rules”)`). Do not hold a containment step while a notice is written; send the first notice as soon as containment allows, and add detail later (`docs/runbooks/breach.md (“1. Scope, roles and rules”)`).

### P2 — degraded, bounded, no invariant broken yet

| # | Entry criterion (any one) | Bound that keeps it P2 | Runbook |
|---|---|---|---|
| P2-a | One tenant or project cannot ingest, or its runs cannot complete, while other tenants are served | Blast radius is one tenant/project; nothing acknowledged is lost yet | [stuck-run](stuck-run.md), [dead-letter](dead-letter.md) |
| P2-b | Dead-letter depth grows for one `task_type` with no dependency outage (`docs/runbooks/dead-letter.md`, “Verification and escalation”) | Work is queued, not necessarily lost. Terminal retention is 30 days from task `created_at`, not from dead-lettering, so remaining investigation time can be much shorter (`EvidgateSweeps.TerminalRetentionDays`, `TaskSweepTaskHandler.SweepSql`) | [dead-letter](dead-letter.md) |
| P2-c | Overdue runs accumulate: the collection-deadline sweep is not settling them (`docs/runbooks/stuck-run.md (“§4.3 Is the sweep running at all?; §5.0 The command surface this runbook relies on”)`) | Evidence stays absent rather than wrong; a missing shard never yields PASS (`AGENTS.md (“Invariants code must keep”)`) | [stuck-run](stuck-run.md) §4.3/§5.1 |
| P2-d | A tenant reports a wrong heal while gates still run; the future false-positive-rate threshold would be another trigger | Healing must never block ingest, runs or gates (`plans/10-operations.md`, FR-10-11); the kill switch bounds it (`DECISIONS.md`, §5 rule 9). **Not possible yet:** automated false-positive-rate detection; the metric is unavailable (`infra/metrics/catalogue.yaml`, `healing_false_positive_rate`), receiver 10.3.1-T3.1 with plan 08 healing work | [kill-switch](kill-switch.md), drills in `plans/10-operations.md`, “Test strategy” |
| P2-e | A deploy, canary or policy revision is blocked or would change decisions in shadow | The compare exists to stop exactly this before customers see it (`plans/10-operations.md (“5. Failure modes and edge cases”)`, `10.2.2-T2`) | release runbook `[OPEN]` 10.2.2-T4 (`plans/10-operations.md (“Story 10.2.2 Deploy with expand/contract and canary — acceptance: a deploy never takes traffic on a version whose migrations have not run, and a decision-changing canary is blocked by the shadow compare”)`) |
| P2-f | A key class is past its rotation window, or a rotation did not complete cleanly | No evidence of use yet; the rotation procedure bounds it (`docs/runbooks/key-rotation.md (“1. Scope, roles and rules”)`) | [key-rotation](key-rotation.md) |
| P2-g | A backlog alert fires for queue age, outbox lag, lease loss or incomplete rate (the FR-10-6 set); or the secret-shape timeout counter escalates per its runbook (host stall, or a timeout refused an otherwise acceptable value) | Ingest keeps accepting within quota instead of failing open (`plans/10-operations.md (“2. Requirements”)`, `plans/10-operations.md, “5. Failure modes and edge cases”`) | [dead-letter](dead-letter.md), [stuck-run](stuck-run.md), [secret-shape-timeout](secret-shape-timeout.md) |

**P2 behaviour.** Work it in the same working session, and record it. The session window is the `[DECISION]` proposal of §2 and **not in force**: until the owner signs it, treat "the same working session" as unset and fall back to the breach runbook's internal S2 cadence, itself `[INFERENCE]`/`[OPEN]` (`docs/runbooks/breach.md (“3.3 Severity to action”)`, `breach.md, “Severity to action”`). If a P2 holds long enough that data could be lost or a tenant's release is blocked, it becomes P1 by re-declaration.

**Healing-scope constraint (P2-d).** A system flip is supported through `psql` as `evidgate_platform_operator`, calling `evidgate.healing_switch_set_system` (migration `0105_platform_operator_role`; bootstrap `evg_db_grant_application_logins` in `scripts/env/lib-db.sh`). The HTTP operator route remains denied. Tenant/project writes require a member session with `healing:configure`; reading the result also needs `healing:triage` (`SetHealingSwitchCommandHandler`, `GetHealingSwitchQueryHandler`; `docs/runbooks/kill-switch.md`, “Prerequisites”). Use the narrowest scope that bounds the incident, and record it.

### P3 — contained; one row, one run, one alert

| # | Entry criterion (any one) | Runbook |
|---|---|---|
| P3-a | One run is stuck past its collection deadline and no other run is affected | [stuck-run](stuck-run.md) §5 |
| P3-b | One task row is dead-lettered once and no depth growth is visible | [dead-letter](dead-letter.md) §3/§5 |
| P3-c | One alert fires and the runbook's own triage shows no customer impact (a false positive with a documented benign cause, `docs/runbooks/breach.md (“3.1 Classes”)`) | the runbook the alert links to |
| P3-d | Telemetry or a dashboard cannot answer a reconstruction question for one run (`plans/10-operations.md (“2. Requirements”)`) | [telemetry](telemetry.md) |

**P3 behaviour.** Handle in working hours, and record it — like the P2 window, "working hours" is the `[DECISION]` proposal of §2 and **not in force**: until the owner signs it, treat the window as unset. `plans/10-operations.md (“5. Failure modes and edge cases”)` fixes only alert grouping and post-drill tuning, not a repeat rule, so the repeat rule below is a proposal too and **not in force**: if the same class fires twice in one week, re-declare P2 and treat the pattern as the incident. Nothing in the repo defines that window today.

### Response targets

**No response-time target for incidents exists in the repo.** The numbers that do exist belong elsewhere and must not be borrowed silently:

| Recorded number | What it is | Status |
|---|---|---|
| first processor→controller notice within 4 h of triage confirmation | breach notice target, not an incident-dispatch target | ⚠️ proposed, pending owner sign-off (`docs/runbooks/breach.md (“1. Scope, roles and rules”)`) |
| 72 h | the **controller's** regulator clock; our duty is prompt notice (`docs/runbooks/breach.md (“1. Scope, roles and rules”)`, `plans/11-security-compliance.md (“2. Requirements”)`) | legal mapping ⚠️, `[OPEN OI-11-08]` |
| escalation timeout 15 min | paging tool's unanswered-page timeout | `[INFERENCE]`, 10.7.2 AC4 (`plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md (“2. User stories and acceptance criteria”)`) |
| P1 always pages; P2 07:00–23:00 local | quiet-hours policy | `[INFERENCE]` for the owner to confirm, 10.7.2 AC6 (`plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md (“2. User stories and acceptance criteria”)`) |

`[DECISION]` proposal for owner sign-off, **not in force**: acknowledge a P1 within 15 min of seeing the page, a P2 in the same working session, a P3 by the next working day. P2/P3 reuse the breach runbook’s proposed session/day cadence; the P1 fifteen-minute acknowledgement is a separate proposal, not its S1 rule (`docs/runbooks/breach.md`, “Severity to action”: start now, first notice inside the proposed 4 h target). Until the owner signs it, every proposed incident-response window remains unset.

## 3. Pilot on-call expectation (takes O2's answer)

**O2 is decided, and the answer is: the owner alone is the only responder.** Owner decision of 2026-10-02, quoted: *"the team is one person (the owner) — one developer who is also Tech Lead and QA/product owner"*, and the 00 §14 unit "now equals the whole team" (`DECISIONS.md (“8. Open decisions (owner)”)`); restated as decided in `AGENTS.md (“Authority and editing rules”)` and applied to scheduling in `plans/features/SOLO-PATH.md (“Solo delivery path (one developer)”)`, `SOLO-PATH.md, Team row` ("One person is developer, Tech Lead and QA/product owner").

What that means, without softening it:

1. **No rota, no second responder, no handover.** The same person is incident lead, triager, executor, comms and clock owner. The breach runbook's role table already shows where that leaves the roles: sender/channel and counsel are `[OPEN]` (`docs/runbooks/breach.md (“1. Scope, roles and rules”)`).
2. **There is no defined out-of-hours cover.** A staffed 24/7 rota is explicitly not available to a solo operator and is `[OPEN]` for the first hire (`plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md (“1. Goal and non-goals”)`). Consequence: a P1 at 03:00 has **no response window and no guaranteed delivery** — `[OPEN]`, not "best effort within N hours", because no source states N.
3. **There is no pager today.** The dead-letter runbook states it plainly: "there is no pager integration yet" (`docs/runbooks/dead-letter.md (“1. Purpose, audience and rules”)`); the routing table is 10.7.2-T1 and the single-operator escalation fallback is 10.7.2-T2, both open (`plans/10-operations.md (“Story 10.7.2 Paging and on-call at the first alert — acceptance: every alert in 10.3.1-T5 reaches a named destination and a person, including out of hours.”)`). Until they land, an alert is only as good as someone looking at the sink or the dashboard.
4. **The "executed by a person who did not write it" half of AC1/AC7 cannot be met by the team as staffed.** AC1 requires a non-author to execute each runbook in staging (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md (“2. User stories and acceptance criteria”)`), and T6.2's staged exercise is "by a non-author" (`10.7.1-T6.2`); the area plan's mitigation is "drill handover at M3" (`plans/10-operations.md (“8. Risks”)`). With one person there is no non-author inside the team; the drill needs the first hire or a named external person. `[OPEN]` — recorded as a gap, not scheduled here.
5. **Single point of failure is recorded, not eliminated.** The risk row names it (High likelihood, Medium impact), with runbooks for a second person and drill handover at M3 as mitigation (`plans/10-operations.md`, “Risks”). No acceptance decision is recorded here. This runbook reduces knowledge dependency only for documented, reachable actions; denied operator routes and missing procedures remain blockers, not executable steps.
6. **Two documents disagree; DECISIONS wins.** The area plan still lists "O2 team size and who is on-call during the pilot" as `[OPEN]` (`plans/10-operations.md (“11. Open questions”)`). Under the repo's precedence rule (`AGENTS.md (“Authority and editing rules”)`) the decision at `DECISIONS.md (“8. Open decisions (owner)”)` governs: **who responds is settled (the owner); what remains open is a second person / first hire and the paging path**, not the identity of the responder.

## 4. Communication path

**There is no named channel, contact or status page in the repo for a service incident.** This section therefore separates what is defined from what is not, and forbids inventing the rest.

### 4.1 Internal (Evidgate)

- **Who is told:** nobody else exists today (§3). The record is the audience; a second reader appears only with the first hire (`plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md (“1. Goal and non-goals”)`).
- **Through what:** `LoggingTaskAlertSink.NotifyAsync` is the shipped task-queue notification seam, not a pager. Alert routing is 10.3.1-T5.3 and the `infra/alerts/routing.yml` table is 10.7.2-T1 (`plans/features/10-operations/10.3.1-telemetry-and-alerting.md`, task 10.3.1-T5.3; paging plan 10.7.2, AC1). **Not possible yet:** repository-defined paging delivery; those tasks must supply the route and destination. No named service-incident destination is defined here (`docs/runbooks/breach.md`, “Senders and channel”).
- **What is said:** ids, closed enum values, counts, timestamps and hashes only; no secret, payload, tenant name or URL (`docs/runbooks/breach.md (“1. Scope, roles and rules”)`). State the current severity, the affected class (§2), the affected org/project ids, what was done, and the next decision point.
- **Where it is written:** `[OPEN]`. The clock-record location is an open item (`docs/runbooks/breach.md (“6. Open items and receivers”)`); 10.7.2 proposes files under `docs/runbooks/records/`, which does not exist on this tree (`plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md (“4. Data model and migrations”)`). Use the field names in §5 wherever the record lives, and say where it is in the record itself.

### 4.2 Customer-facing

Three different paths; only one has a runbook.

| Situation | Path | State |
|---|---|---|
| Personal data exposed, altered or lost, or a tenant credential leaked | processor→controller notice per [breach](breach.md) §4: one notice per affected controller, never naming another tenant; facts only, unknown where unknown. Its §1 records the controller’s proposed/legal 72 h mapping and our prompt-notice duty | notice target and legal mapping remain ⚠️/[OPEN OI-11-08]; named sender/channel remain [OPEN] under 11.9.1-T2.4 |
| Service incident without personal data (outage, backlog, stuck runs) | no customer-notice rule exists. The status-page choice is 04.9.2-T3, not delivered (`plans/04-ingestion-adapters.md (“Story 04.9.2 CLI/SDK release, compatibility and customer docs — acceptance: a tagged release publishes the CLI and adapters with provenance and a changelog, a client older than the supported window gets an actionable error from the server, and a customer can reach a first accepted run from the docs alone.”)`), and the product is `[OPEN]` (OI-04-12, `plans/open-items/02-catalog-ingest-runs-evidence.md (“Area 04 — ingestion and adapters”)`) | `[OPEN]` |
| Support expectation during the pilot | the pilot agreement must state support expectations (`plans/12-pilot-commercial.md (“2. Requirements”)`); the support process with severity definitions and response expectations is 12.2.1-T5, not written (`12.2.1-T4/T5`); the weekly pilot review is the only recurring customer touchpoint recorded (`12.2.1-T4/T5`) | `[OPEN]` |

**Rules for anything said to a customer.**

- Never promise a fix time, a root cause or a data-loss figure that is not in the record (the measured data-loss statement is the DR runbook's job, itself `[OPEN]` 10.5.1-T4, `plans/10-operations.md (“Story 10.5.1 Recoverability — acceptance: a restore into a clean environment reproduces a run, its evidences and its decision, within RPO 15 min / RTO 4 h”)`).
- Never name another tenant, and never send one customer another customer's identifiers (`docs/runbooks/breach.md (“1. Scope, roles and rules”)`, `breach.md, “Notice template”`).
- Do not wait for a complete picture before the first breach notice; send early, add detail later (`docs/runbooks/breach.md (“1. Scope, roles and rules”)`).
- A suspected breach is still communicated on the breach path: the class table decides notice, not certainty (`docs/runbooks/breach.md (“3.1 Classes”)`).

## 5. Incident lifecycle

Seven steps. Each names the field it writes, so the record can be filled in order; field names follow the breach clock record (`docs/runbooks/breach.md (“4.1 Steps”)`) because that is the only record format the repo defines.

**Drill pending — live environment required.** Detection/delivery timestamps, read-only triage results, notice transmission/acknowledgement and resolution verification have not been exercised here. Retain actual redacted outputs during 10.7.1-T6.2; no desk check substitutes for that drill.

**Read-only query roles.** For base-table `public.task`/`public.outbox` inspection, use `evidgate_worker`: migration `0104_platform_worker_role` grants its reads and revokes base-table reads from `evidgate_app`/`evidgate_operator`. Do not copy a sibling example’s older app-role instruction onto those base tables. Tenant-scoped `public.run` inspection uses `evidgate_app` with the incident org’s transaction context (migration `0031_orchestration_run`, app SELECT grant and tenant RLS). For the system-switch row/audit reads, use the emergency privileged `evidgate_owner` path documented by the kill-switch runbook: `evidgate_platform_operator` has EXECUTE only, while `evidgate_operator` has no matching read policy (migrations `0052_healing_switch` and `0105_platform_operator_role`). Never substitute an unscoped app connection or BYPASSRLS.

| # | Step | What you do | Record fields |
|---|---|---|---|
| 1 | **Detect** | Write the time before investigating (`docs/runbooks/breach.md`, “Steps”). **Drill pending:** retain an actual signal and delivery timestamp; source definitions do not prove a deployment emitted or delivered it. | `signal_seen_at`, source, alert name or safe log event id |
| 2 | **Declare** | Match the signal against §2. If it matches, declare: pick the level, assign `incident_id`, and write the first line of the record. If in doubt between two levels, declare the higher one. | `incident_id`, `declared_at`, `severity`, class (§2 row) |
| 3 | **Triage** | Run the applicable read-only detection procedure under the roles below. Decide affected org/project ids; record confirmed or false positive with cause (`docs/runbooks/breach.md`, “Classes”, “Steps”). **Drill pending:** retain redacted live results; where no procedure exists, record the blocker instead of claiming confirmation. | `classified_at`, affected ids, `triage_confirmed_at` |
| 4 | **Mitigate** | Contain first through a supported path: [kill-switch](kill-switch.md), [key-rotation](key-rotation.md), or run cancel per [stuck-run](stuck-run.md). **Not possible yet:** HTTP dead-letter replay is denied by `DenyAllOperatorGate` (02.8.1-T2.1); manually forcing reconciliation or `incomplete` settlement has no operator command (10.7.1-T2 documents this limitation; no enabling manual-settlement command task is assigned). Follow the blocked-action list below; never rewrite evidence. | `mitigation_started_at`, action + id per action |
| 5 | **Communicate** | Update the internal record; use §4.2 when applicable. **Drill pending:** record actual transmission and acknowledgement. **Not possible yet:** guaranteed customer delivery without an agreed sender/channel (11.9.1-T2.4); mark blocked, not sent. | `notice_sent_at`, `channel`, `ack_at` or `ack_missing`, `followup_sent_at[]` |
| 6 | **Resolve** | The invariant must hold again and the criterion no longer match. **Drill pending:** execute the class-specific verification on the affected deployment and attach redacted results; a stopped symptom is insufficient. Record any re-declaration rather than downgrading silently. | `resolved_at`, `severity` if re-declared, verification output |
| 7 | **Follow-up** | Fill the postmortem, list corrective actions, and open one follow-up task per action. **Template and follow-up-task rule are `[OPEN]`, owned by 10.7.1-T6.2** (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md (“10.7.1-T6 — Incident process (M, M3)”)`). Until it lands, write the timeline and the actions in the incident record itself. | `closed_at`, actions + owners, `elapsed` |

**The staged exercise is also `[OPEN]`:** 10.7.1-T6.2 requires "one staged incident exercise by a non-author" and AC7 expects a filled template and a follow-up task (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md (“9. Test plan”)`, `10.7.1-T6.2`); neither has run, and the non-author requirement is blocked by §3 item 4.

**Alert-to-runbook coverage is `[OPEN]`.** AC7 / NFR-10-2 require every 10.3.1-T5 alert to link a runbook and lint to fail otherwise (`plans/10-operations.md`, NFR-10-2; operability plan 10.7.1, AC7 and T6.2). **Not possible yet:** `scripts/check-alert-runbooks.sh`, `scripts/check-alerts.sh` and `infra/alerts/` do not exist. 10.7.1-T6.2 and 10.3.1-T5.1/T5.2 must settle the lint name and provide executable coverage checks (`plans/features/10-operations/10.3.1-telemetry-and-alerting.md`, tasks T5.1–T5.4).

**Not possible yet:** the following intended steps remain in the process; none is permission to bypass a guard.

- **Dead-letter HTTP list/replay and HTTP system flip:** `DenyAllOperatorGate` refuses callers. 02.8.1-T2.1 must supply authenticated operator access. Migration `0105_platform_operator_role` also removes app-role EXECUTE on the system definer; operator HTTP needs its authorized connection path, not merely an opened filter. System `psql` remains supported (§2).
- **Manual reconcile/`incomplete` settlement:** no operator command exists. 10.7.1-T2 documents the automated path and run cancel; no task assigning a new manual-settlement command is recorded. Escalate, do not invent a route.
- **Probe-derived `unavailable`:** 08.6.3-T2.1 supplies dependency health and T2.2 its in-flight re-check/metric. Until then verify refusal of configured `unavailable`, not a live derived result.
- **Paging, escalation and quiet-hours delivery:** 10.7.2-T1/T2 must provide routes/destinations; 10.3.1-T5.1–T5.4 must supply alert rules, links, lint, routing and synthetic triggers.
- **Restore/DR, production release/rollback and internal leak procedures:** receivers 10.5.1-T4, 10.2.2-T4 and 11.3.1-T2 remain open. Preserve the incident and escalate rather than inventing their steps.
- **Named customer delivery and postmortem exercise:** 11.9.1-T2.4 must supply sender/channel; 10.7.1-T6.2 owns the template, follow-up rule and non-author exercise. No notice or drill is claimed complete here.

## 6. Which runbook applies

Every linked runbook in this table exists on this tree except where its State cell says `[OPEN]`. The `kill-switch.md` sibling (10.7.1-T5.1) landed in the same batch as this page; its drill (T5.2) is still open.

| Runbook | Use it when | State on this tree |
|---|---|---|
| [breach](breach.md) | P1-a, P1-b (tenant data), P1-d, P1-e; any personal-data exposure, suspected or confirmed; processor→controller notice and evidence pack | written, declared partial; T2.4 and the rehearsal `[OPEN]` (`docs/runbooks/breach.md (“Status”)`, `breach.md, “Rehearsal — not executed”`) |
| [dead-letter](dead-letter.md) | P1-c support, P2-b, P3-b: classify a `task` row in `dead_letter`; retain the intended one-replay step | written; drill T1.2 remains open. Migration `0104_platform_worker_role` makes replay append `task.dead_letter_replay` atomically; list is read-only. **Not possible yet:** HTTP replay until 02.8.1-T2.1 supplies the operator path; `DenyAllOperatorGate` still denies callers |
| [stuck-run](stuck-run.md) | P2-c, P3-a, and the run side of P1-c: a run past its collection deadline, lease/epoch inspection, settle `incomplete`/`canceled` without inventing evidence | written; T2.2 drill `[OPEN]` (`docs/runbooks/stuck-run.md (“Status”)`) |
| [key-rotation](key-rotation.md) | P1-d rotation step, P2-f: ingest tokens, JWT key, outbound webhook secrets, database credentials, provider keys, pepper versions | written, pepper section is the written half of T4.2; drill `[OPEN]` (`docs/runbooks/key-rotation.md (“Status”)`) |
| [kill-switch](kill-switch.md) | P2-d: system → tenant → project flip, verify `unavailable` cannot be configured, roll back the flip | written (10.7.1-T5.1); drill T5.2 open. **Not possible yet:** observe probe-derived `unavailable`; `dependency_health` remains null until 08.6.3-T2.1/T2.2 (`GET /v1/healing/switch`) |
| [telemetry](telemetry.md) | P3-d and the reconstruction questions of P1-b/P1-e: resource attributes, sampling, redaction | written (`docs/README.md (“Runbooks table row”)`) |
| [secret-shape-timeout](secret-shape-timeout.md) | P3-c for the single `AR-secret-shape-timeout` ticket (`docs/security/alert-rules.md` §6) while triage shows no ordinary value refused because of a timeout; P2-g when the counter rises in 3 consecutive windows or on every `pattern` at once, or such a refusal is confirmed | written; alert rule not deployed, no drill (`docs/runbooks/secret-shape-timeout.md (“Secret-shape timeout runbook”)`) |
| [orphan artifact](orphan-artifact.md) | objects without `ready`, stale uploads, retention/orphan gaps | written, document half of 10.7.1-T3.1; staging drill and AC4 remain open. **Not possible yet:** orphan-object and retention repair paths described as absent there; receivers 06.4.3 and 06.4.5 |
| restore / DR (`docs/runbooks/` — no file yet) | P1-c restore, P1-f: failover decision, data-loss statement at the measured RPO, recovery order | **`[OPEN]`** — 10.5.1-T4 (`plans/10-operations.md (“Story 10.5.1 Recoverability — acceptance: a restore into a clean environment reproduces a run, its evidences and its decision, within RPO 15 min / RTO 4 h”)`) |
| release / rollback (`docs/runbooks/` — no file yet) | P2-e: who deploys, rollback, post-deploy watch window | **`[OPEN]`** — 10.2.2-T4 (`plans/10-operations.md (“Story 10.2.2 Deploy with expand/contract and canary — acceptance: a deploy never takes traffic on a version whose migrations have not run, and a decision-changing canary is blocked by the shadow compare”)`); the canary and automatic rollback it documents are also `[OPEN]` (10.2.2-T2, `10.2.2-T2`) |
| leak (`docs/runbooks/` — no file yet) | a secret leaked but no tenant data or tenant credential was touched — an internal security incident, not a controller notice (`docs/runbooks/breach.md (“3.1 Classes”)`) | **`[OPEN]`** — 11.3.1-T2 (`plans/11-security-compliance.md (“Story 11.3.1 Secrets never leave a reference - acceptance: no secret value is stored in the database, logged, or visible in an error envelope; leak response is rehearsed.”)`) |

Read order for a P1: §2 class → the class's runbook → §4.2 if a customer must be told → §5 record. The runbooks are the authority on their own subject; this page never overrides a step in one of them.

## 7. Evidence table

| Cite | What it proves |
|---|---|
| `AGENTS.md (“Authority and editing rules”)` | Precedence: DECISIONS and 00 win over area and feature plans |
| `AGENTS.md (“Authority and editing rules”)` | O2 recorded as decided: the owner alone; SOLO-PATH is the delivery order |
| `AGENTS.md (“Invariants code must keep”)` | Invariant: every business row has `org_id`, tenant FKs, app role never owner/BYPASSRLS |
| `AGENTS.md (“Invariants code must keep”)` | Invariant: completeness, outcome, decision separate; missing evidence never yields PASS |
| `AGENTS.md (“Invariants code must keep”)` | Invariant: manifests, revisions, evaluations immutable; late evidence is a new revision |
| `AGENTS.md (“Invariants code must keep”)` | Invariant: events acknowledged only after commit; dedupe by `event_id` + payload hash |
| `AGENTS.md (“Invariants code must keep”)` | Invariant: healing never weakens assertions; kill switches before any apply feature |
| `DECISIONS.md (“5. Safe-healing rules (locked)”)` | Healing rule 9: kill switches at system/tenant/project before apply; dependency failure makes healing `unavailable` |
| `DECISIONS.md`, §7 personal-data allocation | `[INFERENCE]` Customer is controller and Evidgate processor for customer test artifacts; legal mapping remains unreviewed here (OI-11-08) |
| `DECISIONS.md (“8. Open decisions (owner)”)` | O2: the team is one person, the owner, also Tech Lead and QA/product owner; 00 §14 unit = whole team |
| `plans/00-overview.md`, §8.7 Healing availability | Three settable modes, `unavailable` derived by the planned dependency-health path, most restrictive wins |
| `plans/00-overview.md`, §11 “Non-functional requirements” | NFR targets are pilot measurement targets, “not an SLA” |
| `plans/00-overview.md`, §11 target table | Durability, ingest p95 < 1 s, decision p95 < 60 s, availability 99.5 %/month, RPO 15 min/RTO 4 h, burst 50 000 events/60 s |
| `plans/10-operations.md (“1. Purpose and scope”)` | Area goal: evidence never lost, bad release stopped, reconstruction from telemetry |
| `plans/10-operations.md (“2. Requirements”)` | Three environments with separate credentials and data |
| `plans/10-operations.md (“2. Requirements”)` | FR-10-6: alert set (queue age, lease loss, incomplete rate, parser errors, artifact failures, healing false positives, outbox lag, dead-letter depth) |
| `plans/10-operations.md (“2. Requirements”)` | FR-10-11: healing switchable at three scopes before apply; a healing failure never blocks ingest, runs or gates |
| `plans/10-operations.md (“2. Requirements”)` | NFR-10-1: telemetry + audit reconstruct any run, decision or dead-letter row |
| `plans/10-operations.md (“2. Requirements”)` | NFR-10-2: 100 % alert-to-runbook coverage; an alert without a link fails the dashboard review |
| `plans/10-operations.md (“3. Key decisions”)` | Alert threshold ownership: numeric threshold, owner, runbook link in one file per environment |
| `plans/10-operations.md (“4. Design”)` | Health/metrics endpoints used as the availability signal |
| `plans/10-operations.md (“4. Design”)` | Alert → routing → runbook → operator action chain, with the action recorded |
| `plans/10-operations.md (“5. Failure modes and edge cases”)` | Failure modes: deploy, canary decision change, worker crash, storage outage, database loss, backlog, alert storm, healing false positive → kill switch |
| `plans/10-operations.md (“7. Test strategy”)` | Drills executed as tests in staging, including kill-switch flip while ingest continues |
| `plans/10-operations.md (“7. Test strategy”)` | Each alert rule triggered synthetically in staging; must fire with right severity and runbook link |
| `plans/10-operations.md (“8. Risks”)` | Healing false positives invisible in aggregate is a named risk |
| `plans/10-operations.md (“8. Risks”)` | Single-operator bus factor risk; mitigation is runbooks for a second person and drill handover at M3 |
| `plans/10-operations.md (“Story 10.2.2 Deploy with expand/contract and canary — acceptance: a deploy never takes traffic on a version whose migrations have not run, and a decision-changing canary is blocked by the shadow compare”)` | 10.2.2-T2 canary + automatic rollback on SLI breach |
| `plans/10-operations.md (“Story 10.2.2 Deploy with expand/contract and canary — acceptance: a deploy never takes traffic on a version whose migrations have not run, and a decision-changing canary is blocked by the shadow compare”)` | 10.2.2-T4 release runbook task (not shipped) |
| `plans/10-operations.md (“Story 10.5.1 Recoverability — acceptance: a restore into a clean environment reproduces a run, its evidences and its decision, within RPO 15 min / RTO 4 h”)` | 10.5.1-T4 DR runbook task with communication and data-loss statement (not shipped) |
| `plans/10-operations.md (“Story 10.7.1 Operable by a second person — acceptance: each runbook is executed once by someone who did not write it, in staging, and its steps are correct without tribal knowledge”)` | Story 10.7.1 acceptance: each runbook executed once by a non-author in staging |
| `plans/10-operations.md (“Story 10.7.1 Operable by a second person — acceptance: each runbook is executed once by someone who did not write it, in staging, and its steps are correct without tribal knowledge”)` | Epic 10.7 task row: incident process = severity ladder, on-call expectation, communication, postmortem |
| `plans/10-operations.md (“Story 10.7.2 Paging and on-call at the first alert — acceptance: every alert in 10.3.1-T5 reaches a named destination and a person, including out of hours.”)` | 10.7.2-T1 routing tool, 10.7.2-T2 single-operator escalation |
| `plans/10-operations.md (“11. Open questions”)` | Area plan still lists O2/on-call as `[OPEN]` (superseded by `DECISIONS.md (“8. Open decisions (owner)”)`) |
| `plans/11-security-compliance.md (“2. Requirements”)` | FR-11-13: breach notified within 72 h, processor notifies controller promptly |
| `plans/11-security-compliance.md (“5. Failure modes and edge cases”)` | Failure mode: secret logged → scanner, purge, rotate, notify if tenant data exposed |
| `plans/11-security-compliance.md (“5. Failure modes and edge cases”)` | Cross-tenant probe handling; NFR-11-1 isolation |
| `plans/11-security-compliance.md (“Story 11.3.1 Secrets never leave a reference - acceptance: no secret value is stored in the database, logged, or visible in an error envelope; leak response is rehearsed.”)` | 11.3.1-T2 leak runbook task (not shipped) |
| `plans/11-security-compliance.md (“Story 11.9.1 Deletion and breach are runnable procedures - acceptance: a deletion request produces a verifiable report, and a breach rehearsal completes the processor→controller notice inside the runbook's target.”)` | 11.9.1-T2 breach runbook task |
| `plans/12-pilot-commercial.md (“2. Requirements”)` | FR-12-3: pilot agreement includes support expectations |
| `plans/12-pilot-commercial.md (“Story 12.2.1 A pilot that can be measured and paid for - acceptance: agreement + DPA signed before any upload, baseline captured for two sprints, weekly reviews held, support hours metered.”)` | 12.2.1-T4 weekly pilot review ritual and blocker escalation |
| `plans/12-pilot-commercial.md (“Story 12.2.1 A pilot that can be measured and paid for - acceptance: agreement + DPA signed before any upload, baseline captured for two sprints, weekly reviews held, support hours metered.”)` | 12.2.1-T5 pilot support process: severity definitions, response expectation, support-hour logging |
| `plans/04-ingestion-adapters.md (“Story 04.9.2 CLI/SDK release, compatibility and customer docs — acceptance: a tagged release publishes the CLI and adapters with provenance and a changelog, a client older than the supported window gets an actionable error from the server, and a customer can reach a first accepted run from the docs alone.”)` | 04.9.2-T3: status-page choice recorded as a decision (not delivered) |
| `plans/open-items/02-catalog-ingest-runs-evidence.md (“Area 04 — ingestion and adapters”)` | OI-04-12: status-page product `[OPEN]` |
| `plans/features/SOLO-PATH.md (“Solo delivery path (one developer)”)` | Solo path status: team is the owner alone; reviews by others or AI against the spec package |
| `plans/features/SOLO-PATH.md (“1. What changes with one developer”)` | One person is developer, Tech Lead and QA/product owner |
| `plans/features/SOLO-PATH.md (“6. Open points”)` | `[OPEN]` who the external reviewer is |
| `plans/features/10-operations/10.7.1-operable-by-a-second-person.md (“1. Goal and non-goals”)` | Non-goals: on-call schedule is O2; DR runbook is 10.5.1-T4, referenced not rewritten |
| `plans/features/10-operations/10.7.1-operable-by-a-second-person.md (“2. User stories and acceptance criteria”)` | AC1: each runbook executed once by a non-author, corrections committed |
| `plans/features/10-operations/10.7.1-operable-by-a-second-person.md (“2. User stories and acceptance criteria”)` | AC7: every T5 alert links a runbook; the incident runbook includes severity ladder, on-call expectation, postmortem template with follow-up rule |
| Migration `0104_platform_worker_role`; `DenyAllOperatorGate` | Replay appends `task.dead_letter_replay` in the same transaction; list is read-only. The feature plan’s historical no-audit claim is superseded; HTTP operator access remains denied pending 02.8.1-T2.1 |
| `plans/features/10-operations/10.7.1-operable-by-a-second-person.md (“9. Test plan”)` | Test plan: staged P2 incident produces a filled postmortem and a follow-up task |
| `plans/features/10-operations/10.7.1-operable-by-a-second-person.md (“9. Test plan”)` | `scripts/check-alert-runbooks.sh` named as the coverage check |
| `docs/runbooks/orphan-artifact.md`, “Status”; operability plan 10.7.1-T3.1 | Document half written; non-author staging drill and AC4 remain open |
| `plans/features/10-operations/10.7.1-operable-by-a-second-person.md (“10.7.1-T6 — Incident process (M, M3)”)` | T6.1 and T6.2 definitions and their "done when" |
| `plans/features/10-operations/10.7.1-operable-by-a-second-person.md (“11. Rollout”)` | T6 lands in M3 after T1, before the pilot's on-call start |
| `plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md (“1. Goal and non-goals”)` | Non-goals: no staffed 24/7 rota for a solo operator, `[OPEN]` for the first hire; severity ladder is 10.7.1-T6 |
| `plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md (“2. User stories and acceptance criteria”)` | AC1 route table `infra/alerts/routing.yml` mapping every alert by severity |
| `plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md (“2. User stories and acceptance criteria”)` | AC3: P1/P2 to a push/phone destination, P3 to chat/email only |
| `plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md (“2. User stories and acceptance criteria”)` | AC4: 15 min escalation timeout `[INFERENCE]`, secondary or auto-ack fallback |
| `plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md (“2. User stories and acceptance criteria”)` | AC6: quiet hours, P1 always pages, P2 07:00–23:00 `[INFERENCE]` |
| `plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md (“3. Contracts touched”)` | Alert label contract: `severity ∈ {P1,P2,P3}`, `owner`, `runbook` |
| `plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md (“4. Data model and migrations”)` | Record location proposal `docs/runbooks/records/` `[INFERENCE]` (directory absent) |
| `plans/features/10-operations/10.3.1-telemetry-and-alerting.md`, T3.1 and T3 delivery notes; `infra/metrics/catalogue.yaml` | Catalogue includes task/dead-letter/lease/incomplete signals; `healing_false_positive_rate` is explicitly unavailable, not zero |
| `plans/features/10-operations/10.3.1-telemetry-and-alerting.md`, tasks T5.1–T5.4 | Rule thresholds, runbook-link lint, routing and synthetic triggers are planned; `check-alerts.sh` is the T5.1 lint name |
| `docs/decisions/O1-pilot-profile.md (“O1 — Pilot profile and success criteria (Test Lead view)”)` | Pilot profile is a working definition, every number `[INFERENCE]` |
| `docs/decisions/O1-pilot-profile.md (“2. Pilot profile (who to look for)”)` | One named QA lead who owns the release call |
| `docs/decisions/O1-pilot-profile.md (“3. Pilot acceptance sheet (what the QA lead signs)”)` | Acceptance: 0 decisions PASS where a required shard or artifact was missing |
| `docs/runbooks/breach.md (“Status”)` | Breach status: partial; T2.4 open; T3 rehearsal not run |
| `docs/runbooks/breach.md (“1. Scope, roles and rules”)` | Scope: suspected/confirmed personal-data breach; availability-only incidents are out of scope |
| `docs/runbooks/breach.md (“1. Scope, roles and rules”)` | The 72 h clock is the controller's; our duty is prompt notice; legal questions `[OPEN OI-11-08]` |
| `docs/runbooks/breach.md (“1. Scope, roles and rules”)` | 4 h notice target ⚠️ proposed, pending owner sign-off |
| `docs/runbooks/breach.md (“1. Scope, roles and rules”)` | Role table; sender/channel and counsel `[OPEN]` |
| `docs/runbooks/breach.md (“1. Scope, roles and rules”)` | No secrets/personal data in records, tickets or notices; ids and closed enums only |
| `docs/runbooks/breach.md (“1. Scope, roles and rules”)` | A breach is never closed by deleting evidence; deletion requests are routed elsewhere |
| `docs/runbooks/breach.md (“1. Scope, roles and rules”)` | Send the first notice early, add detail later |
| `docs/runbooks/breach.md (“3.1 Classes”)` | Classes: Confirmed / Suspected / False positive / Confirmed, no customer data |
| `docs/runbooks/breach.md (“3.1 Classes”)` | False positive closes with the documented benign cause recorded |
| `docs/runbooks/breach.md (“3.2 Signal-to-action matrix”)` | Signal-to-action matrix incl. secret in a log, cross-tenant probe, broken audit chain |
| `docs/runbooks/breach.md (“3.2 Signal-to-action matrix”)` | Broken audit chain: treat as possible tampering, page the incident lead |
| `docs/runbooks/breach.md (“3.3 Severity to action”)` | Internal S1/S2/S3 cadence; response times `[INFERENCE]`/`[OPEN]` |
| `docs/runbooks/breach.md (“4.1 Steps”)` | Clock-record fields: `signal_seen_at`, `classified_at`, `triage_confirmed_at`, `notice_due_at`, `notice_sent_at`, `ack_at`/`ack_missing`, `closed_at`, `elapsed` |
| `docs/runbooks/breach.md (“4.2 Notice template”)` | Facts only; one notice per affected controller, never naming another customer |
| `docs/runbooks/breach.md (“4.3 Senders and channel”)` | Sender and channel `[OPEN]`; T2.4 fills the contact table |
| `docs/runbooks/breach.md (“6. Open items and receivers”)` | Clock-record location and retention `[OPEN]` |
| `docs/runbooks/breach.md (“7. Rehearsal — not executed”)` | The runbook has not been rehearsed |
| `docs/runbooks/dead-letter.md (“Status”)` | Dead-letter status: T1.1 written; T1.2/T2.2 not executed |
| `docs/runbooks/dead-letter.md (“1. Purpose, audience and rules”)` | No pager integration yet; the alert names no person |
| `docs/runbooks/dead-letter.md (“1. Purpose, audience and rules”)` | Never hand-write an UPDATE/DELETE on `public.task` |
| `docs/runbooks/dead-letter.md (“1. Purpose, audience and rules”)` | Record ids, closed enums, counts, timestamps only |
| `docs/runbooks/dead-letter.md (“§2 Detection (alert sink)”)` | `ITaskAlertSink` is the shipped alert seam, three call sites |
| `docs/runbooks/dead-letter.md`, “Verification and escalation”; `TaskSweepTaskHandler.SweepSql` | Escalate repeated death, growing depth, missing tenant, approaching creation-age retention deadline, or operator 401/403 |
| `docs/runbooks/stuck-run.md (“Status”)` | Stuck-run status: T2.1 written; T2.2 drill open |
| `docs/runbooks/stuck-run.md (“§4.3 Is the sweep running at all?; §5.0 The command surface this runbook relies on”)` | Section 4.3 "is the sweep running", and which levers are human-reachable today; operator dead-letter route denied |
| `docs/runbooks/key-rotation.md (“Status”)` | Key-rotation status: T4.1 written; drill and non-author execution open |
| `docs/runbooks/key-rotation.md (“1. Scope, roles and rules”)` | Secret handling, no ad-hoc writes, no invented operator endpoint, who rotates what |
| `docs/runbooks/kill-switch.md (“Status”)` | Kill-switch status: T5.1 written; T5.2 staging drill open, AC6 half open |
| `docs/runbooks/kill-switch.md`, “Audience”, “The system flip”; migration `0105_platform_operator_role` | Tenant/project writes require member `healing:configure`; result reads require `healing:triage`. System writes use `psql` as `evidgate_platform_operator`; HTTP remains fail-closed |
| `docs/README.md (“Runbooks table row”)` | `docs/runbooks/` is the runbook index, and it lists both `runbooks/kill-switch.md` and `runbooks/incident-process.md` under 10.7.1-T5.1/T6.1 (drills T5.2/T6.2 open) |
| `docs/security/alert-rules.md`, “Operational counter rules” | `AR-secret-shape-timeout` is specified, not deployed; its window/escalation proposals do not establish live delivery |
| `docs/runbooks/secret-shape-timeout.md`, “First checks and escalation”; `SecretShapeMetrics.RecordTimeout`; `infra/metrics/catalogue.yaml`, `secret_shape_timeout` | Timeout counter and pattern-only labels exist; P3-c/P2-g triage depends on actual impact |

## 8. What this page does not cover

- **The postmortem template and the follow-up-task rule** — 10.7.1-T6.2 (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md (“10.7.1-T6 — Incident process (M, M3)”)`). `[OPEN]`.
- **The staged incident exercise by a non-author** — same subtask; blocked by §3 item 4 until a second person exists. `[OPEN]`.
- **The alert-to-runbook coverage lint and the alert rules themselves** — 10.7.1-T6.2 and 10.3.1-T5; listing `infra/` and `scripts/` on this tree shows no `infra/alerts/` directory and no `scripts/check-alert-runbooks.sh` (nor `scripts/check-alerts.sh`), and the two plans spell the lint differently. `[OPEN]`.
- **Named contacts, channels, status page, customer response times, support severities** — §4; owned by 11.9.1-T2.4, 12.2.1-T5, 04.9.2-T3. `[OPEN]`.
- **Paging delivery, escalation timeout and quiet hours** — 10.7.2-T1/T2. `[OPEN]`; the numbers in §2 are inferences, not policy.
- **Response-time targets for this ladder** — §2; nothing in the repo defines them, and the proposal there is not in force.
- **Restore/DR and release/rollback procedures** — still `[OPEN]` in §6. The orphan-artifact document is written, but its staging drill and several repair/sweeper capabilities remain open; this page does not invent them.
- **The drill status of every other runbook** — each runbook states its own (`docs/runbooks/dead-letter.md (“Status”)`, `docs/runbooks/stuck-run.md (“Status”)`, `docs/runbooks/key-rotation.md (“Status”)`, `docs/runbooks/breach.md (“Status”)`). AC1 as a whole is therefore not met.
- **Where this page is indexed** — `docs/README.md (“Runbooks table row”)`, in the runbook row that also names `runbooks/kill-switch.md`; that row is the index, so a status change here is carried there in the same edit.

Desk check: 2026-11-21, wave fd69e03b; not checkable without a live environment: signal delivery/timestamps, triage query results, notice transmission/acknowledgement, resolution verification and staging drills.
