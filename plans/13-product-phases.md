# 13 - Product phases, authoring and self-learning repair (G1–G3)

> Contracts: `00-overview.md` §3 (glossary), §4 (phases), §5.4 (tenancy), §7 (identifiers, hashes), §8.7 (incident labels), §9 (API). Facts: `../DECISIONS.md` §3 A2/A4/A5, §4, §5 (safe-healing rules), §7 (personal data), CR-D-2 in [`../docs/decisions/CR-D-2-direction-a.md`](../docs/decisions/CR-D-2-direction-a.md).
> Level: high-level plan; each story gets a detailed plan in `plans/features/13-product-phases/` (00 §12) before build.
> Milestone column: the CR-D-2 phase (G1 Author, G2 Run, G3 Learn and repair, G4 Expand). §4.4 assigns every story of plans 01–12 to a phase.

## 1. Purpose and scope

**Goal.** A QA team writes requirements, gets a reviewed checklist and manual test cases, turns approved test cases into Playwright scripts bound to real elements, runs them in its own CI, on Evidgate’s hosted runner, or by hand, and gets safe, reviewed repair proposals when the application's UI drifts, without a product bug ever being hidden.

**In scope.** The authoring model (feature tree, checklist, manual test case, versions, batched review), project AI settings, requirement → checklist → test case generation, export / import, the authoring screens (G1); script generation against the live application, `evidgate run`, element profiles, the mobile spike with mobile-mcp / mobilewright (G2); multimodal locate fallback, locator-drift vs UI-change classification, test case review proposals, review evidence additions (G3); the measurements behind each phase gate; projects without a repository: requirements and AC as traced entities, release-bound manual runs, test case reports and optional Jira Cloud / Linear connections (13.10); Evidgate-hosted runs of generated scripts (13.11).

**Operator console (13.12).** Evidgate staff manage AI, customer sign-in and mail, plans and quotas, tenants and hosted-runner limits in a separate web console on the operator ingress (FR-13-14); the console is platform administration, not part of the customer product.

**Out of scope.** The platform pieces this plan reuses (tenancy 02, catalog identity 03.1–03.2, ingestion 04, artifacts 06, incidents / candidates / validation / approval 08), mobile execution (G4), hosted execution other than 13.11 (A5), any connection to a customer repository or to GitHub (D-NO-GITHUB), runtime auto-apply (§5.7).

## 2. Requirements

