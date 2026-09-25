# 06 - Evidence and Artifacts (M2)

> Contracts: `00-overview.md` (§3 glossary, §5.3 background work, §5.4 tenancy, §8.4 artifact states, §9.3 endpoints, §2 permissions, §11 NFRs). Facts: `../DECISIONS.md` §4 (S3 + presigned URLs), §7 (personal data in artifacts), §9 (capacity arithmetic), F5/F6.
> Source: design §4 "Artifact và secret", §6 "API event artifact và healing", §6 "Ràng buộc lưu trữ và index" / "Tăng trưởng và xóa", §7 "Muộn và phát lại".
> Level: high-level plan; each story gets a detailed plan in `plans/features/` (00 §12) before build.
> Module ownership: the `evidence` module owns `artifact` and everything in this plan; it never reads another module's tables directly (00 §5.1).

## 1. Purpose and scope

**Goal:** every artifact that backs a release decision is a *verified*, immutable object with a known sensitivity, safe to parse, safe to display to a browser, and deleted on a date we can prove.

**In scope:** `upload-url` / `complete`, the artifact state machine (00 §8.4), server-chosen object keys, media-type/size/checksum policy, sensitivity and `artifact:read_sensitive`, redaction and capture-disabling at source, secret masking before upload, safe parsing (XML/archive/MIME), the isolated viewer origin for HTML reports and Playwright traces, retention and deletion (object-first, `pending_delete`, idempotent retry), the orphan sweeper, tenant deletion covering artifacts and backups, the evidence-revision interface with 05, and storage cost metrics feeding usage.

**Out of scope:** rendering every engine's raw report as a first-class UI view (04 maps producers, 09 renders); video transcoding/thumbnails; capture on customer devices (04/05); hosted runners (V3); DLP-grade content classification beyond the secret-pattern scan (§6).

**Version split**
- **V1 (M2):** upload/complete path, state machine, sensitivity gate, redaction + masking, parser hardening, isolated viewer for HTML reports and Playwright traces, retention + sweeper + tenant deletion, storage usage metrics.
- **V2 (M4):** mobile artifact kinds (device video via range requests, hierarchy dumps) once agent mode exists; tenant deletion extended to healing knowledge records and candidate context (with 08).
- **V3 (M5+):** cold-tier lifecycle rules per project, object-lock/WORM retention for regulated tenants `[OPEN]`.

## 2. Requirements

