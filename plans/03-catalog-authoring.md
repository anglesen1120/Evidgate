# 03 - Catalog and Authoring (G1–G4)

> Contracts: `00-overview.md` §3 (glossary: case, test revision, suite revision, variant, capability), §5.4 (tenancy), §7 (identifiers, hashes), §8 (states), §9 (API). Facts: `../DECISIONS.md` §3 A2/A4, §4 (stack, engines), §5 (safe-healing rules), §6 F1/F9, §9 R3. Source: design §4 (Test creation and management), §6 (Data model), §8 (Security and permission boundaries).
> Level: high-level plan; each story gets a detailed plan in `plans/features/` (00 §12) before build.
> **Phases (CR-D-2):** the Milestone column holds the CR-D-2 phase (G1–G4) from [13 §4.4](13-product-phases.md); M0–M5 in this plan's prose refers to the pre-CR-D-2 sequence. Phase totals (raw): G1 14 · G2 6 · G3 4.5 · G4 13.5 person-days.
## 1. Purpose and scope

**Goal.** A test has one stable identity across renames, moves and imports; every revision that was ever run is immutable and reproducible from a commit; suites declare exactly what a run must execute (members, variants, dependencies, shards, retries, environment needs); and authoring happens either through versioned templates + Git (V1) or through a draft workflow that only ever lands as a pull request (V2).

**CR-D-2 scope split.** Authoring of requirements → checklist → manual test case — the feature tree, checklist, manual test cases with versions and timeline, batched review, project AI settings, the generation pipeline and the authoring screens — is owned by [`13`](13-product-phases.md) epics 13.1, 13.2, 13.3 and 13.4, phase G1. This plan keeps the layer those manual cases link to: stable case identity and immutable revisions (`03.1`, `03.2`; `13.1.1-T4` links a manual test case to a `test_case`), the Git, template and draft path (`03.4`, `03.5`, `03.6`), the AI-draft evaluation (`03.7.1-T3`) and history / baseline (`03.9`). Where this plan's prose describes AI drafting or an editor, read `13` §3–§4 for the current model.

**In scope (V1, M1-M2).** Case identity from the `evidgate.id` annotation or a sidecar manifest; temporary identity for imports without an id; `test_revision` immutability; suite revisions with members, variant hashes, dependency DAG, shard strategy, retry policy and environment requirements; fixture revisions and per-run data namespaces with TTL cleanup of orphans; a GitHub App webhook that registers commits and syncs the catalog; templates for web, API, unit and (V2) mobile; the V1 "template + Git flow" authoring path.

**In scope (V2, M4).** Draft editor with optimistic concurrency, sandboxed dry run, patch/PR creation; AI drafts from approved requirements and context; confirmed bug → candidate regression test. Playwright's planner/generator agents (DECISIONS F1 ✅) are **evaluated** as draft generators — an evaluation task, not a decision.

**Out of scope.** Running the tests (04, 05, 06), the decision semantics of suites (07), healing mechanics (08), all screens (09 renders to these contracts), secret storage and resolution (11 owns the vault/CI side; this plan stores references), source-code coverage instrumentation (never claimed as coverage, source §6), a full IDE (V2 is a draft workflow, not an editor product), hosted runners (V3).

**Version split.** V1: 03.1–03.3, 03.4.1, 03.5.1 (M1-M2). V2: 03.4.2, 03.5.1-T5, 03.6–03.8 (M4). V3: none in this plan.
## 2. Requirements

- **FR-03-1** A case id is a UUID from the `evidgate.id` annotation or a sidecar manifest; renaming or moving a file never creates a new case. A commit that declares the same id twice is a conflict: the sync reports it and creates no revision for the offending file.
- **FR-03-2** An import without an id creates a **temporary identity** from `(producer, suite, name)`; the UI must resolve a mapping when a collision or a rename appears; an imported identity never silently merges with an annotated one.
- **FR-03-3** `test_revision` rows are immutable: revision = `(case_id, revision_hash)` unique per org, carries the commit, file path, extracted assertion metadata and `assertion_hash`; no update and no delete while any run references them.
- **FR-03-4** A `suite_revision` pins members (`case_revision` × variant), the dependency DAG, shard strategy, retry policy and environment requirements; it is immutable once a run references it and is superseded by a new revision.
- **FR-03-5** A variant is the hash of normalised, **secret-free** parameters (browser, device, locale, data set). A parameter set whose key looks like a secret or whose value matches a secret pattern is rejected.
- **FR-03-6** The dependency graph must be acyclic; a cycle is rejected with the offending path. Setup failure of a dependency marks dependants `blocked`, never `passed` (00 §8.3, enforced downstream by 05/07).
- **FR-03-7** Fixtures are versioned revisions referenced by a suite revision; secrets are referenced by name only. Data namespaces are `(tenant, project, run)` scoped and cleaned by TTL after the run terminates, including orphan cleanup for runs that died.
- **FR-03-8** A GitHub App webhook on push registers the commit and syncs cases/suite revisions. Deliveries are signature-verified and deduped by delivery id; a suspended installation disables sync without deleting history.
- **FR-03-9** V1 authoring = templates + Git flow: web template (role/name or test id), API template built from an OpenAPI skeleton **without inferring business assertions**, unit tests stay in the repo's own framework (evidence-only), mobile capability is separate with its own fixtures and locators.
- **FR-03-10** V2 draft workflow: drafts use `ETag`/`If-Match` (stale → 412), a sandbox dry run (no tenant secrets, ephemeral namespace, one-shot identity), and reach the repo only as a patch/branch + PR; the platform never pushes to a protected branch.
- **FR-03-11** V2 AI drafts accept only **approved** requirements and allowed context, record provenance and the decision trail, and never run with sensitive permissions while unapproved.
- **FR-03-12** V2 confirmed bug → candidate regression test: the candidate fails on the buggy build and passes on the fixed build when both builds are available; otherwise the limitation is recorded on the candidate.

**Acceptance criteria (testable).** Given a commit that only moves a file, when synced, then the case id is unchanged and a rename history row exists. Given two files with the same `evidgate.id`, then the sync reports `duplicate_case_id` and both files keep their previous revisions. Given a 3-member dependency cycle, then 422 `dependency_cycle` and no revision is written. Given a suite revision referenced by a run, then any edit attempt returns 409 and a new revision is created instead. Given a variant payload containing `api_token`, then 422 `variant_not_secret_free`. Given a stale `If-Match` on a draft, then 412 and no change. Given an AI draft that was never approved, then its execution request is refused for every pool except the sandbox.
## 3. Key decisions

