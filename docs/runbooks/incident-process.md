# Incident process

Status: **10.7.1-T6.1 written; T6.2 (postmortem template, staged exercise, check-alert-runbooks wiring) open.**

This page is the process, not a tool: no paging route, no rule file under `infra/alerts/` and no coverage lint exists on this tree yet, so every step that depends on one says so. The severity ladder (§2) and the pilot on-call answer (§3) are usable today; the customer-facing half of §4 and the incident-record location in §5 are `[OPEN]`.

Legend as in [`../../AGENTS.md`](../../AGENTS.md): ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`. Every claim below cites the file and line it was read from, and the full list is the [evidence table](#7-evidence-table). Cites are on this worktree's base, `develop` `68bde65d`.

## 1. Scope, audience and rules

**Purpose.** Give one person a decision procedure for an operational incident on the Evidgate pilot: how bad it is (§2), who responds (§3), what is said and to whom (§4), the order of steps (§5), and which existing runbook does the work (§6). The story's acceptance for this half is only "severity ladder covers at minimum P1–P3 with entry criteria" (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md:132`).

**Audience.** The operator on duty — today the owner alone (DECISIONS §8 O2, `DECISIONS.md:153`; `AGENTS.md:42`). Written so a second person can use it without tribal knowledge (`plans/10-operations.md:212`), which is also the area plan's mitigation for the bus-factor risk (`plans/10-operations.md:109`).

**In scope.** Any event that breaks one of the product invariants (`AGENTS.md:50-54`), one of the measured targets (`plans/00-overview.md:450-455`), or the trustworthiness of a decision (`plans/10-operations.md:11`), in dev, staging or prod (`plans/10-operations.md:21`).

**Out of scope.** The content of each technical runbook (§6 links them); the paging tool, its routing table and its escalation timeout (10.7.2, `plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md:13`); the breach runbook's notice template and legal mapping ([breach](breach.md)); customer support severities and response expectations (12.2.1-T5, `plans/12-pilot-commercial.md:210`); DR failover ([`plans/features/10-operations/10.7.1-operable-by-a-second-person.md:13`](../../plans/features/10-operations/10.7.1-operable-by-a-second-person.md)).

**Rules.**

- **The invariant, not the metric, decides the severity.** All numeric targets in the repo are measurement targets "to be measured; not an SLA" (`plans/00-overview.md:447`). A threshold breach is an alert; an invariant breach is an incident.
- **A missing runbook is not permission to improvise.** If the class has no runbook, stop at "contain and record" and escalate; never hand-write a database change (`docs/runbooks/dead-letter.md:19`, `docs/runbooks/key-rotation.md:12`).
- **Ids, closed enums, counts, timestamps and hashes only.** No secret values, payloads, tenant names, repository names, URLs, hostnames or e-mail addresses in tickets, records or chat (`docs/runbooks/breach.md:22`, `docs/runbooks/dead-letter.md:21`).
- **One record per incident, written while it happens**, with the field names in §5. A late record reconstructed from memory is a finding, not a record.
- **Never close an incident by deleting evidence** (`docs/runbooks/breach.md:23`); a deletion request that arrives mid-incident goes to the deletion procedure, not to this one.
- **Severity is a property of the incident, not of the alert.** The alert's `severity` label is `P1|P2|P3` (`plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md:34`); re-declaring is expected (§5) and must be recorded with a reason.

## 2. Severity ladder

Three levels, because the alert contract has exactly three (`severity ∈ {P1,P2,P3}`, `plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md:34`). There is no P4: anything below P3 is a ticket, not an incident. When two triggers match, take the more severe one.

Entry criteria are written as tests over repo invariants and states, so they do not drift with a threshold. **Numeric alert thresholds do not exist on this tree** — the rule files and their thresholds are 10.3.1-T5.1 (`plans/features/10-operations/10.3.1-telemetry-and-alerting.md:135`), not shipped; wherever a number would be needed it is `[OPEN]` here rather than invented.

### P1 — invariant broken; stop everything else

