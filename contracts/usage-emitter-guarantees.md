# Usage ledger emitter guarantees (handoff to the roll-up)

What the emitter of `usage.batch_accepted` guarantees, so the roll-up and reconciliation (12.3.1-T5) can rely on it
without re-deriving it. Source of truth in code:
`src/Modules/Evidgate.Modules.Ingestion/Application/MeterEventBatchTaskHandler.cs`,
`src/Modules/Evidgate.Modules.Ingestion.Contracts/UsageBatchAcceptedContract.cs`,
`src/Modules/Evidgate.Modules.Usage/Infrastructure/Persistence/UsageLedgerStore.cs`. The proving suite is
`IngestUsageMeterTests` (all partial files, `Tenancy.IntegrationTests`).

## 1. Dedupe key

- One ledger row per accepted `event_batch` per metric, key `batch:<event_batch_id>:<metric>` (V1: metric `event`,
  ledger metric `results`, unit `batch`). The table enforces `unique (org_id, billing_key)` and the insert is
  `ON CONFLICT DO NOTHING`.
- The key names the **original** batch. A resend of the same events accepts nothing, so it has no batch and no row. A
  reprocess of a stored report writes no `event_batch` and enqueues no usage row.
- Quantity is the batch's own accepted count, re-read under the tenant's RLS; the task payload is only a claim and a
  disagreement fails the task.

## 2. Transaction boundary

- The metering outbox row is written in the same transaction as the batch commit (ingest route and report parse), so a
  committed batch always has its row and a rolled-back one has none.
- The ledger row is written inside the metering task's tenant transaction. A failed attempt (including a failure after
  the insert ran) rolls the row back; the queue retries the whole task. A roll-up never sees a row of a failed attempt.

## 3. Repeats

| Repeat                                             | Result                                                                          |
| -------------------------------------------------- | ------------------------------------------------------------------------------- |
| Producer resends the batch                         | no new batch, outbox row, task or ledger row                                    |
| Task retried after failure                         | one row, written by the attempt that committed                                  |
| Task or outbox row redelivered                     | no new row; `usage_duplicate_attempt.attempts` counts it                        |
| Stored row differs from the payload (hash compare) | stored row kept, whole call rolled back, no tally, task completes, error logged |
| Metering month already issued                      | row re-dated into the next open month, `original_period` set, audited           |

## 4. What a roll-up may assume

- Summing `usage_ledger_entry.quantity` per `(org, project, period, metric)` counts each accepted event once.
- `usage_duplicate_attempt` is a diagnostic tally, never a quantity; it must not be added to the bill.
- A key that holds a different bill is a permanent conflict, not a duplicate: the stored row is the bill, and the
  conflict is visible only in the log (event 4112), not in a table.
- Rows are append-only; corrections are adjustments, never updates of an emitted row.
