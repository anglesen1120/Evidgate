# Late-metered usage runbook

Status: **written with the read side of the re-dating change (12.3.1, D-S314-M1 = B); no staging drill yet.**

Legend as in [`../../AGENTS.md`](../../AGENTS.md): ✅ verified with a link · ⚠️ unverified · `[INFERENCE]` · `[DECISION]` · `[OPEN]`. Line pins below are this branch's; a symbol named without a line may still move.

## 1. Scope, audience and rules

**Purpose.** Recognise usage that was metered after its billing month was issued, explain why it bills in a later month, and verify that fact in the ledger, the audit chain and the worksheet — without editing a billing row.

**Audience.** The billing operator who reads the operator ingress (`GET /internal/operator/orgs/{o}/usage-ledger`, `…/invoice-worksheet` — `src/Evidgate.Api/Endpoints/UsageOperatorEndpoints.cs:53-55`), admitted by `IOperatorGate`, and the on-call engineer who is asked "why is October's usage on November's invoice".

**In scope.** Rows carrying `usage_ledger_entry.original_period` (nullable `YYYY-MM`; constraints `usage_ledger_entry_original_period_check` and `usage_ledger_entry_original_period_order_check` in `migrations/0094_usage_ledger_late_redate.sql`), the `usage.entry_redated` audit row, the `no_open_period` refusal, and the reads that surface the marker.

**Out of scope.** Issuing, replaying or refusing a statement (12.4.1), raising a quota (`POST /internal/operator/billing/orgs/{o}/quota-raise`, `UsageOperatorEndpoints.cs:61-66`), correcting a period (a correction is a new adjustment row written into an open period by the write path, never an edit), and every operator route outside `/internal/operator`.

**Rules.**

1. **Never `UPDATE` or `DELETE` a ledger row.** The app role holds neither privilege, and since `0092` both are refused for every role, the owner included, as soon as the row's period carries a statement (`migrations/0092_billing_issued_period_mutation_guard.sql:1-16`, trigger `:72-74`). A re-date is decided once, at insert; nothing rewrites it afterwards.
2. **A re-dated row is not a mistake to repair.** It bills the month it landed in, and `original_period` records where it was metered. Read both numbers before answering anyone.
3. **Never edit `period` or `original_period` by hand**, including as the owner: a hand write skips the trigger's lock, its probe and the audit row, and leaves the two months disagreeing with the statement chain.
4. **Name both months in any answer.** "October usage, billed in November" is the honest sentence; the amount belongs to November's statement, not October's.

## 2. What a re-dated row is, and why the walk is race-free

The BEFORE INSERT guard on `public.usage_ledger_entry` (`evidgate.usage_ledger_entry_period_issued_guard()`, `migrations/0087_usage_ledger_issued_period_guard.sql`) takes the shared advisory lock `hashtextextended(org_id || ':' || period, 0)` and probes `billing_statement(org_id, period)` before the row lands. Since `0094` the probe no longer refuses the insert (`migrations/0094_usage_ledger_late_redate.sql`, which replaces the same function): when the targeted month is already issued, the function keeps that month in `original_period`, walks forward one month at a time taking the same shared lock and probing each candidate, and assigns the first month with no statement to `period`. The month is incremented as text, so the calendar's own edges — `0000-01` and the rollover from `9999-11` to `9999-12` — keep their year. Statement inserts take the matching exclusive lock on the same key (`0087`'s `evidgate.billing_statement_period_lock()` trigger), and an issued statement is never removed, so the month it settles on cannot be issued between the probe and the commit. An insert that carries a non-null `original_period` at all — issued month or open one — is refused before the probe (errcode `23514`, constraint `usage_ledger_entry_original_period_supplied`): only the trigger decides the marker, and the message never repeats the value the writer supplied. A period that was never issued keeps the behaviour it had before `0094`: the guard returns the row unchanged.

**Cost, known limit.** One row pays one shared lock and one probe per issued month the walk passes, and a batch pays that per row: the walk is bounded by the calendar rather than by a step count, so an organization with a long issued chain makes a late write proportionally slower. That is the designed trade — the alternative is a row in a billed month — and it is not a fault to page on; a write that waits more than a few seconds on the walk shows up as the same advisory lock wait a statement insert shows.

## 3. What the operator sees

| Surface | What a re-dated row shows | Evidence |
|---|---|---|
| Ledger page, `GET /internal/operator/orgs/{o}/usage-ledger` | `period` = the month it bills in, `original_period` = the month it was metered for, `null` on every other row | `Infrastructure/Persistence/UsageReadStoreSql.cs:24-25` (select), `Application/Queries/UsageLedgerRead.cs:29-33` (wire shape), `:199` (mapping) |
| Invoice worksheet, `GET /internal/operator/orgs/{o}/invoice-worksheet` | the same two fields on the line, in `lines[]` (and on a correction in `adjustments[]` if one was re-dated) | `Infrastructure/Persistence/UsageReadStoreSql.cs:140-141`, `Application/Billing/InvoiceWorksheet.cs:31-34` (shape), `:304` (mapping) |
| Statement document (`content_hash`, `worksheet_json`) | `original_period` appears only on a re-dated row, so a period whose rows were never re-dated hashes exactly as it did before the field existed | `Application/Billing/InvoiceWorksheetCanonical.cs:112-116` |