| # | Decision | Rationale / rejected alternative |
|---|---|---|
| D1 | Case identity comes from the annotation, with a sidecar manifest accepted as an equivalent source | Git holds approved test code (A2); rejects path-derived ids (renames would break history) and DB-assigned ids (the repo could not reference them) |
| D2 | Renames/moves are detected as the *same* id at a new path; imports without an id get a temporary identity that a human or a unique match must bind | Source §4; rejects auto-binding by name alone (two same-named tests in a monorepo is normal) |
| D3 | `test_revision` is append-only with `unique (org_id, case_id, revision_hash)`; content changes create a new revision | A revision that a run or a healing candidate referenced must be reproducible forever (A2, healing rule 3) |
| D4 | Suite revisions are immutable; a change is a new `revision_no` | Manifest and evidence pin a suite revision (05, 06); rejects "edit suite in place" |
| D5 | Variants are content-addressed normalised parameter hashes, validated secret-free at write time | Source §6; keeps planned executions stable and prevents a secret leaking into an immutable row |
| D6 | Dependency edges live in the suite revision (declared metadata), validated as a DAG on write | Independent tests are the default (source §4); rejects an implicit runtime dependency |
| D7 | Fixtures are referenced, never inlined into a suite revision; secrets are names only | A revision is immutable and may be read by every member of the org; secret values must not be readable (00 §2) |
| D8 | Catalog sync is webhook-driven and idempotent per delivery; sync never deletes case history | At-least-once delivery (A10); a missed webhook is repaired by a scheduled reconcile, not by deletes |
| D9 | V1 ships templates + Git flow only; the draft editor and AI drafts are V2 | 00 §1.3 scope decision; template + Git flow is the smallest path to a real pilot |
| D10 | A draft becomes real code only through a PR; no direct push, no merge by the platform | A2; keeps review and CI gates meaningful; healing (08) follows the same rule |
| D11 | Dry run runs in a sandbox with a capability downgrade and no tenant secrets | Source §4 and §8: unapproved AI code must not run with sensitive permissions |
| D12 | Playwright planner/generator (F1 ✅) is evaluated as a draft generator, not adopted | Reuse-an-engine principle; the evaluation must check patch validity and the no-skip rule before anything is adopted (R3, 08 §13) |
| D13 | `case_import_identity` is a separate table from `test_case` | Import identity is provisional and resolvable; a case row is permanent, and mixing the two would create fake cases |
## 4. Design

### 4.1 Tables owned (columns at high level)

| Table | Key columns | Constraints / notes |
|---|---|---|
| `test_case` | `id`, `org_id`, `project_id`, `annotation_id`, `name`, `framework`, `status`, `owner_membership_id`, `first_seen_commit` | `unique (org_id, project_id, annotation_id)`, `unique (org_id, id)`; `annotation_id` immutable once set |
| `case_identity_source` | `id`, `case_id`, `org_id`, `source` (`annotation` \| `sidecar` \| `imported`), `raw_key`, `commit_sha` | `unique (org_id, id)` (T3); composite FK `references test_case (org_id, id)`; provenance for audits ("why is this the same case") |
| `case_import_identity` | `id`, `org_id`, `project_id`, `producer_id`, `suite_key`, `name_hash`, `case_id` (null = unmapped), `state` | `unique (org_id, project_id, producer_id, suite_key, name_hash)` (D13), `unique (org_id, id)` (T3) |
| `case_rename_history` | `id`, `case_id`, `org_id`, `from_path`, `to_path`, `commit_sha`, `detected_at` | `unique (org_id, id)` (T3); append-only; drives the UI rename list (09) |
| `test_revision` | `id`, `org_id`, `case_id`, `revision_hash`, `commit_sha`, `file_path`, `framework`, `language`, `content_sha256`, `assertion_hash`, `metadata jsonb`, `created_at` | `unique (org_id, case_id, revision_hash)`, `unique (org_id, id)` (T3); append-only (D3) |
| `suite` | `id`, `org_id`, `project_id`, `name`, `slug`, `status` | `unique (org_id, project_id, slug)`, `unique (org_id, id)` |
| `suite_revision` | `id`, `org_id`, `suite_id`, `revision_no`, `revision_hash`, `shard_strategy`, `retry_policy jsonb`, `environment_requirements jsonb`, `created_by`, `created_at` | `unique (org_id, suite_id, revision_no)` and `unique (org_id, revision_hash)`, `unique (org_id, id)` (T3); immutable after first referencing run; `environment_requirements` is secret-free and declares base URLs, dependency versions that affect results, the `data_isolation` mode and secret **references** only |
| `suite_member` | `id`, `suite_revision_id`, `org_id`, `case_revision_id`, `variant_id`, `ordinal`, `required` | surrogate `id` + `unique (org_id, id)` (T46); `unique (suite_revision_id, case_revision_id, variant_id)`; dependency edges reference `suite_member.id` |
| `variant` | `id`, `org_id`, `project_id`, `variant_hash`, `params jsonb` (normalised, secret-free) | `unique (org_id, project_id, variant_hash)`, `unique (org_id, id)` (T3) |
| `case_dependency` | `id`, `suite_revision_id`, `org_id`, `parent_member_id`, `child_member_id` | `unique (org_id, id)` (T3); `unique (suite_revision_id, parent_member_id, child_member_id)`; `parent_member_id`/`child_member_id` are composite FKs `(org_id, member_id)` → `suite_member (org_id, id)` (T46); DAG checked on write |
| `fixture_revision` | `id`, `org_id`, `project_id`, `fixture_key`, `revision_no`, `revision_hash`, `seed_version`, `timeout_s`, `cleanup_policy`, `resource_prefix` | `unique (org_id, project_id, fixture_key, revision_no)`, `unique (org_id, id)` (T3) |
| `data_namespace` | `id`, `org_id`, `project_id`, `run_id`, `namespace_key`, `expires_at`, `cleanup_state` | `unique (namespace_key)`, `unique (org_id, id)` (T3); allocation is per run (FR-03-7) |
| `template` | `id`, `org_id` (null = built-in), `kind` (`web` \| `api` \| `unit` \| `mobile`), `name`, `version`, `body_ref`, `conformance_rules jsonb`, `active` | content-addressed body; built-ins versioned like code; catalogue RLS policy `org_id IS NULL OR org_id = evidgate.current_org_id()`; `template` is on the platform-catalogue exception list of the 02.6.1-T2 lint (T46, T3) |
| `git_repo_binding` | `id`, `org_id`, `project_id`, `provider`, `installation_id`, `repository_id`, `default_branch`, `sync_state` | `unique (org_id, project_id, provider, repository_id)`, `unique (org_id, id)` (T3); the CI-side binding stays in 02 (`ci_binding`) |
| `webhook_delivery` | `id`, `org_id`, `provider`, `delivery_id`, `event`, `payload_hash`, `state` | `unique (provider, delivery_id)` — dedupe (A10), `unique (org_id, id)` (T3) |
| `catalog_sync` | `id`, `org_id`, `project_id`, `commit_sha`, `state`, `counts jsonb`, `error`, `started_at`, `finished_at` | `unique (org_id, id)` (T3); one row per sync; drives the "catalog is stale" badge in 09 |
| `draft` (V2) | `id`, `org_id`, `project_id`, `author_membership_id`, `kind`, `base_commit`, `status`, `version`, `content jsonb`, `provenance`, `ai_run_id`, `dry_run_id` | `unique (org_id, id)` (T3); `version` is the ETag source (00 §9.1) |
| `dry_run` (V2) | `id`, `org_id`, `draft_id`, `sandbox_ref`, `state`, `started_at`, `finished_at`, `result_evidence jsonb` | `unique (org_id, id)` (T3); never produces official evidence (06/07 own that rule) |