- **FR-06-1** The client never chooses a key. The server mints `org/<org_id>/project/<project_id>/run/<run_id>/attempt/<attempt_id>/<artifact_id>` and stores it in `artifact.object_key`; `UNIQUE (org_id, object_key)` (source §6). A client-supplied key is rejected.
- **FR-06-2** `POST /v1/artifacts/upload-url` (`artifact:write`, 00 §9.3) takes run/attempt scope, artifact kind, declared media type, declared size and `sha256:<hex>`, validates all of them against the project's kind policy, and returns the server key plus a presigned `PUT` with a short expiry and the checksum header required by SigV4 (AWS ✅).
- **FR-06-3** `POST /v1/artifacts/{a}/complete` (`artifact:write`) verifies the object with `HEAD` (existence, content length, checksum) before writing `ready`; mismatch or absence writes `rejected` with a reason code. Repeating `complete` on a `ready` artifact is a no-op returning the same body.
- **FR-06-4** An object that exists without `ready` is never evidence (00 §8.4). Only `ready` artifacts are listed to reconcile (05) or shown as run evidence (09).
- **FR-06-5** Every artifact kind has a default sensitivity. Reading, listing or issuing a short-lived download URL for a `sensitive` artifact requires `artifact:read_sensitive` (00 §2); the denial is audited.
- **FR-06-6** Redaction is first, at source: a project can disable capture per artifact kind and can apply allowlisted redaction rules (headers, cookies, form fields, query params) in the reporter/CLI before upload. The capability is declared in the adapter protocol, not assumed.
- **FR-06-7** Masking is second: on `complete`, the server scans the object for high-confidence secret patterns; matches raise sensitivity, write a `masking_event`, and either alert or reject when the project sets `reject_on_secret`.
- **FR-06-8** Parsing is hostile-input-safe: XML parsers run with external entities and DTDs disabled and with entity/depth/size/time caps; archive extraction caps entry count, total size, compression ratio and nesting and rejects entries whose normalized path escapes the target; declared media type must match the sniffed signature.
- **FR-06-9** HTML reports and Playwright traces are served from a separate registrable domain with a strict CSP, an `iframe sandbox`, no application cookies and no application origin storage; download URLs are short-lived and per-view.
- **FR-06-10** Per project, artifacts expire after 14 days and their metadata after 90 days by default `[DECISION]` (to confirm with pilots, §11).
- **FR-06-11** Deletion is object-first: `pending_delete` → object delete → verify absent → `deleted`, with idempotent retry; a failed delete never silently drops the row and never deletes the row before the object.
- **FR-06-12** A consistency sweep finds objects with no row, rows with no object, `pending_delete` older than the retry budget and `pending_upload`/`uploaded` rows older than the upload window; every repair is idempotent and audited.
- **FR-06-13** Tenant deletion covers artifacts, artifact metadata, derived usage pointers and backups within the published period and produces a deletion report; it is the only operation allowed to delete evidence outside retention.
- **FR-06-14** `evidence_revision` is **owned and persisted by 05** (`05.3.2-T5`); 06 only supplies the result of a single `EvidenceRepository.freeze(run_id)` call — the ready-artifact set, the required-artifact gaps and the completeness input — and never writes the revision row itself; a frozen revision is never mutated.
- **FR-06-15** Storage usage is measured per org/project as GB-month average, object count, egress bytes and artifact failure counts, and is handed to 12's `usage_ledger_entry` with a unique `billing_key` emitted by this plan (source §9 cost model).

**Acceptance criteria (testable):** a declared checksum that does not match the stored object leaves the artifact `rejected` and no evidence revision counts it; a request naming a key is refused and a server key is returned; a viewer URL for a `sensitive` artifact issued to a member without `artifact:read_sensitive` returns 403 and writes an audit row; a trace zip containing `../` entries is rejected without writing outside the extraction root; a retention run that half-fails leaves `pending_delete` rows that the next run clears, and the total object count converges.

## 3. Key decisions

- `[DECISION]` **Presigned URL with a server-chosen key; no upload bytes through the API.** Rationale: source §6 ("URL ký sẵn là bearer credential… Không cấp key cho client tự chọn") and AWS ✅ ("presigned URLs are bearer tokens that grant access to those who possess them"). Rejected: client-chosen keys (collision, traversal, cross-tenant probing); proxying uploads (bandwidth, memory, timeout risk on 2 MB × 20 artifacts × 50 runs/day — DECISIONS §9 arithmetic ✅).
- `[DECISION]` **Verify on `complete` with `HEAD` + checksum; never trust the client's claim.** SigV4 presigned URLs support SHA-256 checksum headers ✅, and overwriting an existing key is possible ✅, so verification — not key uniqueness alone — is the integrity control.
- `[DECISION]` **Sensitivity is per artifact kind with a project override, never inferred from content.** Reason: content inference is unreliable; kind defaults are reviewable and cheap.
- `[DECISION]` **Separate registrable domain for the viewer.** Rationale: source §8 (isolated origin, CSP, no shared cookies). Rejected: same-origin plus CSP only (a report XSS would sit on the app origin); iframe `sandbox` alone (does not isolate storage or cookies).
- `[DECISION]` **Object-first deletion with `pending_delete` and idempotent retry** (source §6 ✅). Rejected: row-first deletion (an unreferenced object becomes invisible and billable).
- `[DECISION]` **Retention defaults 14 days artifacts / 90 days metadata**, matching the capacity model (DECISIONS §9 ✅) but flagged for pilot confirmation rather than treated as fixed.
- `[DECISION]` **Masking never converts a bad artifact into a good one**: a masked artifact stays evidence, but rejection is opt-in per project and the event is always recorded. Silently dropping artifacts would break the completeness rules (A8).

## 4. Design

**Tables owned by 06**