| # | Entry criterion (any one) | Why this is P1 | Runbook |
|---|---|---|---|
| P1-a | A tenant's row, artifact, presigned URL, audit row or decision is readable or writable by a principal outside that tenant (confirmed or plausible) | Tenant isolation is the first invariant (`AGENTS.md:50`); a cross-tenant path is the red-team failure (`plans/11-security-compliance.md:149`) | [breach](breach.md) §3 |
| P1-b | Evidence or a decision is not what it was: a manifest/revision/evaluation row was edited or deleted (`AGENTS.md:52`), a decision is PASS while a required shard or artifact was missing (`AGENTS.md:51`, `docs/decisions/O1-pilot-profile.md:32`), or a decision changed without an approved shadow diff (`plans/10-operations.md:71`) | Completeness and immutability are the product; a wrong PASS is released to customers (`plans/10-operations.md:11`) | [breach](breach.md) §3 if tenant data is involved, else §5 record + engineering fix |
| P1-c | An acknowledged event is lost: the producer was told "committed" and the row is absent, or a restore lands before the last acknowledged commit | "Acknowledged only after commit" (`AGENTS.md:53`); the crash invariant is "no lost acknowledged event" (`plans/10-operations.md:72`, `:74`) | [dead-letter](dead-letter.md) §2/§7, [stuck-run](stuck-run.md) §5; restore/DR runbook `[OPEN]` 10.5.1-T4 |
| P1-d | A secret reached tenant data or a tenant credential (in a log, an artifact, an error envelope, a screenshot) | Secrets never in logs or labels (`plans/11-security-compliance.md:142`); containment is purge-then-rotate, with the rotation procedure in the key-rotation runbook (`docs/runbooks/key-rotation.md:9`) | [breach](breach.md) §3.2 row 1, then [key-rotation](key-rotation.md) |
| P1-e | The audit hash chain is broken, a gap or a mismatch in one org's chain | Treated as possible tampering; the product loses its reconstruction claim (`docs/runbooks/breach.md:68`, `plans/10-operations.md:34`) | [breach](breach.md) §5.2 |
| P1-f | The service is unusable for every tenant: readiness red on every API replica, or every ingest batch fails | Total loss of the core loop; availability target 99.5 %/month is measured, not promised (`plans/00-overview.md:453`, health endpoints at `plans/10-operations.md:55`) | restore/DR runbook `[OPEN]` 10.5.1-T4; release runbook `[OPEN]` 10.2.2-T4 |

**P1 behaviour.** Drop everything else. Start the record before the first fix. If P1-a or P1-d holds, the breach clock starts at once — record `signal_seen_at` and the notice fields as they stand — but **containment still comes first**, exactly as §5 step 4 orders it and as the breach matrix's ordered first actions do: record ids, times and a hash, restrict access, then purge/redact and rotate, and notify on the facts that gives you (`docs/runbooks/breach.md:58-60`). Do not hold a containment step while a notice is written; send the first notice as soon as containment allows, and add detail later (`docs/runbooks/breach.md:24`).

### P2 — degraded, bounded, no invariant broken yet

| # | Entry criterion (any one) | Bound that keeps it P2 | Runbook |
|---|---|---|---|
| P2-a | One tenant or project cannot ingest, or its runs cannot complete, while other tenants are served | Blast radius is one tenant/project; nothing acknowledged is lost yet | [stuck-run](stuck-run.md), [dead-letter](dead-letter.md) |
| P2-b | Dead-letter depth grows for one `task_type` with no dependency outage (dead-letter §6 escalation list, `docs/runbooks/dead-letter.md:213`) | Work is queued, not lost; retention gives ~30 days (`docs/runbooks/dead-letter.md:215`) | [dead-letter](dead-letter.md) |
| P2-c | Overdue runs accumulate: the collection-deadline sweep is not settling them (`docs/runbooks/stuck-run.md:181`) | Evidence stays absent rather than wrong; a missing shard never yields PASS (`AGENTS.md:51`) | [stuck-run](stuck-run.md) §4.3/§5.1 |
| P2-d | The healing false-positive signal is above its threshold, or a tenant reports a wrong heal, while gates still run | Healing must never block ingest, runs or gates (`plans/10-operations.md:31`, `:77`); the kill switch bounds it (`DECISIONS.md:112`) | [kill-switch](kill-switch.md), drills in `plans/10-operations.md:93` |
| P2-e | A deploy, canary or policy revision is blocked or would change decisions in shadow | The compare exists to stop exactly this before customers see it (`plans/10-operations.md:71`, `:149`) | release runbook `[OPEN]` 10.2.2-T4 (`plans/10-operations.md:151`) |
| P2-f | A key class is past its rotation window, or a rotation did not complete cleanly | No evidence of use yet; the rotation procedure bounds it (`docs/runbooks/key-rotation.md:9`) | [key-rotation](key-rotation.md) |
| P2-g | A backlog alert fires for queue age, outbox lag, lease loss or incomplete rate (the FR-10-6 set) | Ingest keeps accepting within quota instead of failing open (`plans/10-operations.md:26`, `:75`) | [dead-letter](dead-letter.md), [stuck-run](stuck-run.md) |

**P2 behaviour.** Work it in the same working session, and record it. The session window is the `[DECISION]` proposal of §2 and **not in force**: until the owner signs it, treat "the same working session" as unset and fall back to the breach runbook's internal S2 cadence, itself `[INFERENCE]`/`[OPEN]` (`docs/runbooks/breach.md:79`, `:82`). If a P2 holds long enough that data could be lost or a tenant's release is blocked, it becomes P1 by re-declaration.

**Healing-scope constraint (P2-d).** The system-scope flip needs the platform-operator credential, which is not usable on this tree; the tenant and project scopes work with a member session holding `healing:configure` (`docs/runbooks/kill-switch.md:13`). Use the narrowest scope that bounds the incident, and record which scope was flipped.