All three paths above are under `src/Modules/Evidgate.Modules.Usage/`.

`GET /internal/operator/orgs/{o}/cost` aggregates by metric and period and does not carry the marker; it is unchanged (`src/Modules/Evidgate.Modules.Usage/Infrastructure/Persistence/UsageReadStoreSql.cs:158-165`). Both routes named above are on the operator ingress, which the served OpenAPI document, the SDK and the permission catalogue exclude by design (`src/Evidgate.Api/Endpoints/UsageOperatorEndpoints.cs:14-20`), so no contract row or generated client changes with this behaviour.

## 4. The audit row

Every re-dated row is audited as `usage.entry_redated` in the same transaction as the ledger write, one audit row per (month it was metered for, month it was assigned) pair, so a rolled-back caller takes its audit rows with it. The metadata names `original_period`, `period` and `count` — never an amount, a quantity or a billing key. Read it with the org's audit chain (`GET /v1/orgs/{o}/audit`, `audit:read`, filter `action=usage.entry_redated`) or directly:

```sql
select seq, action, metadata, created_at
  from public.audit_log
 where org_id = :org
   and action = 'usage.entry_redated'
 order by seq desc
 limit 50;
```

## 5. The `no_open_period` refusal

A re-date walks forward until it finds a month with no statement. If every remaining month from the requested one through `9999-12` — the last period the `YYYY-MM` form can name — is already issued, the insert is refused (errcode `23514`, constraint `usage_ledger_entry_no_open_period`) rather than silently landing somewhere: such an organization has no open month left to bill. The write path maps that to the typed refusal `no_open_period` on the command path (the store throws `LedgerNoOpenPeriodException`, `RecordUsageBatch` answers the conflict), and the batch is refused as a whole — nothing is dropped silently and nothing is written.

**The one exception to the duplicate rule.** The trigger runs before the insert's `ON CONFLICT (org_id, billing_key) DO NOTHING`, so the walk is settled before the key is compared. A batch whose keys are all already on file therefore answers `duplicates` only while the walk can name a month: a replay of an already-stored key whose requested month's chain is exhausted at `9999-12` gets the same named `no_open_period` refusal instead, and nothing is written either way.

**Recovery.** An issued statement is never deleted and never reopened (`0092`), so no month of the chain becomes open again: re-issuing, editing or deleting a statement is not a recovery, and neither is a replay. A task whose month still has an open month after it succeeds on the next attempt; a row whose chain is exhausted at the calendar's end has no month left to bill, so it stays refused until an owner decides about those statements, and the refusal — not a retry — is what to investigate.

## 6. Verification

```sql
-- the marker itself, for one org and month
select period, original_period, metric, count(*)
  from public.usage_ledger_entry
 where org_id = :org
   and (period = :period or original_period = :period)
 group by period, original_period, metric
 order by period, original_period nulls first, metric;

-- a row can never name a later month as its origin
select id, period, original_period
  from public.usage_ledger_entry
 where org_id = :org and original_period is not null and original_period >= period;
```

The second query returning a row is a defect, not a data-cleanup task: capture the ids, the `usage.entry_redated` rows for that month, and the statement hashes, and escalate before touching anything.

## 7. Evidence table

| Claim | Evidence |
|---|---|
| The insert guard locks and probes the named month | `migrations/0087_usage_ledger_issued_period_guard.sql:23-48` |
| Statement inserts take the exclusive counterpart | `migrations/0087_usage_ledger_issued_period_guard.sql:58-76` |
| The column exists with its format and order checks | `migrations/0094_usage_ledger_late_redate.sql` (`usage_ledger_entry_original_period_check`, `usage_ledger_entry_original_period_order_check`) |
| The insert guard re-dates a late row, refuses a supplied marker and stops when every remaining month is issued | `evidgate.usage_ledger_entry_period_issued_guard()` in `migrations/0094_usage_ledger_late_redate.sql` |
| An issued period's rows cannot be updated or deleted | `migrations/0092_billing_issued_period_mutation_guard.sql:23-74` |
| The ledger page returns the marker | `src/Modules/Evidgate.Modules.Usage/Infrastructure/Persistence/UsageReadStoreSql.cs:24-25`; `Application/Queries/UsageLedgerRead.cs:29-33,199` |
| The worksheet returns the marker | `src/Modules/Evidgate.Modules.Usage/Infrastructure/Persistence/UsageReadStoreSql.cs:140-141`; `Application/Billing/InvoiceWorksheet.cs:31-34,304` |
| The canonical document writes it only when present | `src/Modules/Evidgate.Modules.Usage/Application/Billing/InvoiceWorksheetCanonical.cs:112-116,135-146` |
| Operator routes are outside the served contract and the SDK | `src/Evidgate.Api/Endpoints/UsageOperatorEndpoints.cs:14-20` |
| Re-dating, the audit action and the refusal live in the write path | `src/Modules/Evidgate.Modules.Usage/Application/Commands/RecordUsageBatch.cs`; `src/Modules/Evidgate.Modules.Ingestion/Application/MeterEventBatchTaskHandler.cs` |
