# Baseline metric definitions — v1

| Field | Value |
|---|---|
| Version | v1 (frozen 2026-10-28) |
| Status | frozen |
| Owner task | 12.2.1-T2.1 (`plans/features/12-pilot-commercial/12.2.1-a-pilot-that-can-be-measured.md`) |
| Contract | `plans/12-pilot-commercial.md` §4.3 (`:87-97`), decision rule D2 (`:62`) |
| Next version | v2 only after a deliberate re-cut of the baseline (see section 3) |

Legend: `[INFERENCE]` = our estimate, not verified; `[OPEN]` = unanswered, owner or data needed; `[DECISION]` = decided and recorded elsewhere; `null` = a value that could not be sourced (section 4).

This file freezes the seven metric rows of `plans/12-pilot-commercial.md` §4.3. It measures nothing, contains no customer data and invents no number. Every definition below is the §4.3 wording; "Reading" lines only say how to read it, they never change it.

## 1. Metric rows

Provenance is a `file:line` into the area plan (`plans/12-pilot-commercial.md`).

| # | Metric | Definition | Source | Cadence | Decision rule | Provenance |
|---|---|---|---|---|---|---|
| M1 | Triage time per failure | Median time from a failure being reported to a label+owner being set (self-reported where the tool is not ours) | Ticket system + product timestamps | Weekly | Must improve vs baseline, or a written reason | `plans/12-pilot-commercial.md:91` |
| M2 | Test-maintenance time | Hours per sprint spent fixing test mechanics (excludes product fixes) | Customer timesheet | Per sprint | Must improve, or no-healing-value claim | `plans/12-pilot-commercial.md:92` |
| M3 | Repeated incidents handled correctly | Repeated failures whose label matched the human label later applied | Product labels | Monthly | More than 50 % correct before any V2 PR capability `[DECISION]` | `plans/12-pilot-commercial.md:93` |
| M4 | Retention | Still running the pilot after the window, with at least N runs/week (N is `[OPEN]`: not fixed by §4.3) | Product usage | Monthly | Continues, or has a dated reason | `plans/12-pilot-commercial.md:94` |
| M5 | Contribution margin | Revenue − (API/DB share + object GB-month + egress + AI tokens + support hours) | Ledger + cost model | Monthly | Non-negative by M3 exit, or a written plan | `plans/12-pilot-commercial.md:95` |
| M6 | Heal rate | Accepted candidates ÷ eligible failures | Product | Monthly | Reported only next to missed bugs | `plans/12-pilot-commercial.md:96` |
| M7 | Missed bugs | Bugs reaching production that a healed, quarantined or flaky-bypassed test should have caught | Customer labels | Monthly | A rise is a stop signal | `plans/12-pilot-commercial.md:97` |

Reading, per row (no change to the definition):

- "M3 exit" in the M5 row means the milestone, not metric M3.
- M1: one `failure_ref` per failure (worksheet); the median is taken per week over the failures reported that week. Where the customer's tracker is not ours, the times are self-reported and marked so `[INFERENCE]`.
- M2: product fixes are excluded by the person reporting; the hours are the customer's own timesheet figure, never an estimate of ours.
- M3: no baseline exists for it before the product runs (no product labels), so its baseline cell is `null` with reason `no product before enablement` `[INFERENCE]`; its rule is compared with the 50 % bar only.
- M4: the window and N are not set by §4.3; they come from the pilot agreement (12.2.1-T1) and are `[OPEN]` until then.
- M5: revenue is `[OPEN]` O5 (price) and the cost lines come from the ledger (12.3.1) and the cost model (12.4.1); until they exist the baseline cell is `null`.
- M6, M7: product-side metrics. Their baseline cell is `null` with reason `no product before enablement` `[INFERENCE]`, except that M7 may carry a historical count from the customer's own production-bug labels if the customer has one (source named in the Source column); otherwise `null`.

## 2. Guardrail pair

The guardrail pair is M6 (heal rate) and M7 (missed bugs), exactly `plans/12-pilot-commercial.md:96-97`. Heal rate is never reported, quoted or compared on its own: every place that shows it shows missed bugs next to it, and a rise in missed bugs is a stop signal for healing (decision rule D5 of the 12.5.1 plan).

## 3. Freeze rule (AC2)

- This file is not edited after the first baseline row is written (story 12.2.1 AC2, `plans/features/12-pilot-commercial/12.2.1-a-pilot-that-can-be-measured.md:24`; test row `:83`).
- An edit after that point forces a re-cut baseline: the baseline sprints are measured again under a new version (v2) and the old rows are kept and marked superseded, never rewritten.
- Reason: a before/after comparison with a moved definition is worthless (area plan D2, `plans/12-pilot-commercial.md:62`).
- The first real baseline is pilot one's CI history plus its known-flaky list (`plans/open-items/04-ops-security-commercial.md:71`).

## 4. Missing values (T60)

A cell that cannot be sourced is `null` with a written reason, never 0 and never an estimate presented as a measurement (ruling T60; `plans/features/12-pilot-commercial/12.2.1-a-pilot-that-can-be-measured.md:67`). Null propagation: a weekly median (M1) is taken over the non-null rows only and reports n next to it; a week with no non-null rows is `null`; `0` is only ever a measured zero (for example 0 hours in M2), never a stand-in for unknown.

Customer-data rule (applies to every file under `docs/pilots/`): only aggregates and opaque ids are committed; the `failure_ref` to ticket mapping stays in the pilot's own storage and `failure_ref` is sequential, never derived from a ticket id, hash or title; no names, emails, URLs, hostnames, repo or branch names, test titles, tokens or credentials; personal data or secrets found in customer material are not copied but filed back to the customer (area plan §6). At exit or on a deletion request, per-failure rows are purged or redacted per the agreement's data-handling terms (12.2.1-T1, 11.8.1-T3 data map).

A pilot that refuses a baseline gets the documented self-report fallback (area plan §5), marked as self-reported.

## 5. Pilot acceptance-sheet targets (frozen as written)

Source: `docs/decisions/O1-pilot-profile.md:29-39`. The table abridges the cell wording; the numbers and the pass rule are exact and O1 §3 is authoritative for the full text. Open item OI-12-09 (`plans/open-items/04-ops-security-commercial.md:71`) asks for them to be frozen as written in this kit for pilot one and changed only with data. All are `[INFERENCE]`; no number below is new.

| Target | Value | How measured (O1 §3) |
|---|---|---|
| Ingest completeness | At least 95 % of CI runs reach `complete` or an explained `incomplete`; 0 silently dropped | Reconciler output |
| No false PASS | 0 decisions PASS where a required shard or artifact was missing | Evaluation audit |
| Decision usefulness | QA lead agrees with at least 90 % of sampled runs (20 runs) | Labelled sample |
| Time to decision | p95 under 60 s after finalize | Telemetry |
| Triage time | Median time from red run to classified cause drops by at least 30 % against the team's own baseline (measured with M1; M1's events are failure reported to label+owner set, so the 30 % bar is applied to M1's median) | Two sprints before vs during |
| Flaky visibility | At least 80 % of cases the team already knows are flaky are flagged | Comparison with the team's own list |
| Willingness to continue | Team asks to keep it running or names a price | Conversation |

Pass rule: all of the first four hold, and at least two of the last three. OI-12-09 stays open (`measure in pilot`, before Stage C); this table does not close it.

## 6. Not answered here

OI-12-01 to OI-12-05 and OI-12-08 belong to 12.2.1-T1/T3/T5, 10.8.1-T3 and 12.1.1. M5 touches OI-12-02 and O5 (price).