### P3 — contained; one row, one run, one alert

| # | Entry criterion (any one) | Runbook |
|---|---|---|
| P3-a | One run is stuck past its collection deadline and no other run is affected | [stuck-run](stuck-run.md) §5 |
| P3-b | One task row is dead-lettered once and no depth growth is visible | [dead-letter](dead-letter.md) §3/§5 |
| P3-c | One alert fires and the runbook's own triage shows no customer impact (a false positive with a documented benign cause, `docs/runbooks/breach.md:49`) | the runbook the alert links to |
| P3-d | Telemetry or a dashboard cannot answer a reconstruction question for one run (`plans/10-operations.md:34`) | [telemetry](telemetry.md) |

**P3 behaviour.** Handle in working hours, and record it — like the P2 window, "working hours" is the `[DECISION]` proposal of §2 and **not in force**: until the owner signs it, treat the window as unset. `plans/10-operations.md:76` fixes only alert grouping and post-drill tuning, not a repeat rule, so the repeat rule below is a proposal too and **not in force**: if the same class fires twice in one week, re-declare P2 and treat the pattern as the incident. Nothing in the repo defines that window today.

### Response targets

**No response-time target for incidents exists in the repo.** The numbers that do exist belong elsewhere and must not be borrowed silently:

| Recorded number | What it is | Status |
|---|---|---|
| first processor→controller notice within 4 h of triage confirmation | breach notice target, not an incident-dispatch target | ⚠️ proposed, pending owner sign-off (`docs/runbooks/breach.md:12`) |
| 72 h | the **controller's** regulator clock; our duty is prompt notice (`docs/runbooks/breach.md:11`, `plans/11-security-compliance.md:53`) | legal mapping ⚠️, `[OPEN OI-11-08]` |
| escalation timeout 15 min | paging tool's unanswered-page timeout | `[INFERENCE]`, 10.7.2 AC4 (`plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md:26`) |
| P1 always pages; P2 07:00–23:00 local | quiet-hours policy | `[INFERENCE]` for the owner to confirm, 10.7.2 AC6 (`plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md:28`) |

`[DECISION]` proposal for owner sign-off, **not in force**: acknowledge a P1 within 15 min of seeing the page, a P2 in the same working session, a P3 by the next working day — the same cadence as the breach runbook's internal S1/S2/S3 (`docs/runbooks/breach.md:76-80`), whose own response times are `[INFERENCE]`/`[OPEN]`. Until the owner signs it, treat every window above as unset.

## 3. Pilot on-call expectation (takes O2's answer)

**O2 is decided, and the answer is: the owner alone is the only responder.** Owner decision of 2026-10-02, quoted: *"the team is one person (the owner) — one developer who is also Tech Lead and QA/product owner"*, and the 00 §14 unit "now equals the whole team" (`DECISIONS.md:153`); restated as decided in `AGENTS.md:42` and applied to scheduling in `plans/features/SOLO-PATH.md:3`, `:9` ("One person is developer, Tech Lead and QA/product owner").

What that means, without softening it:

