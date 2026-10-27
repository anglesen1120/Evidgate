# Architecture decision records

**The rule:** an irreversible choice, a licence exception, or a public contract change ⇒ ADR before merge. `scripts/check-adr.sh` enforces index completeness and required sections; `scripts/check-adr-matrix.sh` (ADR-0002) blocks a new `NuGet`/`npm` dependency that has no matrix row.

## Lifecycle

`proposed → accepted → superseded-by NNNN | deprecated` — every status is dated (`## Date`, `YYYY-MM-DD`). Superseded and deprecated ADRs are never deleted: they stay in the index with their final status so the history is preserved.

## Adding an ADR

1. Copy [`0000-template.md`](0000-template.md) to `NNNN-<slug>.md`, where `NNNN` is the next unused four-digit number.
2. Fill all eight sections; mark every external claim with the legend (✅ verified with link · ⚠️ unverified · `[INFERENCE]`).
3. Add a row to the index table below, linking the file.
4. Merge only with the ADR in the same PR; a file missing from the index or missing a required section fails `check-adr.sh`.

## Index

| ADR | Title | Status | Date |
|---|---|---|---|
| [0000-template.md](0000-template.md) | Template | n/a | n/a |
| [0002-compatibility-and-licence-matrix.md](0002-compatibility-and-licence-matrix.md) | Compatibility and licence matrix | accepted | 2026-09-25 |
| [0001-modular-monolith-two-processes.md](0001-modular-monolith-two-processes.md) | One modular monolith, two processes | accepted | 2026-09-25 |
| [0003-migrator-and-db-role-split.md](0003-migrator-and-db-role-split.md) | Migrator process and database role split | accepted | 2026-09-25 |
| [0004-s3-emulator-local-dev.md](0004-s3-emulator-local-dev.md) | S3 emulator for local development | accepted | 2026-09-25 |
| [0005-licence-allow-deny-policy.md](0005-licence-allow-deny-policy.md) | Licence allow/deny policy for NuGet and npm | accepted | 2026-09-25 |
| [0006-cqrs-pipeline-and-data-access.md](0006-cqrs-pipeline-and-data-access.md) | CQRS pipeline and data access | accepted | 2026-09-25 |
| [0007-single-github-app.md](0007-single-github-app.md) | Single GitHub App per environment (production + non-production) | proposed | 2026-10-11 |
| [0008-web-integration-fit.md](0008-web-integration-fit.md) | Web integration fit: SDK behind `HttpClient`, zoneless, Trusted Types, CDK tables, SignalStore | accepted | 2026-10-24 |
| [0009-event-table-partitioning.md](0009-event-table-partitioning.md) | Event-class table partitioning: key, dedupe key and retention windows | proposed | 2026-10-27 |
