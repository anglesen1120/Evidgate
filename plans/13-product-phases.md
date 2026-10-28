# 13 - Product phases, authoring and self-learning repair (G1–G3)

> Contracts: `00-overview.md` §3 (glossary), §4 (phases), §5.4 (tenancy), §7 (identifiers, hashes), §8.7 (incident labels), §9 (API). Facts: `../DECISIONS.md` §3 A2/A4/A5, §4, §5 (safe-healing rules), §7 (personal data), CR-D-2 in [`../docs/decisions/CR-D-2-direction-a.md`](../docs/decisions/CR-D-2-direction-a.md).
> Level: high-level plan; each story gets a detailed plan in `plans/features/13-product-phases/` (00 §12) before build.
> Milestone column: the CR-D-2 phase (G1 Author, G2 Run, G3 Learn and repair, G4 Expand). §4.4 assigns every story of plans 01–12 to a phase.

## 1. Purpose and scope

**Goal.** A QA team writes requirements, gets a reviewed checklist and manual test cases, turns approved test cases into Playwright scripts bound to real elements, runs them in its own CI, and gets safe, reviewed repair proposals when the application's UI drifts, without a product bug ever being hidden.

**In scope.** The authoring model (feature tree, checklist, manual test case, versions, batched review), project AI settings, requirement → checklist → test case generation, export / import, the authoring screens (G1); script generation against the live application, `evidgate run`, element profiles, the mobile spike with mobile-mcp / mobilewright (G2); multimodal locate fallback, locator-drift vs UI-change classification, test case review proposals, review evidence additions (G3); the measurements behind each phase gate.

**Out of scope.** The platform pieces this plan reuses (tenancy 02, catalog identity 03.1–03.2, ingestion 04, artifacts 06, incidents / candidates / validation / approval 08), mobile execution (G4), hosted runners (A5), runtime auto-apply (§5.7).

## 2. Requirements

| ID | Requirement |
|---|---|
| FR-13-1 | Every authoring change (manual or AI) creates an immutable version with author, source (`manual` / `ai` / `import`) and reason; restore creates a new version. |
| FR-13-2 | A manual test case may link to one or more `test_case` identities (03.1.1); a test case without a linked script is visible as uncovered. |
| FR-13-3 | AI generation runs only through the gateway (08.5.1) with redaction (08.5.3), per-tenant opt-in (11.4.1-T3) and credits (08.5.5); provenance stores model, prompt hash and settings version. |
| FR-13-4 | A generated script binds every step to an element found on the live page; absolute XPath, `nth()`, `force: true`, `skip` / `fixme` and catch-and-ignore are never emitted (`DECISIONS.md` §5.2). |
| FR-13-5 | A generated script reaches the repository only as a pull request (A2) and becomes `active` only after 3 of 3 green runs on one build; green-on-retry is `flaky`. |
| FR-13-6 | Element profiles store attributes and structure (role, accessible name, test id, id, text length class, neighbours, position), never input values or user content. |
| FR-13-7 | Within the 08.2.2 label `test_drift`, a rule set separates locator drift (same role and accessible name) from UI change; only locator drift yields a script repair candidate; `product_defect`, `environment`, `data_issue` and `unknown` yield none. |
| FR-13-8 | Every gate metric in §4.3 is computed from stored data, not entered by hand. |

## 3. Key decisions

- `[DECISION]` CR-D-2: the product leads with authoring and self-learning repair; the release decision (07) is reporting, not the lead feature.
- `[DECISION]` A5 kept for G1–G3: generation, runs and replays execute on the customer side (`packages/cli`), never on Evidgate hosts.
- `[DECISION]` Rules decide, models suggest: profile similarity classifies; a model or the multimodal locator only proposes, and every proposal passes 08.4 validation.
- `[DECISION]` 03.7.1-T1/T2 are superseded by 13.3.1; 03.7.1-T3 (evaluate Playwright planner / generator) is kept and feeds 13.5.1-T1.
- `[DECISION]` Mobile (owner, 2026-10-23): mobile-mcp (Mobile Next, Apache-2.0, TypeScript, iOS and Android) with its Playwright-style framework mobilewright is the G2 spike candidate; it replaced Google ARTEMIS (Android only, Python), see CR-D-2 §7. Two techniques are adopted for web now: the pre-action target check (13.5.1-T1) and accessibility-first locating with an OCR / visual fallback (13.7.2). Shipping a mobile producer is a G4 decision.
- `[DECISION]` Role profiles (owner request 2026-10-26, direction accepted; story 13.2.2): the role list is data, not an enum. Evidgate ships read-only seed templates (BA, QA, PM, Dev; vi + en); a project defines its own role profiles by cloning and editing a seed (append-only versions); each generation picks one profile. A profile changes the content and tone of the output for that role and phase, never the output JSON schema, the gateway safety rules (08.5.1, 08.5.2, 08.5.3, 08.5.5) or any permission; role text is untrusted data. G1 ships BA and QA seeds; PM release summary and Dev failure triage are G2 (13.2.2-T8).