| Table | Key columns | Constraints |
|---|---|---|
| `artifact` | `id`, `org_id`, `project_id`, `run_id`, `attempt_id`, `kind`, `object_key`, `media_type`, `bytes`, `checksum`, `sensitivity`, `state`, `expires_at`, `failure_code`, `version` | `UNIQUE (org_id, object_key)`; index `(org_id, run_id)`; `expires_at` index for retention (source §6) |
| `artifact_kind_policy` | `org_id`, `project_id`, `kind`, `max_bytes`, `media_types[]`, `default_sensitivity`, `reject_on_secret`, `capture_enabled` | `UNIQUE (org_id, project_id, kind)` |
| `retention_policy` | `org_id`, `project_id`, `artifact_days`, `metadata_days`, `updated_at` | `UNIQUE (org_id, project_id)`; defaults 14 / 90 |
| `masking_event` | `org_id`, `artifact_id`, `pattern_class`, `hit_count`, `action`, `created_at` | append-only |
| `deletion_job` | `org_id`, `scope` (`artifact` \| `tenant`), `target_id`, `state`, `attempts`, `last_error`, `completed_at` | `UNIQUE (org_id, scope, target_id)` |
| `storage_usage_daily` | `org_id`, `project_id`, `day`, `bytes_stored`, `objects`, `egress_bytes`, `artifact_failures` | `UNIQUE (org_id, project_id, day)` |

**Endpoints**

| Endpoint | Permission | Notes |
|---|---|---|
| `POST /v1/artifacts/upload-url` | `artifact:write` | contract in 00 §9.3; owned here |
| `POST /v1/artifacts/{a}/complete` | `artifact:write` | contract in 00 §9.3; owned here |
| `GET /v1/artifacts/{a}` | `artifact:read` | metadata; hides `sensitive` details without `artifact:read_sensitive` |
| `GET /v1/artifacts/{a}/download-url` | `artifact:read` (+`artifact:read_sensitive`) | short-lived GET URL; per-view; audited (see §13) |
| `GET/PUT /v1/projects/{p}/retention` | `project:read` / `project:write` | retention policy, validated against a floor |
| `POST /v1/projects/{p}/tenant-deletion` (internal, operator-scoped) | operator scope + audit | starts a tenant/project deletion job |

**States** — 00 §8.4 is the contract; this plan adds only the triggers:

| From | Event | To | Guard |
|---|---|---|---|
| `pending_upload` | `upload-url` issued | `pending_upload` | scope, kind, media type, size, checksum validated |
| `pending_upload` | client reports the `PUT` finished | `uploaded` | object may exist; size/checksum verified only at `complete` |
| `uploaded` | `complete` verified | `ready` | `HEAD` size + checksum match |
| `uploaded` | `complete` mismatch / media type / oversize | `rejected` | reason code stored; object deleted best-effort |
| `pending_upload` / `uploaded` | upload window elapsed without `complete` | `rejected` | sweeper (06.4.3) |
| `ready` | retention reached | `pending_delete` | object delete attempted next |
| `pending_delete` | object absent + verified | `deleted` | idempotent; retried until success |

**Flows**
1. **Upload:** client asks for a URL for a run/attempt → server validates against `artifact_kind_policy` → presigns → client `PUT`s and reports completion (`uploaded`) → client calls `complete` → server `HEAD`s, verifies, writes `ready` → 05's reconcile may now count the artifact.
2. **Freeze:** 05 reconciles a run; it calls `EvidenceRepository.freeze(run_id)`; 06 returns `ready` artifacts grouped by requirement, the gaps, and the completeness input; 05 persists the `evidence_revision` and its `missing[]`.
3. **Retention:** nightly job selects `ready` rows past `expires_at` for their project → `pending_delete` → delete → verify → `deleted`; metadata rows expire later under `metadata_days`.
4. **View:** 09 asks for a download URL → 06 checks permission and sensitivity → issues a short-lived URL on the viewer host → the isolated origin loads the trace/HTML.

## 5. Failure modes and edge cases