1. **No rota, no second responder, no handover.** The same person is incident lead, triager, executor, comms and clock owner. The breach runbook's role table already shows where that leaves the roles: sender/channel and counsel are `[OPEN]` (`docs/runbooks/breach.md:13-20`).
2. **There is no defined out-of-hours cover.** A staffed 24/7 rota is explicitly not available to a solo operator and is `[OPEN]` for the first hire (`plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md:13`). Consequence: a P1 at 03:00 has **no response window and no guaranteed delivery** — `[OPEN]`, not "best effort within N hours", because no source states N.
3. **There is no pager today.** The dead-letter runbook states it plainly: "there is no pager integration yet" (`docs/runbooks/dead-letter.md:15`); the routing table is 10.7.2-T1 and the single-operator escalation fallback is 10.7.2-T2, both open (`plans/10-operations.md:225-226`). Until they land, an alert is only as good as someone looking at the sink or the dashboard.
4. **The "executed by a person who did not write it" half of AC1/AC7 cannot be met by the team as staffed.** AC1 requires a non-author to execute each runbook in staging (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md:22`), and T6.2's staged exercise is "by a non-author" (`:133`); the area plan's mitigation is "drill handover at M3" (`plans/10-operations.md:109`). With one person there is no non-author inside the team; the drill needs the first hire or a named external person. `[OPEN]` — recorded as a gap, not scheduled here.
5. **Single point of failure is recorded, not eliminated.** The risk row names it (High likelihood, Medium impact) with runbooks-for-a-second-person and a drill handover at M3 as the only mitigation (`plans/10-operations.md:109`); no acceptance decision is recorded anywhere, so the exposure stays `[OPEN]`. This runbook reduces the *knowledge* dependency (any step below is executable from the page), not the *availability* dependency.
6. **Two documents disagree; DECISIONS wins.** The area plan still lists "O2 team size and who is on-call during the pilot" as `[OPEN]` (`plans/10-operations.md:282`). Under the repo's precedence rule (`AGENTS.md:36`) the decision at `DECISIONS.md:153` governs: **who responds is settled (the owner); what remains open is a second person / first hire and the paging path**, not the identity of the responder.

## 4. Communication path

**There is no named channel, contact or status page in the repo for a service incident.** This section therefore separates what is defined from what is not, and forbids inventing the rest.

### 4.1 Internal (Evidgate)

- **Who is told:** nobody else exists today (§3). The record is the audience; a second reader appears only with the first hire (`plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md:13`).
- **Through what:** the alert sink log line is the only shipped notification seam for the task queue (`docs/runbooks/dead-letter.md:15`, `:26`); alert routing to an on-call channel is 10.3.1-T5.3 and the `infra/alerts/routing.yml` table is 10.7.2-T1, neither shipped (`plans/features/10-operations/10.3.1-telemetry-and-alerting.md:137`, `plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md:23`). No chat tool, e-mail address or phone number is recorded anywhere in the repo — `[OPEN]`.
- **What is said:** ids, closed enum values, counts, timestamps and hashes only; no secret, payload, tenant name or URL (`docs/runbooks/breach.md:22`). State the current severity, the affected class (§2), the affected org/project ids, what was done, and the next decision point.
- **Where it is written:** `[OPEN]`. The clock-record location is an open item (`docs/runbooks/breach.md:193`); 10.7.2 proposes files under `docs/runbooks/records/`, which does not exist on this tree (`plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md:40`). Use the field names in §5 wherever the record lives, and say where it is in the record itself.

### 4.2 Customer-facing

Three different paths; only one has a runbook.

| Situation | Path | State |
|---|---|---|
| Personal data exposed, altered or lost, or a tenant credential leaked | processor→controller notice per the [breach runbook](breach.md) §4: one notice per affected controller, never naming another tenant (`docs/runbooks/breach.md:104`), facts only, "unknown" where unknown (`:104-121`); the controller owns its 72 h clock (`:11`) | notice target and legal mapping ⚠️/`[OPEN OI-11-08]` (`docs/runbooks/breach.md:3`, `:12`); named sender and channel `[OPEN]`, T2.4 (`:127`) |
| Service incident without personal data (outage, backlog, stuck runs) | no customer-notice rule exists. The status-page choice is 04.9.2-T3, not delivered (`plans/04-ingestion-adapters.md:276`), and the product is `[OPEN]` (OI-04-12, `plans/open-items/02-catalog-ingest-runs-evidence.md:42`) | `[OPEN]` |
| Support expectation during the pilot | the pilot agreement must state support expectations (`plans/12-pilot-commercial.md:34`); the support process with severity definitions and response expectations is 12.2.1-T5, not written (`:210`); the weekly pilot review is the only recurring customer touchpoint recorded (`:209`) | `[OPEN]` |

**Rules for anything said to a customer.**

- Never promise a fix time, a root cause or a data-loss figure that is not in the record (the measured data-loss statement is the DR runbook's job, itself `[OPEN]` 10.5.1-T4, `plans/10-operations.md:189`).
- Never name another tenant, and never send one customer another customer's identifiers (`docs/runbooks/breach.md:22`, `:104`).
- Do not wait for a complete picture before the first breach notice; send early, add detail later (`docs/runbooks/breach.md:24`).
- A suspected breach is still communicated on the breach path: the class table decides notice, not certainty (`docs/runbooks/breach.md:45-50`).

## 5. Incident lifecycle

Seven steps. Each names the field it writes, so the record can be filled in order; field names follow the breach clock record (`docs/runbooks/breach.md:88-98`) because that is the only record format the repo defines.

| # | Step | What you do | Record fields |
|---|---|---|---|
| 1 | **Detect** | The alert, the sink line, a dashboard, a customer report, or a failed drill surfaces the signal. Write the time *before* investigating (`docs/runbooks/breach.md:90`). | `signal_seen_at`, source, the alert name or log line |
| 2 | **Declare** | Match the signal against §2. If it matches, declare: pick the level, assign `incident_id`, and write the first line of the record. If in doubt between two levels, declare the higher one. | `incident_id`, `declared_at`, `severity`, class (§2 row) |
| 3 | **Triage** | Confirm or refute the entry criterion with the linked runbook's detection query. Decide the affected org/project ids (ids only). Record "confirmed" or "false positive with cause" (`docs/runbooks/breach.md:92`, `:49`). | `classified_at`, affected ids, `triage_confirmed_at` |
| 4 | **Mitigate** | Contain first: kill switch ([kill-switch](kill-switch.md)), stop the bleeding path, rotate ([key-rotation](key-rotation.md)), replay or settle ([dead-letter](dead-letter.md), [stuck-run](stuck-run.md)). Never repair by deleting or rewriting evidence (`AGENTS.md:52`, `docs/runbooks/breach.md:23`). | `mitigation_started_at`, action + id per action |
| 5 | **Communicate** | Internal record update; customer path per §4.2 only if it applies. Follow-up notices as facts arrive, each numbered (`docs/runbooks/breach.md:97`). | `notice_sent_at`, `channel`, `ack_at` or `ack_missing`, `followup_sent_at[]` |
| 6 | **Resolve** | The invariant holds again and the entry criterion no longer matches. Do not resolve on "the symptom stopped" alone; re-run the class's verification step. Re-declare instead of downgrading silently. | `resolved_at`, `severity` if re-declared, verification output |
| 7 | **Follow-up** | Fill the postmortem, list corrective actions, and open one follow-up task per action. **Template and follow-up-task rule are `[OPEN]`, owned by 10.7.1-T6.2** (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md:133`). Until it lands, write the timeline and the actions in the incident record itself. | `closed_at`, actions + owners, `elapsed` |