## 4. Design

### 4.1 Tables owned (columns at high level)

- `feature_node` (`org_id`, `project_id`, `id`, `parent_id`, `kind` module / feature / category / sub_category, `title`, `position`).
- `checklist_item` (`feature_node_id`, `title`, `priority` critical / high / medium / low, `state`).
- `manual_test_case` (`checklist_item_id`, `code` with project prefix, `title`, `description`, `precondition`, `steps` jsonb of action / expected / data, `priority`).
- `authoring_version` (append-only; entity ref, `version_no`, `source`, `author_membership_id`, `reason`, `content_hash`, `ai_run_id`).
- `authoring_change` (pending batched change; `state` pending / applied / discarded).
- `project_ai_settings` (versioned; the §2 fields with character limits) and `ai_settings_template` (built-in, read-only).
- `manual_case_link` (`manual_test_case_id`, `test_case_id`).
- `generated_script` (`manual_test_case_id`, `test_case_id`, `state` draft / active / flaky / retired, `stability_runs`, `pr_url`).
- `element_profile` (append-only per run and bound step; `case_revision_id`, `step_no`, profile jsonb, `profile_hash`, `spec_version`).
All tables carry `org_id`, composite tenant FKs and FORCE RLS (00 §5.4).
Refinements from the G1 feature plans (S52, 2026-10-23): `plans/features/13-product-phases/13.1.1-feature-tree-and-manual-test-cases.md` adds the support tables `authoring_code_counter` (per-project case-code counter), 13.1.2 adds `checklist_item_lineage` (merge and split lineage) and 13.2.1 adds `ai_settings_use` (one row per `ai_run_id`, the settings version a run used). Each carries `org_id`, composite tenant FKs and FORCE RLS like the tables above; none changes a task size.
Refinements from story 13.2.2 (S92, 2026-10-26): role profiles add the tables `role_prompt_template` (platform, read-only seeds, no `org_id`, like `ai_settings_template`), `role_profile` (identity, project scope), `role_profile_version` (append-only, `content_hash`) and `role_profile_use` (one row per `ai_run_id`: profile id, version, hash, optional run note ≤ 500 code points after NFC). The org-scoped tables carry `org_id`, composite tenant FKs and FORCE RLS like the tables above; the task sizes are in §9 (13.2.2-T3).

### 4.2 Flows

1. **Author:** requirement input → checklist generation → batched review (pending changes) → apply as one version → test case generation → review → approve.
2. **Script:** approved test case + environment URL + seed test → `evidgate generate` on the customer side → bound script → 3 stability runs → PR.
3. **Learn:** green run → reporter captures bound-step profiles → profile store (latest green per step).
4. **Repair:** failed run → incident (08.1) → 08.2.2 label → within `test_drift`, profile rules → locator drift: candidate (08.3) → validation (08.4) → review (08.6.1 + 13.8.2); UI change: test case review proposal.

### 4.3 Phase gates (measured by 13.9.1)

| Phase | Go when | Stop / pivot signal |
|---|---|---|
| G1 Author | At least 5 external teams use authoring for at least 3 weeks and export or use the test cases | Fewer than 3 teams return after week 1 |
| G2 Run | At least 70 % of generated scripts pass 3 of 3 runs on one build without manual edits; at least 3 teams agree to pay | Most scripts need manual repair |
| G3 Learn and repair | 0 adjudicated false repairs on the 08.8.1 corpus over at least 300 proposals, with the rule-of-three caveat (08.8.2-T2); acceptance rate reported, not gated; threshold stays O6 `[OPEN]` | Any false repair whose cause is not understood and fixed |
| G4 Expand | Paying demand per item | — |

### 4.4 Phase map for plans 01–12

The Milestone column of every task row in plans 01–12 holds its phase. Target phases were set per story as below; every dependency of a task was then pulled to the task's phase or earlier, so no task depends on a later phase (`scripts/plan_graph.py`). Stories not named stay in G4.

| Phase | Target stories (before dependency pull-in) |
|---|---|
| G1 | 01.1–01.9; 02.1, 02.2, 02.3.1, 02.3.2, 02.6, 02.7, 02.9; 03.1, 03.2; 08.5.1, 08.5.2, 08.5.3, 08.5.5; 09.1, 09.2, 09.7.1; 10.1, 10.2, 10.3, 10.8, 10.11; 11.1, 11.2, 11.3, 11.4, 11.5, 11.6, 11.8, 11.9; 12.2, 12.3, 12.6 |
| G2 | 01.10; 02.4, 02.8, 02.10.1; 03.3, 03.4, 03.7.1-T3 (story 03.7.1); 04.1, 04.2, 04.3, 04.6, 04.7, 04.8; 05.1–05.6, 05.10; 06.1–06.5; 08.1; 09.3; 10.5, 10.7, 10.9; 12.4 |
| G3 | 03.9; 04.9; 08.2, 08.3, 08.4, 08.6, 08.7, 08.8; 09.4, 09.5, 09.8; 12.5 |
| G4 | Everything else, including 02.3.3, 02.5, 03.5, 03.6.1 (T2…T4), 03.8, 04.4.1 (T2, T3), 04.4.2, 04.5, 05.7.2 (T2), 05.7.3–05.7.6, 05.8, 05.9, 07.1.1 (T2…T4), 07.2.2 (T2…T4), 07.3, 07.4.2, 07.5.2, 07.5.3, 07.5.4, 07.6–07.8, 08.9, 09.6, 09.9, 10.4.1-T2, 10.6.1 (T2, T4), 10.10, 11.7, 12.1 |

