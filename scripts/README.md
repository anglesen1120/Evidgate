# scripts/

Repository tooling (00 §5.2). Current scripts:

| Script                          | Purpose                                                                                                                    | Owner        |
| ------------------------------- | -------------------------------------------------------------------------------------------------------------------------- | ------------ |
| `check-adr.sh`                  | ADR file/section convention check (01.9.1-T1)                                                                              | 01.9.1       |
| `check-adr-matrix.sh`           | Every CPM package and direct npm dependency has a row in ADR-0002 (01.1.1 AC6)                                             | 01.1.1       |
| `check-layout.sh`               | Top-level tree and `.slnx` project list match 00 §5.2 (01.1.1 AC5)                                                         | 01.1.1       |
| `check-sources.sh`              | Exactly one package source per ecosystem (nuget.org / registry.npmjs.org)                                                  | 01.1.1       |
| `smoke-clean-build.sh`          | AC1 clean-clone restore+build of both ecosystems inside Docker                                                             | 01.1.1       |
| `verify.sh`                     | Full local CI gate: restore, build, format, tests, lint, all check scripts (FR-01-1)                                       | 01.2.1       |
| `check-links.sh`                | Internal markdown link check                                                                                               | 01.9.1       |
| `check-contract-conventions.sh` | OpenAPI/Spectral + JSON Schema convention checks                                                                           | 01.3.1       |
| `check_features.py`             | Feature-plan structure checker (id pattern `NN.E.S`, sections, sizes)                                                      | plan tooling |
| `feature_index.py`              | Feature-plan index generation                                                                                              | plan tooling |
| `plan_graph.py`                 | Plan dependency graph                                                                                                      | plan tooling |
| `regen_docs.py`                 | Regenerates docs/source + 00 §14 estimates                                                                                 | plan tooling |
| `sync_feature_headers.py`       | Syncs feature-plan header tables with area plans                                                                           | plan tooling |
| `dev/db-init.sh`                | Local database bootstrap: roles + database as superuser, migrate as owner, grant LOGIN + password (01.4.1-T2.3)            | 01.4.1       |
| `dev/lib.sh`                    | Shared paths, `.env.dev` loading and host-process control for the dev-stack scripts                                        | 01.8.1       |
| `dev/up.sh`                     | One command: compose stack, migrations, dev bucket lifecycle, Api + Worker under `dotnet watch`, waits for readiness (AC1) | 01.8.1       |
| `dev/down.sh`                   | Stops the host processes and the compose services, keeps the volumes (AC4)                                                 | 01.8.1       |
| `dev/logs.sh`                   | Follows the compose services and the host logs                                                                             | 01.8.1       |