**The staged exercise is also `[OPEN]`:** 10.7.1-T6.2 requires "one staged incident exercise by a non-author" and AC7 expects a filled template and a follow-up task (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md:89`, `:133`); neither has run, and the non-author requirement is blocked by §3 item 4.

**Alert-to-runbook coverage is `[OPEN]` too.** AC7 / NFR-10-2 require every 10.3.1-T5 alert to link a runbook and a lint to fail when it does not (`plans/10-operations.md:35`, `plans/features/10-operations/10.7.1-operable-by-a-second-person.md:28`, `:90`). The lint (`scripts/check-alert-runbooks.sh`) does not exist on this tree, the alert rules under `infra/alerts/` do not exist, and the telemetry plan spells the same check as `scripts/check-alerts.sh` (`plans/features/10-operations/10.3.1-telemetry-and-alerting.md:135`) — the name must be settled when T6.2 wires it.

## 6. Which runbook applies

Every linked runbook in this table exists on this tree except where its State cell says `[OPEN]`. The `kill-switch.md` sibling (10.7.1-T5.1) landed in the same batch as this page; its drill (T5.2) is still open.

| Runbook | Use it when | State on this tree |
|---|---|---|
| [breach](breach.md) | P1-a, P1-b (tenant data), P1-d, P1-e; any personal-data exposure, suspected or confirmed; processor→controller notice and evidence pack | written, declared partial; T2.4 and the rehearsal `[OPEN]` (`docs/runbooks/breach.md:3`, `:196`) |
| [dead-letter](dead-letter.md) | P1-c support, P2-b, P3-b: triage a `task` row in `dead_letter`, classify, replay once through the operator path | written; T1.2 drill and the operator path's audit gap `[OPEN]` (`docs/runbooks/dead-letter.md:3`, `plans/features/10-operations/10.7.1-operable-by-a-second-person.md:75`) |
| [stuck-run](stuck-run.md) | P2-c, P3-a, and the run side of P1-c: a run past its collection deadline, lease/epoch inspection, settle `incomplete`/`canceled` without inventing evidence | written; T2.2 drill `[OPEN]` (`docs/runbooks/stuck-run.md:3`) |
| [key-rotation](key-rotation.md) | P1-d rotation step, P2-f: ingest tokens, CI OIDC trust, JWT key, webhook secrets, database credentials, provider/GitHub App keys, pepper versions | written, pepper section is the written half of T4.2; drill `[OPEN]` (`docs/runbooks/key-rotation.md:3`) |
| [kill-switch](kill-switch.md) | P2-d, and any healing action that could alter a gate: system → tenant → project flip, verify `unavailable` is derived, roll back the flip | written (10.7.1-T5.1); the T5.2 staging drill is `[OPEN]` (`docs/runbooks/kill-switch.md:3`) |
| [telemetry](telemetry.md) | P3-d and the reconstruction questions of P1-b/P1-e: resource attributes, sampling, redaction | written (`docs/README.md:8`) |
| orphan artifact (`docs/runbooks/orphan-artifact.md`) | objects without `ready`, `pending_upload` past TTL, retention-sweep failures | **`[OPEN]`** — 10.7.1-T3 not written (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md:112`) |
| restore / DR (`docs/runbooks/` — no file yet) | P1-c restore, P1-f: failover decision, data-loss statement at the measured RPO, recovery order | **`[OPEN]`** — 10.5.1-T4 (`plans/10-operations.md:189`) |
| release / rollback (`docs/runbooks/` — no file yet) | P2-e: who deploys, rollback, post-deploy watch window | **`[OPEN]`** — 10.2.2-T4 (`plans/10-operations.md:151`); the canary and automatic rollback it documents are also `[OPEN]` (10.2.2-T2, `:149`) |
| leak (`docs/runbooks/` — no file yet) | a secret leaked but no tenant data or tenant credential was touched — an internal security incident, not a controller notice (`docs/runbooks/breach.md:50`) | **`[OPEN]`** — 11.3.1-T2 (`plans/11-security-compliance.md:226`) |