Task-level exceptions to the story targets: 09.2.1-T2…T8 → G2; 09.7.1-T3, T5, T9, T10, T11 → G2 and T4, T7, T8 → G4; 09.7.2 → G4; 09.3.1-T5, T9, T10, T11 (decision, waiver and release views) → G4 and 09.3.1-T8 (healed labels) → G3; 05.6.1-T2 (release binding by decision) → G4; 04.5.1-T1 and 06.3.5-T1 (mobile) → G4; 06.4.4-T2 (deleting healing knowledge) → G3; 11.5 → G2; 11.2.1-T4, T5 → G4 and T6 → G3; 11.3.1-T3 → G2; 12.3.1-T2…T6 → G2; 08.5.1-T3, 08.5.2-T2, 08.5.5-T4 → G3; 03.7.1-T1, T2 → G4 (superseded by 13.3.1, §3). Dependency changes: 08.5.1-T1 now depends on 12.3.1-T1 only (was 08.4.3-T3 and 12.3.1-T2), so the gateway can ship in G1 for authoring; 03.7.1-T3 now depends on 03.1.3-T1 only (was also 03.6.1-T2, the G4 dry-run sandbox), because generated scripts run locally or in customer CI (A5). The dependency pull-in still places a few listed tasks earlier than their exception, and the cell wins: 09.3.1-T5 is G3, 11.5.1-T1 and 11.5.2-T1 are G1, 12.3.1-T2 is G1.

Checked against the Milestone cells on 2026-10-26 (`scripts/plan_graph.py --json`): every row above stays a declared target, and each cell that sits in an earlier phase than its row is the dependency pull-in the intro describes, so the cell is authoritative for the current phase. G4-group stories with tasks pulled earlier: 07.1.1-T1, 04.4.1-T1 and 10.4.1-T1 → G1; 07.2.1 (T1, T2), 07.2.2-T1, 07.4.1 (T1, T2), 07.5.1 (T1, T2), 04.4.1-T4…T6, 03.6.1-T1, 05.7.1-T1, 05.7.2-T1 and 10.6.1-T1, T3 → G2; 04.4.2-T1 → G3, because 03.9.2-T1 (G3) depends on it; 07.5.2-T1 → G3, because 09.3.1-T5 (itself pulled to G3 for 09.5.2-T4) depends on it. Four G4-group stories are wholly earlier: 07.2.1, 07.4.1, 07.5.1 and 05.7.1 have only G2 tasks. Ten G2 targets were pulled into G1 completely (05.3.3, 06.1.1, 06.1.2, 06.2.2, 06.2.3, 06.4.1, 06.4.2, 06.4.3, 06.4.5, 06.5.2 — all their tasks are G1), 06.3.5 is wholly G4 per the exceptions line, and the G3 targets 08.2.1 and 08.3.1 are wholly G2. No target was rewritten to match a cell and no task was moved: the table records the target phase, each task row records the current one.

Result after the pull-in, all plans 01–13 (raw person-days, including work already merged and already including the 13.2.2 rows of S92: 8.5 in G1, 1.0 in G2): G1 281.75 · G2 195.5 · G3 106.75 · G4 119.25, summing to 703.25. Stories with tasks in two phases keep both. The Milestone cell of each feature-plan header in `plans/features/` still shows the old M value; `scripts/sync_feature_headers.py` refreshes it when that feature plan is next edited, because any edit brings the plan into `check-dor-security` scope (README §6). The phase in the area plan's task row is authoritative. A story's index phase is the earliest phase of its tasks, so `13.9.1` (one gate-metrics task per phase) shows as G1 in the README index while its T2 and T3 are G2 and G3.

## 5. Failure modes and edge cases

| Case | Handling |
|---|---|
| Two editors change one item | Batched changes apply with `If-Match`; stale → 412, nothing applied |
| Model output fails the schema | Rejected, credit refunded (08.5.5-T2), no version created |
| Requirement contains personal data | Redaction before the provider (08.5.3); tenant without opt-in gets generation disabled, manual authoring still works |
| Live page needs login or data | The customer's seed test runs first; without one, generation stops with an explicit reason |
| Element not found while generating | Step left unbound and the script stays `draft`; never guessed |
| Profile spec changes | `spec_version` stored; similarity only compares equal versions; backfill job re-derives |
| Ambiguous drift | Class `unknown` → no repair; the multimodal locator may propose, and the proposal still needs 08.4 validation |

