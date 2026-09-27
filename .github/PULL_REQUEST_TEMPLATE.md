<!--
Feature branches: feature/<NN.E.S>-<slug> off develop.
Commit messages: Conventional Commits (see .github/CONTRIBUTING.md).
-->

## Story

Story id (NN.E.S): `___.___.___`

Feature plan: `plans/features/…`

## Acceptance criteria and evidence

<!-- Copy the AC table from the feature plan and fill in evidence: the exact
command you ran and its observed output. Formatting/analyzer ACs need a seeded
violation result AND a clean run. -->

| AC  | How verified | Evidence |
| --- | ------------ | -------- |
| AC1 |              |          |

## Definition of Ready (feature-plan and story changes)

<!-- 00 §12 + plans/features/README.md §6: a feature plan this PR adds or changes answers
the four review questions of its §8 on labelled lines. scripts/check-dor-security.sh greps
them here, in the DoR checklist and in every added/changed feature plan. Delete this
section when the PR touches no feature plan and no story. -->

- [ ] **Authorization** — every command/query the story adds names the role/permission it needs, or states `none`
- [ ] **Retention/deletion** — what the story keeps or reads, for how long, and what deletion does to it (00 §11 and the owning retention rule)
- [ ] **Telemetry** — the metric, trace span, audit record or `security_event` it emits, or `none`
- [ ] **Failure path** — what a caller or operator sees on failure: error code/envelope, retry or dead-letter behaviour, runbook note
- [ ] `scripts/check-dor-security.sh` passes on this change set (the DoR checklist, this template and every feature plan added or changed)

## Security, privacy, performance checklist

<!-- 00 §5.7: S = security, P = performance rules. Check the ones relevant to
this change and say how it complies; delete the rest. -->

- [ ] **S1** parameterised SQL only — no string-built SQL anywhere (incl. `set_config`)
- [ ] **S2** authorization in the pipeline, not endpoints — every Mediator handler carries `[Permission]`
- [ ] **S3** no secrets in logs, metric labels or exception messages
- [ ] **P3** no N+1 — list/query handlers issue a bounded number of SQL statements

## Dependencies and decisions

- [ ] Every new NuGet/npm dependency has a row in `docs/adr/0002-compatibility-and-licence-matrix.md` (`scripts/check-adr-matrix.sh` passes)
- [ ] `scripts/licence-scan.sh` passes — deny/warn licences are excepted only via a `scripts/licence-policy.json` `exceptions` entry keyed by the ADR id (01.7.1 AC4)
- [ ] `scripts/audit-deps.sh` passes — exempted advisories are listed in `scripts/exceptions.json` keyed by advisory id with the accepting ADR id (01.7.1 AC6)
- [ ] New packages that run install scripts (npm `preinstall`/`postinstall`) or ship NuGet build targets (`build/`/`buildTransitive/` in the nupkg) are flagged under "Notes for reviewers" (plans/01 §6 supply chain)
- [ ] Test evidence is filled in: every AC row above cites the exact command and observed output; formatting/analyzer ACs show a seeded violation AND a clean run
- [ ] Each plan 01 §3 decision this change implements cites its ADR in the PR description or the code comment
- [ ] `scripts/verify.sh` passed locally before opening this PR

## Notes for reviewers

<!-- Contract changes, deviations from the feature plan, follow-ups. -->
