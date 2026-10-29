# Evidgate — delivery status

Updated 2026-10-27. Source of truth for tasks: `plans/01`–`13` and `plans/features/`; this file tracks execution only. Task ids and sizes come from `scripts/plan_graph.py`.

## 1. Snapshot

### Resume here
- S124 (08.6.3-T1, `0052_healing_switch.sql`) is merged (#260, 569b538f); 11.5.1-T5 (`0053_security_security_event.sql`) is merged (#286, 560b5aba), 0054 (08.5.1-T1.1/T1.2, #306) merged, next free `0055`; T1 stays partial. Older line: Develop: `787f592` (PR #228, S102 02.3.2-T2, merged); 220 first-parent PR merges into develop. Migration `0048_identity_session_routes.sql` is merged in #228. S115 (05.3.2-T5) is in review with `0049_orchestration_evidence_revision.sql` (PR number in the delivery row below); the migration lane is held by S115 until it merges, next free migration `0051`. Closed owner-review rows now live in `docs/phase-review-archive.md` (S127, merged #266, c91f63a5); a row moves there verbatim in the commit that closes it.
- Open PRs: the S91 close-out PR plus dependabot PRs (not lanes).
- Wave layout (owner 2026-10-26): 1 backend + 2 web-only panels; a web-only lane (diff only `apps/web/**`, `fixtures/web-api/**`, `docs/`, `plans/`, `STATUS.md`, `AGENTS.md`) runs phase 1 + `npm run e2e -w apps/web` and never takes the Docker gate lock (`.omp/coordinator/PROJECT.md §Gate`).
- Active lanes (SESSION-CLAIMS 2026-10-27): S91 10.3.1-T2 not started (T1 merged, #203, closed out here); S98 09.1.1-T5 web-only (merged, #213, closed out here). S97 (03.2.1-T5) and S99 (09.1.1-T9.2, #209) are merged and closed out here. S102 (02.3.2-T2, sessions/logout) is merged (#228, 787f592) and closed out here.
- Fully landed on develop (counting S115 05.3.2-T5, S123 05.3.2-T2 and S131 11.5.2-T1 and S132 10.11.1-T3 once merged): 186/657 tasks; 190.75/704.25 raw person-days; remaining 471 tasks / 513.5 d. Via merged PRs: 159 tasks / 165.75 d; the other 27 tasks / 25.0 d are direct commits on develop. Method: task ranges in merged STATUS rows expanded against plan_graph task ids; a parent counts only when all its sub-rows landed.
- Landed by phase (done tasks / raw d): G1 136 / 146.75; G2 44 / 38; G3 0 / 0; G4 6 / 6.
- Plan totals: 657 tasks / 704.25 raw / 845.1 buffered person-days (S130 F47 added 02.3.2-T3, S, V1, G1; the owner decision of 2026-10-30 added 01.2.1-T6, M, V1, G1; the S93 canonical figures were 655 / 702.75 / 843.3). `plan_graph.py` verified in this S102 close-out on `787f592`: errors 0, 205 stories, TOTAL 655 / 702.75 raw / 843.3 buffered. `check_features.py` still exits 1, now with 8 missing 13.x feature plans (13.5.1 to 13.9.1) after the S112 plans for 13.3.1 and 13.4.1 (queue F37 first part, §6 row); both new plans are `proposed, owner confirmation pending`.
- CI: `ci.yml` runs `verify.sh` stages; recent delivery records report CI refused for billing (owner treats as pass), not CI green.
- Owner decisions: O1 (engine), O4, O8 and O10 decided; O3 deferred. O2 note recorded, owner confirmation pending (DECISIONS §8). Open: O5, O6, O7, O9, O11, O12. B2 flow approval and product questions stay in §6.
- Next picks, web-only lane: 09.1.1-T5 is merged (#213) and 09.1.1-T9.2 is done (#209), so the web-only lane moves to G2 (§5).
- **Priority override (owner 2026-10-29): the code-review fix register `plans/features/REVIEW-2026-10-29.md` comes first — its P0 rows (CR-01…CR-06, 6.0 d) before any §5 pick in either lane, its P1 rows before the task named in their *Must precede* cell (07.2.1 waits on CR-07…CR-11); the next free migration `0055` goes to CR-03.** Next picks, backend lane (after P0): refill order in `.omp/coordinator/prompts/held/README.md`; when a backend slot frees, 08.5.1-T1 (XL, split, migration lane) unless §5 ranks another G1 task higher, and 04.1.2-T1 only after S91 releases.
- Next step: §5, 90 dependency-ready tasks / 101.5 d split by backend and web-only lane; §6 lists the non-graph blockers and carried items.
- STATUS split (S126, 2026-10-28; merged #264, a9159257): §3-§4 delivered waves moved verbatim to `docs/status-archive.md` together with the closed §6 rows; the live §6 table is `docs/status-carried.md` (add new carried rows there, at the end).

## 2. Done

Counted by expanding task ranges in merged STATUS rows against plan_graph task ids; a parent task counts only when every defined sub-row has landed on develop; sizes XS .25 S .5 M 1 L 2 XL 3. detail: carried row 'STATUS §2 count log S112-S128' (S144 F61).

| Delivery scope | Tasks | Raw person-days |
|---|---|---|
| Fully landed on develop | 186 | 190.75 |
| Via merged PRs | 159 | 165.75 |
| Direct commits on develop, no PR | 27 | 25.0 |
| Remaining | 471 | 513.5 |
| Plan total | 657 | 704.25 |

Seven partial parents in this table (01.7.1-T3, 01.8.1-T3, 10.2.1-T1, 09.1.1-T6, 10.7.1-T4 (T4.2 staging drill awaits staging), 09.3.1-T2, whose T2.1 is merged (#216, 1742764), T2.2 is delivered by S146 (#302, fbb2fe91) and whose remainder is T2.3 and T2.4; and 09.8.1-T1, whose T1.1 + T1.2 landed in #284 and whose remainder is T1.3 + T1.4) are excluded from done counts and dependency satisfaction; their landed subrows and remainder are recorded in the delivery table and in §5, except 09.1.1-T6, whose remainder T6.2 waits on 02.3.1-T3. Plan-only changes and queue rows add no task credit. Tasks cells list exactly the ids in the delivery evidence; the pre-S93 table also claimed `03.1.1-T3` and `03.1.3-T2/T3`, which have no landed delivery and were removed.

### Done by area

| Group | Tasks | XS | S | M | L | XL | Person-days |
|---|---|---|---|---|---|---|---|
| 01 | 45 | 2 | 10 | 21 | 11 | 1 | 51.5 |
| 02 | 29 | 0 | 8 | 18 | 3 | 0 | 28 |
| 03 | 8 | 0 | 3 | 4 | 1 | 0 | 7.5 |
| 04 | 22 | 0 | 7 | 12 | 3 | 0 | 21.5 |
| 05 | 19 | 0 | 5 | 9 | 5 | 0 | 21.5 |
| 06 | 12 | 0 | 4 | 8 | 0 | 0 | 10 |
| 07 | 6 | 0 | 2 | 4 | 0 | 0 | 5 |
| 08 | 1 | 0 | 0 | 1 | 0 | 0 | 1 |
| 09 | 22 | 0 | 6 | 13 | 3 | 0 | 22 |
| 10 | 5 | 0 | 2 | 2 | 1 | 0 | 5 |
| 11 | 11 | 1 | 5 | 4 | 1 | 0 | 8.75 |
| 12 | 1 | 0 | 0 | 0 | 1 | 0 | 2 |
| 13 | 5 | 0 | 0 | 3 | 2 | 0 | 7 |
| TOTAL | 186 | 3 | 52 | 99 | 31 | 1 | 190.75 |

### Done by phase

| Group | Tasks | XS | S | M | L | XL | Person-days |
|---|---|---|---|---|---|---|---|
| G1 | 136 | 3 | 32 | 73 | 27 | 1 | 146.75 |
| G2 | 44 | 0 | 18 | 23 | 3 | 0 | 38 |
| G3 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| G4 | 6 | 0 | 2 | 3 | 1 | 0 | 6 |
| TOTAL | 186 | 3 | 52 | 99 | 31 | 1 | 190.75 |

### Landed task groups

| Story | Tasks | PR | What shipped |
|---|---|---|---|
| 01.1.1 | 01.1.1-T1–T3 | merged (#2, 4c74fed) | Toolchain pins, workspace skeleton and layout check. |
| 01.2.1 | 01.2.1-T1–T5 | merged (#2, 4c74fed) | Analyzers, format, strict TypeScript and architecture tests. |
| 01.3.1 | 01.3.1-T1–T3 | merged (#2, 4c74fed) | OpenAPI, schemas and adapter-contract trees. |
| 01.9.1 | 01.9.1-T1–T3 | merged (#2, 4c74fed) | Architecture decisions and licence matrix. |
| 01.4.1 | 01.4.1-T1–T4 | merged (#3, b3f9261) | Migrator, roles and RLS baseline (0001–0003). |
| 01.1.2 | 01.1.2-T1–T3 | merged (#3, b3f9261) | Api/Worker hosts and eleven module pairs. |
| 01.8.1 | 01.8.1-T1/2 | merged (#3, b3f9261) | SeaweedFS parity, compose stack and local startup. |
| 01.4.2 | 01.4.2-T1 | merged (#6, cb636ef) | Tenant scope, EF/Dapper binding and interceptor. |
| 01.3.2 | 01.3.2-T4 | merged (#6, cb636ef) | RFC 8785 canonical hashing in .NET and CLI. |
| 01.6.1 | 01.6.1-T1 | merged (#6, cb636ef) | Shared .NET/Vitest test conventions. |
| 02.1.1 | 02.1.1-T1 | merged (#6, cb636ef) | Organization/project schema (0004–0005). |
| 01.4.2, 01.5.1, 01.6.1 | 01.4.2-T2/3, 01.5.1-T1/2/6, 01.6.1-T2/5 | merged (#9, 73f957b) | Durable task, outbox and idempotency tables (0006–0008), schema conventions, UUIDv7, integration harness and Playwright workspace. |
| 01.4.2, 01.5.1, 01.7.1 | 01.4.2-T4, 01.5.1-T3–T5, 01.7.1-T1 | merged (#10, c70585e) | C2 pipeline, endpoint filter, outbox dispatcher (0009), retry/dead-letter/operator routes, crash suites and real CI workflow. |
| 01.3.2, 01.6.1, 01.7.1 | 01.3.2-T1–T3, 01.6.1-T3/4, 01.7.1-T2/4, 01.7.1-T3 partial (landed T3.2; remainder T3.1) | merged (#11, 6dce731) | SDK/codegen drift, dual-runtime validation, parity and contract suites, fixture loaders, isolation, licence/audit/SBOM/Dependabot gates. Branch-protection remainder is not completed. |
| 02.1.2, 02.2.1, 02.6.1, 02.9.1, 04.7.1, 05.1.1, 10.1.1, 10.2.1, 10.8.1, 11.1.1 | 02.1.2-T1, 02.2.1-T1/2, 02.6.1-T1, 02.9.1-T2, 04.7.1-T1, 05.1.1-T1, 10.1.1-T1, 10.8.1-T1, 11.1.1-T2, 10.2.1-T1 partial (landed T1.1–T1.3; remainder T1.4) | merged (#12, db4eb44) | Permissions, membership, RLS self-check, composite FK coverage, adapter/manifest schemas, environment matrix, container images, provider memo and DoR checklist. CDN remainder is not completed. |
| 02.1.1 | 02.1.1-T2/3 | direct commit on develop dcac088, no PR | Registry API, founder membership, quotas and slug grammar (0012–0013). |
| 02.6.1, 02.6.2 | 02.6.1-T2/3, 02.6.2-T1 | direct commit on develop 1e31175, no PR | RLS/definer lint, pre-tenant resolvers, tenant resolution, service-role claim ceiling and org list (0014). |
| 02.7.1 | 02.7.1-T1–T3 | direct commit on develop 7c73b84, no PR | Audit chain, audited mutation pipeline, list/export and chain-verification task (0015). |
| 02.4.1, 02.4.2, 01.8.1 | 02.4.1-T1–T4, 02.4.2-T1/2, 01.8.1-T3 partial (landed T3.1; remainder T3.2) | direct commit on develop a0c3a08, no PR | GitHub OIDC/ingest-token exchange and administration, producer credentials, replay purge and seed-dev (0016–0019). Full local-stack quickstart remains uncertified. |
| 03.1.1, 03.1.3, 03.2.1 | 03.1.1-T1/2, 03.1.3-T1, 03.2.1-T1–T4 | direct commit on develop 0a01875, 2111dfa, 375bf8b, no PR | Case identity, immutable revisions, suites, variants, dependencies, discovery/registration, case/suite routes, diff/dry-run and shared catalog contracts (0020–0022). |
| 03.2.1 | 03.2.1-T5 | direct commit on develop 6a0a102 (0a01875 adjusted the ETags); credited S100; PR #205 merged (423252c) | Suite revision diff/preview API (member/variant/edge/strategy delta) and `ETag`/`If-None-Match` 304 on the suite head; evidence `src/Modules/Evidgate.Modules.Catalog/Endpoints/CatalogEndpoints.cs:83`, `Application/Queries/SuiteQueries.cs:129,194`, tests `tests/Tenancy.IntegrationTests/CatalogSuiteTests.cs:96` (`Suite_CanBeCreatedListedAndRead_WithAnETagAnd304`) and `:595` (`Diff_ReportsMembersEdgesAndStrategyChanges`), preview `:662` (`DryRun_ReturnsTheRealHash_AndWritesNothing`); detail: carried row '03.2.1-T5 suite diff evidence' (S144 F61).|
| 05.1.1, 05.1.2 | 05.1.1-T2–T4, 05.1.2-T1/2 | direct commit on develop 312f55f, no PR | Manifest upload/deduplication and immutable environment revisions (0023–0024). |
| 04.3.1 | 04.3.1-T1 | merged (#13, 02238c1) | Playwright reporter callbacks, event-envelope spool and capability declaration; execution-key/capture-control limitations remain in §6. |
| 04.2.1 | 04.2.1-T2 | merged (#15, 9a856ab) | CLI configuration, dev token, secret redaction, command/exit-code registry and redirect-refusing SDK client. |
| 04.2.1 | 04.2.1-T1 | merged (#23, 8d89194) | CLI auth login/logout, CI exchange, protected token cache and GitHub environment masking. |
| 07.1.1 | 07.1.1-T1 | merged (#16, d7423ba) | Append-only policy revisions, content address, numbering, RLS and domain guards (0025). |
| 02.9.1 | 02.9.1-T1 | merged (#19, 1fc77d3) | Real-host cross-tenant API isolation suite and served-route coverage gate. |
| 04.7.1 | 04.7.1-T2 | merged (#24, 4db71dd) | Adapter capability persistence and first-send registration, exported protocol negotiation and mode rules (0026). |
| 07.1.1 | 07.1.1-T2 | merged (#21, e822473); follow-up merged (#22, 81efe0d) | Server-owned policy hash, exact-number validation and integrity-checked internal reads; follow-up restores locked dependencies and adds numeric boundary coverage. |
| 07.1.1 | 07.1.1-T3 | merged (#27, fc203ae) | Policy create/read/revision-list endpoints, closed validation schema and 64 KiB body cap. |
| 07.1.1 | 07.1.1-T4 | merged (#75, 777c725) | Policy activation/mode/audit/run pinning and append hot-path improvements (0033). |
| 07.3.1 | 07.3.1-T1 | merged (#30, 8360a11) | Waiver create/approve/revoke/list, no self-approval and state-machine guard (0027). |
| 07.3.1 | 07.3.1-T2 | merged (#58, 35ea058) | Quarantine create/remove/list, critical-case refusal and maintainer-only permission (0030). |
| 04.2.2 | 04.2.2-T1 | merged (#33, e1c14ea) | CLI manifest upload, bounded regular-file validation, errors and 401 cache purge. |
| 04.7.1 | 04.7.1-T3 | merged (#34, 529cd0f) | Shared .NET/TypeScript protocol compatibility matrix and unknown-field corpus. |
| 04.2.4 | 04.2.4-T2 | merged (#37, 280b680) | CLI quickstart GitHub/generic recipes and confined exclusive file creation. All rendered command placeholders are now real; package publication remains open in §6. |
| 02.9.1 | 02.9.1-T3 | merged (#28, ba552d2) | Catalog-driven RLS/grant/isolation suites, real JWKS CI fixture and isolation CI job; required-check setting remains owner-blocked. |
| 04.3.2 | 04.3.2-T1 | merged (#32, 32525b6) | Reporter annotation/sidecar identity, variant-hash parity and fail-closed identity gate. |
| 10.1.1 | 10.1.1-T2 | merged (#42, e518c66) | Secret-provider abstraction, guarded consumers and no-secret-literal gate. |
| 02.2.1 | 02.2.1-T3 | merged (#40, 445d68f) | Role/permission seed matrix, viewer denials, service ceiling and secret-exposure tests. |
| 11.2.1 | 11.2.1-T1 | merged (#46, 7ffab75) | Fork-safe customer CI security guide, recipe and YAML-aware lint/self-tests. |
| 04.3.2 | 04.3.2-T2 | merged (#44, b5c579e) | Reporter status/xfail/retry mapping, bounded redacted error summary and skip metadata. |
| 11.6.1 | 11.6.1-T1/2 | merged (#49, 3f3086b) | Dependency/licence gate negative controls, supply-chain recipe and fork-safe quickstart lint wired into the gate. |
| 02.4.3 | 02.4.3-T1 | merged (#50, 40d0653) | ADR-0007 GitHub App decision proposal, permission/token matrix and key-custody runbook stub; owner questions remain open. |
| 06.1.1 | 06.1.1-T1 | merged (#51, a924a9a) | Artifact/kind-policy schema, RLS, keys and EF model (0028). |
| 04.1.2 | 04.1.2-T4 | merged (#55, 1fe2ab4) | Producer-slot schema, registration, first-write binding and read contracts (0029). |
| 11.1.1 | 11.1.1-T1 | merged (#57, d0d532d) | Threat model v1, trust boundaries, STRIDE controls, residual-risk register and model-consistency gate. |
| 06.1.1 | 06.1.1-T2 | merged (#62, b953d21) | Server-chosen artifact keys and internal artifact mint command. |
| 05.2.1 | 05.2.1-T1 | merged (#65, e10a221) | Transactional run/planned-execution creation, shard slots, outbox event and run FKs (0031). |
| 05.2.1 | 05.2.1-T2 | merged (#69, ebb1985) | Idempotent run creation with canonical request identity, replay and conflict semantics. |
| 05.2.1 | 05.2.1-T4 | merged (#77, 44724f0) | CI shard/state expectations and atomic queued INSERT acceptance tests. |
| 05.2.1 | 05.2.1-T5 | merged (#83, fb914f3) | Run purpose/replay-grant request validation and immutable purpose storage; actual replay grants remain unavailable. |
| 05.3.1 | 05.3.1-T1 | merged (#93, 66f2093) | Run state machine, transition contract and state read; nullable evidence pointer (0036). Event and finalize callers have since landed. |
| 05.3.3 | 05.3.3-T1 | merged (#67, d5efd52) | Case-attempt schema, normalization and artifact-attempt FK (0032). |
| 05.3.3 | 05.3.3-T2 | merged (#71, f52d349) | Batch attempt writer, blocked-dependant resolver contract and telemetry. |
| 06.1.2 | 06.1.2-T1 | merged (#73, 9e995ac) | Object-storage abstraction, presigned upload URL, run/policy/slot checks and lifecycle assertion. |
| 06.1.2 | 06.1.2-T2 | merged (#79, 398d832) | Artifact completion verification/promotion, ready/rejected persistence and staging cleanup outbox. |
| 04.1.1 | 04.1.1-T1 | merged (#81, 9fe3149) | Atomic event/batch ingest, producer checks, duplicate handling, attempts and usage event (0034). |
| 04.1.1 | 04.1.1-T3 | merged (#88, f281f7a) | Raw/expanded gzip limits, malformed-input refusals and rejection telemetry. |
| 06.3.1 | 06.3.1-T1 | merged (#85, 3cc6aad) | Bounded XML/JSON parser profile, MIME prefix sniffing and parser-construction architecture guard. |
| 06.3.1 | 06.3.1-T2 | merged (#91, 342641e) | Bounded archive profile, fail-closed MIME table and pinned prefix reads. |
| 06.3.2 | 06.3.2-T1/2 | merged (#95, 9602e54) | Hostile-input corpus, generated bombs, seeded mutation fuzzing, nightly persistence and promoted crash regression. |
| 04.1.2 | 04.1.2-T5 | merged (#87, d3a4854) | Producer-slot close/declarations and CI-run binding comparison (0035). |
| 05.2.2 | 05.2.2-T1 | merged (#99, e17ae0f) | Immutable branch/PR/commit identity, verified-claim mapping and advisory unverified fields (0037). |
| 05.2.2 | 05.2.2-T2 | merged (#106, e225950) | Cursor-paged filtered run list, SDK and ref index (0038). |
| 04.1.1 | 04.1.1-T4 | merged (#97, 367ccb0) | Ingest ordering/restart and deadlock-safe first-event queued→running transition. |
| 06.1.2 | 06.1.2-T3 | merged (#100, a7d5342) | Per-kind static cap/media defaults, lower-only overrides, 413/422 mapping and upload-body limit. |
| 04.2.2 | 04.2.2-T2 | merged (#102, b48c204) | CLI runs create with validated identifiers, stable idempotency key and server errors. |
| 05.3.2 | 05.3.2-T1, 01.7.1-T3 partial (landed T3.2; remainder T3.1) | merged (#117, 43a54e8) | Service-principal finalize, slot close, state transitions, idempotency and reconcile outbox; consumer remains open. |
| 13.1.1, 13.1.2, 13.2.1 | feature plans (T0, docs only) | merged (#115, 83ede00) | G1 authoring/settings feature plans and receiving open-items register; no task completion credited to plan preparation. |
| 13.1.1 | 13.1.1-T1, 01.7.1-T3 partial (landed T3.2; remainder T3.1) | merged (#119, f08eba8) | Authoring tree/manual-case schema, shared version writer and code allocator (0039). |
| 13.1.1 | 13.1.1-T2 | merged (#131, 316d93a) | Authoring CRUD API, concurrency/idempotency, sibling bounds, prefix editing, permissions and SDK (0040). |
| 04.2.3 | 04.2.3-T1/2, 10.2.1-T1 partial (landed T1.1–T1.3; remainder T1.4) | merged (#121, 330f0dc) | CLI spool/lock/cursor, bounded gzip batches, retries, events send and runs finalize. |
| 04.2.4 | 04.2.4-T1, 10.2.1-T1 partial (landed T1.1–T1.3; remainder T1.4) | merged (#124, 9917b09) | CLI artifact upload with descriptor-pinned streaming, signed-header checks, journal and resume. |
| 04.3.3 | 04.3.3-T1, 10.2.1-T1 partial (landed T1.1–T1.3; remainder T1.4) | merged (#124, 9917b09) | Reporter shard identity, persistent shard floor, single writer and spool bounds. |
| 04.3.3 | 04.3.3-T2 | merged (#188, c3f64e5) | Reporter attachment classification, guarded paths, bounded upload drain and artifact references. Built-in identity cannot yet upload without real execution keys; sensitivity/webm limits remain in §6. |
| 06.5.1 | 06.5.1-T1/2, 10.2.1-T1 partial (landed T1.1–T1.3; remainder T1.4) | merged (#127, 3ebba14) | Evidence freeze contract/resolution and deterministic artifact-set hash; guards fail closed. |
| 09.1.1 | 09.1.1-T1 | merged (#130, e2640aa) | Real Angular standalone zoneless app, build/unit/E2E wiring, raw/gzip budgets and lint. |
| 09.1.1 | 09.1.1-T11, 10.2.1-T1 partial (landed T1.1–T1.3; remainder T1.4) | merged (#129, a17bcba); merged (#138, 42dc21a) | Design flows/tokens/layout plus state sheet and token lint (all four subrows landed). External B2 flow approval remains open; no owner approval is implied. |
| 09.1.1 | 09.1.1-T2 | merged (#134, 108f5ef) | Web integration-fit spike and ADR-0008: HttpClient SDK adapter, zoneless, Trusted Types, CDK/virtual scroll and i18n. |
| 09.1.1 | 09.1.1-T3 | merged (#137, 5483e1f) | HttpClient-backed SDK gateway, typed errors, request/idempotency/XSRF interceptors, fixtures and boundary lint. |
| 09.1.1 | T6 partial (landed T6.1; remainder T6.2) | merged (#139, 81da3c8) | i18n catalogs, vi/en builds, extraction/key-completeness checks and per-locale budgets. Locale switch/profile preference remains open. |
| 09.1.1 | 09.1.1-T7 | merged (#144, 9ae0b55) | Shared verdict/state chips, token-styled CDK table, gallery, i18n and automated a11y coverage. |
| 09.1.1 | 09.1.1-T10 | merged (#143, d0baa71) | App-origin CSP/Trusted Types, nonce policy/build helpers, locale server and bypass lint. |
| 09.1.1 | 09.1.1-T12 | merged (#146, 8445b73) | Dev-only fixture backend, variants/latency and OpenAPI response validation/leak gate. |
| 09.1.1 | 09.1.1-T8 | merged (#147, 2d0159d) | Request-state patterns, conflict dialog, Retry-After display and one-intent retry helper. |
| 09.1.1 | 09.1.1-T4 | merged (#151, 8991020) | Phase/permission/scope-aware shell, guards, switcher, theme and Chromium fixtures E2E; real client grants/login remain open. |
| 09.1.1 | 09.1.1-T9.1 | merged (#153, 37f5b79) | Scrubbed lazy web telemetry, route/API/error/vital spans and configurable no-op exporter. |
| 09.1.1 | 09.1.1-T9.2 (T9 complete with T9.1) | merged (#209, 8e33870) | Emission config and opt-out (`doNotTrack`/`msDoNotTrack`/`globalPrivacyControl`), optional `evidgate-service-version` meta, the committed Playwright vitals smoke in `e2e/telemetry.spec.ts` and the audit export cap event; collector origin and CORS stay with 10.3.1-T2/10.2.1-T1.4. |
| 09.1.1 | 09.1.1-T5 (T5.1 + T5.2) | merged (#213, 625e950) | `/login` (Google and GitHub buttons, no password field) and `/auth/callback` replace the placeholder; shared `validateReturnUrl` (`core/routing/return-url.ts`); `SessionStore` client-side logout with an in-memory signed-out latch behind the `SessionEnder` seam, single 401 re-auth keeping the validated `returnUrl`, teardown registry for the connect-ci health timer; login round-trip in `e2e/shell/auth.spec.ts` (fixture IdP via `page.route`). The server logout route is merged in 02.3.2-T2 (#228, 787f592); the web call remains client-only until the F41 `SessionEnder` swap (§6). |
| 09.7.1 | 09.7.1-T1 | merged (#156, 93b104b) | Settings shell, phase/permission-aware tabs, guards, responsive selector and secret-field lint. |
| 02.3.1 | 02.3.1-T1 | merged (#160, 1116d24); merged (#164, ca2bf98) | Google OIDC/PKCE/state/JWKS and GitHub OAuth2 providers, user/session schema and real login completion (0041–0042); no 501 seam remains. |
| 09.2.1 | 09.2.1-T1 | merged (#159, 15b5650) | Onboarding shell/progress/project creation, signalStore, fixture-backed resume and accessible stepper. |
| 09.2.1 | 09.2.1-T2 | merged (#165, 3456d62) | Connect-CI step, GitHub binding/ingest token creation/rotation and component-scoped one-time reveal. |
| 09.7.1 | 09.7.1-T2 | merged (#158, 0db85e3); follow-up merged (#171, ed3223a) | Members/roles tab and built-in role preview; follow-up adds role-edit dialog with sequential writes, refetch and refusal feedback. |
| 02.1.2 | 02.1.2-T2 | merged (#173, ff29a38) | Invite create/resend/cancel, quota/binder contracts, expiry sweep and peppered one-time token (0043); login wiring subsequently landed. |
| 09.7.1 | 09.7.1-T6 | merged (#166, a71387f) | Audit log filters/cursor pages, bounded client-side CSV export and support marker. |
| 09.2.1 | 09.2.1-T7 | merged (#168, 010eba0) | Connection-health derivation and CLI quickstart, token-use timestamp and credential-boundary refresh. |
| 02.3.1 | 02.3.1-T2 | merged (#180, b1d1918) | Invite/domain binding on login, explicit provider linking, audit and membership-user FK (0044). |
| 09.7.1 | 09.7.1-T3 | merged (#177, 9e9cc58) | Secrets metadata/rotation/reminder tab and component-scoped one-time reveal. |
| 09.7.1 | F12 (queue row, no sub-task) | merged (#177, 9e9cc58) | Invite UI reveals/resends/cancels the one-time token; replay without a token prompts Resend. |
| 09.7.1 | F17 (queue row, no sub-task) | merged (#182, ffefc8b) | Fixtures E2E regressions fixed: audit heading/tab expectation and navigation chunk bound. |
| 09.7.1 | 09.7.1-T4 | merged (#182, ffefc8b) | Policy revisions/diff/publish UI, concurrency errors and contract-validated policy fixtures. |
| 09.7.1 | F15 + F16 + F20 (queue rows, no sub-task) | merged (#184, c72a2bf) | Ci-binding metadata pagination, secret lint template/result-type coverage and policy/fixture leak fixes. |
| 13.1.1 | 13.1.1-T3 | merged (#176, c185810) | Authoring timeline/version reads and restore with sibling locking, preconditions and route contracts. |
| 13.1.1 | 13.1.1-T5 | merged (#186, 5cfcf9c) | CSV/XLSX/JSON import/export with bounded parser profile, dry-run, atomic apply, allocator batching and idempotent re-import. |
| 02.3.2 | 02.3.2-T1 | merged (#190, 88fc00f) | Real session authentication, rotation/revocation, throttled sliding expiry, pepper rotation and enforcing XSRF (0045). |
| 02.3.2 | 02.3.2-T2 | merged (#228, 787f592) | Session list, self/admin revoke, revoke-all and logout routes plus bounded leaf-first cleanup and expiry stamping via migration 0048. Whole task credit: the only defined sub-row T2.1 is landed (S, 0.5 d); T1 + T2 fully land story 02.3.2. The web `SessionEnder` swap/session screen and admin all-sessions verb remain separate F41 hand-overs, not unbuilt T2 sub-rows. |
| 13.2.2 | plan change (docs only, no task built) | merged (#194, c866fdc) | Role-profile/prompt-library story and feature plan only; eight planned tasks are unbuilt. |
| 13.x / all | status and consistency sync (docs only, no task built) | merged (#196, b6cace6) | STATUS.md rebuilt against merged work (163/655 tasks, 169.25/702.75 d), AGENTS.md, plan 00 §14/§15, plan 13 §4.4, features README and open-items made consistent; no task credit. |
| 13.2.2 | 13.2.2-T1 | merged (#199, 0114df0) | GitHub review of per-role prompts: `docs/research/role-prompts-github-review.md` (46 repos, licence ledger, gap matrix, ideas not copied; copy nothing for the first seed set `[INFERENCE]`). Docs only; the Done-when test `MemoAndLicenceLedgerComplete` is not built (§6 row "Role-prompt memo check"). |
| 04.1.1 | 04.1.1-T2 | merged (#201, 036c806) | `event_id` dedupe and `sequence_reused` mapping proven on real PostgreSQL; behaviour landed with T1, tests added (`IngestDedupeTests`). |
| 12.3.1 | 12.3.1-T1 (T1.1–T1.4) | merged (#198, 2f66be8) | Append-only `usage_ledger_entry` and `usage_rollup` (0046/0047), `IUsageLedger`/`IQuotaGate` contracts, idempotent `RecordUsageBatch` (one unnest statement, `ON CONFLICT DO NOTHING`). The operator route `POST /internal/operator/billing/usage` and its SDK drift gate stay with 12.3.1-T2.4 (COORDINATOR-DECISION S94); open hand-overs: N-1 probabilistic deadlock test (T5), `egress_gb` null quantity (T2.2), P-3 index include and F4 payload-mismatch (T5), price resolution (T3), `IQuotaGate` implementation (T4.2). |
| 10.3.1 | 10.3.1-T1.1–T1.4 | merged (#203, a9b2d09) | OpenTelemetry in Api and Worker: HTTP→Mediator→Npgsql trace with `request_id`; `run_id`/`job_id`/`trace_id` correlation on spans and logs, carried through the outbox/task row (`_evidgate_correlation`); resource attributes + head sampling with errors always kept; log redaction (`SecretRedactor`, fail closed) before the OTLP exporter; web-vitals intake schema `docs/runbooks/web-vitals-intake.md`. No migration. Residual hardening N1–N9 is in the §6 row "Telemetry redaction/baggage residuals (10.3.1-T1 delivery, S91, PR #203)". |
| 09.3.1 | 09.3.1-T1 (T1.1 + T1.2) | merged (#212, 408f9a8) | Run list at `features/runs/list/`: `RunListStore` (cursor paging, `ref_name` server filter, state/decision/rerun-scope URL filters applied to loaded rows with a 5-page/1000-row auto-load bound), `RunDataService`, virtual-scroll table with `EvidenceBadge` (unavailable) and `DecisionChip` columns, escaped `ref_name`/rerun labels, vi + en strings, review route `dev/run-list`. Decisions D-S101-Q1/Q2 (plan 09.3.1 Notes). |
| 09.3.1 | 09.3.1-T2.1, T2.2 (S103 UI, wire S146); 09.3.1-T4 (UI S111 #232, wire swap S146) | T2.1 merged (#216, 1742764); T2.2 + T4 wire merged in S146 PR #302 (fbb2fe91; T4 complete, counted; parent T2 partial) | Run detail at `features/runs/detail/` (`RunDetailPage`, `RunDetailStore`, run header, `ManifestPanelComponent`), route `runs/:runId`, run-list row link, `fixtures/web-api/run-detail*`, `run-attempts*`, review routes `dev/run-detail`, e2e `run-detail.spec.ts`, vi + en strings. S146 (#302) reads the manifest members and `listRunAttempts` from the wire; healing, decision, rerun labels stay unavailable (carried row 'Run detail web remainders (S146)'). detail: carried row '09.3.1-T2.1/T4 detail' (S144 F61).|
| 11.5.1 | 11.5.1-T1 (T1.1 + T1.2) | merged (#219, a905754) | `docs/security/audit-taxonomy.md`: event fields, 64 emitted actions with file:line, planned-not-emitted table, retention per class (`[OPEN]`), T8 chain design with 10.6.1 AC2; written review of 02.7.1 enforcement against `migrations/0015_identity_audit_log.sql` with gaps G1–G9 in §6. T1.2 verifier and tests shipped with 02.7.1. |
| 09.2.1 | 09.2.1-T5.1 + T5.2 (09.2.1-T5) | merged (#218, 9a0051f; #272, 5c69ba9a); 09.2.1-T5 complete, M, G2, counted in the S129 close-out | Onboarding step 5 `features/onboarding/steps/enable-collection.page.*`: read-only constant default advisory policy (`default-policy.ts`, T62 24 h late window, validated against the createPolicyRevision schema), `policy:write` gate with disabled confirm + "ask your release approver", confirm = createPolicyRevision then activatePolicyRevision (mode advisory, one Idempotency-Key per confirm intent), terminal panel from the response, fixtures `policy-activation-*`, e2e `onboarding-enable-collection.spec.ts`. detail: carried row '09.2.1-T5 onboarding step 5 detail' (S144 F61).|
| 11.1.1 | 11.1.1-T3, 11.1.1-T4 | merged (#224, aa42d7a) | ASVS 5.0.0 L2 level statement and a 17-chapter mapping with 39 published gap rows — 34 L2 gaps plus five traceability observations that ASVS 5.0.0 does not require at L2 (`docs/security/asvs-l2-mapping.md`), plus the V1 security-event baseline: five types with severity, detection point and consumer per row (`docs/security/security-event-baseline.md`). Closes area plan open question 1 (D-S107-Q1); counts bumped in the close-out, and every gap is carried by a §6 row titled "ASVS L2 gaps: …" or by the §6 row "Security-event baseline contradictions and gaps (11.1.1-T4)". |
| 10.7.1 | 10.7.1-T4.1 + T4.2 written half: key rotation runbook | merged (#222, 1adcc47) | `docs/runbooks/key-rotation.md` covers all six key classes (ingest tokens, CI OIDC trust, JWT signing key, webhook secrets, database credentials, provider keys via the 10.1.1-T2 `ISecretProvider`) plus the operator rotation path and its authorisation, and the T4.2 written half (§3: a new `pepper_vN` signs new tokens while old versions verify until their last token, with the session three-step overlap and the invite pepper that has none). Docs-only, tier T0. detail: carried row '10.7.1-T4 key rotation runbook detail' (S144 F61).|
| 11.2.1 | 11.2.1-T3 (T3.1 + T3.2) | merged (#225, f4c7de8) | `docs/security/ingest-token-review.md`: shipped ingest/CI token design reviewed against code at 96e0f17 (scope, TTL, OIDC claim validation, revocation, replay, T5 storage, single return, pepper custody and rotation, 17-row abuse table, five new gaps T3-G1..G5). Docs-only, tier T0. Whole task: counts +1 task / +1.0 d (M); nothing else in §5 was unblocked (04.9.1-T2, 08.4.3-T2, 11.2.1-T4 and 11.2.1-T5 wait on 11.2.1-T6/T4, 05.9.2-T1 on all of 11.2.1). |
| 06.4.6 | 06.4.6-T1 (T1.1 + T1.2) | merged (#234, 7bad46a) | `docs/adr/0009-event-table-partitioning.md` (proposed, owner confirmation pending): option C, monthly partitions by `run_created_at`, widened dedupe key, `DEFAULT` partition + plain `DETACH` with `lock_timeout` ≤ 5 s (D-S113-Q1; PostgreSQL 18 forbids `DETACH CONCURRENTLY` with a default partition), retention table, 90-day cap proposal; CR proposals logged `[OPEN]` as OI-06-17. Docs-only, tier T0. Whole task: counts +1 task / +0.5 d (S); 06.4.6-T2 and T3 remain. |
| 05.5.2 | 05.5.2-T1 (T1.1 + T1.2) | merged (#236, 148bc06) | CI re-run contract change (docs only, tier T0, proposed, owner confirmation pending): 00 §8.1 gains `rerun_scope = ci_attempt` and the attempt rule (triple identity, new run per higher attempt with parent = highest stored lower attempt, `stale_attempt`, partial-attempt label), §9.4 binds the whole triple; 00 §15 row, plan 05 FR-05-7/§4/producer-binding line, DECISIONS §6 F10–F13 (GitHub facts ✅, one ⚠️ kept), OI-05-05 reworded, OI-05-16 added. No claim, wire field, code or contract file changed. |
| 05.3.2 | 05.3.2-T5 (T5.1 + T5.2) | merged (#239, `64318718`) | `migrations/0049_orchestration_evidence_revision.sql`: append-only `evidence_revision` (app role SELECT + INSERT, FORCE RLS, closed `missing[]` codes incl. `setup_error`, gap-free `revision_no` trigger), `evidence_revision_run_idx`, composite pointer FK `run_current_evidence_revision_fk`, `update (current_evidence_revision_id)` grant and `run_evidence_pointer_guard` (latest revision of the same run only, never cleared by the app role (the owner erasure sweep may)). `GetRun` unchanged. D-S115-Q1 (proposed): lock order run row first, then insert, then pointer. |
| 05.3.2 | 05.3.2-T2 (T2.1–T2.4) | merged (#257, e9edb866); T2.1+T2.2 in (#253, 8f3c3eed) | Reconciler, whole. T2.1+T2.2 (#253): `ReconcileRunTaskHandler` claims `orchestrator.run.reconcile` through the task queue (`SKIP LOCKED`), locks the run row `FOR NO KEY UPDATE` first (S115 lock order) and compares declared `(last_sequence, count)` with the acknowledged events in one grouped statement, reporting `missing_shard`, `count_mismatch` and `unknown_status`. detail: carried row '05.3.2-T2 reconciler/acceptance detail' (S144 F61). Acceptance executed at the merged head 8a12963b and the T2.4 Done-when mapping: same carried row.|
| 08.6.3 | 08.6.3-T1 (T1.1 + T1.2) | merged (#260, 569b538f) | `migrations/0052_healing_switch.sql`: `healing_switch` (mode CHECK excludes `unavailable`, `(scope='system') = (org_id is null)`, partial uniques per scope, composite project FK, FORCE RLS with the T72 NULL-org read policy, column-grant writes) plus the `evidgate.healing_switch_set_system` definer; `SetHealingSwitch`/`GetHealingSwitch`, pure `IHealingAvailabilityResolver` (most restrictive wins, no rows = `shadow`), V1 clamp on `Healing:Apply`, `PUT|GET /v1/healing/switch` with OpenAPI + SDK, operator system route behind the fail-closed gate. D-S124-Q1..Q3 (coordinator-decided, owner review non-blocking). Counts bumped at close-out. |
| F47 | F47 (docs/contract, tier T0) | merged (#268, a1483914) | 00 §9.3 gains one block row for the four shipped session routes (`GET /v1/auth/sessions`, `DELETE /v1/auth/sessions/{s}`, `POST /v1/auth/sessions/revoke-all`, `POST /v1/auth/logout`, admin scope `X-Evidgate-Org`) and §15 one row; plan 02 story 02.3.2 gains `02.3.2-T3` (admin ends all sessions of one user, ASVS-GAP-14 7.4.5; planned only, not landed); totals 656 tasks / 703.25 raw / 843.9 buffered. A follow-up row, not a planned task: the landed counts (181 tasks / 187.25 d) do NOT change. |
| 11.5.2 | 11.5.2-T1 (T1.1 + T1.2) | merged (#270, 142b4846) | Docs only (tier T0): `docs/adr/0010-audit-chain-keyed-hash-and-external-checkpoints.md` (`proposed`; AC1; HMAC-SHA-256 keyed chain with `chain_key_id`, secret-store custody, rotation without rewriting history, ECDSA P-256 checkpoints every 15 min, residual risk, three rejected alternatives), its index row, DECISIONS §6 F15-F22 (✅ F15, F16, F18; ✅ with ⚠️ residue F19-F21; ⚠️ F17, F22), the four labelled DoR answers in the 11.5.2 plan §8, the `[OPEN]` change request text for 00 §5.4/T8 and §15 (queued in the 11.5.2 plan Notes, 00 not edited) and the 02.7.1 gap note (`audit_log.chain_key_id`, `IAuditChainSigner`). The ASVS-GAP-24 row stays open until T2 and T3 ship. |
| 10.11.1 | 10.11.1-T3 (T3.1) | merged (#274, e839cbb4) | Docs only (tier T0): `docs/decisions/10.11.1-waf-edge-decision-record.md` (`[OPEN]` O3, no vendor chosen): edge stance dependent on O3, what runs without a WAF (shipped throttle helpers vs AC1-AC8 planned, not merged), what is not covered, edge criteria E1-E10 for 10.8.1-T1, sources and prior art; Notes block appended to the 10.11.1 plan; two carried rows (forwarded-headers handling absent; WAF/edge stance `[OPEN]` on O3, both 10.11.1-T3, S132); D-S132-Q1..Q6 in `docs/phase-review-archive.md`. |
| 12.2.1 | 12.2.1-T2 (T2.1; PARTIAL — T2.2 open, needs signed pilots) | merged (#277, 95892bfd) | Docs only (tier T0): `docs/pilots/metric-definitions.md` (v1 freeze: seven §4.3 rows, guardrail pair, AC2 freeze rule, T60 null rule, O1 §3 targets `[INFERENCE]`), `docs/pilots/baseline-worksheet.md` (template; `null` + reason; raw exports stay outside the repo), `docs/README.md` row, §8 DoR lines in both plans (3 in 12.2.1, 4 in 12.5.1), Notes in both plans, one carried row. T2.2 (two-sprint baseline per signed pilot) stays open; counts and ready set unchanged. |
| 09.8.1 | 09.8.1-T1 (T1.1 + T1.2; PARTIAL, T1.3 + T1.4 open) | merged (#284, 46389453) | WCAG 2.2 AA audit of the six core flows (`docs/a11y/findings-09.8.1-T1.md`) plus keyboard/focus fixes (login focus target, scope-switcher DOM order, run list/detail focus handoffs, shared request-state retry handoff) proven by keyboard-only walkthroughs `e2e/shell/a11y-keyboard-*.spec.ts`; parent T1 stays partial and adds no count. |
| 11.5.1 | 11.5.1-T5 (T5.1 + T5.2) | merged (#286, 560b5aba) | `migrations/0053_security_security_event.sql`: tenant `security_event` (`unique (org_id, id)`, `security_event_list_idx`, FORCE RLS + tenant policy; type/severity sets, `actor`/`details` rules and the grant story in the `migrations/README.md` Notes); `ListSecurityEvents` + `SecurityEventReadSql` behind `GET /v1/orgs/{o}/security-events` (`security_event:read`, five filters, keyset on `(created_at, id)`, 403 at tenant resolution for a non-member or unknown org, 404 only on a resolved-tenant/path mismatch, 422 `request_invalid`), OpenAPI + SDK; baseline §5 rows 6 and 10 closed, D-S137-Q1..Q4 recorded. detail: carried row '11.5.1-T5 security-event detail' (S144 F61).|
## 3-4. Delivered waves

Moved verbatim to docs/status-archive.md

## 5. Ready next (dependencies met; includes partial remainders)

Dependency-ready: **91 tasks / 102.5 d** (G1 38 / 41.0, G2 37 / 38.5, G3 6 / 9, G4 10 / 14), computed by expanding the `plan_graph.py` task dependencies and removing the tasks landed in §2; sizes XS .25 S .5 M 1 L 2 XL 3. This is not owner approval: non-graph blockers (owner decisions, legal/counsel, external accounts, missing feature plans) stay in §6 and are marked in the rows. 01.2.1-T6 (M, G1) joins the set in this count: its only dependency, 01.2.1-T1, is landed. detail: carried row 'STATUS §7 dependency-ready and lanes' (S144 F61).

Stricter variant that also requires every dependency task's whole story to be merged: **47 tasks / 51.0 d** (G1 16 / 18, G2 23 / 23, G3 0 / 0, G4 8 / 10), fully recomputed on `787f592` from the plan graph plus the landed set. This corrects the previous incremental 50 / 53.5 d claim (G1 19 / 20.5); 11.5.1-T3 and 11.5.1-T5 remain included because 11.1.1 is fully landed. S102 adds 02.1.2-T3 to the task-edge ready set but no strict-ready task: 02.1.2-T1's whole story still includes unbuilt T3.

### G1: 37 tasks / 40.0 d (backend lane 28 / 28.0 d, web-only 0 / 0 d, docs-only 9 / 12.0 d)

#### G1 backend lane

| Task | Size | Note | Lock | Migration lane | Shares file with running lane | Split (L/XL) | Buildable |
|---|---|---|---|---|---|---|---|
| 01.8.1-T3 | M (rem T3.2) | Partial: T3.1 seed-dev shipped (`db209e4`, `SeedDevTests`); T3.2 still needs a clean-checkout `up.sh` run and a second-developer review of `docs/dev-setup.md`. | yes | no | no | n/a | yes — files exist; rem = clean-checkout `scripts/dev/up.sh` run (AC1/AC4) + second-dev review of `docs/dev-setup.md` |
| 02.3.1-T3 | S | `GET /v1/me` profile + `PATCH /v1/me {preferred_locale}` | yes | yes (0049+) | S97: `contracts/openapi.yaml`, generated SDK, isolation route cases | n/a | yes |
| 02.1.2-T3 | S | Membership lifecycle (deactivation/removal semantics; may switch to revoke-all, docs/status-carried.md:206). Ready: S102 02.3.2-T2 merged (#228, 787f592). | yes | no | no | n/a | yes — deps 02.1.2-T1 landed, 02.3.2-T2 merged |
| 03.1.4-T1 | S | Decision + contract: optional `requirement_ref`. Partial: decision half landed as D-A6 (`DECISIONS.md:172`, `a4c546c`); the contract half is open — `requirement_ref` and the sidecar `requirement` key appear nowhere under `contracts/`. | yes | yes (0049+) | no | n/a | yes |
| 03.1.4-T2 | S | `platform`, `level`, `purpose` on suite/template | yes | yes (0049+) | S97: Catalog suite create/list endpoint code + tests; `contracts/openapi.yaml`, generated SDK | n/a | yes |
| 03.4.1-T1 | L | GitHub App registration + installation binding + `git_repo_binding`; typed `HttpClient` + RS256 app JWT | yes | yes (0049+) | S97: `contracts/openapi.yaml`, generated SDK, isolation route cases | T1.1-T1.2 ; T1.3-T1.4 | yes (App registration itself needs GitHub org/admin credentials) |
| 04.1.2-T1 | S | Per-project rate limit/quota with `Retry-After`; counters and spans (after S91) | yes | no | S91: `src/BuildingBlocks/Observability/EvidgateTelemetry.cs` (`ingest_*` counters on the batch span) | n/a | yes |
| 04.4.1-T1 | L | Per-producer mapping registry + pytest mapping | yes | yes (0049+) | no | T1.1-T1.2 ; T1.3-T1.4 | yes |
| 04.8.1-T1 | M | Emit `results` usage rows through `IUsageLedger.RecordAsync` in the accepted-batch transaction; unblocked by 12.3.1-T1 (04.1.1-T2 merged). Partial: T1.1 ships the `usage.batch_accepted` outbox row (`IngestEventBatch.cs:590`, `IngestEventBatchTests.cs:186`); T1.2–T1.4 consumer and ledger write remain. | yes | yes (0049+) | no | n/a | yes |
| 06.2.2-T1 | M | Adapter capability + per-project options (disable kinds, allowlisted redaction rules) with 04 | yes | yes (0049+) | no | n/a | yes |
| 06.2.3-T1 | M | Async secret-scan `task` handler (outbox row written by `complete`): scan the object, set `scan_state`, write `masking_event`, enforce `reject_on_secret` | yes | yes (0049+) | no | n/a | yes |
| 06.4.1-T1 | S | `retention_policy` table, validation floor, defaults, `GET/PUT` | yes | yes (0049+) | no | n/a | yes (RetentionLimits floor values still `[OPEN] owner`) |
| 08.5.1-T1 | XL | Evidgate-provided AI gateway (split before build); unblocked by 12.3.1-T1 | yes | yes (0049+) | no | T1.1-T1.2 ; T1.3-T1.4 ; T1.5-T1.6-T1.7 | yes (dep 12.3.1-T1 merged; `MockHealingLlmProvider` covers tests) |
| 10.1.1-T3 | L | Environment bootstrap automation | yes | no | no | T3.1-T3.2 ; T3.3-T3.4 | yes for the scripts; AC5 needs a live instance |
| 10.1.2-T1 | M | Secret inventory and CI check | yes | no | no | n/a | yes |
| 10.1.2-T2 | XS | Repository secret scanning and push protection | yes | no | no | n/a | yes (needs repo-admin token; ⚠️ plan feature availability) |
| 10.2.1-T1 | L (rem T1.4) | Partial: T1.1–T1.3 images/SBOM shipped; T1.4 web bundle hosting and edge CSP/CORS deploy remain. | yes | no | S91: `infra/config-matrix.json` (telemetry endpoint/connect-src keys) | n/a — rem is T1.4 alone (S) | yes for edge config/checks; live publish needs a hosting target (vendor still undecided, 10.8.1) |
| 11.2.1-T2 | M | Fork-PR credential-reachability test | yes | no | no | n/a | yes |
| 11.6.1-T3 | S | Attach SBOM and provenance to each release | yes | no | no | n/a | yes (needs a fixture release tag) |
| 13.1.1-T4 | S | `manual_case_link` to `test_case`; uncovered-case view | yes | yes (0049+) | S97: `contracts/openapi.yaml`, generated SDK, isolation route cases | n/a | yes |
| 13.1.2-T1 | M | `authoring_change` model; apply-as-one-version with `If-Match` and idempotency key | yes | yes (0049+) | S97: `contracts/openapi.yaml`, generated SDK, permissions, isolation route cases | n/a | yes |
| 13.2.1-T1 | M | `project_ai_settings` + `ai_settings_template` (vi + en seeds), API, limits | yes | yes (0049+) | S97: `contracts/openapi.yaml`, generated SDK, isolation route cases | n/a | yes |
| 01.1.1-T4 | XS | .NET 12 LTS / TypeScript 7 upgrade trigger | yes | no | no | n/a | no — V3 gate: .NET 12 not GA and `@angular/build` / `typescript-eslint` peer ranges still exclude TS 7 |
| 01.7.1-T3 | S (rem T3.1) | Partial: T3.2 shipped (`f34d01b` created CODEOWNERS, PR template and CONTRIBUTING together; `6a679d8` and `4cd9789` only revised the template/CONTRIBUTING later); T3.1 branch-protection apply is owner-blocked (403 on the free private plan). | no | no | no | n/a | no — owner-blocked: branch-protection writes return 403 on the free private repo (STATUS §6 row "Branch-protection API") |
| 10.1.1-T4 | M | Isolated artifact-viewer origin | yes | no | no | n/a | no — needs a distinct registrable domain + DNS record + TLS certificate (registrar/DNS action) |
| 10.3.2-T1 | S | ADR: observability backend for M1 with retention and cost limits, plus the compose profile; unblocks 10.3.1-T2 (T1 merged in #203) | yes | no | no | n/a | yes |
| 10.8.1-T2 | L | Hands-on spike on two candidates | yes | no | no | T2.1-T2.2 (candidate A) ; T2.3-T2.4 (candidate B) | no — needs two managed-provider accounts/credentials to provision and measure |
| 11.9.2-T2 | S | Reconcile the audit chain with deletion; LANDED by S147 (#304, a36bccf5; counted in the section 2 totals, its Landed task groups row is added at a later close-out because an insert shifts STATUS.md:N cites): T2.1 written rule, T2.2 audit payload schema plus an erasure-then-verify test | yes | no | no | n/a | yes |

#### G1 web-only lane

| Task | Size | Note | Lock | Migration lane | Shares file with running lane | Split (L/XL) | Buildable |
|---|---|---|---|---|---|---|---|
| 10.7.1-T4 | M | Key rotation runbook | no | no | no | n/a | T4.1 and the written pepper section of T4.2 delivered by S106 (PR #222); remaining: T4.2 staging drill (AC5), see §6 rows |
| 11.9.1-T2 | L | Breach runbook with the 72 h clock (unblocked by 11.5.1-T1, #219). PARTIAL: T2.1–T2.3 merged (#279, f976122d) in docs/runbooks/breach.md (4 h notice target ⚠️, OI-11-08); T2.4 (regulator support, on-call path, counsel review) stays open, see carried row "Breach runbook T2.4 open". | no | no | no | T2.1 ; T2.2 ; T2.3 ; T2.4 | yes for T2.1–T2.3; T2.4 needs counsel review |
| 12.2.1-T2 | M | Baseline measurement kit (T2.1 kit merged in #277; T2.2 baseline remains) | no | no | no | n/a | yes for T2.1 kit; T2.2 needs signed pilots |
| 11.8.1-T1 | L | DPA template + counsel review | no | no | no | T1.1-T1.2 ; T1.3-T1.4 | no — counsel engagement is external and the AC needs `approved` + counsel name in `manifest.yaml` |
| 11.8.1-T2 | M | Privacy notice + counsel review | no | no | no | n/a | no — counsel review/approval is external |
| 12.2.1-T3 | M | Pilot onboarding run + friction log | no | no | no | n/a | no — needs signed pilot orgs and the 09.2.1 wizard |
| 12.6.1-T1 | M | Trademark search classes 9/42 | no | no | no | n/a | no — counsel + NOIP register search are external/legal |
| 13.3.1-T1 | M | Feature plan written (S112, merged #241 ce9999d, `plans/features/13-product-phases/13.3.1-generation-pipeline.md` §10, sub-tasks T1.1 to T1.4); build after owner confirmation of its `[OPEN]` rows OI-13-403, OI-13-404 (extraction caps, PDF library) | no | no | no | n/a | no — OI-13-401 (model pair and cost key, plan 13 Q1) is owner-open and due before 13.3.1-T2, not T1 |
| 13.4.1-T1 | L | Feature plan written (S112, merged #241 ce9999d, `plans/features/13-product-phases/13.4.1-authoring-ui.md` §10, sub-tasks T1.1 to T1.4); design task for the `design-ui` agent, owner approval before T2 to T7 | no | no | no | split by its four sub-tasks; declared L = wireframes/all states + CR-D-2 deltas | no — needs the `design-ui` agent run and owner approval (plan AC1) |

### G2: 37 tasks / 38.5 d (backend lane 28 / 28.5 d = 27 code + 1 docs writing `infra/`, web-only 5 / 7 d, docs-only 4 / 3 d)

#### G2 backend lane

| Task | Size | Note | Lock | Migration lane | Shares file with running lane | Split (L/XL) | Buildable |
|---|---|---|---|---|---|---|---|
| 01.10.1-T1 | M | Emit own CI results in ingest format | yes | no | no | n/a | yes |
| 02.4.3-T2 | M | `github_installation`, install callback, token minting | yes | yes (0049+) | S97: `contracts/openapi.yaml`, `packages/sdk`, `contracts/permissions.json`; S91: additive endpoint registration | n/a | yes |
| 02.4.4-T1 | M | `api_token` table, create/list/revoke API | yes | yes (0049+) | S97: `contracts/openapi.yaml`, `packages/sdk`, `contracts/permissions.json` | n/a | yes |
| 02.8.1-T1 | M | `support_grant` model and request/approve/revoke flow | yes | yes (0049+) | S97: `contracts/openapi.yaml`, `packages/sdk`, `contracts/permissions.json` | n/a | yes |
| 03.3.1-T1 | M | `fixture_revision` schema + suite reference + seed versioning + cleanup policy fields | yes | yes (0049+) | no — same Catalog module as S97, new types only | n/a | yes |
| 03.6.1-T1 | M | Suite draft tables + `ETag`/`If-Match` on the draft routes | yes | yes (0049+) | S97: `contracts/openapi.yaml`, `packages/sdk` | n/a | yes |
| 03.7.1-T3 | S | Evaluate Playwright planner/generator agents | yes | no | no | n/a | yes |
| 04.1.2-T2 | S | Stale/foreign-epoch handling | yes | no | no | n/a | yes |
| 04.1.2-T3 | M | OpenAPI + integration tests for both stories. Partial: events operation documented (`contracts/openapi.yaml:1009,6848,7182`) and the 04.1.1+04.1.2 suite is on develop; the run-create 409 `producer_slots_conflict` still needs documenting. | yes | no | S97: `contracts/openapi.yaml`, `packages/sdk` | n/a | yes — remainder only |
| 04.1.3-T1 | S | Manifest and report caps, page shape | yes | no | S91: `src/BuildingBlocks/Observability/EvidgateTelemetry.cs` (`ingest_cap_exceeded_total{cap}` counter) and `infra/config-matrix.json` (new cap rows); S97: `contracts/openapi.yaml`, `packages/sdk` | n/a | yes |
| 04.4.1-T4 | S | Raw report persistence + `raw_report`/`report_parser` rows | yes | yes (0049+) | no | n/a | yes |
| 04.6.1-T1 | L | Fixture layout `fixtures/<producer>/<case>/` + diff harness (expected events vs produced) | yes | no | no | T1.1+T1.2 \| T1.3+T1.4 | yes |
| 05.2.1-T3 | M | Server-side `execution_key` + uniqueness. Partial: key layout, unique index and the 422 conflict ship (`ExecutionKey.cs:20`, `0031:174`); the golden vectors and the reporter real key (all-zero `PLACEHOLDER_EXECUTION_KEY`) remain. | yes | yes (0049+) | no — `packages/adapter-playwright/src/identity.ts` is CLI-side, no lane owns it | n/a | yes — remainder only |
| 05.2.2-T3 | M | Fork-PR behaviour: labelled, advisory-only, never bound or gating | yes | no | S91: `src/BuildingBlocks/Observability/EvidgateTelemetry.cs` (T3.4 adds a counter name to that file) | n/a | yes |
| 05.4.1-T1 | S | Cancel request row + `POST /v1/runs/{r}/cancel` + permission `run:cancel` | yes | yes (0049+) | S97: `contracts/openapi.yaml`, `packages/sdk`, `contracts/permissions.json` (`run:cancel`) | n/a | yes |
| 05.6.1-T1 | M | `release`/`release_component`/`release_run` tables + create API | yes | yes (0049+) | S97: `contracts/openapi.yaml`, `packages/sdk` | n/a | yes |
| 05.7.1-T1 | L | JobSpec schema + immutability + `job_spec_hash` | yes | yes (0049+) | S97: `contracts/openapi.yaml` (`jobspec_invalid`), `packages/sdk`; `contracts/jobspec.schema.json` is a new file | T1.1+T1.2 \| T1.3+T1.4 | yes |
| 06.1.3-T1 | S | Guards, transition tests, `rejected`/`ready` immutability | yes | yes (0049+) | no | n/a | yes |
| 06.2.1-T1 | M | Kind → default sensitivity map + project override; enforce on read/list/download-url. Partial: kind defaults and override precedence ship in 06.1.2-T3 (`ArtifactKindDefaults.cs:28`); the per-project `PUT` override route and read/list enforcement remain. | yes | no | S97: `contracts/openapi.yaml`, `packages/sdk` | n/a | yes — remainder only |
| 06.3.6-T1 | L | Parse runner as a limited child process | yes | no | T1.1+T1.2 merged (#243, 8b9b210a); T1.3 merged (#250, e037f2fe: parent runner, child rlimits, image/SBOM); T1.4 merged (#262, 961091b2; exit/signal mapping, `artifact_parse` handler, stderr redaction, spawn benchmark; AC4 only partial, so T1 stays partial, no §2 Done row and no count change until the AC4 rows at docs/status-carried.md:349-350 close) | T1.1+T1.2 (S117, #243) \| T1.3 (S121, #250) \| T1.4 (S125, #262) | yes |
| 11.5.1-T3 | M | `docs/security/alert-rules.md` per baseline type from `docs/security/security-event-baseline.md` plus the emit-path coverage test (unblocked by 11.1.1-T4, #224) | yes | no | no | n/a | yes |
| 12.3.1-T3 | M | Versioned price table + loader (price stays `[OPEN] O5`; fixtures only); unblocked by 12.3.1-T1 | yes | no | no | n/a | yes |
| 12.3.1-T5 | M | Roll-up job + reconciliation query; unblocked by 12.3.1-T1 | yes | no | S97: `contracts/openapi.yaml`, `packages/sdk`, `contracts/permissions.json` (`billing:read`) | n/a | yes |
| 02.10.1-T1 | S | Provider choice and sender setup | yes | no | S91: `infra/config-matrix.json` + `.env.dev.template` (new email keys, additive row) | n/a | no |
| 13.5.1-T1 | XL | Feature plan written (S136, merged in #281, cdecef28, `plans/features/13-product-phases/13.5.1-bound-playwright-scripts.md` §10, sub-tasks T1.1 to T1.7); build after owner confirmation of its [OPEN] rows OI-13-510 to OI-13-529 and OI-13-540 to OI-13-543, first OI-13-519 (plan 13 Q3 seed test) and OI-13-511, OI-13-523 to OI-13-526 | yes | no | unknown (`packages/cli`, no lane owns it) | plan slices T1.1 to T1.7 | no |
| 13.5.2-T1 | L | Feature plan written (S136, merged in #281, cdecef28, `plans/features/13-product-phases/13.5.2-local-runs.md` §10, sub-tasks T1.1 to T1.5); build after owner confirmation of its [OPEN] rows OI-13-530 to OI-13-539 and OI-13-544 | yes | no | unknown (`packages/cli`, no lane owns it) | plan slices T1.1 to T1.5 | no |
| 13.6.1-T1 | L | Feature plan written (S139, merged in #289, 8f8e365e, `plans/features/13-product-phases/13.6.1-element-profiles.md` §10, sub-tasks T1.1 to T1.4); build after owner confirmation of its [OPEN] rows OI-13-545 to OI-13-550 and OI-13-559. Not buildable until merged, per task: T1 needs 08.1.2-T2 (masking, T1.3) and 13.5.1-T1 (its T1.5 emitter writes the collector call); T2 needs 13.5.1-T4 (the `generated_script_run` source) and 08.1.2-T2's hook set (T2.3); T3 needs 08.1.2-T3 (no 08.1.2 or 13.5.1 task is merged) | yes | no (the T2 migration number is assigned at build time) | unknown (`packages/adapter-playwright`, no lane owns it) | plan slices T1.1 to T1.4 | no |
| 13.8.1-T1 | L | Feature plan written (S139, merged in #289, 8f8e365e, `plans/features/13-product-phases/13.8.1-locator-drift-vs-ui-change.md` §10, sub-tasks T1.1 to T1.6); build after owner confirmation of its [OPEN] rows OI-13-551 to OI-13-558. Not buildable until merged, per task: T1 needs 08.2.2-T2 and 13.6.1-T3; T2 needs 08.3.1-T1; T3 needs 13.1.2-T1; T4 needs 08.8.1-T1, and its window close also needs 08.8.1-T3 and 08.8.2-T2 (none of these is merged) | yes | no (the T1.2 migration number is assigned at build time) | unknown (no lane owns it) | plan slices T1.1 to T1.6 | no |
| 11.5.2-T2 | M | Signed periodic checkpoint exported to write-once storage (unblocked by 11.5.2-T1, S131 PR #270; ADR-0010 is `proposed` and the 00 §5.4/T8 change request is `[OPEN]`, so confirm both with the owner before building). Needs the 02.7.1 `chain_key_id` writer change first (gap filed in the 02.7.1 plan Notes) | yes | `[OPEN]` the additive `chain_key_id` column is filed on 02.7.1 | unknown (02.7.1 writer, `src/BuildingBlocks/Persistence/Audit/`) | n/a | yes |

#### G2 web-only lane

| Task | Size | Note | Lock | Migration lane | Shares file with running lane | Split (L/XL) | Buildable |
|---|---|---|---|---|---|---|---|
| 09.3.1-T2 | L | Run detail: manifest summary, release components + digests, environment, pinned `manifest_hash`/suite/policy revision (T2.1 merged #216, 1742764; T2.2 S146 #302 fbb2fe91) | no | no | no | T2.1+T2.2 \| T2.3+T2.4 | yes |
| 09.3.1-T3 | L | Evidence panel: completeness verdict, `missing[]` reasons with deep links, immutable evidence-revision selector | no | no | no | T3.1+T3.2 \| T3.3+T3.4 | yes |
| 09.3.1-T4 | M | Attempts table: raw vs normalised status, retry chain, duration, pre-healing failure always visible. Delivered: UI component S111 (PR #232); wire swap + wire fixtures S146 (PR #302, fbb2fe91); healing marker stays unavailable until 08.3.1 (§6 row "Run detail web remainders (S146)") | no | no | no | n/a | yes |
| 09.3.1-T6 | M | Producer/shard view: finalize status, `last_sequence`, count vs expected, stale epochs, late events | no | no | no — consumes the 03.2.1 suite-composition/slot API that S97 is building (dependency, not a shared file) | n/a | yes |
| 10.7.1-T1 | M | Dead-letter runbook | no | no | no (docs/runbooks) | n/a | no |
| 13.7.1-T1 | M | [OPEN] no feature plan exists yet (STATUS §6 row "Feature plans missing for planned stories"); dependency-ready only, not buildable as written | no | no | no | n/a | no |

### G3: 6 tasks / 9 d (backend lane 4 / 6 d = 3 code + 1 web writing `.github/`, web-only 2 / 3 d, docs-only 0 / 0 d) Newly ready after S124: 09.5.2-T3 (M, web-only; its only dependency 08.6.3-T1 landed), counted in this header; its row is added when the lane picks it up (a row insert would shift every STATUS.md:N cite below).

#### G3 backend lane

| Task | Size | Note | Lock | Migration lane | Shares file with running lane | Split (L/XL) | Buildable |
|---|---|---|---|---|---|---|---|
| 04.9.1-T1 | L | `evidgate replay` CLI command | yes | no | no (`packages/cli`) | T1.1+T1.2 \| T1.3+T1.4 | yes |
| 04.9.2-T2 | M | Docs site skeleton | yes | no | no (`scripts/verify.sh`, `ci.yml`, `docs/`) | n/a | yes |
| 09.8.1-T3 | L | Playwright E2E suite for core flows, wired into CI and dogfooded into Evidgate | yes | no | no | T3.1+T3.2 \| T3.3+T3.4 | yes |
| 04.9.2-T1 | M | Publish pipeline for `@evidgate/*`, changelog, signed tag, N-1 window | yes | no | no (`.github/workflows/release.yml`, `scripts/`) | n/a | no |

#### G3 web-only lane

| Task | Size | Note | Lock | Migration lane | Shares file with running lane | Split (L/XL) | Buildable |
|---|---|---|---|---|---|---|---|
| 09.8.1-T1 | L | WCAG 2.2 AA pass on core flows (keyboard, focus, contrast, target size, error identification) with fixes | no | no | no (`apps/web` `shared/ui` tokens; S97 adds no web surface) | T1.1+T1.2 landed (#284, 46389453, partial); T1.3+T1.4 remain | yes |

### G4: 10 tasks / 14 d (backend lane 8 / 11 d = 7 code + 1 docs writing `scripts/` and `fixtures/`, web-only 1 / 1 d, docs-only 1 / 2 d)

#### G4 backend lane

| Task | Size | Note | Lock | Migration lane | Shares file with running lane | Split (L/XL) | Buildable |
|---|---|---|---|---|---|---|---|
| 02.3.3-T1 | XL | SAML 2.0 SSO (metadata, SP-initiated login, assertion mapping, JIT membership policy) | yes | yes (0049+) | S97: `contracts/openapi.yaml`, `packages/sdk`, `contracts/permissions.json` (`sso:read`/`sso:write`) | T1.1+T1.2 \| T1.3+T1.4 \| T1.5+T1.6 | yes |
| 03.5.1-T1 | M | `template` registry + web template | yes | yes (0049+) | S97: `contracts/openapi.yaml`, `packages/sdk` | n/a | yes |
| 05.7.3-T1 | M | Capability matching + pool model | yes | yes (0049+) | S97: `contracts/openapi.yaml`, `packages/sdk`, `contracts/permissions.json` (`agent_pool:*`) | n/a | yes |
| 07.8.1-T1 | M | `deploy_target` and CRUD | yes | yes (0049+) | S97: `contracts/openapi.yaml`, `packages/sdk`, `contracts/permissions.json` | n/a | yes |
| 11.7.1-T2 | M | SAST/DAST baseline | yes | no | no (`.github/workflows` CodeQL, `Directory.Build.props` analyzers) | n/a | partial (SAST half buildable; DAST needs staging) |
| 04.5.1-T1 | L | Appium adapter skeleton + capability document (`engine.family`, driver, platform, `supports.cancel`) | yes | no | no (new `packages/adapter-appium`) | T1.1+T1.2 \| T1.3+T1.4 | no |
| 04.5.1-T2 | M | Appium result mapping in `packages/adapter-appium`; throwaway if `adapter-mobile` wins the 13.7.1 memo | yes | no | no (new `packages/adapter-appium` mapping) | n/a | no |
| 12.1.1-T2 | M | Waived by O1; do not schedule interviews | yes | no | no (`docs/discovery`, `fixtures/discovery/`, `scripts/check-discovery.py`) | T2.1+T2.2 | no |

#### G4 web-only lane

| Task | Size | Note | Lock | Migration lane | Shares file with running lane | Split (L/XL) | Buildable |
|---|---|---|---|---|---|---|---|
| 09.6.1-T6 | M | Suite composition: case revisions × variants, shard strategy, DAG/coverage gap warnings | no | no | no | n/a | yes |
| 12.1.1-T1 | L | Waived by O1; do not schedule interviews | no | no | no (`docs/discovery`, `interviews/`) | T1.1+T1.2 \| T1.3+T1.4 | no |

**Waived:** 12.1.1-T1/T2 customer interviews (O1, 2026-10-02); pilot profile in `docs/decisions/O1-pilot-profile.md`, real reports from the owner’s own pipeline.

## 6. Blocked / carried items

Live table: docs/status-carried.md (moved verbatim on 2026-10-28). Add new rows there, at the end.