## 6. Security and privacy

- AI inputs are untrusted (08.5.2, 11.4.1-T2); generated code runs only on customer infrastructure (A5).
- Requirement documents and images are scanned, size-capped and stored as artifacts with sensitivity (06.2).
- Element profiles keep structure, not content (FR-13-6); screenshots are opt-in per project.
- Permissions: `authoring:read`, `authoring:write`, `authoring:generate`, `script:generate`; seeded into the role mapping (00 §2) by 13.1.1-T2.
- Role profiles (13.2.2): profile text and the per-run note are untrusted user input — length-capped, NFC-normalised, refused when secret-shaped, passed to the model only as delimited data (never as instructions), redacted by 08.5.3 like other input and covered by an injection corpus (13.2.2-T4). Choosing a profile needs `authoring:generate` only and grants nothing; creating or editing one needs the same permission as AI settings (13.2.1, `project:write` recommended, OI-13-301).

## 7. Test strategy

Integration tests on real PostgreSQL for every table (RLS, composite FKs, append-only versions); isolation suite cases for every new route (02.9); generation evaluation set in vi + en (13.3.1-T5); script stability runs against a fixture application in `fixtures/`; profile stability tests (dynamic ids, locales, reorder); E2E Playwright for the authoring screens.

## 8. Risks

| Risk | Mitigation |
|---|---|
| A wrong repair hides a bug | Five layers in CR-D-2 §6.1; shadow window (13.8.1-T4) |
| AI cost exceeds revenue | Rule-first repair, cache, small / large model routing (13.3.1-T4), credits (08.5.5) |
| Generated scripts are flaky | Live binding, locator policy, 3 of 3 stability, testability score (13.5.1) |
| Scope too large for one developer | Phase gates in §4.3 are hard stops |

## 9. Epics, stories and tasks

Sizes: XS 0.25 · S 0.5 · M 1 · L 2 · XL 3 (person-days, one senior full-stack .NET + Angular developer, CR-D-1). Version = V1/V2/V3; Milestone = CR-D-2 phase. Each epic total equals the sum of its task rows.

### Epic 13.1 Authoring model (G1)
#### Story 13.1.1 Feature tree and manual test cases — acceptance: a project holds module → feature → category → checklist item → manual test case, tenant-isolated; every change creates an immutable version; a manual test case can link to a `test_case` identity.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.1.1-T1 | `feature_node`, `checklist_item`, `manual_test_case`, `authoring_version` schema + migrations (FORCE RLS, composite FKs, append-only versions) | L | V1 | G1 | 02.1.1-T1, 01.4.1-T1, 03.1.1-T1 |
| 13.1.1-T2 | CRUD API + `authoring:*` permissions + OpenAPI + audit + isolation route cases | L | V1 | G1 | 13.1.1-T1, 02.2.1-T2, 02.7.1-T2 |
| 13.1.1-T3 | Version and timeline read model; restore creates a new version | M | V1 | G1 | 13.1.1-T2 |
| 13.1.1-T4 | `manual_case_link` to `test_case`; uncovered-case view | S | V1 | G1 | 13.1.1-T2, 03.1.3-T1 |
| 13.1.1-T5 | Import / export CSV, XLSX, JSON with project code prefix; round-trip tests | M | V1 | G1 | 13.1.1-T2 |
#### Story 13.1.2 Batched review — acceptance: comments, merges and deletes on checklist items queue as pending changes and apply as one version with `If-Match`; a stale apply changes nothing.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.1.2-T1 | `authoring_change` model; apply-as-one-version with `If-Match` and idempotency key | M | V1 | G1 | 13.1.1-T3 |
| 13.1.2-T2 | Merge and split of checklist items with lineage | S | V1 | G1 | 13.1.2-T1 |
**Epic 13.1 total: 8.0**