| Case | Handling | Invariant kept |
|---|---|---|
| Client uploads to a key it already used | Presigned `PUT` replaces the object (AWS ✅); the checksum recorded at `complete` is the one evaluated | An artifact row's checksum always describes the object it points at |
| Size/checksum mismatch at `complete` | `rejected` + reason code (`size_mismatch`, `checksum_mismatch`, `media_type_mismatch`, `too_large`) + best-effort object delete | No `ready` without verification; no silent pass |
| `complete` called twice / out of order with `rejected` | Idempotent replay returns the current state; a `rejected` artifact never becomes `ready` | State machine is monotonic per artifact |
| Upload never completed | Sweeper marks `rejected` after the upload window; object deleted if present | Orphans are bounded in time |
| Object deleted out of band (lifecycle rule, operator) | `complete` and every read treat absence as failure → artifact `rejected`/`missing`; completeness reports `artifact missing` | Evidence never claims an object it cannot fetch |
| Decompression bomb / huge trace | Archive and parser caps reject it (06.3.1, 06.3.2) | Ingest cost per artifact is bounded |
| XML with external entity or DTD | Parser profile with entities/DTD disabled; document rejected | Parsing never performs network/file access |
| Archive entry `../etc/passwd` or absolute path | Entry rejected; extraction root normalized and enforced | No write outside the extraction root |
| HTML report containing script/`javascript:` | Served only from the isolated origin with CSP and sandbox | App origin cookies and storage unreachable from artifacts |
| Retention job fails halfway | `pending_delete` retried; deletion job records `last_error`; alert after N attempts | Rows and objects converge; never row-before-object |
| Tenant deletion while uploads/CI still run | Project is marked deleting; new `upload-url` requests get 409; in-flight objects swept | Published deletion period is met and provable |
| Metric pipeline down | Usage rows are recomputed from `artifact` on the next run (idempotent by `billing_key`) | Billing keys never duplicate (source §9) |
| Cross-tenant `run_id` in `upload-url` | Scope check in the tenant transaction; 404 like any other-tenant id (00 §9.2) | No cross-tenant write path |

## 6. Security and privacy

- Artifacts routinely contain personal data (DOM, screenshots, video, request bodies); under PDPL the customer is controller and Evidgate processor, so a DPA precedes the first pilot upload (DECISIONS §7 ✅ Art. 37.2.a). Redaction-at-source and capture-disabling are part of the processing contract, not a nicety (11).
- Storage region is an open owner decision (O3): where the region is outside Vietnam, the 60-day transfer assessment applies (F8 ✅). The plan keeps the region a configuration of the storage adapter so the decision is not baked into code.
- Presigned URLs are bearer credentials ✅: never logged, never emitted into telemetry labels (00 §11), expiry short, and revocation by rotating the signing credentials; `s3:signatureAge`-style bucket policies are available as a second fence if the provider supports them ✅.
- The viewer origin has no application cookies, no shared storage and a strict CSP; artifact HTML is treated as untrusted input everywhere.
- Sensitive reads/download-url issuance are audited with actor, artifact, run, request id (source §8 audit row ✅).
- `artifact:read_sensitive` is checked on every path that can return content, not just the download URL (list, metadata, viewer init, webhook payloads that link artifacts).
- Deletion period and retention defaults are published to customers before the pilot (11/12); the published number is the one the deletion job proves.

## 7. Test strategy

- **Unit:** key minting; size/media/checksum validation; state transitions incl. illegal ones; archive entry-name normalization; parser profile flags; sensitivity resolution per kind; retention date arithmetic.
- **Integration (real PostgreSQL + object storage):** upload → `complete` → `ready` under Testcontainers; checksum mismatch leaves `rejected` and no evidence revision counts it; delete-then-verify loop converges after injected object-store errors; sweeper repairs each orphan class; tenant deletion report lists every deleted object and leaves none behind.
- **Hostile fixtures** (kept in `fixtures/`, reused by 04 and 11): golden Playwright trace zip; HTML report with injected XSS vectors and a form-post exfiltration attempt; XML with XXE payload and billion-laughs; nested zip with a traversal entry; truncated trace; oversized artifact; artifact with a planted fake API key (masking).
- **Browser-level viewer tests:** load a report on the isolated origin, assert the app origin's cookies/localStorage are unreachable, assert CSP blocks inline script, assert an expired URL fails, assert a `sensitive` artifact is not viewable without the permission (09 drives the UI).
- **Deletion timing test:** with a clock override, prove that a run older than `artifact_days` loses objects while its metadata and evaluation remain queryable until `metadata_days`.
- **Capacity sanity:** 50 runs × 20 artifacts × 2 MB ≈ 2 GB/day, ≈ 28 GB at 14 days (DECISIONS §9 ✅) is the fixture scale for the nightly job test, not a benchmark.
- **Not tested here:** engine-specific report rendering (04) and UI lists (09).

