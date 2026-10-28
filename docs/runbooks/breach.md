# Breach runbook

Status: **11.9.1-T2.1, T2.2 and T2.3 written (declared partial delivery); T2.4 is open.** T2.4 is the regulator-support steps, the weekend/holiday on-call path with named people, the post-incident review template and counsel review. No rehearsal has run (11.9.1-T3), no non-author has executed this runbook, and no counsel or owner sign-off exists. Acceptance criterion AC5 is partially met and AC6's rehearsal half is open (§7). [OPEN OI-11-08]

Legend as in [`../../AGENTS.md`](../../AGENTS.md): ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`. Nothing in this runbook is a statement of legal compliance; every legal point is ⚠️ or `[OPEN OI-11-08]` and needs counsel.

## 1. Scope, roles and rules

Evidgate is the **processor** of its customers' test evidence (cited by the 11.9.1 plan header as DECISIONS §7; [area plan 11](../../plans/11-security-compliance.md) §6 "we act as processor"). This runbook covers a suspected or confirmed personal-data security breach in Evidgate's own systems that touches a customer's data. It does not cover a breach inside a customer's CI or tenant that Evidgate did not cause, loss of availability without data exposure (see [environments](environments.md) and plan 10 alerts), or deletion requests (the deletion runbook is 11.9.1-T1.1).

- **The 72 h clock is the controller's, not ours.** The customer (controller) notifies its regulator; our duty is to tell the customer promptly so that clock is not eaten. Whether the decree sets a separate processor deadline, who owns the clock, and whether the clock record must be a durable application record are open legal questions: [OPEN OI-11-08] ([`plans/open-items/04-ops-security-commercial.md:41`](../../plans/open-items/04-ops-security-commercial.md)). The 72 h figure and the article mapping (PDPL Art. 23.1) are taken from the repo's plans (`plans/11-security-compliance.md:124,150`) and are ⚠️ here: no primary source was read for this runbook.
- **Target.** Send the first processor→controller notice within **4 h of triage confirmation** (§3). ⚠️ proposed, pending owner sign-off; [OPEN OI-11-08] ([`11.9.1` AC6](../../plans/features/11-security-compliance/11.9.1-deletion-and-breach-are-runnable-procedures.md)). "Promptly, not at hour 71" (area plan 11 §5, `:150`). The same number must be written into the DPA and this runbook by one coordinated edit (11.9.1 §11); the DPA clause is pending (11.8.1-T1) [OPEN].
- **Roles (role-based; no person or channel is named while T2.4 is open).**

  | Role | Who | Does |
  |---|---|---|
  | Incident lead | Tech Lead (the owner of the breach-runbook artifact, area plan 11 §4.3 `:124`) | triage decision, notice content, evidence pack |
  | Clock owner | the owner (`[INFERENCE]` from OI-11-08) | owns the start and send timestamps in the clock record |
  | Sender and channel | `[OPEN]` named person and channel per customer; T2.4 | sends the notice, confirms receipt |
  | Counsel | `[OPEN]` not engaged; T2.4 | reviews notice wording and legal mapping |

- Never put secret values, credentials, tokens, cookies, raw payloads, artifact content, screenshots of tenant data or any personal data in the runbook, the notice, the clock record, tickets or the evidence pack. Record ids, closed enums, counts, timestamps and hashes only (same rule as [security-event-baseline](../security/security-event-baseline.md) §1). No customer names, repository names, URLs, hostnames or e-mail addresses in free text either: use org and project ids. In a cross-tenant exposure the notice to one controller says "an unauthorised party" and carries only that controller's own ids, never another tenant's identity.
- A breach is never "closed" by deleting evidence. A deletion request that arrives during an incident is routed to the deletion runbook and does not remove incident records.
- Do not wait for a complete picture to send the first notice: send early, then add detail in later notices (EDPB Guidelines 9/2022 ⚠️ `[INFERENCE]`: [source](https://www.edpb.europa.eu/system/files/2023-04/edpb_guidelines_202209_personal_data_breach_notification_v2.0_en.pdf) §II.A.4, paras 44–46, GDPR text, not Vietnamese law; seen as a search summary on 2026-10-28, document not opened).

## 2. Detection sources (T2.1)

State of each source on this base. A source marked planned cannot be queried today; the step says what to do instead.

| # | Source | What it tells you | State | Where to look |
|---|---|---|---|---|
| D1 | Security events feed | five V1 types: `auth_failure`, `cross_tenant_probe`, `secret_scanner_alert`, `kill_switch_used`, `support_access`; severity `info\|warning\|critical` | ⚠️ **read shipped 2026-10-28; emission partial.** `GET /v1/orgs/{o}/security-events` is served from `migrations/0053_security_security_event.sql` and `ListSecurityEvents.cs` (`contracts/openapi.yaml:3672`), and four of five types still have no emitter ([security-event-baseline](../security/security-event-baseline.md) §1 `:15`, §5 rows 2 and 9; receivers 11.5.1-T3.2, 11.1.1-T4.1). `[OPEN]` | none today; use D2–D5 |
| D2 | Audit log | success-only rows (actor, action, object, request and trace id, hashes) with a per-org hash chain | ✅ shipped: `GET /v1/orgs/{o}/audit` (`src/Modules/Evidgate.Modules.Identity/Endpoints/AuditEndpoints.cs:42`), `audit:read`. Limits: it records successful actions only, so a missing row is not proof of no exposure (denied and read-only lookups write no row, [audit-taxonomy](../security/audit-taxonomy.md) `:62,64`); and Evidgate staff cannot query a customer org's audit without a tenant principal (§5.1) `[OPEN]` 02.8.1 | §5 |
| D3 | Audit chain verification | a gap, broken link or hash mismatch in one org's chain | ✅ shipped: task `audit.verify_chain` every 6 h; on a fault it logs event `2400`, increments `audit_chain_verification_failures_total` and dead-letters the task (`src/BuildingBlocks/Persistence/Audit/VerifyAuditChainTask.cs`; [audit-taxonomy](../security/audit-taxonomy.md) §6 `:240`). The scan cannot see deletion of the newest rows or a fully emptied chain (`:242`) | log search for event `2400`; metric `audit_chain_verification_failures_total` |
| D4 | Secret scanner | secret-shaped content in a stored artifact | ⚠️ **detection not shipped:** `artifact.scan_state` is a closed column and nothing writes `flagged` yet (06.2.3-T1; baseline §3.3). A secret found by a human (review, customer report, support) is a D5 signal. `[OPEN]` | none automated today |
| D5 | Support and human reports | customer or engineer says data was exposed, a credential leaked, a wrong tenant saw data | ✅ available, manual | support channel `[OPEN]` (T2.4 names it) |
| D6 | Other planned alerts | auth-failure bursts, cross-tenant probe patterns, supply-chain advisories | ⚠️ planned: alert rules are 11.5.1-T3.1, routing is 10.3.1-T5 | `[OPEN]` |

Rule for every source: the first person who sees a signal writes down the time they saw it (clock record, §5.4 field `signal_seen_at`) before doing anything else.

## 3. Triage (T2.1)

### 3.1 Classes

| Class | Meaning | Action |
|---|---|---|
| **Confirmed** | There is a reasonable degree of certainty that a customer's data was exposed, altered, lost or accessed by someone not entitled to it, through Evidgate's systems or staff. | Start the 4 h target at the moment the incident lead records "confirmed". Run §4. |
| **Suspected** | A signal exists that is not yet explained. | Investigate; write a suspicion entry in the clock record; reassess at each new fact. If it cannot be ruled out within the working session, the incident lead decides whether to treat it as confirmed for notice purposes. [OPEN: no time limit for suspicion is set; counsel + owner, OI-11-08] |
| **False positive** | The signal has a documented benign cause. | Close with the cause recorded (event ids, who confirmed). No notice. |
| **Confirmed, no customer data** | Evidgate's own infrastructure or credentials were affected but no tenant data. | Handle as an internal security incident (rotate per [key-rotation](key-rotation.md)); no controller notice; record the reasoning. |

The processor does not need to assess the risk to individuals before notifying; that assessment is the controller's (EDPB para 44 ⚠️, see §1).

### 3.2 Signal-to-action matrix

Covers every breach-adjacent row of area plan 11 §5 (`plans/11-security-compliance.md:142-152`).

| Signal | Default class | First actions (in order) | Notice? |
|---|---|---|---|
| Secret found in a log or artifact (§5 `:142`) | Suspected → Confirmed if tenant data or tenant credentials were exposed | record ids, times and a hash of the affected log or object range, restrict access to it, then purge or redact under a recorded decision, rotate the secret per [key-rotation](key-rotation.md), identify which tenants were exposed; never paste the secret or log lines into a ticket | yes if tenant data or a tenant credential was exposed |
| Fork-PR exfiltration attempt (`:143`) | False positive or Confirmed | check that no Evidgate credential was reachable from that context; confirm what was read | only if a tenant credential or data left |
| Malicious or rejected artifact (`:144`) | False positive (rejected artifacts stay `rejected`) | confirm the parser rejected it and the isolated origin held | no, unless content escaped |
| Prompt injection in DOM or log (`:145`) | False positive (injection is data) | confirm the validator was the authority and no kill switch was bypassed | no, unless a policy decision was changed |
| Webhook replay or timestamp skew (`:146`) | False positive | confirm one rejection, no state change; escalate if bursts continue | no |
| Compromised dependency or action (`:147`) | Suspected | pin check, advisory triage, emergency rotation path | if a deployed build ran the compromised code with tenant access |
| Support-access misuse (`:148`) | Suspected | `[OPEN]` not executable today: no support-grant handler or audit rows exist (02.8.1, [audit-taxonomy](../security/audit-taxonomy.md) `:181`); until then compare access logs and ask the operator for a written account, and treat "no row found" as unknown, not as no exposure | yes if data outside the grant was read |
| Cross-tenant probe (`:149`) | False positive per single row; Suspected as a pattern | `[OPEN]` a denied foreign lookup writes no audit row (OI-02-31, [audit-taxonomy](../security/audit-taxonomy.md) `:62,182`), so D2 cannot show who read what; use D2 only for successful writes, check the chain (D3), and treat "no row found" as unknown | yes if any foreign row was returned |
| Broken audit chain (D3) | Suspected | treat as possible tampering; preserve the database and logs; page the incident lead | decided on facts |
| Breach on a Friday night (`:150`) | any | the same steps; the weekend/holiday on-call path is T2.4 `[OPEN]`; until it exists the incident lead and clock owner are the fallback and must be reachable | per class |
| Customer-reported exposure (D5) | Suspected | take only ids and times; do not ask the customer to send the exposed data | per class |

### 3.3 Severity to action

Severity here is an internal urgency level, not the closed security-event severity vocabulary.

| Level | Condition | Response time |
|---|---|---|
| S1 | Confirmed, tenant data exposed to an outsider | start now; first notice inside the 4 h target |
| S2 | Suspected with a plausible exposure path | triage decision the same working session; target applies from "confirmed" |
| S3 | Anomaly without an exposure path | next working day; no notice |

Response times are `[INFERENCE]` and `[OPEN]` for owner sign-off; only the 4 h figure is the proposed target.

## 4. Processor→controller notice (T2.2)

### 4.1 Steps (the checklist T3 times)

| Step | Who | Action | Clock record field |
|---|---|---|---|
| B1 | whoever sees the signal | write down the time the signal was seen | `signal_seen_at` |
| B2 | incident lead | assign the incident id, classify (§3.1), list tenants possibly affected by org id | `incident_id`, `classified_at` |
| B3 | incident lead | record "confirmed" | `triage_confirmed_at` (target starts here) |
| B4 | clock owner | start the clock record and set `notice_due_at = triage_confirmed_at + 4 h` ⚠️ | `notice_due_at` |
| B5 | incident lead | fill the notice template (§4.2) from known facts; unknown fields stay "unknown" | |
| B6 | sender | send to each affected controller through its channel | `notice_sent_at`, `channel` |
| B7 | sender | get an acknowledgement or log that none arrived and when it will be retried | `ack_at` or `ack_missing` |
| B8 | incident lead | start the evidence pack (§5) and send follow-up notices as facts arrive | `followup_sent_at[]` |
| B9 | clock owner | close the record when the controller has what it needs | `closed_at`, `elapsed` |

Elapsed time for the target is `notice_sent_at − triage_confirmed_at`. If the target is missed, say so in the record with the reason; a late notice is still sent at once.

### 4.2 Notice template

Facts only; no personal data, no secrets, no speculation. Send one per affected controller, never a message that names another customer.

```
Subject: Security incident notice <incident_id> - Evidgate (processor)