### Epic 13.2 Project AI settings and role profiles (G1, G2)
#### Story 13.2.1 Versioned project AI settings — acceptance: description, environment URL, languages, writing style (≤ 250 chars), coverage strategy (≤ 1 500), four priority criteria, exclusion rules (≤ 1 500) and step detail (≤ 200) are stored per project with templates; the settings version is part of every generation's provenance.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.2.1-T1 | `project_ai_settings` + `ai_settings_template` (vi + en seeds), API, limits | M | V1 | G1 | 13.1.1-T2 |
| 13.2.1-T2 | Settings versioning in provenance and cache keys | S | V1 | G1 | 13.2.1-T1, 08.5.1-T1 |
#### Story 13.2.2 Role profiles and the role prompt library — acceptance: a tenant picks or defines a role profile (seeded BA, QA, PM and Dev templates in vi + en, cloneable and editable, versioned) and chooses one per generation; the profile changes the content and tone of the generated output for that role at that phase, never its output JSON schema, the gateway safety rules (08.5.1, 08.5.2, 08.5.3, 08.5.5) or the caller's permissions; role text is untrusted data (length-capped, normalised, delimited, redacted, covered by an injection corpus); provenance and cache keys carry the profile id and version; the seed prompt library is built from a licence-checked GitHub review.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.2.2-T1 | GitHub review and re-research of per-role prompts (BA, QA / test plan, test-case writer, integration-scenario writer, PM / release summary, dev failure triage) in public repos and prompt collections; the licence of every source is recorded against the ADR-0002 licence rule and no text is copied unless its licence allows closed-source commercial use; output is the memo `docs/research/role-prompts-github-review.md` (shortlist, gaps, licence table) | M | V1 | G1 | — |
| 13.2.2-T2 | Author and refine the seed prompt library per role × phase in vi + en (BA and QA first) from the memo and Evidgate's constraints (output schema, redaction, untrusted input, AC format of 00 and the feature-plan template), with a prompt-review checklist | L | V1 | G1 | 13.2.2-T1, 13.3.1-T3 |
| 13.2.2-T3 | `role_prompt_template`, `role_profile`, `role_profile_version`, `role_profile_use` schema + migration, API, FORCE RLS, composite FKs, audit, quotas | L | V1 | G1 | 13.2.1-T1, 02.2.1-T2, 02.7.1-T2 |
| 13.2.2-T4 | Safety layer: length and normalisation limits, delimited-data prompting, injection test corpus, redaction pass | M | V1 | G1 | 13.2.2-T3, 08.5.2-T1, 08.5.3-T1 |
| 13.2.2-T5 | Profile selection in the generation pipeline: resolve the profile, pin id + version + content hash in provenance and in the cache key | S | V1 | G1 | 13.2.2-T2, 13.2.2-T3, 13.2.2-T4, 13.2.1-T2, 13.3.1-T4 |
| 13.2.2-T6 | Profile selector in the generation flow and the profile editor screens, vi + en (design deltas through 13.4.1-T1 first) | M | V1 | G1 | 13.2.2-T5, 13.4.1-T1, 13.4.1-T4, 13.4.1-T5 |
| 13.2.2-T7 | Evaluation fixtures per seed role added to the 13.3.1-T5 set; regression gate on a prompt, seed-version or model change; custom profiles are not gated | M | V1 | G1 | 13.2.2-T2, 13.2.2-T5, 13.3.1-T5 |
| 13.2.2-T8 | PM release-summary and Dev failure-triage seeds: output kinds, schemas and fixtures, built once run results exist | M | V1 | G2 | 13.2.2-T2, 13.2.2-T5, 13.2.2-T7, 05.3.2-T5 |
Notes (S92, 2026-10-26, owner request): the three-layer design is seed templates (platform, read-only) → project role profiles (cloned and edited, append-only versions) → a per-generation choice (one profile plus an optional short run note stored with the run only). The role list is data, never an enum: Evidgate ships seeds, tenants define the rest. Picking a profile grants no permission (02.2 unchanged; generation still needs `authoring:generate`). G1 builds BA and QA seeds, clone-and-edit and the pick at generation time; PM release summary and Dev failure triage are T8 (G2); the test-plan and integration-scenario output kinds need their own schemas and generation tasks that no plan owns yet (OI-13-305 in `plans/open-items/05-product-phases-g1.md`). Phase placement relies on CR-D-2 (`docs/decisions/CR-D-2-direction-a.md`: authoring is the G1 lead feature). Detail: `plans/features/13-product-phases/13.2.2-role-profiles-and-prompt-library.md`.
**Epic 13.2 total: 11.0** (1.5 + 9.5: T1 1.0 + T2 2.0 + T3 2.0 + T4 1.0 + T5 0.5 + T6 1.0 + T7 1.0 + T8 1.0)

### Epic 13.3 Requirement → checklist → test case (G1)
#### Story 13.3.1 Generation pipeline — acceptance: requirement text, documents or images produce a prioritised checklist and then test cases through the gateway with redaction, opt-in and credits; output is schema-validated; provenance and cost are stored per action.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.3.1-T1 | Input intake: text, DOCX / PDF text extraction, images (type and size caps, stored as artifacts) | M | V1 | G1 | 13.1.1-T2, 06.1.2-T3 |
| 13.3.1-T2 | Checklist generation (structured output, priority from the settings criteria) | L | V1 | G1 | 13.3.1-T1, 13.2.1-T1, 08.5.1-T1, 08.5.5-T2, 11.4.1-T3 |
| 13.3.1-T3 | Test case generation from checklist items, plus rebuild and translate actions | L | V1 | G1 | 13.3.1-T2 |
| 13.3.1-T4 | Cache by hash of (input, settings version, model); small / large model routing | M | V1 | G1 | 13.3.1-T2 |
| 13.3.1-T5 | Evaluation set: 20 requirement fixtures in vi + en, rubric, regression gate on prompt or model change | M | V1 | G1 | 13.3.1-T3 |
**Epic 13.3 total: 7.0** Feature plan for story 13.3.1 (S112, 2026-10-27): `plans/features/13-product-phases/13.3.1-generation-pipeline.md` (19 sub-tasks of at most 0.5 d summing to the task sizes above, estimate unchanged; open items OI-13-401 to OI-13-413 in `plans/open-items/05-product-phases-g1.md`).