**`suite_revision.environment_requirements` (secret-free).** The JSON declares base URLs, warm-up/preconditions, dependency versions that affect results, secret **references** only (never values), and the `data_isolation` mode: `isolated` (default) or `shared`. `shared` means the suite declares that it cannot isolate its data, so it must not run concurrently with another run against the same environment: the V2 environment lock (05.7.3-T4) takes an exclusive claim keyed by environment. `isolated` means each run gets its own environment and data, so no lock is taken. This declaration is the single source of truth for isolation needs: `environment_revision` (05.1.2-T1) stores no isolation flag, and the lock reads this suite-side value (05 §4.1 `environment_lock`). The field is written with the revision and inherits its immutability (`revision_no + 1` supersedes it, D4).

### 4.2 Endpoints owned / used

Owned (shapes in `contracts/openapi.yaml`; 00 §9.1 conventions):

| Endpoint | Permission | Notes |
|---|---|---|
| `GET /v1/projects/{p}/cases`, `GET,PATCH /v1/cases/{c}`, `GET /v1/cases/{c}/revisions` | `case:read` / `case:write` | PATCH changes metadata (owner, labels), never the id, path-derived identity or a revision |
| `POST /v1/projects/{p}/case-import-mappings`, `POST /v1/case-import-mappings/{m}/resolve` | `case:write` | resolve `unmapped`/`conflicted` import identities (D2, D13) |
| `GET,POST /v1/projects/{p}/suites`, `POST /v1/suites/{s}/revisions`, `GET /v1/suite-revisions/{r}`, `GET /v1/suite-revisions/{r}/diff` | `suite:read` / `suite:write` | create is idempotent by `Idempotency-Key`; diff returns the member/variant/DAG delta vs another revision |
| `GET,POST /v1/projects/{p}/fixtures`, `POST /v1/fixtures/{f}/revisions` | `fixture:write` | seeds and cleanup policies; no secret values accepted |
| `POST /v1/webhooks/github` | signature (`X-Hub-Signature-256`), not a session | tenant resolved through an `evidgate_resolve` SECURITY DEFINER function keyed on the GitHub installation/repository id → `(org_id, project_id, binding_id, status)`, then the normal tenant scope opens (00 §5.4.8, T1); `unique (provider, delivery_id)` makes replays idempotent; forged signatures → 401 and never processed |
| `GET,POST /v1/projects/{p}/drafts`, `GET,PATCH /v1/drafts/{d}` (V2) | `draft:write` | `ETag`/`If-Match`; stale → 412 |
| `POST /v1/drafts/{d}/dry-run`, `POST /v1/drafts/{d}/pull-request` (V2) | `draft:dry_run` / `draft:merge_request` | PR creation is idempotent by `(branch, commit)` |
| `POST /v1/projects/{p}/ai-drafts` (V2) | `ai_draft:request` | requires approved requirements as input; records provenance |

Used: 04 supplies raw producer data for import identity; 05 locks a `suite_revision` into a manifest and allocates data namespaces; 06 holds fixture artifacts; 07 reads quarantine references to cases; 08 reads incidents and writes healing candidates against case revisions; 09 renders case/suite/draft screens; 11 owns secret storage behind the references used here; 12 meters usage.


Additive endpoints accepted while detailing (TL T56; enter `contracts/openapi.yaml` with the story that builds them): `GET /v1/suites/{s}` (`suite:read`), `GET /v1/fixtures/{f}` (`suite:read`), `GET /v1/projects/{p}/case-import-mappings` (`case:read`), `POST /v1/projects/{p}/git-repo-bindings` (`project:write`, audited), `GET /v1/projects/{p}/catalog-syncs` (`suite:read`), `GET /v1/drafts/{d}/dry-runs` (`draft:dry_run`). New error codes (`case_field_immutable`, `mapping_conflict`, `suite_slug_taken`, `secret_value_rejected`, `namespace_collision`, `id_source_conflict`, `duplicate_case_id`, `revision_immutable`) are registered in `contracts/openapi.yaml` (T52). Draft transitions `dry_failed → dry_running` and `pr_open → draft` are in 00 §8.10.

### 4.3 Flows

**Catalog sync (V1).** GitHub push/tag → `POST /v1/webhooks/github` → verify signature → resolve the tenant through `evidgate_resolve` (SECURITY DEFINER, lookup key = GitHub `installation_id`/`repository_id`; 00 §5.4.8, T1) → open the tenant scope → dedupe by `delivery_id` → enqueue `catalog_sync` on the `task` queue (00 §5.3) → worker fetches the commit tree via the App installation (read-only; typed `HttpClient` + RS256 app JWT, T46) → discovers `evidgate.id` annotations and sidecar manifests → upserts `test_case` (+ `case_identity_source`) and appends `test_revision` rows → detects renames/moves (`case_rename_history`) and duplicates (conflict report) → registers suite declarations as new `suite_revision` rows when their definition changed → emits `catalog.synced` through the outbox. A scheduled reconcile (Cronos schedule with unique fire key `(schedule_name, fire_at)`, 00 §5.3) repairs missed deliveries without deleting anything (D8).

**Import identity (V1).** A run's producer reports a test with no annotation → `case_import_identity` row in `unmapped` (key = `producer+suite+name`). If exactly one new annotation with the same classified name appears in the same commit-range and no other candidate matches, the sync proposes a mapping; anything else stays `conflicted`/`unmapped` for the UI to resolve (D2). Mapped rows keep the original key for future imports.

**Variant hash.** Normalise (lower-case enum values, apply defaults, sort keys, drop keys marked non-identifying) → reject secret-looking keys/values → canonical JSON (RFC 8785, 00 §7) → `sha256` → reuse the existing `variant` row when the hash exists.

**Suite revision.** Members + variants + edges + shard strategy + retry policy + environment requirements are written in one transaction: validate the DAG (topological sort; reject cycles with the path), validate that every member's `case_revision` belongs to the same org/project, compute `revision_hash`, insert. Referenced revisions cannot be edited (409) — a change creates `revision_no + 1` (D4).

