# Phase review: decisions waiting for owner review

This file records every PR or task decision the owner has not explicitly reviewed. A future phase review (owner or agent) reads it to find what to check. It does not gate any merge.

How rows get here (owner rule 2026-10-11, coordinator flow):

- A PR on the owner path is merged by the coordinator after the 5-minute owner wait if the owner has not merged it. Every `[DECISION]`, contract change or security-posture change it carries is added below in the same turn.
- A panel choice decided by the coordinator (`PANEL-CHOICE`, decided with a jev choice call) is added below, with the jev label and confidence.
- When the owner confirms or overrides a row, it moves to **Closed** with the date, or the override task/PR.

Columns: Id · Date · Source (session/task or PR) · Question · Decided · How decided · Where recorded (file:line in the repo) · State (`open`, `owner-confirmed <date>`, `override → <task/PR>`).

## Open

| Id | Date | Source | Question | Decided | How decided | Where recorded | State |
|---|---|---|---|---|---|---|---|
| D-S26-Q2 | 2026-10-11 | S26 05.2.1-T1 PANEL-CHOICE | `planned_execution.execution_key`/`variant_hash` are NOT NULL but the byte layout belongs to T3.1, and case_id + normalized variant hash live inside Catalog. A: T1 derives them now (internal `ExecutionKey` in Orchestrator.Domain, documented length-prefixed layout, 2 additive read methods on `ICatalogContracts`); B: hash raw manifest params (diverges, rework later); C: stop and ask | A (provisional, panel continues) | jev choice C 0.62 / A 0.38, confidence 0.43, so below 0.7 → owner; A chosen to keep the flow (panel's recommendation, only additive contract) | plan 05.2.1 §10 Notes + PR FINAL-REPORT (file:line when posted) | open |
| D-S26-Q1 | 2026-10-11 | S26 05.2.1-T1 PANEL-CHOICE | Manifest shard ids are explicit, `RegisterSlotsAsync(runId, shardCount)` makes `shard-1..N`. A: pass shardCount, `shard_id = 'shard-<ordinal>'`, manifest label kept in `shard_plan`, no Ingestion change; B: explicit-id overload in 04.1.2 | A | jev choice A 0.97 | plan 05.2.1 §10 Notes + PR FINAL-REPORT | open |
| D-62 | 2026-10-11 | PR #62 06.1.1-T2 (merged b953d21, coordinator 5-min timeout) | Artifact record command decisions | (1) four additive 422 codes `object_key_not_allowed`, `kind_unknown`, `checksum_invalid`, `size_out_of_range` in ErrorCodes + 00 §9.2/§15; (2) the internal command does not check run existence (06.1.2-T1 owns it); (3) fallback sensitivity per README T57(a): trace, dom_snapshot, a11y_snapshot, patch sensitive; video, screenshot, raw_report, ui_hierarchy normal; (4) named codes produced by the handler, not the validator; (5) org-wide session (no project scope) refused with 403 `forbidden` | Panel [DECISION]s; timeout merge, jev reviews_cover_final 0.61, handed_over_not_dropped 0.72 | `plans/features/06-evidence-artifacts/06.1.1-artifact-record-and-server-chosen-keys.md:177-183`, `plans/00-overview.md:321`, `:528` | open |

## Closed

| Id | Date | Source | Decision | State |
|---|---|---|---|---|
| D-S25-A | 2026-10-10 | S25 07.3.1-T2 PANEL-CHOICE | No active policy revision → quarantine write allowed (jev A 0.98) | owner-confirmed 2026-10-11 (#58) |
| D-58 | 2026-10-11 | PR #58 | `quarantine:write` for `test_maintainer` only; error codes 422 `critical_case_not_quarantinable`, 409 `quarantine_conflict`; `expires_at` without upper bound | owner-confirmed 2026-10-11 |
| D-57 | 2026-10-11 | PR #57 | RR-1 (T9) and RR-2 (T77) accepted, owner Tech Lead; new `check-threat-model` stage | owner-confirmed 2026-10-11 |
| D-44 | 2026-10-08 | PR #44 | SEC-2 `error_summary` first line verbatim (bounded); unexpected pass → failed + xfail; `ext.attempt` dropped | owner-confirmed |
| D-S22 | 2026-10-10 | S22 refill | `event_slot_fk` moves from 04.1.2-T4.1 to 04.1.1-T1 | owner-confirmed 2026-10-10 |