### Epic 13.4 Authoring screens (G1)
#### Story 13.4.1 Authoring UI — acceptance: an approved design (`design-ui` agent) precedes build; tree, table with filters, test case editor, batched changes panel, generation flow, timeline, settings and export work in vi + en and meet WCAG 2.2 AA; the navigation hides items of a later phase and the usage page shows the AI credit balance and usage by action.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.4.1-T1 | UI design via the `design-ui` agent: wireframes and all states for the authoring flows, the phase-gated navigation and the CR-D-2 deltas to existing pages (`docs/design/web-ui/prompts-crd2/`) | L | V1 | G1 | 09.1.1-T11 |
| 13.4.1-T2 | Feature tree and table view with filters | L | V1 | G1 | 13.4.1-T1, 09.1.1-T4, 13.1.1-T2 |
| 13.4.1-T3 | Test case editor, checklist review and batched changes panel | L | V1 | G1 | 13.4.1-T2, 13.1.2-T1 |
| 13.4.1-T4 | Generation flow screens (input, progress, credit cost shown before the call) | M | V1 | G1 | 13.4.1-T3, 13.3.1-T3 |
| 13.4.1-T5 | Timeline / versions, AI settings and export screens | M | V1 | G1 | 13.4.1-T2, 13.1.1-T3, 13.1.1-T5, 13.2.1-T1 |
| 13.4.1-T6 | E2E Playwright flows for authoring in vi + en | M | V1 | G1 | 13.4.1-T4, 13.4.1-T5 |
| 13.4.1-T7 | Navigation regrouped by phase (items of a later phase hidden, not disabled) and the AI credit balance and usage by action on the usage page (G1 builds only the credit region; the quota and retention regions stay 09.7.1-T5, G2) | M | V1 | G1 | 13.4.1-T1, 09.1.1-T4, 08.5.5-T5 |
Notes (S92): the generation flow screens of 13.4.1-T4 carry the role-profile selector (one profile per generation, optional run note) and 13.4.1-T5 sits next to the profile editor; both are built by 13.2.2-T6, which depends on the 13.4.1-T1 design deltas, so `13.4.1-T1` must include the selector and the editor in its wireframes and states (vi + en).
**Epic 13.4 total: 10.0** Feature plan for story 13.4.1 (S112, 2026-10-27): `plans/features/13-product-phases/13.4.1-authoring-ui.md` (21 sub-tasks of at most 0.5 d summing to the task sizes above, estimate unchanged; open items OI-13-501 to OI-13-506 and OI-13-509 in `plans/open-items/05-product-phases-g1.md`).

### Epic 13.5 Script generation and local runs (G2)
#### Story 13.5.1 Test case → bound Playwright script — acceptance: an approved test case with an environment URL and a seed test yields a TypeScript Playwright script whose every step is bound to an element found on the live page; the locator policy holds; forbidden patterns are never emitted; the script reaches the repository only as a PR and becomes `active` after 3 of 3 green runs on one build; a scripts screen shows status, stability, unbound steps, lint results, PR state and testability.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.5.1-T1 | `evidgate generate` in `packages/cli`: seed test, live page exploration, per-step element binding with a pre-action target check (element exists, unique, visible, actionable), unbound steps keep the script `draft` | XL | V1 | G2 | 04.2.1-T2, 13.1.1-T4, 03.7.1-T3 |
| 13.5.1-T2 | Locator policy and lint (role / label / test id → text → CSS; no absolute XPath or `nth()`), reusing the `packages/healing-validator` detectors | M | V1 | G2 | 13.5.1-T1, 08.4.1-T2 |
| 13.5.1-T3 | Model-assisted step mapping through the gateway, only for steps the rules cannot bind | L | V1 | G2 | 13.5.1-T1, 08.5.1-T1, 08.5.5-T2 |
| 13.5.1-T4 | Stability check 3 of 3 on one build; `generated_script` states draft / active / flaky | M | V1 | G2 | 13.5.1-T1, 04.3.1-T1 |
| 13.5.1-T5 | PR delivery through 03.4.2 with the `evidgate.id` annotation linking test case, script and `test_case` | M | V1 | G2 | 13.5.1-T4, 03.4.2-T1 |
| 13.5.1-T6 | Testability score per project | S | V1 | G2 | 13.5.1-T1 |
| 13.5.1-T7 | Generated scripts screen: status, 3-of-3 stability, unbound steps, lint results, PR state and testability score (design via `design-ui` first) | M | V1 | G2 | 13.5.1-T2, 13.5.1-T4, 13.5.1-T5, 13.5.1-T6 |
#### Story 13.5.2 Local runs — acceptance: `evidgate run` executes selected generated tests on a developer machine and ingests results and redacted snapshots exactly like CI.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.5.2-T1 | `evidgate run` command wrapping the Playwright reporter and the ingest path | L | V1 | G2 | 04.2.1-T1, 04.3.1-T1 |
| 13.5.2-T2 | Quickstart and troubleshooting docs (vi + en) | S | V1 | G2 | 13.5.2-T1 |
**Epic 13.5 total: 12.0**

