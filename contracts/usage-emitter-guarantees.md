# Usage ledger emitter guarantees (handoff to the roll-up)

What the emitter of `usage.batch_accepted` guarantees, so the roll-up and reconciliation (12.3.1-T5) can rely on it
without re-deriving it. Source of truth in code:
`src/Modules/Evidgate.Modules.Ingestion/Application/MeterEventBatchTaskHandler.cs`,
`src/Modules/Evidgate.Modules.Ingestion.Contracts/UsageBatchAcceptedContract.cs`,
`src/Modules/Evidgate.Modules.Usage/Infrastructure/Persistence/UsageLedgerStore.cs`. The proving suites are
`IngestUsageMeterTests` (all partial files) and, for the reprocess path, `ReprocessReportTaskTests.ProductionUsage.cs`,
both in `Tenancy.IntegrationTests`.

## 1. Dedupe key

- One ledger row per accepted `event_batch` per metric, key `batch:<event_batch_id>:<metric>` (V1: metric `event`,
  ledger metric `results`, unit `batch`). The table enforces `unique (org_id, billing_key)` and the insert is
  `ON CONFLICT DO NOTHING`.
- The key names the **original** batch. A resend of events that are all stored already accepts nothing: it records an
  `event_batch` receipt with `accepted = 0` (an audit row, never metered) and queues no usage row. A replay or
  reprocess of a stored report that inserts nothing adds no batch, no usage row and no ledger row.
- Quantity is the batch's own accepted count, re-read under the tenant's RLS; the task payload is only a claim and a
  disagreement fails the task.

## 2. Transaction boundary

- The metering outbox row is written in the same transaction as the batch commit (ingest route and report parse) for
  every batch that accepted at least one event; a rolled-back batch has none.
- The ledger row is written inside the metering task's tenant transaction. A failed attempt (including a failure after
  the insert ran) rolls the row and its duplicate tally back; the queue retries the whole task. A roll-up never sees a
  row of a failed attempt.
- Delivery is at-most-once per batch in the end: a task that exhausts the queue's retry cap is dead-lettered and its
  batch stays unbilled until an operator replays it.

## 3. Repeats

| Repeat                                             | Result                                                                             |
| -------------------------------------------------- | ---------------------------------------------------------------------------------- |
| Producer resends the batch                         | zero-accepted receipt only; no outbox row, task or ledger row                      |
| Task retried after failure                         | one row, written by the attempt that committed                                     |
| Same outbox row dispatched again                   | absorbed by the queue's task key; the handler does not run                         |
| Task re-run, or a second outbox row for the batch  | no new row; `usage_duplicate_attempt.attempts` counts it                           |
| Stored row differs from the payload (hash compare) | stored row kept, whole call rolled back, no tally, task completes, conflict logged |
| Metering month already issued                      | row re-dated into the next open month, `original_period` set, audited              |

## 4. What a roll-up may assume

- Summing `usage_ledger_entry.quantity` per `(org, project, period, metric)` never counts an accepted batch twice.
  It does not prove every accepted batch is billed: a reconciliation compares `sum(event_batch.accepted)` of metered
  batches with the ledger sum and treats dead-lettered `usage.batch_accepted` tasks and key conflicts as unbilled work.
- `usage_duplicate_attempt` is a diagnostic tally, never a quantity; it must not be added to the bill.
- A key that holds a different bill is a permanent conflict, not a duplicate: the stored row is the bill, even when it
  is lower than the accepted count. The conflict is not stored in a table; it is logged (events 4112 from the handler,
  2479 from the ledger store) and counted by the billing-key-conflict telemetry counter.
- Rows are append-only; corrections are adjustments, never updates of an emitted row.