**Fixtures and namespaces.** A suite revision references fixture revisions; at run creation (05) the orchestrator asks the catalog module (`Evidgate.Modules.Catalog.Contracts`) for a namespace `(tenant, project, run)`, which inserts `data_namespace` with `expires_at = run deadline + grace`. A TTL sweeper — a Cronos schedule firing one `task` row per fire via unique `(schedule_name, fire_at)` (00 §5.3) — cleans expired namespaces whose run is terminal and marks `cleanup_failed` for operator follow-up after N attempts; orphan namespaces (no live run) are always cleaned.

**Draft → PR (V2).** Template or AI produces draft content → `dry_run` in the sandbox (own namespace, no tenant secrets, capability downgraded) → a `dry_passed` draft can create a branch + commit + PR via the App installation token; the PR body links the draft, the dry-run result and (for AI) the approved requirement. Merging the PR triggers the normal catalog sync, which reconciles the draft to the new case/revision (D10).

**Bug → regression candidate (V2).** An incident labelled `product_defect` (08, human ground truth) may generate a candidate test draft from the failure evidence. If the buggy and fixed builds are both available as evidence, the candidate must fail on the buggy one and pass on the fixed one; otherwise the candidate carries an explicit recorded limitation and cannot be marked verified.

### 4.4 States owned here (canonical list: 00 §8.10, T46)

`case_import_identity`: `unmapped → mapped | conflicted` (`conflicted → mapped` after human resolution). `suite_revision`: `draft → published` (published = referenced by a run; effectively frozen). `catalog_sync`: `queued → running → done | failed`. `data_namespace`: `active → cleaned | cleanup_failed`. `draft` (V2): `draft → dry_running → dry_passed | dry_failed → pr_open → merged | closed`. `fixture_revision`: immutable on create. Every transition here is audited through 02.7.1.

### 4.5 Default role mapping (catalog permissions)

Roles are seeded as data from `contracts/permissions.json` (02 §3 D2); 00 §2 lists the roles and defers the catalog strings and their default mapping to this section (§13 CR-5). `yes` = granted by default.

| Permission | `viewer` | `qa_engineer` | `test_maintainer` | `release_approver` | `tenant_admin` |
|---|---|---|---|---|---|
| `case:read` | yes | yes | yes | yes | yes |
| `suite:read` | yes | yes | yes | yes | yes |
| `case:write` | - | - | yes | - | yes |
| `suite:write` | - | - | yes | - | yes |
| `fixture:write` | - | - | yes | - | yes |
| `draft:write` (V2) | - | yes | yes | - | yes |
| `draft:dry_run` (V2) | - | yes | yes | - | yes |
| `draft:merge_request` (V2) | - | yes | yes | - | yes |
| `ai_draft:request` (V2) | - | yes | yes | - | yes |

- `viewer` and `release_approver` are read-only here: release policy and waivers are 07's permissions (`policy:write`), never a catalog write, and neither role edits a case, suite or fixture.
- `qa_engineer` reads and drafts (`draft:write`, `draft:dry_run`, `draft:merge_request`, `ai_draft:request`); it cannot write a case, suite or fixture - that is `test_maintainer`.
- `test_maintainer` owns case/suite/fixture writes and the draft workflow; it still cannot approve a sensitive healing candidate it authored (DECISIONS §5.5).
- `tenant_admin` holds every permission in this table as a role grant; that grant does not bypass revision immutability (D4), the 409 on a referenced revision or the secret-free rules (FR-03-5).
- Open: Q4 changes only the `draft:merge_request` cell - whether a `test_maintainer` may open a PR from a `dry_failed` draft (`allow_unverified`) or that stays `tenant_admin`-only.

## 5. Failure modes and edge cases

| Case | Handling | Invariant kept |
|---|---|---|
| Two files declare the same `evidgate.id` in one commit | Sync reports `duplicate_case_id`, writes no revision for those files, keeps previous revisions, surfaces the conflict in 09 | One id = one case; history is never overwritten |
| File moved/renamed with the same id | Same case, new `file_path`, `case_rename_history` row | Identity survives refactors (00 §3) |
| Test deleted (or file removed) | Case marked `missing` (status), history and revisions kept; suites keep referencing the last revision until the suite revision changes | Deleting code never silently drops evidence history |
| Imported test later gets an annotation | Mapping binds the import key to the annotated case; historical revisions are re-pointed only by an explicit mapping action, never automatically | No silent merge of two identities |
| Imported key collides (two same-named tests) | `conflicted`; UI mapping required | Ambiguity is resolved by a human, not by a guess |
| Sidecar manifest names a file that does not exist / is malformed | Sync warning row, no case created, other files still processed (partial sync is reported, not silent) | One bad file cannot block the catalog |
| Variant params contain a secret-looking key or value | 422 `variant_not_secret_free` | Secrets never enter an immutable row (D5) |
| Dependency cycle or self-dependency | 422 `dependency_cycle` with the path; revision not written | DAG is always acyclic (FR-03-6) |
| Edit a suite revision already used by a run | 409 `revision_frozen`, response suggests creating a new revision | Evidence points at an immutable input |
| Suite revision references another project's case revision | 422 `cross_project_member` (and composite FK as backstop) | No cross-project or cross-tenant membership |
| Fixture namespace orphaned by a crashed run | TTL sweeper cleans by `expires_at` regardless of run state; retries idempotently; failures surface to operators | Test data does not leak between runs (source §4) |
| Webhook duplicated, replayed or forged | Duplicate `delivery_id` → 200 no-op; forged/missing signature → 401, nothing processed | At-least-once delivery, exactly-once effect (A10) |
| App installation suspended or uninstalled | `git_repo_binding.sync_state = disabled`, sync stops, catalog keeps serving last known state; UI shows staleness | Losing Git access never destroys history |
| GitHub API rate limit or 5xx during sync | Retry with backoff, `catalog_sync` failed, scheduled reconcile repairs later | Sync is resumable and idempotent |
| OpenAPI skeleton cannot express an assertion | Template emits the request skeleton + schema-conformance checks only and marks the assertion as author-supplied | No inferred business assertion (FR-03-9) |
| Dry run fails or sandbox unavailable | Draft stays `dry_failed` with the reason; PR creation requires `dry_passed` unless a `test_maintainer` sets `allow_unverified` (recorded in the PR body) (T46) | Unverified drafts cannot masquerade as verified |
| Stale `If-Match` on a draft edit | 412 (00 §9.1); the client reloads and re-applies | No lost update |
| AI draft is asked to run in a pool with sensitive permissions | Refused while unapproved (`permission_denied`), only the sandbox capability is offered | Unapproved AI code never runs with sensitive permissions (FR-03-11) |
| AI draft reproduces or weakens an assertion | Assertion hash + human review; the draft cannot modify an existing revision | Intent is never silently changed (healing rule 1) |
## 6. Security and privacy