| ID | Requirement |
|---|---|
| FR-13-1 | Every authoring change (manual or AI) creates an immutable version with author, source (`manual` / `ai` / `import`) and reason; restore creates a new version. |
| FR-13-2 | A manual test case may link to one or more `test_case` identities (03.1.1); a test case without a linked script is visible as uncovered. |
| FR-13-3 | AI generation runs only through the gateway (08.5.1) with redaction (08.5.3), per-tenant opt-in (11.4.1-T3) and credits (08.5.5); provenance stores model, prompt hash and settings version. |
| FR-13-4 | A generated script binds every step to an element found on the live page; absolute XPath, `nth()`, `force: true`, `skip` / `fixme` and catch-and-ignore are never emitted (`DECISIONS.md` §5.2). |
| FR-13-5 | A generated script becomes an immutable Evidgate script revision after the authoring review (A2) and becomes active only after 3 of 3 green runs on one build; green-on-retry is flaky. Customer CI fetches revisions with evidgate scripts pull; nothing is pushed to a repository. |
| FR-13-6 | Element profiles store attributes and structure (role, accessible name, test id, id, text length class, neighbours, position), never input values or user content. |
| FR-13-7 | Within the 08.2.2 label `test_drift`, a rule set separates locator drift (same role and accessible name) from UI change; only locator drift yields a script repair candidate; `product_defect`, `environment`, `data_issue` and `unknown` yield none. |
| FR-13-8 | Every gate metric in §4.3 is computed from stored data, not entered by hand. |
| FR-13-9 | A requirement (user story, acceptance criterion, epic, spec section) is versioned append-only with author, source and reason (source `tracker` is the 13.10.4 sync, an extension of FR-13-1's list); links to checklist items and manual test cases pin a version; coverage per requirement is computed from stored links and results. |
| FR-13-10 | A manual result counts in a release decision only through a closed manual run bound to that release: closing writes an immutable manual evidence revision, a result after close is refused (409), and no manual result changes an evaluation or a consumed release decision (07.5.5). |
| FR-13-11 | A test case report is an immutable snapshot; every format (PDF, DOCX, XLSX, web) renders the same snapshot; a share link exposes only that snapshot. |
| FR-13-12 | Tracker connections read stories, AC, epics and bugs; the only write is creating one issue from a failed manual result or failed case attempt on an explicit user action with tracker:create_issue, idempotent per (connection, source result), audited, never automatic; OAuth tokens go only to the provider's authorization and API hosts and never reach a browser, a log or an audit record. |
| FR-13-13 | Hosted execution uses only an approved script revision pinned by hash; no repository fetch. The starting boundary is one disposable microVM per job. Guest egress is HTTPS only to at most 10 declared staging origins per project; only the trusted worker may reach the artifact-upload host. Refuse private, loopback, link-local and metadata addresses at connect time, including redirects and DNS changes. Hosted minutes count against the included monthly subscription quota; refuse new hosted runs at exhaustion, with no overage billing (FR-12-10). |
| FR-13-14 | Evidgate staff (`platform_operator`) administer platform-wide settings only through an operator console, a separate web app served on the operator ingress (`/internal/operator/*`, never `/v1`, never the public ingress). Access needs real workforce sign-in with MFA; no tenant data is shown without an approved support grant (FR-02-13). Scope: AI provider, model, spend cap and kill switch; customer sign-in methods and mail; subscription plans and per-tenant assignment; tenant directory, usage, support grants and deletion requests; hosted-runner limits. Every change is a new configuration revision (actor, time, previous and new non-secret values, before/after hashes) audited in the same transaction and effective without a redeploy; secret values are write-only, held in the secret store, and never read back, logged, audited or hashed. The console adds no second-person approval except where an existing row already requires it (08.5.5-T5 above its threshold, 06.4.4-T3 tenant confirmation). |

## 3. Key decisions

- `[DECISION]` D-REALIGN (owner 2026-11-26): only a closed manual run bound to a release contributes manual evidence to its release decision (D2). Jira Cloud and Linear import stories, AC, epics and bugs; creating one bug from a failed result on explicit user action is their only write (D3).
- `[DECISION]` CR-D-2: the product leads with authoring and self-learning repair; the release decision (07) is reporting, not the lead feature.
- `[DECISION]` A5 with the 2026-11-26 exception: generation runs through the gateway; healing replays run on the customer side; generated scripts run in customer CI, locally, or on the Evidgate-hosted runner (13.11, D-HOSTED-RUN).
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
- Epic 13.5: `generated_script` (`manual_test_case_id`, `test_case_id`, `state` draft / active / flaky / retired, `stability_runs`); `script_revision` (append-only; manual test case and catalog `test_case` link, content, `content_hash`, `approved_by`). The script store is the only home of approved scripts (D-NO-GITHUB).
- `element_profile` (append-only per run and bound step; `case_revision_id`, `step_no`, profile jsonb, `profile_hash`, `spec_version`).
- Epic 13.10 (projects without a repository): `requirement` (`feature_node_id` null, `kind` user_story / acceptance_criterion / epic / spec_section / other, `title`, `external_ref` null, `parent_requirement_id` null, `tracker_item_id` null, added with `tracker_item`), `requirement_version` (append-only; `requirement_id`, `version_no`, `body` ≤ 20 000 code points after NFC, `content_hash`, `source` manual / import / tracker / ai, `author_membership_id`, `reason`), `requirement_link` (`requirement_id`, pinned `requirement_version_id`, target `checklist_item_id` or `manual_test_case_id`), `manual_run` (`name`, `build_label` ≤ 128, `environment_revision_id` null, `release_id` null, `release_components_hash` set at close, `state` open / closed), `manual_run_case` (`manual_run_id`, `manual_test_case_id`, pinned authoring version), `manual_result` (append-only; `manual_run_case_id`, `status` passed / failed / blocked / skipped, `note` ≤ 4 000 code points, `executed_by` membership, `executed_at`, evidence artifact refs), `manual_evidence_revision` (append-only; `manual_run_id`, `release_id`, `release_components_hash`, results jsonb, `content_hash`), `test_report` (immutable snapshot written complete by the async job, which holds the progress state; scope, sections, `language`, pinned versions, `content_hash`), `test_report_share` (`test_report_id`, token hash, `expires_at`, `revoked_at`), `tracker_connection` (`provider` jira_cloud / linear, project scope, `state` active / needs_reauth / disconnected, encrypted tokens, `create_issue_enabled` default false, sync schedule), `tracker_item` (`tracker_connection_id`, provider id, `kind`, key, url, `content_hash`, last seen, removed upstream flag), `tracker_item_link` (`tracker_item_id` → `manual_test_case_id` or `test_case_id`), `tracker_issue_created` (`connection_id`, source `manual_result_id` or `case_attempt` key, `tracker_item_id`; unique per connection and source).
- Epic 13.11: `hosted_run_request` (run id, script revision hash, staging origin, state `requested` / `queued` / `running` / `done` / `refused`) and `hosted_egress_origin` (project-scoped allow-list).
All tables carry `org_id`, composite tenant FKs and FORCE RLS (00 §5.4).
Refinements from the G1 feature plans (S52, 2026-10-23): `plans/features/13-product-phases/13.1.1-feature-tree-and-manual-test-cases.md` adds the support tables `authoring_code_counter` (per-project case-code counter), 13.1.2 adds `checklist_item_lineage` (merge and split lineage) and 13.2.1 adds `ai_settings_use` (one row per `ai_run_id`, the settings version a run used). Each carries `org_id`, composite tenant FKs and FORCE RLS like the tables above; none changes a task size.
Refinements from story 13.2.2 (S92, 2026-10-26): role profiles add the tables `role_prompt_template` (platform, read-only seeds, no `org_id`, like `ai_settings_template`), `role_profile` (identity, project scope), `role_profile_version` (append-only, `content_hash`) and `role_profile_use` (one row per `ai_run_id`: profile id, version, hash, optional run note ≤ 500 code points after NFC). The org-scoped tables carry `org_id`, composite tenant FKs and FORCE RLS like the tables above; the task sizes are in §9 (13.2.2-T3).

### 4.2 Flows

1. **Author:** requirement input → checklist generation → batched review (pending changes) → apply as one version → test case generation → review → approve.
2. **Script:** approved test case + environment URL + seed test → `evidgate generate` on the customer side → bound script → authoring review → script revision in Evidgate → 3 stability runs on that revision.
3. **Learn:** green run → reporter captures bound-step profiles → profile store (latest green per step).
4. **Repair:** failed run → incident (08.1) → 08.2.2 label → within `test_drift`, profile rules → locator drift: candidate (08.3) → validation (08.4) → review (08.6.1 + 13.8.2); UI change: test case review proposal.
5. **Spec-only:** documents / CSV / Jira / Linear → requirements → checklist → manual test cases → manual run (bound to a release) → release decision + test case report (PDF / DOCX / XLSX / read-only link). No repository is needed.
6. **Hosted:** approved test case → script revision (hosted generation or `evidgate generate`) → hosted run → normal evidence → evaluation → report.

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
| G4 | Everything else, including 02.3.3, 02.5, 03.5, 03.6.1 (T2, T3), 03.8, 04.4.1 (T2, T3), 04.4.2, 04.5, 05.7.2 (T2), 05.7.3–05.7.6, 05.8, 05.9, 07.1.1 (T2…T4), 07.2.2 (T2…T4), 07.3, 07.4.2, 07.5.2, 07.5.4, 07.6–07.8, 08.9, 09.6, 09.9, 10.4.1-T2, 10.6.1 (T2, T4), 10.10, 11.7, 12.1 |

Task-level exceptions to the story targets: 09.2.1-T2…T8 → G2; 09.7.1-T3, T5, T9, T10, T11 → G2 and T4, T7, T8 → G4; 09.7.2 → G4; 09.3.1-T5, T9, T10, T11 (decision, waiver and release views) → G4 and 09.3.1-T8 (healed labels) → G3; 05.6.1-T2 (release binding by decision) → G4; 04.5.1-T1 and 06.3.5-T1 (mobile) → G4; 06.4.4-T2 (deleting healing knowledge) → G3; 11.5 → G2; 11.2.1-T6 → G3; 11.3.1-T3 → G2; 12.3.1-T2…T6 → G2; 08.5.1-T3, 08.5.2-T2, 08.5.5-T4 → G3; 03.7.1-T1, T2 → G4 (superseded by 13.3.1, §3). Dependency changes: 08.5.1-T1 now depends on 12.3.1-T1 only (was 08.4.3-T3 and 12.3.1-T2), so the gateway can ship in G1 for authoring; 03.7.1-T3 now depends on 03.1.3-T1 only (was also 03.6.1-T2, the G4 dry-run sandbox), because customer-side generation does not use that sandbox. The dependency pull-in still places a few listed tasks earlier than their exception, and the cell wins: 09.3.1-T5 is G3, 11.5.1-T1 and 11.5.2-T1 are G1, 12.3.1-T2 is G1.

Pulled to G2 for 13.11 (owner 2026-11-26): 05.7.3-T2, 05.7.4-T1, 05.7.4-T3, 05.9.2-T1, 11.2.1-T4, 11.2.1-T5, 07.5.3-T1, 07.5.3-T2, plus any dependency `plan_graph.py` reports.

Pulled to G1 for 13.12 (owner 2026-11-27): 02.8.1-T1 and 02.8.1-T2, because the console shell needs the operator access path before the pilot; 02.8.1-T1 also depends on 13.12.1-T2, which supplies the operator authentication the grant API has been waiting for (OI-02-10).

Checked against the Milestone cells on 2026-10-26 (`scripts/plan_graph.py --json`): every row above stays a declared target, and each cell that sits in an earlier phase than its row is the dependency pull-in the intro describes, so the cell is authoritative for the current phase. G4-group stories with tasks pulled earlier: 07.1.1-T1, 04.4.1-T1 and 10.4.1-T1 → G1; 07.2.1 (T1, T2), 07.2.2-T1, 07.4.1 (T1, T2), 07.5.1 (T1, T2), 04.4.1-T4…T6, 03.6.1-T1, 05.7.1-T1, 05.7.2-T1 and 10.6.1-T1, T3 → G2; 04.4.2-T1 → G3, because 03.9.2-T1 (G3) depends on it; 07.5.2-T1 → G3, because 09.3.1-T5 (itself pulled to G3 for 09.5.2-T4) depends on it. Four G4-group stories are wholly earlier: 07.2.1, 07.4.1, 07.5.1 and 05.7.1 have only G2 tasks. Ten G2 targets were pulled into G1 completely (05.3.3, 06.1.1, 06.1.2, 06.2.2, 06.2.3, 06.4.1, 06.4.2, 06.4.3, 06.4.5, 06.5.2 — all their tasks are G1), 06.3.5 is wholly G4 per the exceptions line, and the G3 targets 08.2.1 and 08.3.1 are wholly G2. No target was rewritten to match a cell and no task was moved: the table records the target phase, each task row records the current one.

Historical result after the 2026-10-26 pull-in, all plans 01–13 (raw person-days, including work already merged and already including the 13.2.2 rows of S92: 8.5 in G1, 1.0 in G2): G1 283.75 · G2 196 · G3 106.75 · G4 119.25, summing to 705.75. Stories with tasks in two phases keep both. The Milestone cell of each feature-plan header in `plans/features/` still shows the old M value; `scripts/sync_feature_headers.py` refreshes it when that feature plan is next edited, because any edit brings the plan into `check-dor-security` scope (README §6). The phase in the area plan's task row is authoritative. A story's index phase is the earliest phase of its tasks, so `13.9.1` (one gate-metrics task per phase) shows as G1 in the README index while its T2 and T3 are G2 and G3.

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
| Requirement gets a new version (edit or sync) | Linked checklist items and cases are flagged `requirement_changed`; coverage shows them stale until re-linked |
| Manual result after close | 409 `manual_run_closed` |
| Hosted script targets an undeclared origin | Navigation refused; the case attempt fails with `egress_refused`, and the run continues |
| Create-bug retried | Returns the existing issue (idempotent) |

## 6. Security and privacy

- AI inputs are untrusted (08.5.2, 11.4.1-T2); generated code runs on customer infrastructure or in the isolated 13.11 executor (A5).
- Requirement documents and images are scanned, size-capped and stored as artifacts with sensitivity (06.2).
- Element profiles keep structure, not content (FR-13-6); screenshots are opt-in per project.
- Permissions: `authoring:read`, `authoring:write`, `authoring:generate`, `script:generate`; seeded into the role mapping (00 §2) by 13.1.1-T2.
- Role profiles (13.2.2): profile text and the per-run note are untrusted user input — length-capped, NFC-normalised, refused when secret-shaped, passed to the model only as delimited data (never as instructions), redacted by 08.5.3 like other input and covered by an injection corpus (13.2.2-T4). Choosing a profile needs `authoring:generate` only and grants nothing; creating or editing one needs the same permission as AI settings (13.2.1, `project:write` recommended, OI-13-301).
- Hosted runs: VM/microVM boundary per 11 D4. Egress allow-list. App-under-test credentials are secret references resolved only inside the executor and redacted from logs and artifacts.
- Tracker write scopes are requested only when create-bug is enabled.
- `tracker:create_issue` lets a user create a tracker issue and nothing else.

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
#### Story 13.5.1 Test case → bound Playwright script — acceptance: an approved test case with an environment URL and a seed test yields a TypeScript Playwright script whose every step is bound to an element found on the live page; the locator policy holds; forbidden patterns are never emitted; authoring review publishes an immutable script revision and becomes `active` after 3 of 3 green runs on one build; a scripts screen shows status, stability, unbound steps, lint results, script revision state and testability.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.5.1-T1 | `evidgate generate` in `packages/cli`: seed test, live page exploration, per-step element binding with a pre-action target check (element exists, unique, visible, actionable), unbound steps keep the script `draft` | XL | V1 | G2 | 04.2.1-T2, 13.1.1-T4, 03.7.1-T3 |
| 13.5.1-T2 | Locator policy and lint (role / label / test id → text → CSS; no absolute XPath or `nth()`), reusing the `packages/healing-validator` detectors | M | V1 | G2 | 13.5.1-T1, 08.4.1-T2 |
| 13.5.1-T3 | Model-assisted step mapping through the gateway, only for steps the rules cannot bind | L | V1 | G2 | 13.5.1-T1, 08.5.1-T1, 08.5.5-T2 |
| 13.5.1-T4 | Stability check 3 of 3 on one build; `generated_script` states draft / active / flaky | M | V1 | G2 | 13.5.1-T1, 04.3.1-T1 |
| 13.5.1-T5 | Script store and delivery: script_revision append-only (content_hash, test_case link, evidgate.id annotation), approval through the authoring review; evidgate scripts pull fetches an approved revision bundle with a project token and verifies its hash; runs pin the revision hash; no PR, no repository access | L | V1 | G2 | 13.5.1-T4 |
| 13.5.1-T6 | Testability score per project | S | V1 | G2 | 13.5.1-T1 |
| 13.5.1-T7 | Generated scripts screen: status, 3-of-3 stability, unbound steps, lint results, script revision state and testability score (design via `design-ui` first) | M | V1 | G2 | 13.5.1-T2, 13.5.1-T4, 13.5.1-T5, 13.5.1-T6 |
#### Story 13.5.2 Local runs — acceptance: `evidgate run` executes selected generated tests on a developer machine and ingests results and redacted snapshots exactly like CI.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.5.2-T1 | `evidgate run` command wrapping the Playwright reporter and the ingest path | L | V1 | G2 | 04.2.1-T1, 04.3.1-T1 |
| 13.5.2-T2 | Quickstart and troubleshooting docs (vi + en) | S | V1 | G2 | 13.5.2-T1 |
**Epic 13.5 total: 13.0** Feature plan for story 13.5.1 (S136, 2026-10-28): `plans/features/13-product-phases/13.5.1-bound-playwright-scripts.md` (10.5 d including the revised L script-store task; open items OI-13-510 to OI-13-529 and OI-13-540 to OI-13-543 in `plans/open-items/05-product-phases-g1.md`). Feature plan for story 13.5.2 (S136, 2026-10-28): `plans/features/13-product-phases/13.5.2-local-runs.md` (2.5 d, estimate unchanged; open items OI-13-530 to OI-13-539 and OI-13-544 in the same register).

### Epic 13.6 Element memory (G2)
#### Story 13.6.1 Element profiles — acceptance: each green run records a profile per bound step, keyed to the case revision and step; profiles hold structure, not content; the latest green profile per step is queryable; similarity is a versioned function with stability tests.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.6.1-T1 | Reporter capture of the bound-step accessibility subtree with CLI-side redaction | L | V1 | G2 | 04.3.1-T1, 08.1.2-T2 |
| 13.6.1-T2 | `element_profile` store (append-only per run, latest-green view), RLS, retention | M | V1 | G2 | 13.6.1-T1, 06.1.2-T2 |
| 13.6.1-T3 | Similarity function spec v1 + stability tests (dynamic ids, locales, reorder) | L | V1 | G2 | 13.6.1-T2, 08.1.2-T3 |
**Epic 13.6 total: 5.0** Feature plan for story 13.6.1 (S139, 2026-10-28): `plans/features/13-product-phases/13.6.1-element-profiles.md` (11 sub-tasks of at most 0.5 d summing to the task sizes above, estimate unchanged; open items OI-13-545 to OI-13-550 and OI-13-559 in `plans/open-items/05-product-phases-g1.md`).

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
**Epic 13.8 total: 7.0** Feature plan for story 13.8.1 (S139, 2026-10-28): `plans/features/13-product-phases/13.8.1-locator-drift-vs-ui-change.md` (15 sub-tasks of at most 0.5 d summing to the task sizes above, estimate unchanged; open items OI-13-551 to OI-13-558 in the same register).

### Epic 13.9 Phase gates
#### Story 13.9.1 Gate metrics — acceptance: each §4.3 gate metric is computed from stored data and readable by the operator; the G1 free trial also reports the measured model cost per AI action.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.9.1-T1 | G1 usage metrics: active teams, weekly return, exports, measured model cost per AI action | S | V1 | G1 | 13.1.1-T5, 08.5.5-T1 |
| 13.9.1-T2 | G2 script stability metric: share passing 3 of 3 without manual edits | S | V1 | G2 | 13.5.1-T4 |
| 13.9.1-T3 | G3 false-repair adjudication report | S | V1 | G3 | 13.8.1-T4, 08.8.1-T3 |
**Epic 13.9 total: 1.5**

### Epic 13.10 Projects without a repository (G1, G2)
#### Story 13.10.1 Requirements, user stories and acceptance criteria — acceptance: a project stores user stories, AC, epics and spec sections as versioned requirements, links them to checklist items and manual test cases, and shows coverage per requirement, with no Git binding.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.10.1-T1 | `requirement` (kind `user_story` / `acceptance_criterion` / `epic` / `spec_section` / `other`, `title`, optional `external_ref` with the 03.1.4 `requirement_ref` character rule, optional `feature_node_id`, optional `parent_requirement_id` so AC sit under a story, optional `tracker_item_id`, column added by the 13.10.4-T1 migration) + `requirement_version` (append-only, body ≤ 20 000 code points after NFC, `content_hash`, `source` manual / import / tracker / ai, author, `reason`) schema + migration (FORCE RLS, composite FKs) | L | V1 | G1 | 13.1.1-T1, 01.4.1-T1 |
| 13.10.1-T2 | CRUD + version list + restore-as-new-version API (`authoring:read` / `authoring:write`), `If-Match`, audit, OpenAPI, isolation route cases | M | V1 | G1 | 13.10.1-T1, 02.2.1-T2, 02.7.1-T2 |
| 13.10.1-T3 | `requirement_link` to `checklist_item` and `manual_test_case` pinning the requirement version; coverage read model per requirement (linked cases, approved cases, latest manual result from 13.10.2, linked automated `test_case`; until G2 supplies results the manual-result part reads "not available", never zero or passing) | M | V1 | G1 | 13.10.1-T2, 13.1.1-T4 |
| 13.10.1-T4 | Import user stories and AC from CSV, XLSX and Markdown (heading = story, list item = AC, a `Given/When/Then` block kept verbatim as one AC) through the 13.1.1-T5 importer; spec text uploaded through 13.3.1-T1 can be saved as `spec_section` requirements | M | V1 | G1 | 13.10.1-T2, 13.1.1-T5, 13.3.1-T1 |
| 13.10.1-T5 | Generation input: 13.3.1 accepts `requirement_ids[]` (pinned versions) besides text and documents; provenance stores the versions; generated checklist items are linked to their requirement | S | V1 | G1 | 13.10.1-T3, 13.3.1-T2 |
| 13.10.1-T6 | UI via the `design-ui` agent first: requirement tree/list, editor with versions, link panel, coverage view, vi + en, WCAG 2.2 AA | L | V1 | G1 | 13.10.1-T3, 13.4.1-T1 |
| 13.10.1-T7 | Requirement change impact: a new requirement_version flags every link pinned to an older version requirement_changed; coverage counts those links as stale; re-link to the new version or dismiss with a reason (audited) | M | V1 | G1 | 13.10.1-T3 |
#### Story 13.10.2 Manual execution runs — acceptance: a tester executes selected manual test cases, records passed / failed / blocked / skipped with notes and evidence files, and gets run and coverage summaries; a closed run bound to a release counts in that release’s decision (07.5.5); no manual result changes an evaluation or a consumed decision.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.10.2-T1 | `manual_run` (name, `build_label` free text ≤ 128, optional `environment_revision_id`, optional `release_id` (composite FK to `release`), `release_components_hash` set at close, state open → closed with transition trigger), `manual_run_case` (manual test case + pinned authoring version), `manual_result` (append-only; status `passed` / `failed` / `blocked` / `skipped`, note ≤ 4 000 code points, executed_by membership, executed_at; latest row per run case wins) schema + migration (FORCE RLS, composite FKs) | L | V1 | G2 | 13.1.1-T1, 13.1.1-T3 |
| 13.10.2-T2 | API: create a run from a selection (feature node, requirement, or case list), record a result with `Idempotency-Key`, close a run; read `authoring:read`, write new permission `manual_run:write` seeded to `qa_engineer`, `test_maintainer`, `tenant_admin`; audit, OpenAPI, isolation route cases | M | V1 | G2 | 13.10.2-T1, 02.2.1-T2, 02.7.1-T2 |
| 13.10.2-T3 | Evidence files on a result through the 06 artifact path (sensitivity class, async secret scan), never inlined | M | V1 | G2 | 13.10.2-T2, 06.1.2-T3, 06.2.3-T1 |
| 13.10.2-T4 | Run summary and coverage read models (latest result per case, by feature node and by requirement), consumed by the 13.10.3 report | M | V1 | G2 | 13.10.2-T2, 13.10.1-T3 |
| 13.10.2-T5 | Close and freeze: closing a run writes an immutable manual_evidence_revision (latest result per case, release id, release components hash, content_hash) and enqueues policy.release_aggregate for the bound release; a result after close returns 409 manual_run_closed; a test proves a consumed release decision row is never changed | M | V1 | G2 | 13.10.2-T2, 07.5.3-T1 |
| 13.10.2-T6 | UI via the `design-ui` agent first: run list, keyboard-first execution screen (pass / fail / blocked / skip, attach evidence), run summary, vi + en, WCAG 2.2 AA | L | V1 | G2 | 13.10.2-T2, 13.4.1-T1 |
| 13.10.2-T7 | Releases for projects without a repository: create a release with user-named components (component key + free label, digest optional) through 05.6.1, and bind a manual run to it at creation or before close | S | V1 | G2 | 13.10.2-T1, 05.6.1-T1 |
#### Story 13.10.3 Test case reports — acceptance: a project builds an immutable report with selected sections (test plan, test cases, traceability matrix with gaps, execution). A release scope shows the bound release decision (PASS / FAIL / INCONCLUSIVE with reasons); any other scope shows counts by status labelled "no release decision". PDF, DOCX, XLSX and the read-only share link render the same snapshot.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.10.3-T1 | `test_report` immutable snapshot (scope: feature nodes / requirements / manual run / release; sections; language vi/en; pinned requirement + case versions and, when present, manual results and bound release decision with reasons; `content_hash`) built by an async task-queue job; create needs `authoring:write`, read `authoring:read`; audit, OpenAPI, isolation route cases | M | V1 | G1 | 13.10.1-T3, 01.6.1-T1 |
| 13.10.3-T2 | Section builders: test plan (scope, requirement list, priorities, free-text strategy and environment fields), test cases (steps, data, expected results by feature/requirement), traceability matrix requirement ↔ case with uncovered requirements and unlinked cases flagged, execution section with the bound release decision (PASS / FAIL / INCONCLUSIVE with reasons) when the scope is a release, otherwise counts by status labelled "no release decision". Before any manual run exists show "no manual execution recorded", never "0 failed" | M | V1 | G1 | 13.10.3-T1 |
| 13.10.3-T3 | XLSX and DOCX renderers on the existing hand-written OOXML writer of `AuthoringExporter` (one sheet / heading per section), vi + en strings, formula-injection escaping as in the CSV/XLSX export | L | V1 | G1 | 13.10.3-T2, 13.1.1-T5 |
| 13.10.3-T4 | PDF renderer: choose a .NET PDF library through ADR-0002 (licence, CVE, maintenance) with an ADR row; fallback if none passes: the web report's print stylesheet ("Save as PDF"), recorded in the ADR | M | V1 | G1 | 13.10.3-T2 |
| 13.10.3-T5 | Read-only share link: unguessable token (≥ 128 bit, stored hashed like the landed 02.4.4-T1 API token), off by default, issue and revoke need `project:write` (OI-13-604), expiry (default 30 days), revocable, audited, `noindex`, no session cookie, serves only the snapshot | M | V1 | G1 | 13.10.3-T1 |
| 13.10.3-T6 | UI via the `design-ui` agent first: report builder (scope, sections, language, format), report history, download, share-link management, the public read-only report page; vi + en, WCAG 2.2 AA | L | V1 | G1 | 13.10.3-T2, 13.4.1-T1 |
| 13.10.3-T7 | Automated results section: for a release scope, per component the run decision and automated case results grouped by requirement through manual_case_link → test_case; hosted and CI runs alike | M | V1 | G2 | 13.10.3-T2, 07.5.3-T2 |
#### Story 13.10.4 Optional connections: Jira Cloud, Linear — acceptance: a project picks Jira Cloud, Linear or no connection; Jira and Linear connect by OAuth and import stories, AC, epics and bugs; re-sync versions requirements only on content change; bugs link to test cases and can seed a regression test case; a user can create one bug from a failed result; nothing else is written; no GitHub or repository connection exists (D-NO-GITHUB).
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.10.4-T1 | `tracker_connection` (provider `jira_cloud` / `linear`, project-scoped, `create_issue_enabled` default false, state `active` / `needs_reauth` / `disconnected` with transition trigger (`active` and `needs_reauth` may each move to `disconnected`), OAuth tokens encrypted at rest with the existing ASP.NET Data Protection key ring, never returned or logged) + `tracker_item` (provider id, `kind`, key, url, content hash, last seen) schema + migration (this migration also adds `requirement.tracker_item_id` with its composite tenant FK); OAuth authorization-code flow with `state` and PKCE where supported, read scopes Jira `read:jira-work offline_access` and Linear `read`; enabling create-bug re-runs consent, adding Jira `write:jira-work` or Linear `issues:create`; connect/disconnect need `project:write` | L | V1 | G1 | 13.10.1-T1, 02.2.1-T2 |
| 13.10.4-T2 | Jira Cloud import: site choice via accessible resources (`cloudId`), JQL or project/epic filter, map issue type: story → `user_story` requirement, epic → `epic` requirement parent of its stories, bug → `bug` tracker item, others → `other` tracker items; neither bugs nor other items become requirements, AC from a chosen custom field or a description section headed "Acceptance criteria", Atlassian Document Format → plain text/Markdown, `external_ref` = issue key; dedupe by (connection, issue id) | L | V1 | G1 | 13.10.4-T1, 13.10.1-T2 |
| 13.10.4-T3 | Linear import over GraphQL: team / project / cycle filter, map issue type or label: story → `user_story` requirement, epic → `epic` requirement parent of its stories, bug → `bug` tracker item, others → `other` tracker items; neither bugs nor other items become requirements, AC from a description section headed "Acceptance criteria" (Markdown) or sub-issues by label, `external_ref` = identifier; same dedupe | M | V1 | G1 | 13.10.4-T1, 13.10.1-T2 |
| 13.10.4-T4 | Sync: "sync now" and a scheduled sync per connection (Cronos, default daily), new `requirement_version` only on content-hash change, removed upstream items marked, 429 `Retry-After` and refresh-token handling (expired refresh → `needs_reauth`), outbound hosts fixed to the provider API hosts, audit | M | V1 | G1 | 13.10.4-T2, 13.10.4-T3 |
| 13.10.4-T5 | Security and isolation tests: state/PKCE mismatch refused, tokens absent from responses/logs/audit, cross-tenant connection access refused, revoked grant → `needs_reauth`, oversized issue text truncated per the requirement cap; no write scope when create-bug is disabled, no write call without explicit action, idempotent retry | M | V1 | G1 | 13.10.4-T4 |
| 13.10.4-T6 | UI via the `design-ui` agent first: project Integrations page with Jira / Linear cards (all optional), connect flow, filter picker with preview, import, sync status; vi + en, WCAG 2.2 AA | L | V1 | G1 | 13.10.4-T4, 13.4.1-T1 |
| 13.10.4-T7 | Onboarding: the old connect-CI/GitHub step is replaced by a path choice (Jira / Linear / documents only) and an optional "send results from your CI" step that issues a project ingest token; verify today's behaviour first and record it | S | V1 | G1 | 13.10.4-T6 |
| 13.10.4-T8 | Bugs in the workflow: link a bug tracker_item to manual test cases / test_case identities (tracker_item_link), show linked open bugs on the case and in reports, and "generate regression test case from bug" as a 13.3.1 input (bug text as untrusted data) | S | V1 | G1 | 13.10.4-T2, 13.3.1-T3 |
| 13.10.4-T9 | Create bug from a failed manual result or failed case attempt: explicit action needing tracker:create_issue; payload title, steps, expected, actual, environment, release/build label and Evidgate evidence URLs (no file upload); Idempotency-Key + unique (connection, source); Jira create issue + remote link, Linear issueCreate + attachmentCreate link; stores tracker_issue_created and a bug tracker_item; audit tracker.issue.created; UI button via design-ui | L | V1 | G2 | 13.10.4-T4, 13.10.2-T2 |
**Epic 13.10 total: 38.0** (13.10.1 8.5 · 13.10.2 8.5 · 13.10.3 9.0 · 13.10.4 12.0)
Notes (D-REALIGN): requirements, reports and tracker import are P1; manual execution, release decisions, the automated report section and explicit create-bug are P2. A frozen, release-bound manual revision feeds 07.5.5; tracker imports never trigger writes. No project requires a repository (D-NO-GITHUB).

### Epic 13.11 Hosted runs (G2)
#### Story 13.11.1 Hosted runs — acceptance: a user with hosted_run:create runs an approved script revision on the Evidgate-hosted runner against a declared staging origin; results arrive as normal run evidence and reach the evaluation and release decision; the executor is VM/microVM-isolated with egress limited to declared origins; app credentials are secret references never visible in logs or artifacts; hosted minutes are included in each plan’s monthly quota, with new hosted runs refused on exhaustion and no overage billing; a kill switch stops all hosted runs.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.11.1-T1 | Decision record and threat-model rows `TH-hosted-*` (claim/finalize routes, egress, credential, tenant isolation) + egress/SSRF spec | M | V2 | G2 | 05.9.2-T1, 11.2.1-T4 |
| 13.11.1-T2 | Hosted run request API (`hosted_run_request`, `Idempotency-Key`, creates the run through 05.2.1 with `mode=hosted`), permission `hosted_run:create` | M | V2 | G2 | 05.2.1-T2, 13.5.1-T5 |
| 13.11.1-T3 | Hosted worker service identity and claim through `job` / `job_lease` | L | V2 | G2 | 05.7.1-T1, 05.7.2-T1, 05.7.4-T1 |
| 13.11.1-T5 | Hosted executor in one disposable microVM per job; build only after 05.9.2-T1 and 11.2.1-T5 supply boundary evidence recorded in 13.11.1-T1 | XL | V2 | G2 | 11.2.1-T5, 13.11.1-T1, 13.11.1-T3 |
| 13.11.1-T6 | Egress allow-list (`hosted_egress_origin`) and connect-time SSRF guard (private, loopback, link-local and metadata refused; redirects and DNS re-resolution re-checked) | M | V2 | G2 | 13.11.1-T5, 13.11.1-T1 |
| 13.11.1-T7 | App-under-test secret references: tenant secret store, injection inside the executor only, redaction | M | V2 | G2 | 13.11.1-T3 |
| 13.11.1-T8 | Browser image build, digest pin, scan, licence check | S | V2 | G2 | 05.7.1-T1 |
| 13.11.1-T9 | Artifacts through the 06 upload path | S | V2 | G2 | 13.11.1-T5, 06.1.3-T1 |
| 13.11.1-T10 | Hosted generation for projects without their own tests (`evidgate generate` inside the executor, output to the script store) | M | V2 | G2 | 13.11.1-T5, 13.5.1-T5 |
| 13.11.1-T11 | Meter hosted `runner_minutes` against the included monthly subscription quota using the existing gate (12.3.1 / 04.8.1); refuse new hosted runs at exhaustion, no overage billing. Set per-plan allowances after 05.9.2-T1 measures cost | M | V2 | G2 | 12.3.1-T4, 13.11.1-T5 |
| 13.11.1-T12 | Kill switch, per-tenant concurrency caps, fair scheduling | M | V2 | G2 | 05.7.3-T2, 12.3.1-T4 |
| 13.11.1-T13 | UI via `design-ui` first: Run button, staging origin picker, progress, result link; vi + en, WCAG 2.2 AA | M | V2 | G2 | 13.11.1-T2, 13.5.1-T7 |
| 13.11.1-T14 | Negative tests (egress, SSRF, credential leak, cross-tenant) + hosted e2e on the fixture app | M | V2 | G2 | 13.11.1-T6, 13.11.1-T7, 13.11.1-T9 |
| 13.11.1-T15 | Hosted runbook (cancel, kill, incident) | S | V2 | G2 | 13.11.1-T12 |
**Epic 13.11 total: 15.5** (13.11.1, 14 tasks).
Notes (owner 2026-11-27, D-HOSTED-RUN): P4. The starting defaults are one microVM per job, HTTPS only, at most 10 declared staging origins per project, two concurrent jobs per tenant, a 15-minute timeout and a minimal digest-pinned, scanned image. 05.9.2-T1 confirms them with evidence before the executor build and supplies cost data for the monthly minute allowances.

### Epic 13.12 Operator admin console (G1, G2)
#### Story 13.12.1 Console shell, operator sign-in and configuration history — acceptance: Evidgate staff reach a separate console only on the operator ingress after workforce sign-in with MFA as a provisioned `platform_operator`; a tenant member, CI token, billing role alone or a request on the public ingress is refused and writes nothing; every platform setting is stored as an immutable, versioned revision (actor, time, previous and new non-secret values, before/after hashes), written with the audit row in one transaction, applied without a redeploy, and a stale edit is refused with the current revision; write-only secrets reach the secret store, are never shown again and never appear in logs, history or audit; approved design precedes every console screen.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.12.1-T1 | UI design via the `design-ui` agent first (Area UX): console information architecture and layouts for sign-in/MFA, configuration history, AI, sign-in and mail, plans and quotas, tenants and hosted runner; the write-only secret field pattern; stale-revision, refusal, loading, empty and error states; vi + en, WCAG 2.2 AA; the owner approves it before any console screen is built | M | V1 | G1 | 09.10.1-T1, 09.10.1-T3 |
| 13.12.1-T2 | Operator identity: workforce sign-in with MFA for provisioned `platform_operator` staff, an operator-only session, antiforgery and expiry on the operator ingress, and a real `IOperatorGate` replacing the deny-all gate (operator-auth receiver for 02.8.1); tests: no-MFA, expired, public-ingress, tenant-member and billing-role-only refusals | L | V1 | G1 | 02.3.2-T1, 02.2.1-T2 |
| 13.12.1-T3 | Configuration framework: append-only platform setting revisions with `If-Match`, current-revision pointer, audited publish and no-redeploy refresh in one transaction with audit; writable versioned secret-store capability (write, rotate, retire) beside the read-only `ISecretProvider`, with opaque secret references; tests: stale 412, rollback on audit failure, no secret value in any row, log or response | L | V1 | G1 | 13.12.1-T2, 02.7.1-T2, 11.5.2-T1, 10.1.1-T2, 10.7.1-T4 |
| 13.12.1-T4 | Console web shell as a separate Angular application on the operator origin (navigation from the approved design, MFA sign-in, current settings and revision history with who/when/previous value, vi + en); tests: unauthenticated, expired-session and stale-revision paths | L | V1 | G1 | 13.12.1-T1, 13.12.1-T3, 02.8.1-T2, 09.10.1-T2 |
**Story 13.12.1 total: 7.0**
#### Story 13.12.2 AI settings — acceptance: the console displays Anthropic API as the fixed V1 provider; staff select a Claude model from its versioned catalog, enter the provider API key write-only, set the global spend cap and flip the AI kill switch; every change is a configuration revision effective on the next gateway call without a redeploy; disabled means no provider call; the key is never shown, logged or audited; the existing alert at 80 % of the cap and the per-tenant caps still apply.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.12.2-T1 | Operator API and gateway activation: Anthropic API as the fixed provider, Claude model from its versioned catalog, provider key staged in the secret store and activated only after validation (rotation per 10.7.1-T4), global daily cap, AI kill switch through an operator-ingress-only path to the existing system switch definer (08.6.3-T1, the `psql` runbook path stays); tests: invalid key keeps the old one, cap and disabled refuse a gateway call with no provider request, no secret in audit | L | V1 | G1 | 13.12.1-T3, 08.5.5-T3, 08.5.5-T5, 08.6.3-T1 |
| 13.12.2-T2 | AI settings screen from the approved design (read-only Anthropic API provider, model picker from its versioned catalog, write-only key entry with last-rotated metadata, cap, kill switch confirm); tests: stale revision, refused write, key never echoed | M | V1 | G1 | 13.12.1-T1, 13.12.1-T4, 13.12.2-T1 |
**Story 13.12.2 total: 3.0**
#### Story 13.12.3 Sign-in and mail settings — acceptance: staff set the Google Workspace and Microsoft Entra ID client id and write-only secret used for customer sign-in, the default for the per-org magic-link switch and the mail provider settings; every change is a configuration revision effective without a redeploy; an invalid configuration never replaces a working one; staff sign-in never depends on these customer-facing settings; secrets are never shown again.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.12.3-T1 | Operator API and live handlers for Google Workspace and Entra ID client id and secret (secret in the secret store, rotation per 10.7.1-T4, issuer and tenant checks unchanged); tests: wrong secret keeps the old configuration, a customer login still works after rotation | M | V1 | G1 | 13.12.1-T3, 02.3.1-T4 |
| 13.12.3-T2 | Sign-in settings screen from the approved design (provider cards, write-only secret entry, status of the active revision); tests: stale, refusal and secret-never-echoed | M | V1 | G1 | 13.12.1-T1, 13.12.1-T4, 13.12.3-T1 |
| 13.12.3-T3 | Operator API and live sender for the magic-link platform default (applies to organizations created afterwards, no rewrite of an existing org value, OI-02-42) and the mail provider settings behind `IEmailSender` (02.10.1-T2.3) with a write-only provider credential; tests: invalid provider keeps the old one, the next real send uses the new revision | L | V1 | G2 | 13.12.1-T3, 02.3.4-T3, 02.10.1-T2 |
| 13.12.3-T4 | Magic-link and mail settings screens from the approved design (default switch, provider form, credential entry, test send result); tests: stale, refusal and secret-never-echoed | M | V1 | G2 | 13.12.1-T1, 13.12.3-T2, 13.12.3-T3 |
**Story 13.12.3 total: 5.0**
#### Story 13.12.4 Plans and quotas — acceptance: staff define versioned subscription plans (hosted runner minutes, AI credit and other metered quotas), assign a plan to a tenant, and the quota gate enforces the assigned plan from the next metering decision; a plan change is prospective, never rewrites the append-only usage ledger or resets consumption, bills no overage, and is audited; hosted-minute and AI-credit exhaustion refuse new work; the existing two-person rule above the 08.5.5-T5 threshold is kept.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.12.4-T1 | Plan model and limit resolution: versioned plan revisions (hosted minutes, AI credit, other metered quotas per 12.3.1 and 04.8.1), resolved by the quota gate from the tenant's assigned plan; tests: boundary at the limit, plan switch mid-period, ledger unchanged, no overage | L | V1 | G2 | 13.12.1-T3, 12.3.1-T4, 04.8.1-T1, 08.5.5-T1, 13.11.1-T11 |
| 13.12.4-T2 | Assign a plan to a tenant and grant or adjust AI credit through 08.5.5-T5 (the second-operator rule above the threshold stays); audited, prospective; tests: unknown tenant, replay, the threshold refusal | M | V1 | G2 | 13.12.4-T1, 02.8.1-T2, 08.5.5-T5 |
| 13.12.4-T3 | Plan editor and tenant assignment screens from the approved design; tests: stale revision, refused write, exhaustion state | M | V1 | G2 | 13.12.1-T1, 13.12.1-T4, 13.12.4-T2 |
**Story 13.12.4 total: 4.0**
#### Story 13.12.5 Tenants — acceptance: staff see the tenant directory with status and usage and can request a support grant and a deletion; no tenant payload is shown without an approved support grant (FR-02-13: tenant-admin approval, time-boxed, every read audited with the grant id); a deletion needs the matching approved grant and the tenant-admin confirmation token (06.4.4-T3), a second request for the same tenant is refused while one is open, and the console never hard-deletes.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.12.5-T1 | Paged directory API: organization status and usage summaries only (no tenant business data), usage through 12.3.1-T6; tests: payload fields exclude tenant data, billing-only role refusal | L | V1 | G2 | 13.12.1-T3, 02.8.1-T2, 12.3.1-T6 |
| 13.12.5-T2 | Operator commands: request and revoke a support grant (approval stays with the tenant admin), start and show a deletion request through 06.4.4-T3; tests: expired or other-tenant grant refused, missing confirmation token refused, duplicate refused | M | V1 | G2 | 13.12.5-T1, 06.4.4-T3, 11.5.1-T2, 11.5.2-T1 |
| 13.12.5-T3 | Directory, usage, support-grant and deletion screens from the approved design; tests: grant-required state, refusal and asynchronous deletion outcome | M | V1 | G2 | 13.12.1-T1, 13.12.1-T4, 13.12.5-T2 |
**Story 13.12.5 total: 4.0**
#### Story 13.12.6 Hosted runner settings — acceptance: staff set hosted-runner concurrency, timeout and the allowed image list within the hard envelope recorded by 13.11.1-T1; a change is a configuration revision applied to new jobs, not running ones, without a redeploy; only digest-pinned, scanned images from the allow-list are accepted; no setting weakens isolation, egress or the per-plan quota.
| ID | Task | Size | Version | Milestone | Depends on |
|---|---|---|---|---|---|
| 13.12.6-T1 | Operator API and scheduler/executor enforcement for per-tenant concurrency, job timeout and the image allow-list; tests: out-of-envelope value refused, unlisted or unpinned image refused, running job keeps its pinned envelope | L | V2 | G2 | 13.12.1-T3, 13.11.1-T1, 13.11.1-T5, 13.11.1-T8, 13.11.1-T12 |
| 13.12.6-T2 | Hosted runner settings screen from the approved design; tests: stale revision, refused write, envelope bounds shown | M | V2 | G2 | 13.12.1-T1, 13.12.1-T4, 13.12.6-T1 |
**Story 13.12.6 total: 3.0**
**Epic 13.12 total: 26.0** (13.12.1 7.0 · 13.12.2 3.0 · 13.12.3 5.0 · 13.12.4 4.0 · 13.12.5 4.0 · 13.12.6 3.0; 18 tasks; G1 12.0, G2 14.0).
Notes (owner 2026-11-27, P1): served only on the operator ingress; no two-person approval because the owner is the only operator, except 08.5.5-T5 above its threshold and the 06.4.4-T3 tenant confirmation, which stay. OI-08-01 fixes the V1 provider to Anthropic API; operators select a supported Claude model from the versioned catalog. The G2 screens follow their dependencies (mail 02.10.1, quota gate 12.3.1-T4, tenant deletion 06.4.4-T3, hosted runner 13.11.1).

## 10. Dependencies

- **01** (`01.4.1-T1`), **02** (`02.1.1-T1`, `02.2.1-T2`, `02.7.1-T2`), **03** (`03.1.1-T1`, `03.1.3-T1`, `03.7.1-T3`), **04** (`04.2.1-T1`, `04.2.1-T2`, `04.3.1-T1`), **05** (`05.2.1-T2`, `05.3.2-T5`, `05.6.1-T1`, `05.7.1-T1`, `05.7.2-T1`, `05.7.3-T2`, `05.7.4-T1`, `05.9.2-T1`), **06** (`06.1.2-T2`, `06.1.2-T3`, `06.1.3-T1`), **08** (`08.1.2-T2`, `08.1.2-T3`, `08.2.2-T2`, `08.3.1-T1`, `08.4.1-T2`, `08.5.1-T1`, `08.5.2-T1`, `08.5.3-T1`, `08.5.5-T2`, `08.6.1-T1`, `08.8.1-T1`, `08.8.1-T3`), **09** (`09.1.1-T4`, `09.1.1-T11`, `09.5.1-T2`), **11** (`11.2.1-T4`, `11.2.1-T5`, `11.4.1-T3`), **12** (`12.3.1-T4`); epic 13.10 adds **01** (`01.6.1-T1`), **06** (`06.2.3-T1`), **07** (`07.5.3-T1`, `07.5.3-T2`) and **13.3.1-T1** (intake); epic 13.12 adds **02** (`02.3.1-T4`, `02.3.2-T1`, `02.3.4-T3`, `02.8.1-T2`, `02.10.1-T2`), **04** (`04.8.1-T1`), **06** (`06.4.4-T3`), **08** (`08.5.5-T1`, `08.5.5-T3`, `08.5.5-T5`, `08.6.3-T1`), **09** (`09.10.1-T1`, `09.10.1-T2`, `09.10.1-T3`), **10** (`10.1.1-T2`, `10.7.1-T4`), **11** (`11.5.2-T1`), **12** (`12.3.1-T6`) and **13.11.1** (`T1`, `T5`, `T8`, `T11`, `T12`).

## 11. Open questions

| # | Question | Owner | Needed by |
|---|---|---|---|
| Q1 | Default model pair (small / large) and its cost per action `[OPEN]` | Owner | before 13.3.1-T2 |
| Q2 | Credit amounts per AI action. Rule `[DECISION]` (owner, 2026-10-23, CR-D-2 §6.2): at least 3× the cost per action measured in the G1 free trial, reviewed monthly; the amounts stay `[OPEN]` | Owner | before the G2 offer (12.4.1-T4) |
| Q3 | Who supplies the seed test when the customer has none | Owner + pilot QA lead | before 13.5.1-T1 |
| Q4 | Role-profile governance: who may create or edit profiles, per-tenant limits, sharing across projects, whether the per-run note is stored, and the test-plan / integration-scenario output kinds; defaults `[OPEN]` in `plans/open-items/05-product-phases-g1.md` (OI-13-301 to OI-13-305) | Owner | before 13.2.2-T3 (permission, limits, note), before any test-plan story (output kinds) |
| Q5 | When does the AI step read tracker context through the vendors' MCP servers? Default: after 08.5.1, not before G2 `[OPEN]` | Owner | after 08.5.1 (no task now) |

## 12. Estimate summary

| Epic | V1 | V2 | V3 | Total |
|---|---|---|---|---|
| 13.1 Authoring model | 8.0 | 0 | 0 | 8.0 |
| 13.2 Project AI settings and role profiles | 11.0 | 0 | 0 | 11.0 |
| 13.3 Requirement → checklist → test case | 7.0 | 0 | 0 | 7.0 |
| 13.4 Authoring screens | 10.0 | 0 | 0 | 10.0 |
| 13.5 Script generation and local runs | 13.0 | 0 | 0 | 13.0 |
| 13.6 Element memory | 5.0 | 0 | 0 | 5.0 |
| 13.7 Mobile spike and multimodal locate fallback | 5.0 | 0 | 0 | 5.0 |
| 13.8 Drift classification and repair proposals | 7.0 | 0 | 0 | 7.0 |
| 13.9 Phase gates | 1.5 | 0 | 0 | 1.5 |
| 13.10 Projects without a repository | 38.0 | 0 | 0 | 38.0 |
| 13.11 Hosted runs | 0 | 15.5 | 0 | 15.5 |
| 13.12 Operator admin console | 23.0 | 3.0 | 0 | 26.0 |
| **Total** | **128.5** | **18.5** | **0** | **147.0** |

→ with 20 % buffer **176.4** person-days. By phase (raw): G1 74.0 · G2 62.5 · G3 10.5 (13.10: G1 26.5, G2 11.5; 13.11: G2 15.5; 13.12: G1 12.0, G2 14.0).

## 13. Contract change requests

| # | Request | Why |
|---|---|---|
| CR-1 | 00 §3: glossary entries `feature node`, `checklist item`, `manual test case`, `authoring version`, `element profile`, `generated script`, `locator drift`, `UI change` | New names used by 13, 08 and 09 |
| CR-2 | 00 §2: permissions `authoring:read`, `authoring:write`, `authoring:generate`, `script:generate` in the default role mapping | Roles are seeded as data |
| CR-3 | 00 §8: states for `generated_script` (draft → active / flaky → retired) and `authoring_change` (pending → applied / discarded) | Canonical state list |
| CR-4 | 00 §4: phases G1–G4 replace milestones M0–M5 as the Milestone value in every plan | CR-D-2 |
| CR-5 | 00 §3: glossary entries `role profile`, `role prompt template` (S92, 2026-10-26; logged in 00 §15) | New names used by 13.2.2, 13.3.1 and 13.4.1 |
| CR-6 | 00 §3: glossary entries `requirement`, `requirement version`, `manual run`, `manual result`, `test case report`, `share link`, `tracker connection` (logged in 00 §15) | New names used by 13.10 |
| CR-7 | 00 §2: permission `manual_run:write` (`qa_engineer`, `test_maintainer`, `tenant_admin`); `contracts/permissions.json` follows in 13.10.2-T2 | Manual results need their own write string |
| CR-8 | 00 §8: states for `manual_run` (open → closed) and `tracker_connection` (active ↔ needs_reauth; either → disconnected); `manual_result`, `requirement_version` and `test_report` are append-only / immutable | Canonical state list |
| CR-9 | Earlier spec-only limits on manual evidence and tracker writes superseded by D-REALIGN (see CR-10–CR-12) | Owner decision |
| CR-10 | 00 §2: `tracker:create_issue` and `hosted_run:create` | D-REALIGN: separate explicit actions |
| CR-11 | 00 §3: manual evidence revision, tracker item kind, script revision, hosted run | D-REALIGN: common terms across authoring, policy and reports |
| CR-12 | 00 §8.5/§8.6: release inputs and manual aggregation rows | D-REALIGN: closed release-bound evidence only |
| CR-13 | 00 §2 and §9.1: the operator console is a separate web app on the operator ingress (`/internal/operator/*`, never `/v1`); 00 §15 logs it. No `contracts/` change is made here; the operator routes follow in 13.12.1-T3 onward | Owner 2026-11-27: Operator admin console (FR-13-14, Epic 13.12) |