## 8. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| A customer report XSS escapes the viewer and reaches the app origin | Low | High | Separate registrable domain, CSP, iframe sandbox, no cookies, security review in 11 and a browser-level test suite in CI |
| Presigned URL leaks (CI log, chat) and is used to overwrite or read an object | Medium | High | Short expiry, per-view URLs, never logged, no client-chosen keys, checksum verification makes silent replacement detectable, credential rotation as revocation |
| Parser hardening is bypassed (new archive format, XXE variant) | Medium | High | Allowlisted media types only, caps independent of format, fuzz suite in CI, no parser for unknown types (reject rather than best-effort) |
| Masking misses customer secrets, which then sit in storage and in any LLM context | Medium | High | Redact at source (FR-06-6), server scan, `sensitive` classification, healing context is opt-in and redacted (08/11); incident runbook in 11 |
| Retention defaults wrong for a pilot (too short → evidence gone; too long → cost) | Medium | Medium | Defaults are conservative and configurable per project; DECISIONS §9 arithmetic drives cost discussion; confirm before M3 (§11) |
| Deletion job and tenant deletion miss backups, leaving data past the published period | Medium | High | Tenant deletion is a first-class job with a report; backup coverage is a 10 dependency and part of the pilot readiness checklist (11/12) |
| Nightly job cost/throughput (28 GB scale) exceeds the pilot budget | Low | Medium | Job is batched and metered; storage metrics (06.5.2) make the trend visible before pricing (12) |
| Evidence revision frozen while a late artifact arrives | Medium | Medium | Freeze is a snapshot; late artifacts create a new revision through 05 and never mutate a used one (07.5) |

## 9. Epics, stories and tasks

### Epic 06.1 Upload path and artifact state machine

#### Story 06.1.1 Artifact record and server-chosen keys — acceptance: a request that supplies `object_key` is refused; the created row's key follows `org/project/run/attempt/artifact` and is unique per org.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 06.1.1-T1 | `artifact` table + migration (state, kind, sensitivity, bytes, checksum, `expires_at`, `UNIQUE(org_id, object_key)`) | M | V1 | M2 | 01.4.1-T1, 01.4.1-T3, 02.6.1-T1 |
| 06.1.1-T2 | Key minting service + rejection of client keys | S | V1 | M2 | 06.1.1-T1 |

#### Story 06.1.2 Presigned upload and completion — acceptance: an upload whose bytes and checksum match becomes `ready`; a mismatch becomes `rejected` with a reason code and is never counted as evidence.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 06.1.2-T1 | `POST /v1/artifacts/upload-url` (scope, kind, media type, size, checksum → key + presigned `PUT` + expiry + checksum header) | M | V1 | M2 | 06.1.1-T2, 05.2.1-T1, 05.3.3-T1 |
| 06.1.2-T2 | `POST /v1/artifacts/{a}/complete` (`HEAD` size + checksum verify, idempotent, reason codes) | M | V1 | M2 | 06.1.2-T1 |
| 06.1.2-T3 | Media-type allowlist, per-kind size caps, 413/422 mapping | S | V1 | M2 | 06.1.2-T1 |

#### Story 06.1.3 State-machine guards — acceptance: illegal transitions return a conflict and leave the row unchanged; `rejected` and `ready` are terminal for their branches.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 06.1.3-T1 | Guards, transition tests, `rejected`/`ready` immutability | S | V1 | M2 | 06.1.2-T2 |

### Epic 06.2 Sensitivity, redaction and masking