- **Untrusted input everywhere.** Repository content, sidecar manifests, OpenAPI files, producer names and AI output are untrusted: parsed with size/type limits, never executed at sync time, and never used to build SQL or file paths directly (DECISIONS F4, plan 11).
- **GitHub App least privilege.** Read-only repository content + webhooks for V1; write access to contents/PRs only when the V2 draft workflow ships. The V1 sync permission set (Contents: read, Metadata: read) was verified against GitHub's documentation in 03.4.1-T1.1 (2026-11-13, `plans/features/03-catalog-authoring/03.4.1-commit-registration-and-catalog-sync.md` §3 note); Metadata auto-grant stays ⚠️ until the registration UI is read.
- **Drafts and the platform.** The platform never holds a user's Git credentials; installation tokens are short-lived and never stored. A draft is only ever a PR (D10); protected branches are never written to.
- **AI drafts.** Approved requirements only; LLM output is structured draft content with no shell, no secret access and no policy power (source §8); provenance (`ai_run_id`, model id, prompt hash) is stored so an auditor can see what produced the code; unapproved AI code runs only in the sandbox (D11).
- **Secrets.** Suite/fixture revisions carry references, never values (D7); variant normalisation rejects secret-shaped data (D5); resolution happens in the customer CI or the agent's environment (11).
- **Personal data.** Test names, file paths, labels and fixture seed data can contain personal data (DECISIONS §7). Catalog stores metadata and hashes, not report bodies (those live in 06); retention and deletion of case/fixture history follow 11, and tenant deletion removes catalog rows except the minimum audit trail.
- **Immutability as integrity.** `test_revision` / `suite_revision` append-only rules plus `revision_hash` are the anti-tamper mechanism that healing (08) and evaluation (07) depend on; any change is detectable by re-hashing from the pinned commit.
## 7. Test strategy

- **Behaviours.** Annotation → case → revision on a real commit; rename keeps the id; import without id then mapping; suite revision write with members + variants + DAG; catalog sync repairing a missed webhook; template conformance (`evidgate init` output registers a case id); dry-run gating of PR creation.
- **Boundaries.** Zero cases in a repo; 2 000 cases in one sync (pagination); two files with one id; duplicate member/variant in a suite; self-dependency and a 3-node cycle; variant hash stability across key reordering and default filling; `revision_no` conflict on parallel suite writes; namespace expiry at the grace boundary; ETag stale/current.
- **Failure cases.** Forged webhook signature; repeated delivery id; App installation suspended; GitHub 5xx/429 mid-sync; malformed sidecar manifest; variant containing `api_token`; sandbox timeout; dry run that passes but whose PR is later rejected by CI.
- **Fixtures.** Golden repos under `fixtures/catalog/`: `annotated-basic`, `renamed-file`, `duplicate-id`, `sidecar-manifest`, `no-ids-imported`, `monorepo-same-names`, `openapi-skeleton` — each with the expected case/revision/rename/mapping output as JSON.
- **Proof of the bug→regression rule (V2).** A fixture pair of builds (one buggy, one fixed) with the same run evidence shape, asserting fail-on-buggy and pass-on-fixed, and a third case where only one build exists asserting the recorded limitation is set.
- **Stack.** xUnit v3 + Shouldly unit tests for parsing, hashing and DAG validation; Testcontainers.PostgreSQL integration tests for schema immutability, composite FKs, RLS and sync transactions (00 §5.5); template-emitted customer tests stay TypeScript/Vitest (unchanged); the sandbox dry run is tested against a mock runner in 05's contract tests.
## 8. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Annotation discipline in customer repos is weak (tests without ids) | high | medium | Import identity + UI mapping (D2/D13); onboarding in 12 makes annotated tests the default; templates always emit an id |
| Monorepo same-name collisions make auto-mapping wrong | medium | high | Never auto-bind on name alone; propose a mapping only on a unique match, otherwise require the UI (D2) |
| Catalog sync load (large repos, many commits) | medium | medium | Incremental sync per push, scheduled reconcile, pagination and rate-limit-aware backoff; measure in 10 |
| Git host changes webhook payloads or App permissions | medium | medium | Payload hash + dedupe table, contract fixtures for the events we consume, ✅ permission list verified in 03.4.1-T1.1 (Metadata auto-grant ⚠️) |
| Sandbox dry run leaks tenant data into a draft or vice versa | low | high | Ephemeral namespace, no tenant secrets, one-shot identity, capability downgrade (D11); tested in 11 |
| V2 scope creep: the draft editor becomes an IDE | medium | high | V1 is template + Git only (D9); V2 acceptance is "draft → dry run → PR", nothing more |
| AI drafts produce plausible but wrong assertions | high | medium | Human review + assertion hash + provenance; AI code bound to approved requirements; evaluation task 03.7.1-T3 measures validity before any adoption |
| Bug→regression candidates marked verified without a control build | medium | high | Explicit recorded limitation field; the "verified" flag requires fail-on-buggy + pass-on-fixed evidence |
| Fixture namespaces accumulate and inflate storage | medium | low | TTL sweeper + orphan cleanup + operator-visible `cleanup_failed` (FR-03-7); storage measured in 10 |
| Suite revision immutability slows legitimate suite edits | low | low | New revision per change, `diff` endpoint to see the delta; the run references a revision, not a mutable suite |
## 9. Epics, stories and tasks

Sizes: XS 0.25 · S 0.5 · M 1 · L 2 · XL 3 (person-days, one senior full-stack .NET + Angular developer, CR-D-1). Version = V1/V2/V3; milestones from 00 §4. Each epic total equals the sum of its task rows.
### Epic 03.1 Test identity and revisions
#### Story 03.1.1 Stable case identity — acceptance: a case keeps its id across rename/move; a duplicated id in one commit is a reported conflict and creates no revision for the offending files; the identity source is recorded.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 03.1.1-T1 | `test_case` + `case_identity_source` schema; `evidgate.id` annotation parser and sidecar manifest reader (fixed sidecar filename; annotation and sidecar disagreeing = conflict, 00 §7) | L | V1 | G1 | 02.1.1-T1, 01.4.1-T1 |
| 03.1.1-T2 | Rename/move detection + `case_rename_history`; duplicate-id conflict reporting; `missing` status | S | V1 | G1 | 03.1.1-T1 |
**Story 03.1.1 total: 2.5**
#### Story 03.1.2 Imported identity and mapping — acceptance: an import without an id creates a provisional identity; a collision requires UI resolution; a mapped identity never absorbs an annotated case automatically.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 03.1.2-T1 | `case_import_identity` + provisional key `(producer, suite, name)` + unique-match proposal rules | M | V1 | G1 | 03.1.1-T1, 04.3.2-T1, 04.4.1-T1 |
| 03.1.2-T2 | Mapping API + resolution rules for `conflicted`/`unmapped` (UI contract for 09) | M | V1 | G1 | 03.1.2-T1 |
**Story 03.1.2 total: 2.0**
#### Story 03.1.3 Immutable test revisions — acceptance: `test_revision` rows cannot be updated or deleted while referenced; the same content yields the same `revision_hash`; the assertion hash is stored.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 03.1.3-T1 | `test_revision` schema, content + assertion hashing, append-only enforcement (privileges + trigger) | M | V1 | G1 | 03.1.1-T1 |
**Story 03.1.3 total: 1.0**
#### Story 03.1.4 Requirement link, taxonomy and execution-coverage definition — acceptance: a case may carry an optional `requirement_ref`, platform × level × purpose are stored fields, and UI coverage is defined as declared executions, not code coverage.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 03.1.4-T1 | Decision + contract: optional `requirement_ref` on `test_case` (or recorded decision to drop it); 00 §7 and §15 updated before schema | S | V1 | G1 | 03.1.1-T1 |
| 03.1.4-T2 | Add `platform`, `level`, `purpose` to suite/template with an enum catalogue; mobile emulation counts only as web | S | V1 | G1 | 03.2.1-T1 |
| 03.1.4-T3 | State the coverage definition in 00 §3 and 09: ratio of declared case × variant executions with an accepted result; a Chromium pass never masks a Firefox failure | S | V1 | G1 | 03.1.4-T2, 03.2.1-T2 |