Read order for a P1: §2 class → the class's runbook → §4.2 if a customer must be told → §5 record. The runbooks are the authority on their own subject; this page never overrides a step in one of them.

## 7. Evidence table

| Cite | What it proves |
|---|---|
| `AGENTS.md:36` | Precedence: DECISIONS and 00 win over area and feature plans |
| `AGENTS.md:42` | O2 recorded as decided: the owner alone; SOLO-PATH is the delivery order |
| `AGENTS.md:50` | Invariant: every business row has `org_id`, tenant FKs, app role never owner/BYPASSRLS |
| `AGENTS.md:51` | Invariant: completeness, outcome, decision separate; missing evidence never yields PASS |
| `AGENTS.md:52` | Invariant: manifests, revisions, evaluations immutable; late evidence is a new revision |
| `AGENTS.md:53` | Invariant: events acknowledged only after commit; dedupe by `event_id` + payload hash |
| `AGENTS.md:54` | Invariant: healing never weakens assertions; kill switches before any apply feature |
| `DECISIONS.md:112` | Healing rule 9: kill switches at system/tenant/project before apply; dependency failure makes healing `unavailable` |
| `DECISIONS.md:142` | Customer is controller, Evidgate processor for customer test artifacts |
| `DECISIONS.md:153` | O2: the team is one person, the owner, also Tech Lead and QA/product owner; 00 §14 unit = whole team |
| `plans/00-overview.md:303` | Healing availability: three settable modes, `unavailable` derived, most restrictive wins |
| `plans/00-overview.md:447` | Every NFR target is a pilot measurement target, "not an SLA" |
| `plans/00-overview.md:450-455` | Durability, ingest p95 < 1 s, decision p95 < 60 s, availability 99.5 %/month, RPO 15 min/RTO 4 h, burst 50 000 events/60 s |
| `plans/10-operations.md:11` | Area goal: evidence never lost, bad release stopped, reconstruction from telemetry |
| `plans/10-operations.md:21` | Three environments with separate credentials and data |
| `plans/10-operations.md:26` | FR-10-6: alert set (queue age, lease loss, incomplete rate, parser errors, artifact failures, healing false positives, outbox lag, dead-letter depth) |
| `plans/10-operations.md:31` | FR-10-11: healing switchable at three scopes before apply; a healing failure never blocks ingest, runs or gates |
| `plans/10-operations.md:34` | NFR-10-1: telemetry + audit reconstruct any run, decision or dead-letter row |
| `plans/10-operations.md:35` | NFR-10-2: 100 % alert-to-runbook coverage; an alert without a link fails the dashboard review |
| `plans/10-operations.md:46` | Alert threshold ownership: numeric threshold, owner, runbook link in one file per environment |
| `plans/10-operations.md:55` | Health/metrics endpoints used as the availability signal |
| `plans/10-operations.md:60` | Alert → routing → runbook → operator action chain, with the action recorded |
| `plans/10-operations.md:70-77` | Failure modes: deploy, canary decision change, worker crash, storage outage, database loss, backlog, alert storm, healing false positive → kill switch |
| `plans/10-operations.md:93` | Drills executed as tests in staging, including kill-switch flip while ingest continues |
| `plans/10-operations.md:95` | Each alert rule triggered synthetically in staging; must fire with right severity and runbook link |
| `plans/10-operations.md:108` | Healing false positives invisible in aggregate is a named risk |
| `plans/10-operations.md:109` | Single-operator bus factor risk; mitigation is runbooks for a second person and drill handover at M3 |
| `plans/10-operations.md:149` | 10.2.2-T2 canary + automatic rollback on SLI breach |
| `plans/10-operations.md:151` | 10.2.2-T4 release runbook task (not shipped) |
| `plans/10-operations.md:189` | 10.5.1-T4 DR runbook task with communication and data-loss statement (not shipped) |
| `plans/10-operations.md:212` | Story 10.7.1 acceptance: each runbook executed once by a non-author in staging |
| `plans/10-operations.md:220` | Epic 10.7 task row: incident process = severity ladder, on-call expectation, communication, postmortem |
| `plans/10-operations.md:225-226` | 10.7.2-T1 routing tool, 10.7.2-T2 single-operator escalation |
| `plans/10-operations.md:282` | Area plan still lists O2/on-call as `[OPEN]` (superseded by `DECISIONS.md:153`) |
| `plans/11-security-compliance.md:53` | FR-11-13: breach notified within 72 h, processor notifies controller promptly |
| `plans/11-security-compliance.md:142` | Failure mode: secret logged → scanner, purge, rotate, notify if tenant data exposed |
| `plans/11-security-compliance.md:149` | Cross-tenant probe handling; NFR-11-1 isolation |
| `plans/11-security-compliance.md:226` | 11.3.1-T2 leak runbook task (not shipped) |
| `plans/11-security-compliance.md:312` | 11.9.1-T2 breach runbook task |
| `plans/12-pilot-commercial.md:34` | FR-12-3: pilot agreement includes support expectations |
| `plans/12-pilot-commercial.md:209` | 12.2.1-T4 weekly pilot review ritual and blocker escalation |
| `plans/12-pilot-commercial.md:210` | 12.2.1-T5 pilot support process: severity definitions, response expectation, support-hour logging |
| `plans/04-ingestion-adapters.md:276` | 04.9.2-T3: status-page choice recorded as a decision (not delivered) |
| `plans/open-items/02-catalog-ingest-runs-evidence.md:42` | OI-04-12: status-page product `[OPEN]` |
| `plans/features/SOLO-PATH.md:3` | Solo path status: team is the owner alone; reviews by others or AI against the spec package |
| `plans/features/SOLO-PATH.md:9` | One person is developer, Tech Lead and QA/product owner |
| `plans/features/SOLO-PATH.md:106` | `[OPEN]` who the external reviewer is |
| `plans/features/10-operations/10.7.1-operable-by-a-second-person.md:13` | Non-goals: on-call schedule is O2; DR runbook is 10.5.1-T4, referenced not rewritten |
| `plans/features/10-operations/10.7.1-operable-by-a-second-person.md:22` | AC1: each runbook executed once by a non-author, corrections committed |
| `plans/features/10-operations/10.7.1-operable-by-a-second-person.md:28` | AC7: every T5 alert links a runbook; the incident runbook includes severity ladder, on-call expectation, postmortem template with follow-up rule |
| `plans/features/10-operations/10.7.1-operable-by-a-second-person.md:75` | Operator gate denies every caller today; list/replay write no audit row (dead-letter §5.4) |
| `plans/features/10-operations/10.7.1-operable-by-a-second-person.md:89` | Test plan: staged P2 incident produces a filled postmortem and a follow-up task |
| `plans/features/10-operations/10.7.1-operable-by-a-second-person.md:90` | `scripts/check-alert-runbooks.sh` named as the coverage check |
| `plans/features/10-operations/10.7.1-operable-by-a-second-person.md:112` | Orphan-artifact runbook task (T3.1), not written |
| `plans/features/10-operations/10.7.1-operable-by-a-second-person.md:132-133` | T6.1 and T6.2 definitions and their "done when" |
| `plans/features/10-operations/10.7.1-operable-by-a-second-person.md:140` | T6 lands in M3 after T1, before the pilot's on-call start |
| `plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md:13` | Non-goals: no staffed 24/7 rota for a solo operator, `[OPEN]` for the first hire; severity ladder is 10.7.1-T6 |
| `plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md:23` | AC1 route table `infra/alerts/routing.yml` mapping every alert by severity |
| `plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md:25` | AC3: P1/P2 to a push/phone destination, P3 to chat/email only |
| `plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md:26` | AC4: 15 min escalation timeout `[INFERENCE]`, secondary or auto-ack fallback |
| `plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md:28` | AC6: quiet hours, P1 always pages, P2 07:00–23:00 `[INFERENCE]` |
| `plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md:34` | Alert label contract: `severity ∈ {P1,P2,P3}`, `owner`, `runbook` |
| `plans/features/10-operations/10.7.2-paging-and-on-call-at-the-first-alert.md:40` | Record location proposal `docs/runbooks/records/` `[INFERENCE]` (directory absent) |
| `plans/features/10-operations/10.3.1-telemetry-and-alerting.md:121` | Metrics catalogue incl. dead-letter depth, lease-loss rate, incomplete rate, healing false-positive rate |
| `plans/features/10-operations/10.3.1-telemetry-and-alerting.md:135-138` | T5.1 numeric thresholds; T5.2 runbook links and lint (spelled `check-alerts.sh`); T5.3 routing; T5.4 synthetic trigger |
| `docs/decisions/O1-pilot-profile.md:3` | Pilot profile is a working definition, every number `[INFERENCE]` |
| `docs/decisions/O1-pilot-profile.md:21` | One named QA lead who owns the release call |
| `docs/decisions/O1-pilot-profile.md:32` | Acceptance: 0 decisions PASS where a required shard or artifact was missing |
| `docs/runbooks/breach.md:3` | Breach status: partial; T2.4 open; T3 rehearsal not run |
| `docs/runbooks/breach.md:9` | Scope: suspected/confirmed personal-data breach; availability-only incidents are out of scope |
| `docs/runbooks/breach.md:11` | The 72 h clock is the controller's; our duty is prompt notice; legal questions `[OPEN OI-11-08]` |
| `docs/runbooks/breach.md:12` | 4 h notice target ⚠️ proposed, pending owner sign-off |
| `docs/runbooks/breach.md:13-20` | Role table; sender/channel and counsel `[OPEN]` |
| `docs/runbooks/breach.md:22` | No secrets/personal data in records, tickets or notices; ids and closed enums only |
| `docs/runbooks/breach.md:23` | A breach is never closed by deleting evidence; deletion requests are routed elsewhere |
| `docs/runbooks/breach.md:24` | Send the first notice early, add detail later |
| `docs/runbooks/breach.md:45-50` | Classes: Confirmed / Suspected / False positive / Confirmed, no customer data |
| `docs/runbooks/breach.md:49` | False positive closes with the documented benign cause recorded |
| `docs/runbooks/breach.md:58-70` | Signal-to-action matrix incl. secret in a log, cross-tenant probe, broken audit chain |
| `docs/runbooks/breach.md:68` | Broken audit chain: treat as possible tampering, page the incident lead |
| `docs/runbooks/breach.md:76-82` | Internal S1/S2/S3 cadence; response times `[INFERENCE]`/`[OPEN]` |
| `docs/runbooks/breach.md:88-98` | Clock-record fields: `signal_seen_at`, `classified_at`, `triage_confirmed_at`, `notice_due_at`, `notice_sent_at`, `ack_at`/`ack_missing`, `closed_at`, `elapsed` |
| `docs/runbooks/breach.md:104` | Facts only; one notice per affected controller, never naming another customer |
| `docs/runbooks/breach.md:127` | Sender and channel `[OPEN]`; T2.4 fills the contact table |
| `docs/runbooks/breach.md:193` | Clock-record location and retention `[OPEN]` |
| `docs/runbooks/breach.md:196` | The runbook has not been rehearsed |
| `docs/runbooks/dead-letter.md:3` | Dead-letter status: T1.1 written; T1.2/T2.2 not executed |
| `docs/runbooks/dead-letter.md:15` | No pager integration yet; the alert names no person |
| `docs/runbooks/dead-letter.md:19` | Never hand-write an UPDATE/DELETE on `public.task` |
| `docs/runbooks/dead-letter.md:21` | Record ids, closed enums, counts, timestamps only |
| `docs/runbooks/dead-letter.md:26` | `ITaskAlertSink` is the shipped alert seam, three call sites |
| `docs/runbooks/dead-letter.md:210-217` | Escalation list: repeated death after one replay, growing depth, missing tenant, retention deadline, 401/403 |
| `docs/runbooks/stuck-run.md:3` | Stuck-run status: T2.1 written; T2.2 drill open |
| `docs/runbooks/stuck-run.md:181-215` | Section 4.3 "is the sweep running", and which levers are human-reachable today; operator dead-letter route denied |
| `docs/runbooks/key-rotation.md:3` | Key-rotation status: T4.1 written; drill and non-author execution open |
| `docs/runbooks/key-rotation.md:9-13` | Secret handling, no ad-hoc writes, no invented operator endpoint, who rotates what |
| `docs/runbooks/kill-switch.md:3` | Kill-switch status: T5.1 written; T5.2 staging drill open, AC6 half open |
| `docs/runbooks/kill-switch.md:13` | Tenant/project scope needs a member session with `healing:configure`; the system scope's platform-operator credential is not usable today |
| `docs/README.md:8` | `docs/runbooks/` is the runbook index, and it lists both `runbooks/kill-switch.md` and `runbooks/incident-process.md` under 10.7.1-T5.1/T6.1 (drills T5.2/T6.2 open) |

