# Baseline worksheet (template)

**Template only: no pilot rows yet (12.2.1-T2.2).** Copy this file per pilot and sprint into `docs/pilots/<pilot>/baseline/sprint-1.md` and `sprint-2.md` when a pilot is signed.

Definitions: `docs/pilots/metric-definitions.md` is v1 and unchanged. If it has changed since the first row of any baseline, the baseline is invalid and must be re-cut (v1 freeze rule, section 3 of that file).

## Rules for every cell

- Never 0 for an unknown: write `null` and a reason in the Reason column (T60). Allowed example reasons: `no product before enablement`, `customer did not keep a timesheet`, `export not provided`.
- Self-reported values say `self-reported` in the Source column.
- Only aggregates and opaque ids are committed. Raw customer exports (ticket exports, timesheets, CI history dumps) stay in the pilot's own storage, are referenced here by a description of where they are, and are redacted: no names, emails, tokens, credentials, or unredacted artifacts. Do not ask a pilot to send credentials.
- `failure_ref` is an opaque sequential id (for example `F-001`), never a person, ticket id, hash or title; the mapping to real tickets stays in the pilot's own storage.
- No URLs, hostnames, repo or branch names, or test titles in any cell. Personal data or secrets seen in customer material are not copied; they are filed back to the customer.
- "Raw measurements" in story AC2 means the derived per-failure minutes and per-sprint hours in this sheet, not the exports.
- Null handling: M1's median is over non-null rows only with n reported; no non-null rows gives `null`; `0` is only a measured zero.
- At pilot exit or on a deletion request, per-failure rows are purged or redacted per the agreement's data-handling terms.

## Header

| Field | Value |
|---|---|
| Pilot | `<pilot id>` |
| Sprint | `sprint-1` or `sprint-2` |
| Window | `<start>` to `<end>` |
| Definitions version | v1 |
| Raw export location (description only) | `<where it lives outside the repo>` |

## Metric rows

One row per metric of `metric-definitions.md` section 1.

| Metric | Value | Source | Cadence | As of | Reason (if null) |
|---|---|---|---|---|---|
| M1 Triage time per failure (median) | `<minutes or null>` | `<ticket system or self-reported>` | weekly | `<date>` | |
| M2 Test-maintenance time | `<hours or null>` | `<customer timesheet>` | per sprint | `<date>` | |
| M3 Repeated incidents handled correctly | `null` | product labels | monthly | `<date>` | no product before enablement |
| M4 Retention | `null` | product usage | monthly | `<date>` | N and window not fixed (pilot agreement) |
| M5 Contribution margin | `null` | ledger + cost model | monthly | `<date>` | revenue (O5) or ledger/cost model not available |
| M6 Heal rate | `null` | product | monthly | `<date>` | no product before enablement |
| M7 Missed bugs | `null` | customer labels | monthly | `<date>` | no product before enablement (or a historical count with its source) |

M6 is only shown next to M7 (guardrail pair).

## Per-failure triage rows (feeds M1)

| failure_ref | Reported at | Label + owner set at | Minutes | Source | Reason (if null) |
|---|---|---|---|---|---|
| `F-001` | `<timestamp>` | `<timestamp>` | `<n or null>` | `<ticket system or self-reported>` | |