**Story 03.1.4 total: 1.5** — `[INFERENCE]` sizes; added from review 2026-10-02 §5.4.

**Epic 03.1 total: 7**
### Epic 03.2 Suite revisions
#### Story 03.2.1 Versioned suites — acceptance: a suite revision pins members, variants, DAG, shard strategy, retry policy and environment requirements; cycles are rejected; a referenced revision cannot be edited.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 03.2.1-T1 | `suite` / `suite_revision` / `suite_member` schema + create API + `revision_hash` | M | V1 | G1 | 03.1.3-T1 |
| 03.2.1-T2 | Variant normalisation, secret-free validation, `variant` dedupe by hash | S | V1 | G1 | 03.2.1-T1 |
| 03.2.1-T3 | Dependency DAG validation (topological sort, cycle path in the 422) + `case_dependency` | M | V1 | G1 | 03.2.1-T1 |
| 03.2.1-T4 | Shard strategy, retry policy and `environment_requirements` fields (base URLs, dependency versions that affect results, `data_isolation` (`isolated` = isolated data, `shared` = not isolated; read by 05.7.3-T4), secret references only) + secret-free validation against 05's manifest needs | M | V1 | G1 | 03.2.1-T3 |
| 03.2.1-T5 | Suite revision diff/preview API + `ETag` on the suite head | S | V1 | G1 | 03.2.1-T4 |
**Epic 03.2 total: 4.0**
### Epic 03.3 Fixtures and data namespaces
#### Story 03.3.1 Fixtures and per-run data — acceptance: a suite revision references fixture revisions by identity; each run gets its own namespace; expired and orphan namespaces are cleaned and reported.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 03.3.1-T1 | `fixture_revision` schema + suite reference + seed versioning + cleanup policy fields | M | V1 | G2 | 03.2.1-T1 |
| 03.3.1-T2 | `data_namespace` allocation contract used by 05 + TTL/orphan cleanup as a Cronos schedule firing a `task` row (unique fire key, 00 §5.3) | M | V1 | G2 | 03.3.1-T1, 01.5.1-T1 |
| 03.3.1-T3 | Secret-reference rule + lint that rejects secret values in fixtures/suites/variants | S | V1 | G2 | 03.2.1-T2, 11.3.1-T1 |
**Epic 03.3 total: 2.5**
### Epic 03.4 Git integration
#### Story 03.4.1 Commit registration and catalog sync — acceptance: a push registers the commit and syncs cases/suite revisions idempotently; a duplicate or forged delivery never changes data; a suspended installation stops sync without deleting history.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 03.4.1-T1 | GitHub App registration + installation binding (minimal permission list verified in T1.1; Metadata auto-grant ⚠️) + `git_repo_binding`; GitHub API access via typed `HttpClient` + RS256 app JWT (`Microsoft.IdentityModel.JsonWebTokens`), no Octokit (T46) | L | V1 | G1 | 02.1.1-T2 |
| 03.4.1-T2 | Webhook receiver: `X-Hub-Signature-256` verify → `evidgate_resolve` SECURITY DEFINER pre-tenant resolution (`installation_id`/`repository_id` → `org_id`, T1) → tenant scope → `webhook_delivery` dedupe → `catalog_sync` task | M | V1 | G1 | 03.4.1-T1 |
| 03.4.1-T3 | Catalog sync task (tree walk, annotation/sidecar discovery, case upsert, revision append, rename + conflict report) + scheduled reconcile (Cronos, 00 §5.3) | M | V1 | G2 | 03.4.1-T2, 03.1.1-T2, 03.2.1-T1 |
**Story 03.4.1 total: 4.0**
#### Story 03.4.2 Pull-request plumbing (V2) — acceptance: an installation token can create a branch, a commit and a PR for a draft, idempotently, and never pushes to a protected branch; the token is never persisted.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 03.4.2-T1 | Installation-token handling, branch/commit/PR creation service (typed `HttpClient`, T46), idempotency by `(branch, commit)`, PR body with evidence links | M | V2 | G2 | 03.4.1-T1, 03.6.1-T1 |
**Epic 03.4 total: 5.0 · (V1 4.0 · V2 1.0)**
### Epic 03.5 Templates
#### Story 03.5.1 Template library and Git flow — acceptance: each template produces a test that registers a case id and passes conformance checks; the API template never invents business assertions; unit and mobile templates stay in their own frameworks.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 03.5.1-T1 | `template` registry + web template (role/name preference, test id, conformance rules) | M | V1 | G4 | 03.1.1-T1 |
| 03.5.1-T2 | API template generated from an OpenAPI skeleton (request skeleton + schema conformance only; assertion left to the author) | M | V1 | G4 | 03.5.1-T1, 04.3.1-T1 |
| 03.5.1-T3 | Unit template: keep the repo's own framework, evidence-only mapping rules | S | V1 | G4 | 04.4.1-T1 |
| 03.5.1-T4 | `evidgate init` wiring + conformance check run in CI (template output must register a case id) | S | V1 | G4 | 03.5.1-T1, 03.5.1-T2 |
| 03.5.1-T5 | Mobile capability template (Appium, separate fixtures and locators, platform capability document) | S | V2 | G4 | 03.5.1-T1, 05.7.3-T1 |
**Story 03.5.1 total: 3.5 · (V1 3.0 · V2 0.5)**
**Epic 03.5 total: 3.5 · (V1 3.0 · V2 0.5)**
### Epic 03.6 Draft editor (V2)
#### Story 03.6.1 Draft → dry run → PR — acceptance: a draft is edited with `If-Match` (stale → 412), dry runs in a sandbox with no tenant secrets, and reaches the repo only as a PR that links the draft and the dry-run result.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 03.6.1-T1 | `draft` schema, `version`-based `ETag`/`If-Match`, state machine, audit integration | M | V2 | G2 | 03.2.1-T5, 02.7.1-T2 |
| 03.6.1-T2 | `dry_run` sandbox: ephemeral data namespace, capability downgrade, no tenant secrets, result capture | L | V2 | G4 | 03.3.1-T2, 05.7.4-T2, 05.7.3-T1 |
| 03.6.1-T3 | PR creation from `dry_passed` drafts (delegates to 03.4.2-T1) + `allow_unverified` override with reason (T46) | M | V2 | G4 | 03.6.1-T2, 03.4.2-T1 |
| 03.6.1-T4 | Draft reconciliation on PR merge/close (draft → catalog sync → case/revision link) | M | V2 | G4 | 03.6.1-T3, 03.4.1-T3 |
**Epic 03.6 total: 5.0**
### Epic 03.7 AI drafts (V2)
#### Story 03.7.1 AI-assisted drafts — acceptance: only approved requirements and allowed context are accepted; provenance is stored; an unapproved AI draft can never run with sensitive permissions; Playwright's generator agents are evaluated against a fixture set before any adoption.

