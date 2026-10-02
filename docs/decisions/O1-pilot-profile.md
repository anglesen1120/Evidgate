# O1 — Pilot profile and success criteria (Test Lead view)

> Status: **working definition, not a validated market finding.** Written 2026-10-02 at the owner's request, without customer interviews (owner decision in chat: interviews are not required). Every number is `[INFERENCE]` from test-management practice and must be re-checked against the first real data. Source questions: DECISIONS §8 O1, O9, 12.1.1/12.2.1.

## 1. What this replaces, and the risk it leaves

The M0 gate (00 §4) says "a team commits to provide data and trial". With no interviews there is no evidence yet that anyone wants the product. The mitigation is to make the first pilot cheap to start and fast to falsify:

1. **Pilot zero = Evidgate's own pipeline** (01.10.1 dogfood fixture + 04.6.1). It proves the loop end to end but proves nothing about demand.
2. **Pilot one = one friendly external team**, recruited from the owner's network or from open-source Playwright projects, as soon as the §6 vertical slice exists in a demo. A working demo is the interview.
3. **Decision point**: if no external team agrees to send real CI results within 4 weeks of the demo, stop building V1 breadth and revisit the premise (00 §4 stop rule).

## 2. Pilot profile (who to look for)

| Attribute | Target | Why |
|---|---|---|
| Stack | Playwright (TypeScript) tests on GitHub Actions; optional second producer pytest/JUnit | The only V1 adapters (00 §1.3) |
| Suite size | 200–1 500 automated tests, at least one nightly or per-PR run | Big enough to show flakiness and missing evidence, small enough to onboard in one sitting |
| Pain | Visible flakiness (reruns, "just rerun it"), releases decided by gut feel or a green tick that hides skipped tests | This is what completeness-aware PASS/FAIL/INCONCLUSIVE fixes |
| Release rhythm | At least 2 releases a month | Gives decisions to measure within the pilot window |
| People | One named QA lead or tester who owns the release call and will sign the acceptance sheet | Without this nobody closes O9 or the acceptance criteria |
| Data | Test results and traces can leave the company in redacted form; no regulated personal data in artifacts for the first pilot | Keeps PDPL work (plan 11) out of the pilot's critical path |
| Anti-profile | Mobile-first, no CI today, hosted-runner needs, more than ~5 000 tests, mandatory on-premise | Out of V1 scope |

## 3. Pilot acceptance sheet (what the QA lead signs)

Measured over a 4-week window after the first accepted run. Targets are starting points for the first pilot, to be tightened with data.

| Metric | Target | How measured |
|---|---|---|
| Ingest completeness | At least 95 % of CI runs reach `complete` or an explained `incomplete`; 0 runs silently dropped | Reconciler output (05.3.2) |
| No false PASS | 0 decisions PASS where a required shard or artifact was missing | Evaluation audit (07.2.x) |
| Decision usefulness | The QA lead agrees with the decision in at least 90 % of sampled runs (20 runs) | Labelled sample (12.5.1) |
| Time to decision | p95 under 60 s after finalize (00 §11) | Telemetry |
| Triage time | Median time from red run to classified cause drops by at least 30 % against the team's own baseline | Two sprints before vs during (12.2.1) |
| Flaky visibility | Flaky cases are listed with a repeat count; at least 80 % of cases the team already knows are flaky are flagged | Comparison with the team's own list |
| Willingness to continue | The team asks to keep it running after the window, or names a price they would pay | Conversation, recorded in 12.5.1 |

Pass rule for the pilot: all of the first four hold, and at least two of the last three.

## 4. Selecting the cases for the critical list (O9)

The Test Lead recommendation for the first workshop (1–2 hours, QA lead plus product owner):

- List at most 15 cases, drawn only from four classes: money moves, login/authorization, data loss, legal/consent.
- Each case records its business intent in one sentence and an owner; cases not on the list are never gating in the first pilot.
- Until the list exists the project stays advisory (DECISIONS O9).

## 5. What to prepare before the first external demo

- A seeded demo tenant with a deliberately flaky test, a missing-shard run and a clean run, so the three decisions can be shown in five minutes.
- The sales-free one-pager: "what you send, what you get, what leaves your network", in vi and en.
- A written data-handling note (what is stored, retention defaults, deletion on request) even before the DPA is final.

## 6. Open points

- `[OPEN]` Who the first external team is. Owner action: shortlist 3 candidates from personal network before the demo.
- `[OPEN]` Price signal (O5). Ask for it only after the demo, not before.
