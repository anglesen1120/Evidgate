# ADR-0005 — Licence allow/deny policy for NuGet and npm

## Status

accepted (2026-09-25)

## Date

2026-09-25

## Context

DECISIONS.md §4 locks the licence rule: every dependency must allow closed-source commercial SaaS — no AGPL/SSPL/BUSL/RPL, no paid-maintenance EULAs, no non-commercial licences; LGPL needs an ADR. The rule covers **both** NuGet and npm (01 FR-01-10, NFR-01-3; TL T40 puts the supply-chain gate on both ecosystems). A policy is only real if it is mechanical: ADR-0002 is the package matrix, `check-adr-matrix.sh` blocks unregistered packages, and the scanners enforce the licence side. This ADR fixes what the allow/deny lists contain and how exceptions work.

## Decision

- One shared policy file, `scripts/licence-policy.json` (⚠️ pending — delivered by 01.7.1-T2 together with the scanners), consumed by both ecosystem scans:
  - **npm**: `license-checker` 25.0.1 scans installed licences against the policy.
  - **NuGet**: the licence-scan tool is **pending** — chosen and licence-checked in 01.7.1-T2 (TL T40), at which point this ADR's Evidence and the policy file gain the tool row. ⚠️ until then.
- **Allow:** MIT, Apache-2.0, BSD-2-Clause, BSD-3-Clause, ISC, PostgreSQL, CC0-1.0, 0BSD, Unlicense — and anything equivalent the matrix row records with ✅ evidence.
- **Deny:** AGPL-*, SSPL-1.0, BUSL-*, RPL-*, paid-maintenance/"Open Source Maintenance Fee" EULAs, non-commercial licences (e.g. Xceed Community Licence), `NOASSERTION`/missing licence, and any dual-licence whose only open option falls in this list.
- **LGPL-*** is neither allowed nor denied outright: it requires an ADR row before merge.
- **Exception mechanism:** a denied licence may only ship via an ADR row (ADR id recorded in `licence-policy.json` — ⚠️ pending, 01.7.1-T2 — with a named owner); NFR-01-3 — no other exception channel exists.
- Enforcement: `check-adr-matrix.sh` (registration) already blocks unregistered packages; the licence gate in CI (01.7.1) fails on a denied licence or a missing policy entry.

## Alternatives rejected

- **Per-ecosystem policies or a hand-maintained "approved packages" list** — the rule is one rule; two lists drift apart. One policy file + the ADR-0002 matrix is the single source (01 §3 licence-gate decision).
- **Allow-by-default (flag only denied hits)** — a missing or unparseable licence must fail closed, not pass silently; `NOASSERTION` is in the deny class for that reason.
- **SPDX-expression-only matching without per-package evidence** — the licence must be evidenced (nuspec `<license>` tag, licence file in the nupkg, or registry record), not just claimed by a dependency's metadata; ADR-0002 rows carry that evidence per package.
- **Deferring the licence gate to "later"** — FR-01-10/AC require it on every PR from M1; the matrix half already runs via `check-adr-matrix.sh`.

## Consequences

- Every `PackageVersion`/`PackageReference` and every npm dependency gets an ADR-0002 row **and** an allowed licence; both halves gate CI.
- `license-checker` is itself a devDependency subject to the same rule: BSD-3-Clause ✅ (registry record fetched 2026-09-25) — it needs an ADR-0002 row when added (root `devDependencies`, agent Lint's workspace).
- The pending NuGet scanner is an unresolved ⚠️ carried to 01.7.1-T2; this ADR records the obligation, not the choice.
- LGPL dependency proposals (e.g. Hangfire was rejected on LGPL-3.0 grounds) require an ADR row and owner, not just a matrix row.

## Evidence

- ✅ Licence rule text — DECISIONS.md §4 (repo, read 2026-09-25); enforcement obligations in 01 FR-01-10, NFR-01-3 (repo, read 2026-09-25).
- ✅ `license-checker` 25.0.1, BSD-3-Clause — https://registry.npmjs.org/license-checker/25.0.1 (fetched 2026-09-25); chosen in plans/01-foundation.md §3 (licence-gate decision row).
- ✅ Denied-class precedents already on record: MediatR 14.2.0 RPL-1.5/commercial ✅, JsonSchema.Net 9.4.0 maintenance-fee EULA ✅, FluentAssertions 8.11.0 non-commercial ✅, MinIO AGPL-3.0 ✅ — ADR-0002 "Alternatives rejected" and ADR-0004 (each with its source link).
- ⚠️ NuGet licence-scan tool: selection and licence check deferred to 01.7.1-T2 (TL T40) — pending, not decided here.
- ✅ TL T40 supply-chain row — plans/features/README.md §3 (repo, read 2026-09-25).

## Revisit trigger

- A dependency we cannot replace falls in a denied class (e.g. a required driver relicenses to BUSL), or the NuGet scanner lands in 01.7.1-T2 → update Evidence and `licence-policy.json` in the same PR.