> **CR-D-2 note (2026-10-23).** `03.7.1-T1` and `03.7.1-T2` are **superseded by `13.3.1`** — requirement → checklist → test case generation through the gateway with redaction, opt-in, credits and provenance, plus batched review and immutable versions ([13 §3](13-product-phases.md) `[DECISION]`). In the CR-D-2 model AI output is authoring data, never executable code — script generation is `13.5` and reaches the repository only as a pull request (A2) — so the unapproved-AI-code path and its sandbox permission policy no longer exist `[INFERENCE]`. Both rows keep their ids and stay for history: each is G4 and no new work starts on it. `03.7.1-T3` is **kept** (G2) and feeds `13.5.1-T1`: the Playwright planner / generator evaluation is an input to script generation.

| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 03.7.1-T1 | AI draft pipeline: approved-requirement input, context allowlist, redaction, provenance (`ai_run_id`, prompt hash, model) | L | V2 | G4 | 03.6.1-T1, 11.4.1-T3 |
| 03.7.1-T2 | Permission policy enforcement: unapproved AI code runs only in the sandbox capability; approval before PR/merge; audit trail | M | V2 | G4 | 03.7.1-T1, 02.2.1-T2 |
| 03.7.1-T3 | **Evaluate** Playwright planner/generator agents (DECISIONS F1 ✅) as draft generators against the fixture set: validity rate, no-skip compliance, review effort, cost | S | V2 | G2 | 03.1.3-T1 |
**Epic 03.7 total: 3.5**
### Epic 03.8 Confirmed bug → regression candidate (V2)
#### Story 03.8.1 Regression candidates from confirmed bugs — acceptance: a candidate generated from a confirmed defect fails on the buggy build and passes on the fixed build when both are available; otherwise the limitation is recorded and the candidate cannot be marked verified.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 03.8.1-T1 | Candidate regression test draft from confirmed incident evidence (why the assertion matches the failure) | L | V2 | G4 | 03.7.1-T1, 08.2.2-T3 |
| 03.8.1-T2 | Proof harness: fail-on-buggy / pass-on-fixed using run evidence; recorded-limitation field when a control build is missing | M | V2 | G4 | 03.8.1-T1, 06.1.2-T2, 07.2.1-T2 |
**Epic 03.8 total: 3.0**
### Epic 03.9 History and baseline (V1) [PROPOSAL review 2026-10-02]
#### Story 03.9.1 Case result history and baseline read model — acceptance: a case shows its recent results per variant, a new failure is distinguished from one already failing on the main branch, and history respects RLS and retention.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 03.9.1-T1 | `case_result_history` read table projected from accepted evidence revisions by an outbox consumer; Dapper read model | M | V1 | G3 | 05.3.2-T5, 06.4.6-T1 |
| 03.9.1-T2 | Classification new failure vs failing on main and flake-rate input; `GET …/cases/{id}/history` with paging | M | V1 | G3 | 03.9.1-T1 |
| 03.9.1-T3 | RLS, retention and replay tests (projection rebuild from revisions gives the same rows) | S | V1 | G3 | 03.9.1-T1 |

**Story 03.9.1 total: 2.5** — `[INFERENCE]` sizes; added from review 2026-10-02 §5.4.

#### Story 03.9.2 Historical import to seed a baseline — acceptance: a team can import past JUnit/Playwright JSON results as `purpose = import` runs that feed history and never a release decision.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 03.9.2-T1 | Import endpoint accepting an archive of past reports with explicit commit/branch/time; runs marked `import` and excluded from evaluation | M | V1 | G3 | 03.9.1-T1, 04.4.2-T1 |
| 03.9.2-T2 | `evidgate import-history` CLI command with a dry run and a size cap | S | V1 | G3 | 03.9.2-T1, 04.2.3-T1 |
| 03.9.2-T3 | Tests: an import never changes a decision or quota ledger beyond its own usage rows, and is idempotent | S | V1 | G3 | 03.9.2-T1 |

**Story 03.9.2 total: 2** — `[INFERENCE]` sizes; added from review 2026-10-02 §5.4.

**Epic 03.9 total: 4.5**

## 10. Dependencies