To: <controller contact>                 From: <named sender> [OPEN: T2.4]
Sent at (UTC): <notice_sent_at>          Notice number: <n> (initial | update)

1. What we know: <one factual paragraph, no speculation>
2. When: first signal <signal_seen_at>, confirmed <triage_confirmed_at>, started <unknown | time>
3. Your data affected: <org id, project ids, counts, data classes, or "unknown">
4. Not known yet: <list>
5. What we did so far: <containment actions, with times>
6. What you can do now: <actions for the controller, e.g. rotate tokens we list by id>
7. What you can request from us: <evidence pack items in §5, regulator-support contact [OPEN: T2.4]>
8. Our contact for this incident: <named contact and channel> [OPEN: T2.4]
9. Next update by: <time>
```

The controller needs enough for its own regulator notice (nature of the breach, categories and approximate counts, likely consequences, measures taken; GDPR Art. 33(3) shape ⚠️ `[INFERENCE]`, not the Vietnamese decree): items 1, 3, 4 and 5 supply them; counsel confirms the exact required contents `[OPEN OI-11-08]`.

### 4.3 Senders and channel

`[OPEN]` No named person and no channel is recorded in the repo. T2.4 fills a table of controller, contact, channel and backup channel. Until then the clock owner writes down, in the clock record, which address or channel was used and why. The DPA notice clause is pending (11.8.1-T1) `[OPEN]`; nothing here quotes it.

## 5. Evidence pack (T2.3)

Purpose: give the controller facts for its own filing and let Evidgate show what it did. Content is ids, counts, timestamps and hashes. The pack is assembled from documented commands; no script exists (a harness is 11.9.1-T3.1's).

### 5.1 Audit extract (✅ shipped)

Caller needs a principal of the affected org with `audit:read` (`plans/00-overview.md:377-378`). **[OPEN]** the platform operator principal carries no permissions (`src/Modules/Evidgate.Modules.Identity/Authorization/PermissionEvaluator.cs:63`) and the support-grant path is not built (02.8.1), so Evidgate staff cannot read a customer org's audit rows without a tenant principal; for an affected customer org the extract is pulled by, or with, that customer's `tenant_admin`. State this in the notice if it applies.

| Call | Use |
|---|---|
| `GET /v1/orgs/{o}/audit?from=<iso>&to=<iso>&actor_type=&action=&object_type=&limit=` | cursor page, newest first; page with `next_cursor` until it is null |
| `GET /v1/orgs/{o}/audit/export?format=jsonl\|csv&from=<iso>&to=<iso>` | the filtered set as a file; default range 30 d, max 92 d, at most 100 000 events, otherwise 422 `export_range_too_large`; the export writes an `audit.export` row |

Each JSONL row (and each list-endpoint item) carries `seq`, `created_at`, `actor_type`, `actor_id`, `action`, `object_type`, `object_id`, `request_id`, `trace_id`, `before_hash`, `after_hash`, `metadata`, `prev_hash`, `entry_hash` (`src/Modules/Evidgate.Modules.Identity.Contracts/AuditDtos.cs:19-34`). **CSV drops `prev_hash`, `before_hash`, `after_hash` and `metadata`** (`AuditEndpoints.cs:29-32`), so a pack that must show a chain walk uses `format=jsonl`. The export is tenant data, not a sanitised artifact: `actor_id` identifies a person and `metadata` can hold free text (e.g. a `reason`, an e-mail domain, a slug; [audit-taxonomy](../security/audit-taxonomy.md) `:75-76`). Store it only with the incident lead and clock owner; keep the file, its `sha256` and the call parameters in the pack; do not paste rows into tickets or chat and do not copy those fields into the timeline or scope text.

### 5.2 Chain check (✅ shipped, indirect)

Record whether event `2400` or the metric `audit_chain_verification_failures_total` fired for the org in the window; if the pack must show a chain walk, re-compute `entry_hash` from the JSONL extract (the rule is `AuditChainVerifier`, [audit-taxonomy](../security/audit-taxonomy.md) §6 `:239`). Limits: it cannot see deletion of the newest rows (`:242`), and the hash is unkeyed, so an owner-role or superuser rewrite, or self-consistent appended rows, would pass (OI-02-32, `:242`). A passing check is not proof of no tampering; say so in the pack and never present it to a controller or regulator as integrity assurance.

### 5.3 Security-events extract (✅ shipped)

`GET /v1/orgs/{o}/security-events` requires `security_event:read` (`tenant_admin` in the default role bindings; the code is tenant-assignable) and is served since 2026-10-28 (11.5.1-T5, `migrations/0053_security_security_event.sql`); until the four missing emitters land (B-D) a page is often empty. **Step at 02:00:** export the page for the incident window with `type`/`severity`/`from`/`to`, record `next_cursor`, and write "security-events extract empty" with the date in the pack when it has no rows.

### 5.4 Clock record and timeline

Planned location: one file per incident under `docs/runbooks/records/` `[INFERENCE]` from OI-11-08 `[OPEN OI-11-08]`; the directory does not exist and this runbook creates none. Until the owner decides, keep the record in the incident ticket and attach it to the pack; the ticket system, its access list and its retention are not identified in the repo `[OPEN]` (B-G), so keep org ids and times there and nothing from the audit export. Template:

```
incident_id: <id>            class: <confirmed|suspected|false_positive|no_customer_data>
signal_seen_at:  <UTC>       source: <D1..D6>
classified_at:   <UTC>       triage_confirmed_at: <UTC>
notice_due_at:   <UTC>  (+4 h ⚠️ proposed)
notice_sent_at:  <UTC>       channel: <per customer>      ack_at: <UTC | missing>
followup_sent_at: [<UTC>, ...]
elapsed (sent - confirmed): <h:mm>      target_met: <yes|no, reason>
clock_owner: <role>          incident_lead: <role>
```

Timeline table in the pack: one row per event with UTC time, who, what, source id (request id, audit `seq`, ticket id). No free text containing tenant data.

### 5.5 Scope statement

One paragraph per affected org: org id, project ids, data classes touched (evidence artifacts, run metadata, audit rows, account data), counts, time window, how it was bounded (audit rows, chain state), and what is unknown. Never list individuals.

### 5.6 Pack contents (checklist)

- [ ] Audit export file + sha256 + call parameters (§5.1)
- [ ] Chain-check result (§5.2)
- [ ] Security-events extract, or the dated "not available" line (§5.3)
- [ ] Clock record (§5.4) and timeline
- [ ] Scope statement per org (§5.5)
- [ ] Copies of every notice sent, with send time and channel
- [ ] Containment and rotation actions with times, ids only

## 6. Open items and receivers

| ID | Gap | Receiver |
|---|---|---|
| B-A | Named senders, channels and weekend/holiday on-call path; regulator-support steps; post-incident review template; counsel review and sign-off; recording in `docs/compliance/manifest.yaml` (the directory does not exist) | 11.9.1-T2.4 (carried row "Breach runbook T2.4 open"); counsel + Tech Lead |
| B-B | Legal questions: processor deadline, clock owner, durable record, notice contents | OI-11-08 (owner + counsel) |
| B-C | 4 h target sign-off; same number into the DPA | owner; 11.8.1-T1 |
| B-D | Security-events feed and four missing emitters | 11.5.1-T3.2, 11.5.1-T5 |
| B-E | Secret-scanner detection writes nothing yet | 06.2.3-T1 |
| B-F | Evidgate staff cannot read a customer org's audit without a tenant principal | 02.8.1 |
| B-G | Clock-record location and retention; the ticket system's access list and retention | OI-11-08; owner |
| B-H | Timed rehearsal and drill record (AC6 second half, AC7) | 11.9.1-T3 |

## 7. Rehearsal — not executed

[OPEN] The runbook has not been rehearsed. 11.9.1-T3.1 must time steps B1–B9 (§4.1), assemble the §5.6 pack from the documented commands, and record elapsed `notice_sent_at − triage_confirmed_at` against the 4 h target in `docs/security/breach-drill-<date>.md`. A tabletop comes first, then a live drill on fixtures; the drill uses fixture tenants and synthetic identifiers only.
