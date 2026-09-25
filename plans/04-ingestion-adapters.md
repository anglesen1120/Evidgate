# 04 - Ingestion and Adapters (M1-M4)

> Contracts: `00-overview.md` §7 (ids/hashes), §8.3-§8.4 (attempt status, artifact), §9.3-§9.4 (endpoints, event envelope), §10 (adapter protocol), §11 (NFR).
> Facts: `../DECISIONS.md` §3 (A9, A10), §4 (stack, engines, licence rule), §5 (healing is out of this plan), §6 F9 (JUnit XML has no single standard), §9 (R10 dogfooding, capacity arithmetic).
> Source: design §4 (runner and adapter contract, capability, isolation), §5 (run lifecycle sequence), §6 (API event/artifact, envelope), §7 (consistency and recovery: spool, order, finalize, reprocess vs rerun).
> Level: high-level plan; each story gets a detailed plan in `plans/features/` (00 §12) before build.

## 1. Purpose and scope

**Goal:** get a customer's real test results from CI (and later from an agent) into Evidgate as **verified, ordered, deduplicated events** plus raw reports, with adapters that map each producer's format without guessing.

**In scope:** the event ingest API (00 §9.4), the `evidgate` CLI (`@evidgate/cli`), the Playwright reporter adapter, the JUnit XML mapper (pytest `junitxml` first), golden fixtures and the conformance harness, the capability/protocol contract (00 §10), usage events for accepted results, and the V1/M2 reprocess path for stored raw reports.

**Out of scope:** evidence completeness verdicts and the evidence freeze result (06) — `evidence_revision` rows are persisted by 05 — policy evaluation and decisions (07), the artifact object lifecycle (06), agent execution (05), healing (08), pricing (12).

**Version split.** V1 = event API, CLI, Playwright reporter, JUnit mapper, fixtures, protocol v1, in `ingest_only` and CI mode. V1/M2 = reprocess of a stored raw report, usage events per accepted result. V2 = Appium/mobile mapping (Android conditional, 00 §1.3). V3 = nothing owned here (iOS/hosted runners are 05).

## 2. Requirements

