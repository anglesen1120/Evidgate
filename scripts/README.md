# scripts/

Repository tooling (00 §5.2). Current scripts:

| Script                          | Purpose                                                                              | Owner        |
| ------------------------------- | ------------------------------------------------------------------------------------ | ------------ |
| `check-adr.sh`                  | ADR file/section convention check (01.9.1-T1)                                        | 01.9.1       |
| `check-adr-matrix.sh`           | Every CPM package and direct npm dependency has a row in ADR-0002 (01.1.1 AC6)       | 01.1.1       |
| `check-layout.sh`               | Top-level tree and `.slnx` project list match 00 §5.2 (01.1.1 AC5)                   | 01.1.1       |
| `check-sources.sh`              | Exactly one package source per ecosystem (nuget.org / registry.npmjs.org)            | 01.1.1       |
| `smoke-clean-build.sh`          | AC1 clean-clone restore+build of both ecosystems inside Docker                       | 01.1.1       |
| `verify.sh`                     | Full local CI gate: restore, build, format, tests, lint, all check scripts (FR-01-1) | 01.2.1       |
| `check-links.sh`                | Internal markdown link check                                                         | 01.9.1       |
| `check-contract-conventions.sh` | OpenAPI/Spectral + JSON Schema convention checks                                     | 01.3.1       |
| `check_features.py`             | Feature-plan structure checker (id pattern `NN.E.S`, sections, sizes)                | plan tooling |
| `feature_index.py`              | Feature-plan index generation                                                        | plan tooling |
| `plan_graph.py`                 | Plan dependency graph                                                                | plan tooling |
| `regen_docs.py`                 | Regenerates docs/source + 00 §14 estimates                                           | plan tooling |
| `sync_feature_headers.py`       | Syncs feature-plan header tables with area plans                                     | plan tooling |