### Epic 13.6 Element memory (G2)
#### Story 13.6.1 Element profiles — acceptance: each green run records a profile per bound step, keyed to the case revision and step; profiles hold structure, not content; the latest green profile per step is queryable; similarity is a versioned function with stability tests.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.6.1-T1 | Reporter capture of the bound-step accessibility subtree with CLI-side redaction | L | V1 | G2 | 04.3.1-T1, 08.1.2-T2 |
| 13.6.1-T2 | `element_profile` store (append-only per run, latest-green view), RLS, retention | M | V1 | G2 | 13.6.1-T1, 06.1.2-T2 |
| 13.6.1-T3 | Similarity function spec v1 + stability tests (dynamic ids, locales, reorder) | L | V1 | G2 | 13.6.1-T2, 08.1.2-T3 |
**Epic 13.6 total: 5.0**

### Epic 13.7 Mobile spike and multimodal locate fallback (G2, G3)
#### Story 13.7.1 Mobile spike (mobile-mcp / mobilewright) — acceptance: 10 real tasks on one Android emulator and one iOS simulator, each run twice: first driven by a model through mobile-mcp, then replayed as a mobilewright script without a model; pass rate, time per step, model cost and repeatability are recorded; a memo states whether a model-driven run converts to a deterministic mobilewright script, the licence status of every driver in the dependency tree (the `mobilecli` driver is FSL-1.1-ALv2, CR-D-2 §7), and what `DECISIONS.md` §7 and ADR-0002 / ADR-0005 would need.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.7.1-T1 | Install pinned versions on an Android emulator and an iOS simulator; run 10 tasks twice through mobile-mcp and once more as mobilewright scripts; record measurements | M | V1 | G2 | — |
| 13.7.1-T2 | Run-to-script feasibility, `adapter-mobile` shape, licence (incl. `mobilecli` FSL-1.1) / transfer findings; go / no-go memo | M | V1 | G2 | 13.7.1-T1 |
#### Story 13.7.2 Multimodal locate fallback — acceptance: when profile similarity is ambiguous, an accessibility + OCR + visual locator may propose a candidate that still passes all 08.4 validation; it never decides the class.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.7.2-T1 | OCR / visual locate behind the gateway, budgeted per incident | L | V1 | G3 | 13.6.1-T3, 08.5.1-T1 |
| 13.7.2-T2 | Evaluation on the locator-break corpus against similarity-only | M | V1 | G3 | 13.7.2-T1, 08.8.1-T1 |
**Epic 13.7 total: 5.0**

### Epic 13.8 Drift classification and repair proposals (G3)
#### Story 13.8.1 Locator drift vs UI change — acceptance: within the 08.2.2 label `test_drift`, profile rules separate locator drift from UI change; locator drift yields a candidate locator and a matching test case edit; UI change yields a test case review proposal and no script repair; other labels yield nothing; a per-project shadow window measures before PRs are offered.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.8.1-T1 | Rule set over profiles: same role and accessible name → locator drift; missing or changed meaning → UI change | L | V1 | G3 | 13.6.1-T3, 08.2.2-T2 |
| 13.8.1-T2 | Candidate generation from the profile, feeding the 08.3 candidate model | M | V1 | G3 | 13.8.1-T1, 08.3.1-T1 |
| 13.8.1-T3 | Test case review proposal for UI change, linked to authoring versions | M | V1 | G3 | 13.8.1-T1, 13.1.2-T1 |
| 13.8.1-T4 | Per-project shadow window and its metrics | M | V1 | G3 | 13.8.1-T2, 08.8.1-T1 |
#### Story 13.8.2 Review evidence — acceptance: a repair proposal shows before / after screenshots (where the project opted in) and an accessibility / DOM diff in addition to the 08.6.1 fields.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.8.2-T1 | Capture and diff data for before / after | M | V1 | G3 | 13.8.1-T2, 08.6.1-T1 |
| 13.8.2-T2 | Review screen additions, plus the failure label and drift class on the failure inbox and detail (design via `design-ui` first) | M | V1 | G3 | 13.8.2-T1, 09.4.1-T1, 09.4.1-T2, 09.5.1-T1, 09.5.1-T2 |
**Epic 13.8 total: 7.0**

