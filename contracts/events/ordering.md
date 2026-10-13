# Event ordering, restart and sequence-gap contract

`POST /v1/runs/{r}/events` (00 §9.3) commits one producer batch of ≤ 500 event envelopes
(00 §9.4; the schema is `contracts/schemas/event-envelope.v1.json`) in one transaction. This
document is the ordering half of that contract, delivered by **04.1.1-T4** (story 04.1.1 §6/§10,
rulings T13/T84/T91), and it is the source for anyone reading the `event` table:

- `migrations/0034_ingestion_event_and_batch.sql` §4 — the storage and the unique keys,
- `src/Modules/Evidgate.Modules.Ingestion/Application/Commands/IngestEventBatch.cs` — the handler,
- `contracts/schemas/event-envelope.v1.json` — the envelope, including `occurred_at`.

## 1. Arrival order is storage order; nothing is ever reordered

- The batch is inserted by **one** `insert … select from jsonb_to_recordset(@events)` statement, in the
  request's array order (04.1.1-T2, P2). Every row's `event.id` is a **server-minted UUIDv7**.
  **Nothing may depend on `id` for order.** A UUIDv7's sub-millisecond bits are not a documented monotonic
  sequence, so `order by id` only _approximates_ arrival order across millisecond-separated moments (two
  different requests) and promises **nothing** about the events of one batch — minting order inside a
  millisecond is an implementation detail of the generator, not a contract. Within a batch every row also
  shares one `received_at` (the statement's `now()`), so no stored column orders the rows of a batch: their
  request order is reproducible only from the request body itself.
- Batch boundaries are read from `received_at`, never from `id`: all rows of one accepted batch carry the
  same `received_at` (one transaction), and successive batches of a run carry non-decreasing ones.
- `event.sequence` and `event.received_at` keep what the producer sent / the server wrote; ingest never
  rewrites, renumbers or reorders them.
- Out-of-order arrivals are stored **as sent**: a batch may carry a `sequence` below an earlier batch's
  high-water mark, a batch may split one producer's run of sequences across several batches, and a
  `sequence` hole may be sent. None of these is a refusal. The only sequence-related refusal is a
  **reused** `(run_id, producer_id, sequence)` — the unique key
  `event_run_producer_sequence_key`, refused `422 sequence_reused` with the failing indices (AC6). A resend
  of the identical batch is `duplicate` per `event_id` (AC2), never a sequence error.
- `occurred_at` is the producer's **runner clock** and `received_at` is the server's write time. Both are
  informative metadata: **neither ever orders execution, reconciliation or replay**. Only
  `(producer_id, sequence)` does (below).

## 2. Readers sort by `(producer_id, sequence)`

Every reader of stored events — 05.3.2's reconciler, 04.2.3's replay, 07.5.1's re-evaluation — orders by
`(producer_id, sequence)` within a run. That order is served by the unique key
`event_run_producer_sequence_key (org_id, run_id, producer_id, sequence)`; it is left-prefixed by
`(org_id, run_id)`, so the per-run read needs no second index (0034 §4).

A reader that needs the batch boundaries of the ingest itself (which run of events arrived together)
orders by `received_at` and `id` — those are the batch's own metadata, not an execution order.

## 3. Sequence-gap detection is derived, never stored (no migration)

`producer_slot.last_sequence` is the **recorded** reconciliation anchor: the handler sets it to
`greatest(last_sequence, max(sequence of the events this batch newly accepted))` for every producer whose
batch accepted at least one event (`UpdateSlotSql`, statement (h)). It is monotone and never moves back,
and a batch with `accepted = 0` (an all-duplicate resend) does not touch it.

There is deliberately **no** gap column and no gap flag. A consumer (05's reconciliation, plan 05 §4)
derives the gap from what is on file. Sequences need not start at 1 — the envelope allows
`sequence >= 0` (`contracts/schemas/event-envelope.v1.json`, `sequence.minimum` 0) and
`event_sequence_check` matches it — so the rule is stated base-independently:

> For a `(run_id, producer_id)`, with `min_stored` the lowest stored `event.sequence` and `last_sequence`
> the slot's high-water mark, a gap exists while
> `count(distinct event.sequence) < last_sequence - min_stored + 1` — i.e. some sequence in
> `[min_stored, last_sequence]` was never stored. A hole _below_ `min_stored` (an unwritten prefix: the
> producer simply started higher) is **not detectable** from these columns and is not reported.

For a producer whose first sequence is 1 this is the familiar `last_sequence > count(distinct sequence)`;
for 0-based producer (0, 1, 3 with a high-water mark of 3) it reports the missing 2.

That comparison uses only stored columns, so it survives a restart, a re-upload and a replayed batch, and
it needs no migration. Ingest records the high-water mark and **never rejects a batch for a gap**: a hole
is a reconciliation fact, not an ingest error (04.1.1 §6/§10 T4.1).

## 4. Restart semantics (T84)

- A restart reuses the **same deterministic `producer_id`** (T54); the producer resumes from its own
  checkpoint, i.e. its next batch continues at `checkpoint + 1`, and that batch is accepted normally — the
  stored `producer_slot.last_sequence` is the checkpoint the reconciliation compares against.
- A batch under a `producer_id` the run's manifest did not declare has no `producer_slot` row and is
  refused `422 unknown_producer` with no write (`event_slot_fk`, 0029/0034; the handler answers it from
  the slot lookup).
- Slot binding (T9) is unchanged by a restart: the slot stays bound to the token that first wrote it, and
  a different identity is `403 producer_slot_taken`.

## 5. First-event run transition and its lock order (T13, T91, T101)

The first accepted batch of a `queued` run drives `queued → running` through 05's
`IRunStateTransition` (00 §8.1, T13), inside the same transaction, so a refused batch leaves the run
`queued` and a canceled or complete run is never moved.

Lock order (the reason this handler holds no shared lock when it transitions):

1. one **unlocked**, RLS-scoped read of `run.state`;
2. **only when the state is `queued`**, the transition — which loads the run row `FOR NO KEY UPDATE` —
   runs **before** any shared read;
3. then the `FOR SHARE` scope read (`IRunEventScope`), which is self-compatible with the lock this
   transaction already holds.

Taking the shared read first and upgrading to `FOR NO KEY UPDATE` afterwards would deadlock (40P01)
between two concurrent first batches, because each would wait for the other's shared lock. With this
order two racing first batches give **one winner and one no-op** (the idempotent transition of T82), and a
batch on a `running`, `collecting`, `complete`, `incomplete` or `canceled` run skips the transition
entirely, so the ingest hot path keeps its single `FOR SHARE` read (T91/T101) and a run is never
re-transitioned.