#### Story 06.2.1 Sensitivity classification and access control — acceptance: any path returning a sensitive artifact without `artifact:read_sensitive` returns 403 and is audited.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 06.2.1-T1 | Kind → default sensitivity map + project override; enforce on read/list/download-url | M | V1 | M2 | 06.1.1-T1, 02.2.1-T1, 02.2.1-T2 |
| 06.2.1-T2 | `GET /v1/artifacts/{a}/download-url` + audit rows for sensitive issuance | S | V1 | M2 | 06.2.1-T1, 06.3.3-T1 |

#### Story 06.2.2 Redaction and capture control at source — acceptance: a project that disables a kind receives no artifacts of that kind, and configured redaction rules remove the fields before upload.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 06.2.2-T1 | Adapter capability + per-project options (disable kinds, allowlisted redaction rules) with 04 | M | V1 | M2 | 04.7.1-T1, 06.1.1-T1 |

#### Story 06.2.3 Secret masking — acceptance: a planted key in a trace is detected on `complete`, records a `masking_event`, raises sensitivity, and rejects only when the project sets `reject_on_secret`.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 06.2.3-T1 | Server-side secret scan on `complete` + `masking_event` + project reject flag | M | V1 | M2 | 06.1.2-T2 |

### Epic 06.3 Safe parsing and isolated viewer

#### Story 06.3.1 Parser hardening — acceptance: XML with an external entity is rejected, depth/size caps are enforced, and no parser performs network or filesystem access.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 06.3.1-T1 | Parser profile (entities/DTD off, depth/size/time caps) for XML/HTML/JSON | M | V1 | M2 | 06.1.1-T1 |
| 06.3.1-T2 | Archive limits (entries, ratio, nesting), path-traversal rejection, MIME sniff vs declared | M | V1 | M2 | 06.3.1-T1 |

#### Story 06.3.2 Hostile-input fixtures — acceptance: the fixture suite runs in CI and fails the build when any guard regresses.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 06.3.2-T1 | XXE / billion-laughs / zip-bomb / traversal / truncated-artifact fixtures + fuzz run | M | V1 | M2 | 06.3.1-T2 |

#### Story 06.3.3 Isolated viewer origin — acceptance: an artifact page on the viewer domain cannot read app cookies or app-origin storage and is blocked by CSP from inline script.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 06.3.3-T1 | Viewer origin (separate registrable domain), CSP, iframe sandbox, no app cookies, short-lived URLs | L | V1 | M2 | 10.1.1-T4, 11.1.1-T1 |

#### Story 06.3.4 Rendering reports and traces — acceptance: a Playwright trace and an HTML report open on the isolated origin with short-lived URLs; an expired URL fails closed.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 06.3.4-T1 | HTML report serving (untrusted, CSP-fenced, no same-origin assets) | M | V1 | M2 | 06.3.3-T1 |
| 06.3.4-T2 | Playwright trace serving (index + resources, path-traversal safe) | M | V1 | M2 | 06.3.3-T1, 06.3.1-T2 |

#### Story 06.3.5 Mobile artifact kinds — acceptance (V2): device video streams with range requests and hierarchy dumps render in the viewer without new origins.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 06.3.5-T1 | Mobile kinds end-to-end (video range requests, hierarchy dump parsing) | S | V2 | M4 | 04.5.1-T1, 05.7.4-T1 |

### Epic 06.4 Retention, deletion and sweeper

#### Story 06.4.1 Retention policy — acceptance: a project can set artifact and metadata days above the floor; defaults are 14 and 90.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 06.4.1-T1 | `retention_policy` table, validation floor, defaults, `GET/PUT` | S | V1 | M2 | 06.1.1-T1, 02.2.1-T1, 02.2.1-T2 |

#### Story 06.4.2 Retention deletion job — acceptance: a half-failed run leaves `pending_delete` rows that the next run clears; no row is deleted before its object.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 06.4.2-T1 | pg-boss retention job (select → `pending_delete` → delete → verify → `deleted`, idempotent retry) | M | V1 | M2 | 06.4.1-T1, 01.5.1-T2, 01.5.1-T3 |