### Epic 13.9 Phase gates
#### Story 13.9.1 Gate metrics — acceptance: each §4.3 gate metric is computed from stored data and readable by the operator; the G1 free trial also reports the measured model cost per AI action.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.9.1-T1 | G1 usage metrics: active teams, weekly return, exports, measured model cost per AI action | S | V1 | G1 | 13.1.1-T5, 08.5.5-T1 |
| 13.9.1-T2 | G2 script stability metric: share passing 3 of 3 without manual edits | S | V1 | G2 | 13.5.1-T4 |
| 13.9.1-T3 | G3 false-repair adjudication report | S | V1 | G3 | 13.8.1-T4, 08.8.1-T3 |
**Epic 13.9 total: 1.5**

## 10. Dependencies

- **01** (`01.4.1-T1`), **02** (`02.1.1-T1`, `02.2.1-T2`, `02.7.1-T2`), **03** (`03.1.1-T1`, `03.1.3-T1`, `03.4.2-T1`, `03.7.1-T3`), **04** (`04.2.1-T1`, `04.2.1-T2`, `04.3.1-T1`), **05** (`05.3.2-T5`), **06** (`06.1.2-T2`, `06.1.2-T3`), **08** (`08.1.2-T2`, `08.1.2-T3`, `08.2.2-T2`, `08.3.1-T1`, `08.4.1-T2`, `08.5.1-T1`, `08.5.2-T1`, `08.5.3-T1`, `08.5.5-T2`, `08.6.1-T1`, `08.8.1-T1`, `08.8.1-T3`), **09** (`09.1.1-T4`, `09.1.1-T11`, `09.5.1-T2`), **11** (`11.4.1-T3`).

## 11. Open questions

| # | Question | Owner | Needed by |
|---|---|---|---|
| Q1 | Default model pair (small / large) and its cost per action `[OPEN]` | Owner | before 13.3.1-T2 |
| Q2 | Credit amounts per AI action. Rule `[DECISION]` (owner, 2026-10-23, CR-D-2 §6.2): at least 3× the cost per action measured in the G1 free trial, reviewed monthly; the amounts stay `[OPEN]` | Owner | before the G2 offer (12.4.1-T4) |
| Q3 | Who supplies the seed test when the customer has none | Owner + pilot QA lead | before 13.5.1-T1 |
| Q4 | Role-profile governance: who may create or edit profiles, per-tenant limits, sharing across projects, whether the per-run note is stored, and the test-plan / integration-scenario output kinds; defaults `[OPEN]` in `plans/open-items/05-product-phases-g1.md` (OI-13-301 to OI-13-305) | Owner | before 13.2.2-T3 (permission, limits, note), before any test-plan story (output kinds) |

## 12. Estimate summary

| Epic | V1 | V2 | V3 | Total |
|---|---|---|---|---|
| 13.1 Authoring model | 8.0 | 0 | 0 | 8.0 |
| 13.2 Project AI settings and role profiles | 11.0 | 0 | 0 | 11.0 |
| 13.3 Requirement → checklist → test case | 7.0 | 0 | 0 | 7.0 |
| 13.4 Authoring screens | 10.0 | 0 | 0 | 10.0 |
| 13.5 Script generation and local runs | 12.0 | 0 | 0 | 12.0 |
| 13.6 Element memory | 5.0 | 0 | 0 | 5.0 |
| 13.7 Mobile spike and multimodal locate fallback | 5.0 | 0 | 0 | 5.0 |
| 13.8 Drift classification and repair proposals | 7.0 | 0 | 0 | 7.0 |
| 13.9 Phase gates | 1.5 | 0 | 0 | 1.5 |
| **Total** | **66.5** | **0** | **0** | **66.5** |

→ with 20 % buffer **79.8** person-days. By phase (raw): G1 35.5 · G2 20.5 · G3 10.5.

## 13. Contract change requests

| # | Request | Why |
|---|---|---|
| CR-1 | 00 §3: glossary entries `feature node`, `checklist item`, `manual test case`, `authoring version`, `element profile`, `generated script`, `locator drift`, `UI change` | New names used by 13, 08 and 09 |
| CR-2 | 00 §2: permissions `authoring:read`, `authoring:write`, `authoring:generate`, `script:generate` in the default role mapping | Roles are seeded as data |
| CR-3 | 00 §8: states for `generated_script` (draft → active / flaky → retired) and `authoring_change` (pending → applied / discarded) | Canonical state list |
| CR-4 | 00 §4: phases G1–G4 replace milestones M0–M5 as the Milestone value in every plan | CR-D-2 |
| CR-5 | 00 §3: glossary entries `role profile`, `role prompt template` (S92, 2026-10-26; logged in 00 §15) | New names used by 13.2.2, 13.3.1 and 13.4.1 |
