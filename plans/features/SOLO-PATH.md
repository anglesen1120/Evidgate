# Solo delivery path (one developer)

> Status: **working plan from owner decisions of 2026-10-02** (DECISIONS §8 O2: the team is the owner alone; O3 deferred; reviews may be done by others or by AI against the plan and spec). Sizes come from `scripts/plan_graph.py` over the current task rows; the remaining-work figures subtract the 56 tasks `STATUS.md` §2 lists as done (63.5 raw days). **Recomputed 2026-10-02 (evening)** after the de-duplication, ordering fixes and the `seed-dev` bootstrap. Calendar numbers are `[INFERENCE]` arithmetic, not a forecast. Replaces the multi-lane assumption of `README.md` §5.5 for scheduling.

## 1. What changes with one developer

| Topic | Before | Now |
|---|---|---|
| Team | 2–3 engineers, Tech Lead + QA lead | One person is developer, Tech Lead and QA/product owner |
| Parallel lanes (`README.md` §5.5) | A/B/C lanes | One lane; order = the stage list below |
| Independent review | A person other than the author | A reviewer who did not write the change (another person or an AI session) working **only from the spec package** (§3) |
| Hosting provider (O3) | Decide before M2 | **Deferred.** Dev and staging run on the compose stack; no real pilot data, no restore drill, no `10.2.2` deploy until O3 is decided |
| AI (O4) | Open | Evidgate-provided credit through an Evidgate-controlled gateway (story 08.5.5); V1 healing stays rule-based |
| Customer interviews (O1) | M0 gate | Waived; pilot profile in `docs/decisions/O1-pilot-profile.md` |

## 2. Stages

Remaining raw person-days (done tasks excluded; dependency closure included). Each stage ends with a demonstrable result and a decision point.

### Stage A — API + CLI loop, no UI (78.75 raw days remaining)

Demo result: a Playwright run in CI sends a manifest and events through the CLI; the server finalizes it and returns **PASS / FAIL / INCONCLUSIVE with completeness**, and a run with a missing shard never yields PASS (source §12 Sprint-2 demo).

Stories, in build order (remaining days): **01.8.1-T3 `seed-dev` bootstrap** (1.0: org, project, dev user, memberships, CI binding — the way into the system before login exists; part 2, 01.8.1-T4, mints a dev ingest token and the sample policy once 02.4.2 and 07.1.1 land, 0.5) · 02.1.1 org/project API (1.5) · 02.6.1 + 02.6.2 tenant context (2.0) · 02.7.1 audit writer (2.0; 05.3.1 writes audit rows) · 02.4.1 + 02.4.2 CI authentication (6.0) · 03.1.1 + 03.1.3 + 03.2.1 stable case identity and suite revisions (5.0) · 05.1.1 + 05.1.2 manifest and environment revision (5.0) · 04.1.1 + 04.1.2 atomic batch ingest (6.0) · 05.2.1 + 05.2.2 run creation with branch/PR identity (7.5) · 04.2.1 + 04.2.2 + 04.2.3 CLI (4.5; the CLI skeleton 04.2.1-T2 comes first and uses `EVIDGATE_DEV_TOKEN`) · 04.3.1 + 04.3.2 Playwright reporter (4.0) · 05.3.1 + 05.3.2 + 05.3.3 state machine, reconcile, attempts (10.0) · 06.1.1 + 06.1.2 + 06.5.1 artifact completion and completeness (6.0) · 07.1.1 + 07.2.1 + 07.2.2 + 07.3.1 + 07.5.1 policy and evaluator (9.25).

Calendar `[INFERENCE]`: ×1.4 buffer ≈ 110 days ≈ 22 weeks; with a 0.7 focus factor (review, interruptions, context switching) ≈ 158 days ≈ 31 weeks. The per-story days above are the story's remaining tasks; the 78.75 total is the dependency closure (it also pulls in done-adjacent tasks such as 02.1.1-T3 quota columns and 03.2.1-T5 diff API).