## 8. What this page does not cover

- **The postmortem template and the follow-up-task rule** — 10.7.1-T6.2 (`plans/features/10-operations/10.7.1-operable-by-a-second-person.md:133`). `[OPEN]`.
- **The staged incident exercise by a non-author** — same subtask; blocked by §3 item 4 until a second person exists. `[OPEN]`.
- **The alert-to-runbook coverage lint and the alert rules themselves** — 10.7.1-T6.2 and 10.3.1-T5; listing `infra/` and `scripts/` on this tree shows no `infra/alerts/` directory and no `scripts/check-alert-runbooks.sh` (nor `scripts/check-alerts.sh`), and the two plans spell the lint differently. `[OPEN]`.
- **Named contacts, channels, status page, customer response times, support severities** — §4; owned by 11.9.1-T2.4, 12.2.1-T5, 04.9.2-T3. `[OPEN]`.
- **Paging delivery, escalation timeout and quiet hours** — 10.7.2-T1/T2. `[OPEN]`; the numbers in §2 are inferences, not policy.
- **Response-time targets for this ladder** — §2; nothing in the repo defines them, and the proposal there is not in force.
- **Restore/DR, release/rollback and orphan-artifact procedures** — the runbooks listed `[OPEN]` in §6. This page routes to them; it does not replace them.
- **The drill status of every other runbook** — each runbook states its own (`docs/runbooks/dead-letter.md:3`, `docs/runbooks/stuck-run.md:3`, `docs/runbooks/key-rotation.md:3`, `docs/runbooks/breach.md:3`). AC1 as a whole is therefore not met.
- **Where this page is indexed** — `docs/README.md:8`, in the runbook row that also names `runbooks/kill-switch.md`; that row is the index, so a status change here is carried there in the same edit.