#### Story 06.4.3 Orphan sweeper — acceptance: every orphan class in §5 is detected and repaired idempotently with an audit row.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 06.4.3-T1 | Sweeper (object without row, row without object, stuck `pending_delete`, stale `pending_upload`/`uploaded`) | M | V1 | M2 | 06.4.2-T1 |

#### Story 06.4.4 Tenant deletion — acceptance: deleting a tenant/project removes artifacts, metadata and usage pointers within the published period and emits a report.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 06.4.4-T1 | Tenant/project deletion job + report (artifacts, metadata, usage pointers, backup coverage with 10) | M | V1 | M2 | 06.4.2-T1, 10.5.1-T1, 10.5.1-T2 |
| 06.4.4-T2 | Extend deletion to healing knowledge records and candidate context | S | V2 | M4 | 08.7.1-T1 |

#### Story 06.4.5 Deletion verification — acceptance: delete/re-run tests and alerts prove convergence and surface failures within one cycle.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 06.4.5-T1 | Idempotency re-run tests, failure alerting, audit assertions | S | V1 | M2 | 06.4.2-T1, 06.4.3-T1 |

### Epic 06.5 Evidence revision interface and storage usage

#### Story 06.5.1 Freeze interface with 05 — acceptance: one call returns ready artifacts, required-artifact gaps and the completeness input; a frozen revision never changes afterwards.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 06.5.1-T1 | `EvidenceRepository.freeze(run)` interface contract with 05 | M | V1 | M2 | 06.1.2-T2, 05.3.2-T5 |
| 06.5.1-T2 | Required-artifact readiness check → `missing[]` (`artifact missing`) | M | V1 | M2 | 06.5.1-T1 |
| 06.5.1-T3 | Immutability guard + late-artifact path (new revision, never mutate) | S | V1 | M2 | 06.5.1-T1, 07.5.1 |

#### Story 06.5.2 Storage usage metrics — acceptance: per org/project the day's stored bytes, object count, egress and failure counts are queryable and reconcile with `artifact`.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 06.5.2-T1 | `storage_usage_daily` roll-up job (GB-month average, egress, failures) | M | V1 | M2 | 01.5.1-T2, 01.5.1-T3, 10.3.1-T3 |
| 06.5.2-T2 | Usage-ledger handoff to 12's `usage_ledger_entry` with unique `billing_key` | S | V1 | M2 | 06.5.2-T1, 12.3.1-T1 |

## 10. Dependencies

- **01 foundation** (`01.4.1-T1` migrations, `01.4.1-T3` RLS baseline, `01.5.1-T2`/`01.5.1-T3` outbox + pg-boss, `01.3.1-T1` OpenAPI skeleton): storage emulator choice (licence check ⚠️, DECISIONS §4), migration tooling, contract generation for the artifact endpoints, CI for the fixture suite.
- **02 identity/tenancy** (`02.6.1-T1` RLS template + policies, `02.2.1-T1`/`02.2.1-T2` permission catalogue and guard, `02.7.1-T1`/`02.7.1-T2` audit core): `org_id` scoping + RLS, `artifact:write` / `artifact:read` / `artifact:read_sensitive`, project-level permission checks, audit core.
- **04 ingestion/adapters** (`04.7.1-T1` adapter protocol schema, `04.5.1-T1` Appium adapter (V2)): adapter contract for artifact capture options and redaction; CLI upload path; producer-side kind declaration; the contract is extended in 04, the server side is owned here.
- **05 runs/orchestration** (`05.2.1-T1` run creation, `05.3.3-T1` `case_attempt`, `05.3.2-T2` reconcile, `05.3.2-T5` `evidence_revision`): run/attempt ids and lifecycle, reconcile triggering `freeze`, late-window handling that produces a new revision, cancel-race behaviour (a canceled run keeps evidence but never becomes `complete`).
- **08 healing** (`08.7.1-T1` knowledge records): knowledge records and candidate context deleted by tenant deletion (V2 story 06.4.4-T2); healing context must be redacted before it leaves the tenant (DECISIONS §7).
- **09 web UI:** artifact lists and the viewer entry points; `sensitive` gating in the UI; UX rules from 00 §11 (empty, cancelled, partial-evidence states).
- **10 operations** (`10.1.1-T4` isolated viewer origin, `10.3.1-T3` metrics catalogue, `10.5.1-T1`/`10.5.1-T2` backups): DNS/TLS for the isolated viewer origin, nightly job scheduling, metrics pipeline, backup coverage for the deletion report, restore drill that includes object storage.
- **11 security/compliance** (`11.1.1-T1` threat model incl. the artifact/viewer boundary; ⚠️ no 11 task defines the masking secret taxonomy — `11.3.1-T3` and `11.3.1-T5` depend on `06.2.3-T1`, they do not provide its input): threat model for artifacts and the viewer, secret taxonomy for masking, DPA and retention/deletion publication, pen test scope.
- **12 pilot/commercial** (`12.3.1-T1` usage ledger): published retention defaults and deletion period, storage/egress pricing inputs.