- **FR-04-1 (atomic batch)** `POST /v1/runs/{r}/events` validates the whole batch, canonicalises it and commits it in one transaction; the response acknowledges each event as `accepted` or `duplicate` (00 §9.4). A schema error, an `execution_key` that is not a planned execution of the run (422 `unknown_execution_key`, T10), or an unknown `producer_id` (422 `unknown_producer`, T9) rejects the **whole** batch with the failing indices; the same `event_id` with a different payload rejects the batch 409 `duplicate_different_payload` (T50).
- **FR-04-2 (limits)** ≤ 500 events and ≤ 1 MiB **before decompression** per request. `Content-Encoding: gzip` is accepted under an expanded-size and ratio cap; exceeding any cap → 413 `batch_too_large` without partial commit.
- **FR-04-3 (producer sequence)** `(run, producer_id, sequence)` is unique and never reused; a restarted shard uses a new `producer_id` or resumes from a checkpoint. Order is by `(producer_id, sequence)`, never receive time. `producer_id` must be a registered `producer_slot` of the run (T9): run create writes one slot per manifest shard (contract in `Evidgate.Modules.Ingestion.Contracts.IProducerRegistry.RegisterSlotsAsync`, invoked by 05's run-create command inside the same tenant transaction); the first accepted write binds the slot to the caller's token identity (`bound_token_jti`, plus `bound_ci_run_ref` from the GitHub `run_id`/`run_attempt` claims when present, which must match the run's declared CI reference). An unknown producer → 422 `unknown_producer`; a write or `finalize` under a slot bound to another identity → 403 `producer_slot_taken`.
- **FR-04-4 (auth)** CI authenticates with a GitHub OIDC JWT or a project ingest token exchanged at `POST /v1/ci/token` for a short-lived `ci_producer` token scoped to one project (02 owns minting; this plan owns the client side and the failure paths).
- **FR-04-5 (no blind rerun)** On upload failure the CLI retries the same events with the same `event_id`s until its deadline and never re-runs business logic (DECISIONS A9; source §7 "CI mất mạng").
- **FR-04-6 (mapping fidelity)** Each adapter maps to the raw status **as sent** plus a normalised status (00 §8.3); the mapper never overwrites the raw value, and every normalised row records `parser_id` + `parser_version`.
- **FR-04-7 (raw report kept)** The raw report object is stored before parsing and is the input for reprocess; reprocess with a new parser version creates a **new evidence revision** and is distinct from a rerun (source §7).
- **FR-04-8 (capability gate)** An adapter declares a capability document (`protocol.major/minor`, engine family/version, platforms, `supports`, `mode`); the server rejects an incompatible major and never gate-qualifies `ingest_only` results without a proven complete manifest (00 §10).
- **FR-04-9 (usage)** Every accepted batch is metered **once per batch per metric** (quantity = event count) as `usage_ledger_entry` rows with idempotent `billing_key` `batch:<event_batch_id>:<metric>` (T43, T50), so duplicates, retries and redelivery never bill twice (source §7 "ACK bị mất").
- **NFR-04-1** p95 < 1 s per batch within limits; 50 000 events in 60 s sustained with batching (00 §11, 10 load test).
- **NFR-04-2** CLI/adapters run on Node 22+ on Linux, macOS and Windows (00 §11).
- **NFR-04-3** Untrusted input (report XML, attachment names, ext fields) is parsed defensively: no external entities, bounded depth and size, no code execution (11).

**Acceptance criteria (testable):** resending a committed batch returns `duplicate` for every event and creates no new rows and no new billing row; a batch with one bad event returns 422 with that event's index and commits nothing; a batch containing an `execution_key` not planned in the manifest returns 422 `unknown_execution_key` with the failing indices (T10); a write or `finalize` under a `producer_id` that has no slot returns 422 `unknown_producer`, and under a slot bound to another token identity returns 403 `producer_slot_taken` (T9); two batches from the same producer with a reused sequence reject the second; a gzip bomb of 50 MiB expanded from 20 KiB returns 413; a pytest report whose XML is truncated yields raw statuses preserved, one `unknown` normalised status and `completeness = incomplete`; events for a `canceled` run are accepted and marked `after_cancel = true` and the run never completes (T12); reprocess of the stored raw report creates a new evidence revision with the new `parser_version` and leaves the original revision untouched.

## 3. Key decisions

| # | Decision | Rationale | Rejected alternative |
|---|---|---|---|
| D1 | One batch endpoint, per-event `accepted`/`duplicate` ack, batch-level rejection | Client needs a deterministic retry unit; a partial commit would make count reconciliation (05) unreliable | Per-event 200/4xx (chattier, ambiguous partial state) |
| D2 | `event_id` + canonical payload hash for dedupe; `billing_key` = `batch:<event_batch_id>:<metric>` (one ledger row per batch per metric, T43) | At-least-once delivery (A10); no double count on ACK loss (source §7) | Server-assigned ids (break client-side retry after a lost response) |
| D3 | Order by `(producer_id, sequence)`; out-of-order arrivals are stored, not reordered by arrival | Runner clocks are informative only; receive time is not ordering | Sort by `occurred_at` |
| D4 | Raw report is always stored, and the parser identity is a column, not a code constant | Reprocess must be reproducible and auditable; F9 says formats differ per producer | Parse on the fly, discard raw (loses the evidence trail and blocks reprocess) |
| D5 | Golden fixtures per producer, checked against the **original** report | Adapter bugs must be caught by us, not by a customer (00 G10/R10) | Unit tests with synthetic events only |
| D6 | Capability/protocol as files in `contracts/adapter/` | Adapters must be implementable outside TypeScript (00 §4 contracts rule) | TS-only interfaces |
| D7 | `ingest_only` mode is first-class and explicitly not gate-eligible | Customers with unsupported engines still get value; the gate must stay honest | Refuse unsupported producers entirely |
| D8 | Adapter logic pins no engine version of its own; the engine version travels in the capability document and (V2) in the immutable JobSpec/image digest | DECISIONS §4 "Evidgate never pins latest inside runners"; evidence reproducibility stays in the JobSpec, not in the reporter package | Reporter tries to detect the Playwright version at runtime |

## 4. Design

**Module.** `src/Modules/Evidgate.Modules.Ingestion` owns parsing, staging and dedupe (commands/handlers per 00 §5.5; writes EF Core 10, reads Dapper); `Evidgate.Modules.Ingestion.Contracts` holds the interfaces other modules call (`IProducerRegistry`, the artifact/revision callouts). `event` rows are read by 06 (evidence) and 07 (policy) through views, never joined by other modules' tables.

**Tables owned (high level).**

| Table | Key columns | Constraints |
|---|---|---|
| `event` | `id`, `org_id`, `run_id`, `event_id` (producer-supplied, dedupe identity — distinct from server `id`), `producer_id`, `sequence`, `job_id`, `epoch`, `execution_key`, `attempt_no`, `type`, `raw_status`, `normalized_status`, `duration_ms`, `occurred_at`, `received_at`, `payload_hash`, `payload` (jsonb `ext`), `artifact_ids[]`, `after_cancel` bool | unique `(org_id, id)` (T3); unique `(org_id, run_id, producer_id, sequence)`; unique `(org_id, run_id, event_id)`; composite FKs `(org_id, run_id)` and `(org_id, run_id, producer_id)` → `producer_slot`; plain table - time partitioning needs these unique keys redesigned first (§8) |
| `producer_slot` | `id`, `org_id`, `run_id`, `shard_index`, `producer_id`, `bound_token_jti`, `bound_ci_run_ref`, `last_sequence`, `closed_at` | unique `(org_id, id)` (T3); unique `(org_id, run_id, producer_id)`; inserted by 05's run-create command via `IProducerRegistry.RegisterSlotsAsync` in the same tenant transaction (T9) |
| `event_batch` | `id`, `org_id`, `run_id`, `producer_id`, `event_count`, `bytes_in`, `bytes_expanded`, `accepted`, `duplicate`, `request_id` | audit of every request; keeps rejection reasons out of `event` |
| `raw_report` | `id`, `org_id`, `run_id`, `producer_id`, `artifact_id`, `producer_kind`, `parser_id`, `parser_version`, `parsed_at`, `status` (`parsed`\|`malformed`\|`unsupported`) | one row per parsing; reprocess adds a row, never overwrites |
| `report_parser` | `parser_id`, `version`, `producer_kind`, `schema_version`, `notes` | registry; parser identity is data |
| `adapter_capability` | `id`, `org_id`, `name`, `protocol_major`, `protocol_minor`, `engine_family`, `engine_version`, `platforms[]`, `supports` (jsonb), `mode`, `first_seen_at` | unique `(org_id, name, protocol_major, protocol_minor, engine_version)` |

`artifact` and the evidence **freeze** result stay with 06; `usage_ledger_entry` (12) and `evidence_revision` (05, persisted by 05 from 06's freeze result) are **not** owned here; this plan calls their interfaces. `idempotency_record` and the `task` queue table are owned by 01.

**Endpoints.**

| Endpoint | Owner | Notes |
|---|---|---|
| `POST /v1/ci/token` | 02 (client side here) | CLI exchanges OIDC JWT or ingest token |
| `POST /v1/runs/{r}/events` | **04** | 00 §9.3/§9.4; permission `event:write` |
| `POST /v1/runs/{r}/finalize` | **04** (semantics + reconciliation in 05) | producer, shard, `last_sequence`, `count`, `manifest_hash` → 202; rejected 403 `producer_slot_taken` unless the caller is the token identity bound to the slot (T9) |
| `POST /v1/artifacts/upload-url`, `POST /v1/artifacts/{a}/complete` | 06 (CLI side here) | adapter uploads traces/videos/raw reports |
| `POST /v1/runs/{r}/reprocess` | **04** (new; see §13) | raw report + `parser_version` → new evidence revision |
| `GET /v1/runs/{r}/reports/{report_id}/comparison` | **04** | permission `run:read`; parsed events per case vs the raw report's declared counts/ids with discrepancy reasons; raw bytes only via the `raw_report` artifact id + `download-url` (`artifact:read`, sensitivity gate) (00 §9.3, 04.4.1-T6) |

**Flows.**

1. *CI run.* CLI `evidgate manifests upload` → `runs create` (Idempotency-Key) → engine runs with the reporter → reporter emits envelopes → CLI spools, batches and posts → CLI uploads artifacts → CLI `finalize`. The whole path is restartable: the spool is a file on disk, keyed by `(run_id, producer_id, sequence)`.
2. *Batch ingest* (`IngestEventBatch` command handler). Decode (gzip, if any) → enqueue-limit check → validate against the envelope schema (Corvus.Json.Validator; the Ajv 8 validator lives in the TypeScript CLI and both run the same `fixtures/contracts/` accept/reject corpus in CI — any disagreement fails both builds, T42) → canonicalise (00 §7 RFC 8785, `Evidgate.Canonicalization`) → hash → resolve the caller's producer slot and apply the binding rule (register identity on first write; 403 `producer_slot_taken` / 422 `unknown_producer`, T9) → check every `execution_key` against the run's planned executions (422 `unknown_execution_key` with indices, T10) → insert `event` rows in **one round trip**: `INSERT … SELECT FROM unnest(@arrays) ON CONFLICT (org_id, run_id, event_id) DO NOTHING RETURNING event_id`, then one select of the conflicting `event_id`s to compare payload hashes (same hash → acked `duplicate`; different hash → 409 `duplicate_different_payload` reject of the whole batch; a `(run, producer, sequence)` violation raises and is mapped to 422 `sequence_reused`) (T50, 00 §5.7 P2) → update the slot's `last_sequence`, insert the `event_batch` row and one outbox row carrying per-metric counts; usage metering is emitted once per batch per metric (`billing_key = batch:<event_batch_id>:<metric>`), never one ledger row per event (T43, T50) → return per-event acks in the same order as sent.
3. *Raw-report path (ingest_only / JUnit producers).* `upload-url` → PUT raw XML → `complete` → parse with `parser_id`/`parser_version` → mapped events (synthetic `producer_id`) → `raw_report` row → reconciler trigger.
4. *Reprocess.* Select a `raw_report` row → re-parse with a newer `parser_version` → new `event` set (new producer + sequences) → 06's freeze result → 05 persists a **new** `evidence_revision`; the previous revision stays queryable. Rerun (05) remains a different action with its own id.

**States.** Producer slot: `open → closed` (`closed_at` set by `finalize`); a `stale` marker on foreign-epoch events. Raw report: `parsed | malformed | unsupported`. Event rows are append-only. Item states follow 00 §8.3 (`passed | failed | skipped | timed_out | error | blocked`).

**Adapter mapping (Playwright).** Verified from the reporters page ✅ ([playwright.dev/docs/test-reporters](https://playwright.dev/docs/test-reporters), read 2026-09-24): a custom reporter implements `onBegin(config, suite)`, `onTestBegin(test, result)`, `onTestEnd(test, result)`, `onEnd(result)` with types from `@playwright/test/reporter`; built-in reporters document retry/flaky outcomes and a blob reporter whose file names carry the shard number and that exists to merge sharded runs; the JUnit reporter has an `includeProjectInTestName` option, so projects are a first-class axis. ⚠️ Not opened in this task: the `--shard` CLI syntax page (`/docs/test-sharding`), the exact `TestResult.retry` field name, and the attachment field names (the perfetto reporter docs mention "paths to the attachment files"). Mapping: annotation `evidgate.id` → `case_id`; project + normalised parameters → `variant_hash`; retry index + 1 → `attempt_no` `[INFERENCE]`; `status` values map to `passed | failed | timed_out | skipped | interrupted→error` with the raw string kept; skips distinguish intended `skip` from `fixme` (the latter never counts as an allowed skip, 00 §8.6); attachments map to artifacts by MIME and size policy of 06; one producer per shard, sequence per shard. Test Agents' "healer produces a skipped test" (F1) is **not** accepted as evidence of passing.

**Adapter mapping (JUnit XML, pytest first).** F9 ✅: JUnit XML has no single cross-producer schema, so mapping is per producer and versioned. `<testcase classname>/<name>` → case identity hint, `<failure>` → `failed`, `<error>` → `error`, `<skipped>` → `skipped`, `time` → `duration_ms`; `testsuites/testsuite` counters are recorded as *declared* counts and compared with observed ones (a mismatch is a completeness input, not a silent pass). Parsing: external entity resolution and DTD loading disabled, entity-expansion and depth caps, size cap, UTF-8/UTF-16 + BOM handling, control characters normalised; malformed XML is stored raw with `status = malformed` and yields `unknown` statuses (never PASS, 00 §8.6).

## 5. Failure modes and edge cases

| Case | Handling | Invariant kept |
|---|---|---|
| ACK lost, client resends | Same `event_id` + same canonical hash → `duplicate`, no new row, no new billing row | No double count (source §7) |
| Same `event_id`, different payload | Whole batch 409 `duplicate_different_payload` with `index` (T50) | No silent overwrite |
| Sequence reused after restart | Second batch's `(run, producer, sequence)` insert violates the unique key → whole batch 422 `sequence_reused`; CLI must mint a new `producer_id` | `(run, producer, sequence)` unique |
| `producer_id` with no registered slot | Batch rejected 422 `unknown_producer` (T9) | Only manifest shards write |
| Write/finalize under a slot bound to another token | 403 `producer_slot_taken` (T9) | A leaked token cannot forge a shard |
| Event with an `execution_key` not in the manifest | Whole batch 422 `unknown_execution_key` with indices (T10) | Forged/unplanned events never stored |
| Events arrive out of order | Stored by identity; readers sort by `(producer, sequence)` | Receive time never orders execution |
| Body > 1 MiB or > 500 events | 413 before any write | No partial commit |
| gzip bomb | Expanded-bytes/ratio cap → 413, connection closed, counter incremented | Bounded memory |
| Finalize before events | Finalize recorded; reconciliation waits for count/sequence (05) | Missing data never completes |
| Run canceled while uploading | Events for a canceled run are accepted and marked `after_cancel = true`; the run stays `canceled` (T12) | Canceled never becomes complete |
| Foreign epoch (old worker alive) | Stored with the foreign epoch as stale evidence | Never completes the current job |
| Malformed/truncated XML | Raw stored, `malformed`, `unknown` statuses | Never PASS on unknown |
| Unsupported producer version | Registry miss → `unsupported`, raw kept, run `incomplete` with `missing[]` reason | Honest incompleteness |
| Attachment name/path traversal or bad MIME | Rejected at `upload-url`/`complete` with a filtered reason | No path or header injection |
| Clock skew between runners | `occurred_at` stored but never used for ordering; durations from monotonic clocks | Reprocessable timeline |

## 6. Security and privacy

- `ci_producer` tokens are minted per CI job, short-lived, scoped to one project; the project in the URL must match the token scope → 403 (02 + 00 §9.1).
- Presigned upload URLs are bearer credentials with a short expiry and a server-chosen key; the client never picks an object key (06).
- XML parsing runs with external entities, network access and DTDs disabled; parser input is size- and depth-capped; the parser runs with the same RLS-scoped application role.
- Events carry raw producer `ext` payloads: fields are allow-listed per adapter; accepted `ext` keys are recorded in the capability document; secret-looking keys are dropped at the boundary and never logged.
- Artifacts can contain personal data (DECISIONS §7): the CLI refuses to attach files outside its allowed directories, and sensitivity classification is set at `upload-url` time (06, 11).
- Tenant isolation: every row carries `org_id`; composite FKs and RLS per 00 §5.4 (`ITenantScope.BeginAsync` + parameterised `set_config`, FORCE RLS); ingestion must pass the cross-tenant suite in 11.
- Logs and metrics carry `request_id`, `run_id`, `producer_id` and counters only (bytes, rejected indices); never event bodies or headers.

## 7. Test strategy

- **Behaviours:** a committed batch is idempotent end to end (resend → all `duplicate`); out-of-order delivery reproduces the same normalised timeline; a shard restart with a new `producer_id` is accepted while a reused sequence is not.
- **Boundaries:** exactly 500 events and exactly 1 MiB pass; 501 events / 1 MiB + 1 byte fail; gzip ratio just under and just over the cap; long Unicode names, surrogate pairs, 4-byte emoji in `test.title` and XML attributes; empty suite (declared zero cases, 00 §8.6).
- **Failure cases:** truncated `</testcase>`, mixed encodings, XML with a DOCTYPE/entity, an event with an unknown `type`, an event with an `execution_key` absent from the manifest (batch reject 422 `unknown_execution_key` with indices, T10), a write under an unknown `producer_id` (422 `unknown_producer`) or under a slot bound to another token identity (403 `producer_slot_taken`, T9), attachments over the size cap, 429 with `Retry-After` (CLI honours it), a 500 from the API mid-batch (CLI retries the same batch).
- **Fixtures:** `fixtures/<producer>/<case>/` holds the original report plus the expected normalised events; the conformance harness diffs expected vs produced (per-producer golden set, D5). First fixture is Evidgate's own CI run (R10 ✅): Playwright reporter output, a pytest `junitxml` file, one deliberately malformed file, one shard-merged set.
- **Reprocess:** same raw report + a bumped `parser_version` yields a new evidence revision; the original revision hash is unchanged; a rerun of the same selection yields a different `run_id`.
- **Isolation:** server integration tests run on real PostgreSQL (xUnit v3 + Testcontainers.PostgreSQL 4.15, Respawn between tests) with RLS on; one test asserts no rows are visible without `set_config('evidgate.org_id', …, true)` in the transaction. CLI/adapter packages keep Vitest.

## 8. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Producer formats vary more than expected (F9 ✅) | High | Mapping bugs reach customers | Per-producer registry, golden fixtures, `unsupported` is explicit, never guess |
| Playwright reporter API differs from the docs at the pinned version | Medium | Adapter rework | Pin `@playwright/test` (00 §4 1.63) and add a version-guard test at adapter start; unverified API details marked ⚠️ in §4 are confirmed in the first feature plan |
| CLI spool exhausts CI disk on long outages | Medium | Lost evidence | Hard size cap + clear error naming the run and unsent sequence range; never silent drop |
| Batch limit too small for big shards | Medium | More requests, more latency | Load test in 10; the limit is a contract change if numbers say otherwise |
| Duplicate detection cost grows with event volume | Medium | Ingest latency | Unique indexes on `(run, producer, sequence)` and `(run, event_id)`; partition `event` by time only when measured (source §6, line 212); a PostgreSQL unique constraint on a partitioned table must include the partition key, so partitioning `event` by time requires redesigning both dedupe keys in the same migration **first**, and global dedupe is not assumed to survive the change |
| Adapters become a license/compliance problem | Low | Blocked release | Licence rule (DECISIONS §4) applied in 01; adapters are our own code, engines are customer-installed |

## 9. Epics, stories and tasks

### Epic 04.1 Event ingest API
#### Story 04.1.1 Atomic batch ingest with per-event ack — acceptance: a valid batch commits fully and acks every event; one invalid event rejects the whole batch with its index and commits nothing.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 04.1.1-T1 | Envelope v1 JSON Schema + canonicalisation (RFC 8785) + atomic commit path as an `IngestEventBatch` command/handler (Corvus.Json.Validator for schema checks; writes `event` rows and, through 05's attempt interface 05.3.3-T2, the `case_attempt` row in the same transaction) | L | V1 | M1 | 01.3.1, 05.3.3-T2 |
| 04.1.1-T2 | `event_id` dedupe as a single-statement `unnest` insert `ON CONFLICT (org_id, run_id, event_id) DO NOTHING RETURNING event_id` + one select to compare payload hashes (same hash → `duplicate`; different hash → 409 `duplicate_different_payload` with index); `sequence` conflict → 422 `sequence_reused` (T50, P2) | M | V1 | M1 | 04.1.1-T1 |
| 04.1.1-T3 | Limits: 500 events / 1 MiB pre-decompression, gzip + bomb guard (413) | M | V1 | M1 | 04.1.1-T1 |
| 04.1.1-T4 | Producer/sequence rules: uniqueness, restart via new producer or checkpoint, ordering by `(producer, sequence)` | M | V1 | M1 | 04.1.1-T1 |
#### Story 04.1.2 Quota, epochs and observability — acceptance: a burst above quota gets 429 + `Retry-After`; a foreign-epoch event is stored as stale evidence and never completes a job.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 04.1.2-T1 | Per-project rate limit/quota with `Retry-After`; counters and OTel spans per batch | S | V1 | M1 | 04.1.1-T3 |
| 04.1.2-T2 | Stale/foreign-epoch event handling (epoch semantics for CI shards in V1; agent epochs validated with 05.7.2-T1 in V2) | S | V1 | M2 | 04.1.1-T4 |
| 04.1.2-T3 | OpenAPI + integration tests (xUnit v3 + Testcontainers.PostgreSQL, RLS on) for both stories | M | V1 | M1 | 04.1.1-T4 |
| 04.1.2-T4 | `producer_slot` table (DbUp migration) + `IProducerRegistry.RegisterSlotsAsync` contract + implementation; called by 05's run-create command in the same tenant transaction, one slot per manifest shard (T9) | M | V1 | M1 | 01.4.1-T1 |
| 04.1.2-T5 | Producer-slot binding enforcement: first write binds `bound_token_jti`/`bound_ci_run_ref`, foreign identity → 403 `producer_slot_taken`, unknown producer → 422 `unknown_producer`; the check lives in this module — 05's finalize handler (05.3.2-T1) enforces it by calling the `IProducerRegistry` contract, so only the bound identity may `finalize` (T9); 422 `unknown_execution_key` batch reject with indices (T10); accept-and-mark `after_cancel` after cancel (T12) | M | V1 | M1 | 04.1.1-T1, 04.1.2-T4 |

### Epic 04.2 `evidgate` CLI
#### Story 04.2.1 Authenticate from CI — acceptance: the CLI exchanges an OIDC JWT or ingest token for a scoped `ci_producer` token, caches it, refreshes before expiry and fails loudly on 403.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 04.2.1-T1 | CLI skeleton, `EVIDGATE_*` config, OIDC/ingest-token exchange, token cache + refresh, exit codes | M | V1 | M2 | 02.4.1, 02.4.2 |
#### Story 04.2.2 Manifest and run — acceptance: `evidgate run create` is idempotent for the same key and payload, prints the run id, and returns 409 on a different payload.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 04.2.2-T1 | `manifests upload` (upload, verify, print `manifest_hash`) | S | V1 | M1 | 05.1.1-T4 |
| 04.2.2-T2 | `runs create` with `Idempotency-Key`, `--json`, policy/suite/env flags | S | V1 | M1 | 05.2.1-T1 |
#### Story 04.2.3 Spool, send and finalize — acceptance: with the API down for 10 minutes the CLI spools within its size cap, then delivers every event with `duplicate` acks ignored, and never re-runs tests.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 04.2.3-T1 | Disk spool with size cap, batching, deadline-bounded retry with jitter, resume after process restart | L | V1 | M1 | 04.1.1-T2 |
| 04.2.3-T2 | `finalize` command (producer, shard, `last_sequence`, `count`, `manifest_hash`); surfaces 403 `producer_slot_taken` / 422 `unknown_producer` (T9) | S | V1 | M1 | 05.3.2-T1 |
#### Story 04.2.4 Artifacts and quickstart — acceptance: a trace and a screenshot upload through presigned URLs and reach `ready`; `evidgate quickstart` produces a working CI recipe.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 04.2.4-T1 | `artifacts upload` (upload-url → PUT → complete), retry and checksum verification | M | V1 | M2 | 06.1.1, 06.1.2 |
| 04.2.4-T2 | `quickstart`, docs, GitHub Actions example workflow (OIDC, no long-lived secret) | S | V1 | M2 | 04.2.1-T1 |
| 04.2.4-T3 | CLI end-to-end test against the local stack from the fixture run | M | V1 | M2 | 04.2.3-T1, 04.6.1-T2 |

### Epic 04.3 Playwright reporter adapter
#### Story 04.3.1 Custom reporter bound to the documented API — acceptance: the adapter implements the documented reporter callbacks and emits one envelope per test result, with the capability document registered at first send.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 04.3.1-T1 | `@evidgate/playwright-reporter` skeleton (`onBegin`/`onTestBegin`/`onTestEnd`/`onEnd`), capability document, version guard | M | V1 | M1 | 04.7.1-T1 |
#### Story 04.3.2 Identity, retries and skips — acceptance: `evidgate.id` drives `case_id`; retries become `attempt_no` 1..n; `fixme`/`skip` are distinguishable and never map to `passed`.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 04.3.2-T1 | Case/variant identity mapping (annotation, project + normalised params → `variant_hash`) | L | V1 | M1 | 03.1.1, 03.1.3 |
| 04.3.2-T2 | Status mapping incl. retries, `timedOut`, interrupted, and skip vs `fixme` | M | V1 | M1 | 04.3.2-T1 |
#### Story 04.3.3 Shards and attachments — acceptance: each shard is its own producer with its own sequence; attachments map to artifacts by MIME/size policy.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 04.3.3-T1 | Shard → producer mapping (blob/merge aware), sequence per shard, restart rule | M | V1 | M1 | 04.1.1-T4 |
| 04.3.3-T2 | Attachment → artifact upload (trace, video, screenshot), size caps, allow-listed directories | M | V1 | M2 | 04.2.4-T1 |

### Epic 04.4 JUnit XML mapper
#### Story 04.4.1 pytest `junitxml` mapping — acceptance: a pytest report maps to events whose counts equal the declared counts, raw statuses are preserved and the parser version is recorded; a stored report can be read back against its parsed events per case, and every discrepancy carries a reason (declared vs observed count, unmapped case, status mismatch, malformed).
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 04.4.1-T1 | Per-producer mapping registry + pytest mapping (`testcase`, `failure`, `error`, `skipped`, `time`) | L | V1 | M1 | 04.7.1-T1 |
| 04.4.1-T2 | Hardened XML parsing: XXE/DTD off, expansion + depth caps, encoding/BOM, control characters | M | V1 | M1 | 04.4.1-T1 |
| 04.4.1-T3 | Malformed/truncated handling → raw stored, `unknown` status, `missing[]` reason | M | V1 | M1 | 04.4.1-T1 |
| 04.4.1-T4 | Raw report persistence + `raw_report`/`report_parser` rows with `parser_id`/`parser_version` | S | V1 | M2 | 06.1.2, 06.3.1 |
| 04.4.1-T5 | Mapping document per producer + declared-vs-observed count check | M | V1 | M2 | 04.4.1-T1 |
| 04.4.1-T6 | Report-comparison read API: parsed events per case vs the stored raw report's declared counts/ids with discrepancy reasons (declared vs observed count, unmapped case, status mismatch, malformed) — `GET /v1/runs/{r}/reports/{report_id}/comparison`, permission `run:read`; raw bytes are never inlined, the response carries the `raw_report` artifact id for `download-url` (`artifact:read` + sensitivity gate, 06.2.1-T1) | M | V1 | M3 | 04.4.1-T4, 04.4.1-T5 |
#### Story 04.4.2 Reprocess a stored raw report — acceptance: reprocessing with a new parser version creates a new evidence revision and never mutates the previous one or the original report.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 04.4.2-T1 | Reprocess flow (new producer + sequences, new parser version, `evidence_revision` persisted by 05 from 06's freeze result, labelled in UI/API) | M | V1 | M2 | 04.4.1-T4, 06.5.1, 05.3.2-T5 |

### Epic 04.5 Appium and mobile mapping (V2)
#### Story 04.5.1 Appium adapter — acceptance: a UiAutomator2 run maps to events with device identity in the variant; the adapter refuses XCUITest on non-macOS hosts and declares it as unsupported.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 04.5.1-T1 | Appium adapter skeleton + capability document (`engine.family`, driver, platform, `supports.cancel`) | L | V2 | M4 | 04.7.1-T1 |
| 04.5.1-T2 | Mobile identity mapping (platform capability + fixture, never web locators) and app checksum in the variant | M | V2 | M4 | 03.2.1 |
| 04.5.1-T3 | Device exclusive-lease client (05.8.1-T1) + reset-by-policy enforcement before start | M | V2 | M4 | 05.8.1-T1 |
| 04.5.1-T4 | Appium golden fixtures (UiAutomator2; XCUITest macOS-only fixtures marked ⚠️ until a macOS host exists) | M | V2 | M4 | 04.6.1-T1 |

### Epic 04.6 Golden fixtures and conformance harness
#### Story 04.6.1 Per-producer golden set — acceptance: the harness diffs normalised output against the original report and fails the build on any unexplained difference.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 04.6.1-T1 | Fixture layout `fixtures/<producer>/<case>/` + diff harness (expected events vs produced) | L | V1 | M1 | 04.1.1-T1 |
| 04.6.1-T2 | Dogfood fixture: Evidgate's own CI results ingested (R10 ✅) incl. one malformed file and one shard-merged set | M | V1 | M2 | 04.6.1-T1, 01.7.1 |
| 04.6.1-T3 | CI job running the conformance suite per adapter (fixtures only, no network) | S | V1 | M1 | 04.6.1-T1, 01.7.1-T1 |

### Epic 04.7 Capability document and protocol versioning
#### Story 04.7.1 Protocol v1 contract — acceptance: an adapter with an incompatible major is rejected at registration/claim with a clear error; `ingest_only` results are viewable but never gate-eligible.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 04.7.1-T1 | `contracts/adapter/` JSON Schemas: capability document + protocol operations (`discover`, `prepare`, `execute`, `collect`, `cleanup`) | M | V1 | M1 | 01.3.1 |
| 04.7.1-T2 | Server-side capability validation, major rejection, minor compatibility rules, `ingest_only` semantics | M | V1 | M1 | 04.7.1-T1 |
| 04.7.1-T3 | Compatibility matrix test (major/minor negotiation, unknown capability fields ignored) | S | V1 | M2 | 04.7.1-T2 |

### Epic 04.8 Usage events for accepted results
#### Story 04.8.1 Idempotent billing key — acceptance: a redelivered batch and a reprocess of the same acceptance produce exactly one `usage_ledger_entry` row per metric per batch.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 04.8.1-T1 | Emit `usage_ledger_entry` rows per batch per metric (quantity = event count) via an outbox task, `billing_key = batch:<event_batch_id>:<metric>` (T43, T50) | M | V1 | M2 | 04.1.1-T2, 12.3.1-T1 |
| 04.8.1-T2 | No-double-count integration test (resend, reprocess, retry-fail-pass) + handoff notes to the usage roll-up | S | V1 | M2 | 04.8.1-T1 |

## 10. Dependencies

| Plan | Needed for | Story/task ids (checked against the sibling plan files 2026-09-24, plan 10 re-checked 2026-09-25) |
|---|---|---|
| 01 | contracts pipeline, generated schemas, integration harness, own CI | 01.3.1, 01.3.2, 01.6.1, 01.7.1 |
| 02 | OIDC login, `POST /v1/ci/token`, scoped `ci_producer` tokens, RLS | 02.2.1, 02.4.1, 02.4.2, 02.6.1 |
| 03 | stable case ids, revisions, suites, variants, fixtures | 03.1.1, 03.1.3, 03.2.1, 03.3.1 |
| 05 | manifest verification, run creation (calls `IProducerRegistry.RegisterSlotsAsync` in its tenant transaction, 04.1.2-T4), environment revisions, attempt write interface, finalize semantics (binding check via `IProducerRegistry`, T9), evidence-revision persistence, job epochs, device lease | 04.1.2-T4, 05.1.1-T4, 05.1.2-T1, 05.2.1-T1, 05.3.2-T1, 05.3.2-T5, 05.3.3-T2, 05.7.2-T1, 05.8.1-T1 |
| 06 | artifact upload/complete, raw-report storage, evidence freeze result (`freeze(run_id)`; 05 persists the revision) | 06.1.1, 06.1.2, 06.3.1, 06.5.1 |
| 07 | completeness/decision consumption of events | 07.2.1, 07.5.1 |
| 09 | reprocess shown as its own action, not a rerun | 09.3.1 |
| 10 | ingest load test at the 833/s burst target | 10.6.1-T1, 10.6.1-T3 |
| 11 | untrusted-input (XML, artifact) review, secrets, PDPL | 11.2.1, 11.3.1, 11.8.1 |
| 12 | usage ledger schema for `usage_ledger_entry` | 12.3.1-T1 |

## 11. Open questions

| # | Question | Owner | Needed by |
|---|---|---|---|
| Q1 | Which JUnit producer is the pilot's (assumed pytest `junitxml`, O1) and does it emit nested `testsuites`? | Tech Lead | M1 start |
| Q2 | Do pilot CIs split Playwright runs with `--shard`, or with separate matrix jobs? (changes producer/shard mapping) ⚠️ | Tech Lead | M1 start |
| Q3 | Exact gzip expanded-size/ratio cap value (needs the 10 load test) | Tech Lead | M2 |
| Q4 | Are raw reports retained as artifacts of the run for the whole retention window? (cost vs reprocess) | Tech Lead | M2 |
| Q5 | Does the pilot need `ingest_only` at all, or is CI mode always full? | Product owner | M2 |

## 12. Estimate summary

| Epic | V1 | V2 | V3 | Total |
|---|---|---|---|---|
| 04.1 Event ingest API | 9.0 | — | — | 9.0 |
| 04.2 `evidgate` CLI | 7.0 | — | — | 7.0 |
| 04.3 Playwright reporter | 6.0 | — | — | 6.0 |
| 04.4 JUnit XML mapper | 7.5 | — | — | 7.5 |
| 04.5 Appium mapping | — | 5.0 | — | 5.0 |
| 04.6 Fixtures and conformance | 3.5 | — | — | 3.5 |
| 04.7 Capability and protocol | 2.5 | — | — | 2.5 |
| 04.8 Usage events | 1.5 | — | — | 1.5 |
| **Total** | **37.0** | **5.0** | **—** | **42.0** |

→ with 20 % buffer **50.4** person-days (V1 44.4, V2 6.0; unit: one senior full-stack .NET + Angular developer for server work, Node/TypeScript unchanged for CLI/adapters, CR-D-1).

## 13. Contract change requests

> **Status:** Applied 2026-09-24: CR-1 → 00 §9.3; CR-2 → 00 §10. Applied 2026-09-25 by integrator: CR-3 → 00 §9.3.

| # | Request | Why | Affects |
|---|---|---|---|
| CR-1 | Add `POST /v1/runs/{r}/reprocess` (permission `run:create`; body: `raw_report_id`, `parser_version`; 202 → new evidence revision) to 00 §9.3 | Reprocess of a stored raw report with a new parser must be a first-class, labelled action distinct from rerun (source §7); it has no endpoint today | 00 §9.3, 05 (`evidence_revision` persistence, 05.3.2-T5, from 06's freeze result), 09 (UI must show "reprocessed", not "rerun") |
| CR-2 | State explicitly in 00 §10 that the runner engine version is carried in the capability document and (V2) in the immutable JobSpec + engine image digest | Prevents each adapter from inventing its own version-pinning rule; keeps DECISIONS §4 "never pins latest inside runners" true while making evidence reproducible | 00 §10, 05.7.1-T1 |
| CR-3 | Add `GET /v1/runs/{r}/reports/{report_id}/comparison` (permission `run:read`) to 00 §9.3 — Applied 2026-09-25 by integrator | A stored raw report must be readable against its parsed events per case with discrepancy reasons, so a declared-vs-observed count mismatch or an unmapped case is explainable without re-parsing; no endpoint existed | 00 §9.3, 09 (report comparison view) |

2026-09-25 CR-D-1 + TL review: server module migrated to .NET 10 + CQRS (`src/Modules/Evidgate.Modules.Ingestion` + `.Contracts`; Corvus.Json.Validator on the server, Ajv 8 kept in the TS CLI with a shared `fixtures/contracts/` parity corpus, T42; xUnit v3 + Testcontainers.PostgreSQL; `ITenantScope`/`set_config`); `producer` replaced by `producer_slot` with token-identity binding on writes and `finalize` (T9); `event_id` declared as its own dedupe column (F-04-2); unknown `execution_key` rejects the batch 422 `unknown_execution_key` (T10); different-payload duplicate → 409 (T50); post-cancel events accepted with `after_cancel = true` (T12); hot path inserts via one `unnest` statement and usage is metered once per batch per metric `batch:<event_batch_id>:<metric>` (T50, T43); new tasks 04.1.2-T4/T5 (+2 tasks, +2.0 PD → raw 42.0 / buffered 50.4; 00 §14 roll-up needs recompute).