### Stage B — see it and gate on it (+19.5 raw days)

Demo result: sign in, see the run list, run detail, evidence panel and decision; a GitHub check run carries the decision on the PR. Stories: 02.3.1 login (2.0) · 09.1.1 shell, session, shared state components (T1, T3, T4, T5, T7, T8, and the T2 integration spike they depend on) · 09.3.1 T1, T2, T3, T5 run list, detail, evidence, decision · 07.6.1 GitHub check run (02.4.3-T1/T2 GitHub App installation, now an explicit dependency of 07.6.1-T1) · 07.5.2 decision read API.

Cumulative A + B = 98.25 raw days: ≈ 28 weeks with the 1.4 buffer, ≈ 39 weeks with the 0.7 focus factor.

**Decision point after B:** send the demo to the first external team (O1 pilot one). If no external team agrees to send real CI results within 4 weeks, stop V1 breadth and revisit the premise.

### Stage C — pilot hardening (only after the decision point)
Reports and artifacts beyond the slice (06.x), waivers/quarantine (07.3.2, 07.4.x), reruns and cancel (05.4, 05.5.1, 05.5.2), usage metering and quota (04.8, 12.3), notifications (02.10.1), CLI release and docs (04.9.2), observability baseline (10.3), security gates that protect real customer data (11.2–11.4). Shadow healing (08.1–08.4, 04.9.1) is the last V1 block.

### Stage D — V2 (healing apply, AI credit, agent mode)
Story 08.5.5 and 08.5.x; hosting (O3) must be decided before any real customer data.

## 3. Spec package (what a reviewer receives)

Because reviews are done from the plan and spec, a change is reviewable only when these exist and are linked in the PR:

1. The feature plan of the story (acceptance criteria AC1…ACn, the four DoR answers, test plan).
2. The contract diff (`contracts/openapi.yaml`, JSON schemas, migrations) and the 00 §15 row when a contract changes.
3. A test list mapping each AC to a named test (the reviewer checks tests, not memory).
4. The `scripts/verify.sh` output for the changed stages.

The reviewer answers per AC: pass / fail / not covered, and flags anything in the diff not required by the spec. A security-sensitive story (tenant isolation, auth, healing safety) is reviewed by a second reviewer with a different prompt or person. The author never closes a review thread alone.

## 4. Deferred or cut for the solo path

SAML (02.3.3), custom roles, operator console beyond the grant API (02.8), billing/invoicing tooling beyond the ledger (12.4), trademark follow-through (12.6), device/mobile work (04.5, 05.8), V3 visual regression, SeaweedFS-specific hardening beyond what runs today, extra review ceremonies that duplicate the spec-package check. Each remains in its area plan; none is deleted.

## 5. Getting in before login (Stage A bootstrap)

Login (02.3.1) is Stage B, so Stage A needs another way to create the first tenant. `Evidgate.Migrator seed-dev` (01.8.1-T3/T4) refuses any environment except dev and creates: one org, one project, a dev user holding `tenant_admin` and `release_approver`, one CI binding for the dev repository, then (after 02.4.2 and 07.1.1) one ingest token printed once and one sample policy revision. The CLI reads that token from `EVIDGATE_DEV_TOKEN` (04.2.1-T2, refused on staging/prod). A real GitHub Actions run uses the OIDC path (02.4.1) against the seeded CI binding. API tests use a test principal and never the seed.

## 6. Open points

- `[OPEN]` O3 interpretation: "phase 2" is read as "after the Stage B decision point and before any real customer data". If you mean something else, change the sentence in DECISIONS §8 O3.
- `[OPEN]` Who the external reviewer is. The spec package (§3) is the contract with them. Recommendation and information needed: `plans/open-items/README.md`.
- `[INFERENCE]` The 0.7 focus factor is an assumption; replace it with the measured throughput of Stage A's first three stories.