- **01** (`01.4.1-T1` migrations, `01.3.1-T1` contracts pipeline, `01.5.1-T1` worker/outbox + `task`-queue bootstrap).
- **02** (`02.1.1-T1` org/project, `02.2.1-T1` permissions including `case:*`, `suite:*`, `fixture:*`, `draft:*`; `02.7.1-T2` audit writer; `02.6.*` RLS for every table here).
- **04** (`04.4.1-T1` JUnit mapping, which produces the producer/suite/name keys imports without an id depend on; `04.3.1-T1` reporter skeleton and capability document; `04.2.1-T1` CLI exchange, which consumes the catalog's id rules).
- **05** (`05.7.3-T1` pools and capability matching, `05.7.4-T2` executor for the dry-run sandbox, `05.1.1-T1` manifest that locks a suite revision).
- **06** (`06.1.2-T2` artifact completion), **07** (`07.2.1-T2` evidence revision used by the bug→regression proof; quarantine references cases), **08** (`08.2.2-T3` human triage as ground truth), **09** (case/suite/draft screens), **10** (capacity and storage measurements), **11** (`11.4.1-T3` outbound context control; secret storage, untrusted code, PDPL), **12** (usage metering for dry runs and AI drafts).
## 11. Open questions

| # | Question | Owner | Needed by |
|---|---|---|---|
| Q1 | Sidecar manifest filename, format and precedence vs the annotation when both exist in one file [OPEN] | Tech Lead + QA Lead | before 03.1.1-T1 is detailed |
| Q2 | Where dependency edges are declared: test metadata, suite definition file, or a CLI flag | QA Lead | before 03.2.1-T3 |
| Q3 | OpenAPI skeleton scope in V1: which parts of a spec produce assertions (status/schema only?) | Tech Lead | before 03.5.1-T2 |
| Q4 | May a `test_maintainer` create a PR from a `dry_failed` draft (with `allow_unverified`), or is it admin-only? | Product owner + QA Lead | before 03.6.1-T3 |
| Q5 | Which GitHub App permissions are the minimum for V1 sync only (answered in 03.4.1-T1.1: Contents: read + Metadata: read; Metadata auto-grant ⚠️) | Tech Lead | before 03.4.1-T1 |
| Q6 | Fixture seed format and who authors seed data (customer repo vs Evidgate-side fixture repo) | QA Lead | before 03.3.1-T1 |
| Q7 | Should imported (unmapped) results be visible in reports before mapping, or hidden until resolved? | Product owner | before 03.1.2-T2 |
| Q8 | Does V2 need multi-repo projects (one project, several repositories)? | Product owner | before 03.4.1-T1 |
## 12. Estimate summary

| Epic | V1 | V2 | V3 | Total |
|---|---|---|---|---|
| 03.1 Test identity and revisions | 7.0 | 0 | 0 | 7.0 |
| 03.2 Suite revisions | 4.0 | 0 | 0 | 4.0 |
| 03.3 Fixtures and data namespaces | 2.5 | 0 | 0 | 2.5 |
| 03.4 Git integration | 4.0 | 1.0 | 0 | 5.0 |
| 03.5 Templates | 3.0 | 0.5 | 0 | 3.5 |
| 03.6 Draft editor | 0 | 5.0 | 0 | 5.0 |
| 03.7 AI drafts | 0 | 3.5 | 0 | 3.5 |
| 03.8 Bug → regression candidate | 0 | 3.0 | 0 | 3.0 |
| 03.9 History and baseline | 4.5 | 0 | 0 | 4.5 |
| **Total** | **25.0** | **13.0** | **0** | **38.0** |

→ with 20 % buffer **45.6** person-days
## 13. Contract change requests

> **Status:** Applied 2026-09-24: CR-1 → 00 §7 (conflict rule; filename in 03.1.1-T1); CR-2 → 00 §8.10; CR-3 → 00 §3; CR-4 → 00 §9.3; CR-5 → 00 §2 (mapping owned by 03 §4); CR-6 confirmed after the 09 renumbering.

> For the integrator; 00/`DECISIONS.md` are not edited by this plan.
> 2026-09-25 CR-D-1 + TL review applied: migrated to the .NET 10 stack (`task` queue + Cronos schedules instead of pg-boss; xUnit v3 + Testcontainers.PostgreSQL instead of Vitest/Testcontainers 12 on server code) and rulings T1 (`evidgate_resolve` webhook pre-tenant resolution, 03.4.1-T2), T3 (`unique (org_id, id)` on every owned table), T46 (`suite_member` surrogate `id` so `case_dependency` FKs are implementable, `allow_unverified` typo fixed, suite/fixture revision states now in 00 §8.10, built-in `template` catalogue RLS pattern + lint exception, GitHub App via typed `HttpClient` + RS256 app JWT — no Octokit; 03.4.1-T1 resized M→L).

| # | Request | Why |
|---|---|---|
| CR-1 | 00 §7: record that a case id may also come from a **sidecar manifest** (`evidgate.id` inside it), which is an accepted alternative source with a documented filename and precedence rule (source §4) | 00 §7 names only the annotation; the design permits a sidecar manifest, and implementers need one precedence rule |
| CR-2 | 00 §8: add state machines for `draft` (`draft → dry_running → dry_passed|dry_failed → pr_open → merged|closed`), `case_import_identity` (`unmapped → mapped|conflicted`), `catalog_sync` (`queued → running → done|failed`) and `data_namespace` (`active → cleaned|cleanup_failed`) | Drafts and imports are proposed here without a canonical state list; 00 §8 covers only run/job/artifact/evidence/healing/waiver |
| CR-3 | 00 §3: add glossary entries for `template`, `fixture` / `fixture revision`, `data namespace`, `draft`, `dry run`, `catalog sync`, `case import identity` | These names are now used by 03, 05, 09 and must match everywhere |
| CR-4 | 00 §9.3: add the catalog endpoints to the core list — `GET /v1/projects/{p}/cases`, `POST /v1/suites/{s}/revisions`, `GET /v1/suite-revisions/{r}/diff`, `POST /v1/webhooks/github`, and (V2) `POST /v1/drafts/{d}/dry-run` / `POST /v1/drafts/{d}/pull-request` | They are the only way to see cases, version suites and receive Git events; the SDK/OpenAPI story needs them listed |
| CR-5 | 00 §2: add the permissions this plan uses (`case:read`, `case:write`, `suite:read`, `suite:write`, `fixture:write`, `draft:write`, `draft:dry_run`, `draft:merge_request`, `ai_draft:request`) to the default role mapping (`qa_engineer` gets draft/read; `test_maintainer` gets case/suite/fixture write; `release_approver` and `viewer` stay read-only) | Roles are seeded as data (02 §3 D2); the mapping must be defined once |
| CR-6 | Integrator: 09's dependency set on this plan is **reconciled** (re-verified from disk 2026-09-24) — `09.6.1-T1` → `03.5.1-T1`, `09.6.1-T2` → `03.6.1-T1`, `09.6.1-T3` → `03.2.1-T5` + `03.6.1-T1`, `09.6.1-T4` → `03.6.1-T2`, `09.6.1-T5` → `03.7.1-T1`, `09.6.1-T6` → `03.2.1-T1` + `03.2.1-T3` + `03.2.1-T4`, `09.2.1-T6` → `03.1.1-T1` + `03.1.2-T2`; all of these exist in this plan. Keep 03's ids stable — fix any future mismatch on the 09 side, never by renumbering 03 | Earlier 09 revisions used a different 03 numbering (e.g. `03.3.2-T1`, which never existed); recorded so the integrator can confirm the 09↔03 matrix in one pass without moving 03's scope |

2026-10-23 CR-D-2: requirement -> checklist -> manual test case authoring ownership moved to `13` epics 13.1 / 13.2 / 13.3 / 13.4 (G1); `03.7.1-T1` and `03.7.1-T2` marked superseded by `13.3.1` (rows kept, G4), `03.7.1-T3` kept (G2) and feeding `13.5.1-T1`; its dependency on `03.6.1-T2` (dry-run sandbox, G4) is dropped because the evaluation runs generated scripts locally or in the customer's CI (A5), as `13.5` does. No task ids or sizes changed; phases per 13 §4.4.
