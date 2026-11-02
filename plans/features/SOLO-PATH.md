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

### Pilot-0 — dogfood cut of Stages A + B

Owner decision 2026-10-31 (D-PILOT0): deliver a dogfood Pilot-0 first, then continue the remaining features. Pilot-0 is a delivery-order cut through Stages A + B, not a replacement: the Stage A/B task lists, the 78.75 / 19.5 / 98.25 figures, all estimates, Milestone cells and plan rows stay as they are, and nothing is deleted from the area plans. Pilot-1 (the first external team) is the Stage B decision point below.

**Demo result:** the Evidgate `apps/web` Playwright e2e run (`npm run e2e -w apps/web`) reports through `packages/adapter-playwright` and the CLI to the local dev stack with the seeded dev token (§5). The run gets PASS / FAIL / INCONCLUSIVE with completeness, `evidgate gate` exits with the decision, and the web shows the decision view and the evidence panel. A missing shard never yields PASS.

**Critical path (backend, in order).** Dependencies are quoted from the plan rows; every dependency of these tasks is already merged on develop (STATUS §2), except that 01.8.1-T3 (a dependency of the side-lane task 01.8.1-T4) is partial: T3.1 landed, T3.2 open.

|Step|Item|Plan row|Dependency cell|Size → days|
|---|---|---|---|---|
|1|CR-08 late events never reach a revision|`REVIEW-2026-10-29.md:34`|Must precede 07.5.1, 07.2.1|L → 2.0|
|1|CR-09 `event.stale` never written|`REVIEW-2026-10-29.md:35`|Must precede 05.7.2, 07.2.1|M → 1.0|
|1|CR-11 freeze counts `ready` raw reports|`REVIEW-2026-10-29.md:37`|Must precede 07.2.1|M → 1.0|
|1|CR-10 remainder: carried rows "runs stuck in running/queued past collection_deadline_at (S152, CR-10)" and "collection-deadline outbox key not re-armed after dead-letter (S152, CR-10)" (CR-07 and the collecting-run sweep merged in #330)|`docs/status-carried.md` (cite by title)|none recorded|remainder not sized (CR-10 as a whole is M in `REVIEW-2026-10-29.md:36`; #330 landed part)|
|2|07.2.1-T1 evaluator core|`07-policy-decisions.md:169`|05.3.2-T5, 06.5.1-T1|M → 1.0|
|3|07.2.1-T2 completeness/outcome/decision + `reasons[]`|`07-policy-decisions.md:170`|07.2.1-T1|M → 1.0|
|4|07.2.2-T1 evaluation task handler|`07-policy-decisions.md:175`|07.2.1-T2, 01.5.1-T2, 01.5.1-T3|M → 1.0|
|4|07.2.2-T2 golden matrix M1–M18|`07-policy-decisions.md:176`|07.2.1-T2|M → 1.0|
|5|07.2.2-T3 exceptions snapshot|`07-policy-decisions.md:177`|07.2.2-T1, 07.3.1-T1, 07.3.1-T2|S → 0.5|
|6|07.5.2-T1 `GET /v1/runs/{r}/decision`|`07-policy-decisions.md:219`|07.2.2-T1, 01.3.1-T1, 02.2.1-T1|S → 0.5|

**Side lanes** (start when their dependency is merged; they do not block the critical path until the demo):

|Item|Plan row|Dependency cell|Size → days|
|---|---|---|---|
|01.8.1-T4 seed part 2 (dev ingest token, sample policy)|`01-foundation.md:238`|01.8.1-T3, 02.4.2-T1, 07.1.1-T1|S → 0.5|
|07.6.1-T2 `evidgate gate`|`07-policy-decisions.md:238`|07.5.2-T1, 04.2.1-T1|M → 1.0|
|09.3.1-T3 evidence panel|`09-web-ui.md:212`|05.3.2-T1, 06.5.1-T1|L → 2.0|
|09.3.1-T5 decision view|`09-web-ui.md:214`|07.5.2-T1|M → 1.0|

**Days (raw, plan sizes × AGENTS.md scale XS 0.25 · S 0.5 · M 1 · L 2 · XL 3).** Critical path: CR-08 2.0 + CR-09 1.0 + CR-11 1.0 (register sizes L, M, M) + 07.2.1-T1 1.0 + 07.2.1-T2 1.0 + 07.2.2-T1 1.0 + 07.2.2-T2 1.0 + 07.2.2-T3 0.5 + 07.5.2-T1 0.5 = 9.0 days (the CR-10 remainder is unsized and not counted; size it when scheduling). Side lanes: 0.5 + 1.0 + 2.0 + 1.0 = 4.5 days. Pilot-0 sized total 9.0 + 4.5 = 13.5 raw days. Calendar `[INFERENCE]`: ×1.4 buffer = 18.9 days; with the 0.7 focus factor ≈ 27 days; the e2e wiring through the adapter and CLI is not sized in the plans.

**Deferred to Pilot-1 (external team):** 02.4.3-T2/T3 + 07.6.1-T1 GitHub App and check run; 07.5.1 late re-evaluation; 04.1.2 rate limit/epoch; 05.2.2-T3 fork PR; 03.2.1-T2..T4; 09.3.1-T6..T11; register rows CR-12..CR-16; and O3 hosting (before any real customer data). 02.4.1-T2..T4 (OIDC CI token) are already landed on develop (STATUS §2, a0c3a08), so no work remains there; Pilot-0 uses the seeded dev token instead of OIDC.

### Stage B — see it and gate on it (+19.5 raw days)

Demo result: sign in, see the run list, run detail, evidence panel and decision; a GitHub check run carries the decision on the PR. Stories: 02.3.1 login (2.0) · 09.1.1 shell, session, shared state components (T1, T3, T4, T5, T7, T8, and the T2 integration spike they depend on) · 09.3.1 T1, T2, T3, T5 run list, detail, evidence, decision · 07.6.1 GitHub check run (02.4.3-T1/T2 GitHub App installation, now an explicit dependency of 07.6.1-T1) · 07.5.2 decision read API.

Cumulative A + B = 98.25 raw days: ≈ 28 weeks with the 1.4 buffer, ≈ 39 weeks with the 0.7 focus factor.

**Decision point after B:** send the demo to the first external team (O1 pilot one). If no external team agrees to send real CI results within 4 weeks, stop V1 breadth and revisit the premise.

### Stage C — pilot hardening (only after the decision point)
Reports and artifacts beyond the slice (06.x), waivers/quarantine (07.3.2, 07.4.x), reruns and cancel (05.4, 05.5.1, 05.5.2), usage metering and quota (04.8, 12.3), notifications (02.10.1), CLI release and docs (04.9.2), observability baseline (10.3), security gates that protect real customer data (11.2–11.4). Shadow healing (08.1–08.4, 04.9.1) is the last V1 block.

### Stage D — V2 (healing apply, AI credit, agent mode)
Story 08.5.5 and 08.5.x; hosting (O3) must be decided before any real customer data.

**Notes (S186, 2026-11-02): Pilot-0 exit check observed on the local dev stack.** (a) `up.sh --reset --with-token` plus migrator plus `seed-dev` works from a clean checkout once `.env.dev` carries `EVIDGATE__CI__PEPPER` and `EVIDGATE__CI__SIGNINGKEY`; `--reset` deletes the pg-data/s3-data volumes, so it is for a throwaway stack only, and an existing dev stack is stopped (`down.sh`) while the dogfood runs under `COMPOSE_PROJECT_NAME=evidgate-pilot0`. (b) The apps/web Playwright smoke test reports through `packages/adapter-playwright` and the CLI. Only `smoke.spec.ts` (the vi boot test) runs through the reporter: the seeded catalog holds exactly one case, so a planned execution exists for one test, and the other apps/web specs (csp, shell, telemetry, static-server) have no planned execution and would be refused. (c) The evaluator produces a live decision and `GET /v1/runs/{r}/decision` serves it. (d) `evidgate gate` exits 0 for an all-green run (PASS, complete), 1 for a forced failure (FAIL), 2 for a two-shard run with one shard finalized (the run stays pending; the INCONCLUSIVE deadline path was not exercised). (e) NOT met: the web app needs a session and the dev user has no login (`GET /v1/runs/{r}` is 401 with the dev token), so no live screenshots exist. Gaps fixed in the PR: the seed now activates the sample policy and requires the seeded suite. Carried: placeholder execution key in the reporter markers, no manifest generator for customer CI (04.2.2-T3), web needs a human login (02.3.1; 02.4.1 is the CI OIDC exchange), INCONCLUSIVE path not exercised. Runbook: `docs/runbooks/pilot0-dogfood.md`.

**Notes (S187, 2026-11-02, `PR #395`, merged as `5a4e7b81`): Pilot-0 exit check (e): sign-in met, shell blocked on 09.1.1-T4.2 (S190); Development-only sign-in of the seeded dev user.** `POST /v1/dev/session` (anonymous, body `{"secret"}`, HTTPS only) is mapped only on a Development host with `EVIDGATE__DEVLOGIN__ENABLED=true` and `EVIDGATE__DEVLOGIN__SECRET` set; it issues the same `__Host-` session and antiforgery cookies as a federated login through the shared session-completion path, so logout and self-revoke behave as in 02.3.2. Live proof: 204 with cookies, 401 wrong secret, 503 plaintext, 404 flag absent. It is not the 02.3.1 federated login receiver.

The S186 Notes above landed in #392 (merge a7c6d7c1).

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