Sibling story ids referenced above are ⚠️ pending confirmation: `plans/` contained only `00-overview.md` when this plan was written; ids follow 00 §6 and are reconciled during integration.

## 11. Open questions

| # | Question | Owner | Needed by |
|---|---|---|---|
| 06-O1 | Confirm artifact 14 d / metadata 90 d defaults with the pilots (cost vs. audit need) | Owner + Tech Lead | M2 end |
| 06-O2 | Storage region/provider and any VN residency requirement (O3) | Owner | M2 start |
| 06-O3 | Maximum single artifact size per kind (2 MB assumed from the capacity model; traces and video exceed it) | Tech Lead | 06.1.2-T3 |
| 06-O4 | Are Playwright traces `sensitive` by default (they can embed request bodies and auth state)? | Tech Lead + security review | 06.2.1-T1 |
| 06-O5 | Which object-storage emulator is allowed by the licence rule (⚠️ DECISIONS §4) — decides testcontainers setup — **Closed 2026-09-25:** SeaweedFS (Apache-2.0 ✅), MinIO rejected (AGPL-3.0 ✅); DECISIONS §4, 01.8.1-T1 | Tech Lead | 01, before 06.1.2-T1 |
| 06-O6 | Is `reject_on_secret` needed in V1, or is alert-and-classify enough for the first pilot? | QA lead | 06.2.3-T1 |
| 06-O7 | Published deletion period for tenants (days) and whether backups count inside it | Owner + legal input | 06.4.4-T1 |

## 12. Estimate summary

| Epic | V1 | V2 | V3 | Total |
|---|---|---|---|---|
| 06.1 Upload path and artifact state machine | 4.5 | 0 | 0 | 4.5 |
| 06.2 Sensitivity, redaction and masking | 3.5 | 0 | 0 | 3.5 |
| 06.3 Safe parsing and isolated viewer | 7.0 | 0.5 | 0 | 7.5 |
| 06.4 Retention, deletion and sweeper | 4.0 | 0.5 | 0 | 4.5 |
| 06.5 Evidence revision interface and storage usage | 4.0 | 0 | 0 | 4.0 |
| **Total** | **23.0** | **1.0** | **0** | **24.0** |

→ with 20 % buffer **28.8** person-days (one senior full-stack TS developer; 00 §6 units).

## 13. Contract change requests

> **Status:** Applied 2026-09-24: 06-CR-1 → 00 §9.3; 06-CR-2 → 00 §7.

| # | Request | Why |
|---|---|---|
| 06-CR-1 | Add `GET /v1/artifacts/{a}/download-url` to 00 §9.3 with permission `artifact:read` (plus `artifact:read_sensitive` for sensitive artifacts). | The isolated viewer needs a short-lived, per-view URL; without a row in the core table the endpoint would be invented in three places (06, 09, 04 CLI). |
| 06-CR-2 | Pin the artifact `kind` allowlist (`trace`, `video`, `screenshot`, `raw_report`, `dom_snapshot` — 00 §3) and the `sensitivity` values (`normal`, `sensitive`) in 00 §7. | Adapters, the viewer and the policy engine must agree on both enums; leaving them implicit invites drift between 04, 06 and 09. |
