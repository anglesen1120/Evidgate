# ADR-0002 — Compatibility and licence matrix

## Status

accepted

## Date

2026-09-25

## Context

DECISIONS.md §4 fixes the stack (.NET 10 LTS, Node 26, TypeScript 6.0.x) and the licence rule: every dependency must allow closed-source commercial SaaS — no AGPL/SSPL/BUSL/RPL, no paid-maintenance EULAs, no non-commercial licences; LGPL needs an ADR. Story 01.1.1 AC6 requires this ADR to be the running register: one row per NuGet package name (`PackageVersion`/`GlobalPackageReference` in `Directory.Packages.props`, `PackageReference` in every `*.csproj`/`*.props`/`*.targets`) and per npm dependency name (`dependencies`/`devDependencies`/`optionalDependencies`/`peerDependencies` in the root and every workspace `package.json`). `scripts/check-adr-matrix.sh` fails CI when a package is added without a row. All facts below were re-verified against nuget.org (flat-container nuspec + nupkg contents) and registry.npmjs.org on 2026-09-25, not copied from memory.

## Decision

Toolchain pins:

| Item | Pin | Evidence |
|---|---|---|
| .NET SDK | `global.json` `10.0.100` + `rollForward: latestFeature`, `allowPrerelease: false` | resolves to installed 10.0.401; any 10.0.x accepted, 11.x rejected |
| Node | `.nvmrc` `26`, `engines.node >=26 <27`, `.npmrc engine-strict=true` | Node 26 LTS (DECISIONS §4.3) |
| TypeScript | `typescript` `6.0.3` exact (root devDependency) | latest 6.0.x on the registry; 7.x forbidden until Angular/`typescript-eslint` peers allow it (DECISIONS §4.2, 01.1.1-T4) |
| NuGet source | `nuget.config`: `<clear/>` + nuget.org v3 + `packageSourceMapping` `*` → nuget.org | T40 dependency-confusion defence |
| npm source | `.npmrc` `registry=https://registry.npmjs.org/` | T40 |

NuGet packages (Central Package Management, `Directory.Packages.props`):

| Package | Version | Target-framework evidence | Licence | Evidence |
|---|---|---|---|---|
| Mediator.Abstractions | 3.0.2 | nuspec dep groups net8.0, netstandard2.0 | MIT (LICENSE file in nupkg) | https://api.nuget.org/v3-flatcontainer/mediator.abstractions/3.0.2/mediator.abstractions.nuspec |
| Mediator.SourceGenerator | 3.0.2 | nuspec dep group netstandard2.0 (analyzer) | MIT (LICENSE file in nupkg) | https://api.nuget.org/v3-flatcontainer/mediator.sourcegenerator/3.0.2/mediator.sourcegenerator.nuspec |
| Microsoft.EntityFrameworkCore | 10.0.12 | nuspec dep group net10.0 | MIT | https://api.nuget.org/v3-flatcontainer/microsoft.entityframeworkcore/10.0.12/microsoft.entityframeworkcore.nuspec |
| Microsoft.EntityFrameworkCore.Relational | 10.0.12 | nuspec dep group net10.0 | MIT | https://api.nuget.org/v3-flatcontainer/microsoft.entityframeworkcore.relational/10.0.12/microsoft.entityframeworkcore.relational.nuspec |
| Microsoft.Extensions.Hosting | 10.0.12 | nuspec dep groups incl. net10.0 | MIT | https://api.nuget.org/v3-flatcontainer/microsoft.extensions.hosting/10.0.12/microsoft.extensions.hosting.nuspec |
| Microsoft.Extensions.Http.Resilience | 10.10.0 | nuspec dep groups incl. net10.0 | MIT | https://api.nuget.org/v3-flatcontainer/microsoft.extensions.http.resilience/10.10.0/microsoft.extensions.http.resilience.nuspec |
| Microsoft.IdentityModel.JsonWebTokens | 8.23.0 | nuspec dep groups incl. net10.0 | MIT | https://api.nuget.org/v3-flatcontainer/microsoft.identitymodel.jsonwebtokens/8.23.0/microsoft.identitymodel.jsonwebtokens.nuspec |
| Npgsql | 10.0.3 | nuspec dep groups incl. net10.0 | PostgreSQL | https://api.nuget.org/v3-flatcontainer/npgsql/10.0.3/npgsql.nuspec |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | nuspec dep group net10.0 | PostgreSQL | https://api.nuget.org/v3-flatcontainer/npgsql.entityframeworkcore.postgresql/10.0.3/npgsql.entityframeworkcore.postgresql.nuspec |
| Dapper | 2.1.89 | nuspec dep groups incl. net10.0 | Apache-2.0 | https://api.nuget.org/v3-flatcontainer/dapper/2.1.89/dapper.nuspec |
| dbup-postgresql | 7.0.1 | nuspec dep groups net8.0, netstandard2.0 | MIT | https://api.nuget.org/v3-flatcontainer/dbup-postgresql/7.0.1/dbup-postgresql.nuspec |
| Corvus.Json.Validator | 5.6.1 | nuspec dep groups incl. net10.0 | Apache-2.0 | https://api.nuget.org/v3-flatcontainer/corvus.json.validator/5.6.1/corvus.json.validator.nuspec |
| FluentValidation | 12.1.1 | nuspec dep group net8.0 | Apache-2.0 | https://api.nuget.org/v3-flatcontainer/fluentvalidation/12.1.1/fluentvalidation.nuspec |
| Cronos | 0.13.0 | nuspec dep groups incl. net6.0, netstandard2.0 | MIT | https://api.nuget.org/v3-flatcontainer/cronos/0.13.0/cronos.nuspec |
| AWSSDK.S3 | 4.0.103.4 | nupkg lib/{net8.0,netcoreapp3.1,netstandard2.0,net472} | Apache-2.0 | https://api.nuget.org/v3-flatcontainer/awssdk.s3/4.0.103.4/awssdk.s3.nuspec |
| OpenTelemetry.Extensions.Hosting | 1.19.1 | nuspec dep groups incl. net10.0 | Apache-2.0 | https://api.nuget.org/v3-flatcontainer/opentelemetry.extensions.hosting/1.19.1/opentelemetry.extensions.hosting.nuspec |
| Microsoft.NET.Test.Sdk | 18.10.1 | nuspec dep groups net8.0, net462 | MIT | https://api.nuget.org/v3-flatcontainer/microsoft.net.test.sdk/18.10.1/microsoft.net.test.sdk.nuspec |
| xunit.v3 | 4.0.1 | nuspec dep groups net8.0, net472 | Apache-2.0 | https://api.nuget.org/v3-flatcontainer/xunit.v3/4.0.1/xunit.v3.nuspec |
| xunit.runner.visualstudio | 4.0.0 | nuspec dep groups net8.0, net472 | Apache-2.0 | https://api.nuget.org/v3-flatcontainer/xunit.runner.visualstudio/4.0.0/xunit.runner.visualstudio.nuspec |
| Shouldly | 4.3.0 | nuspec dep groups net8.0, net9.0, netstandard2.0 | BSD-3-Clause | https://api.nuget.org/v3-flatcontainer/shouldly/4.3.0/shouldly.nuspec |
| NSubstitute | 6.2.0 | nuspec dep groups net8.0, netstandard2.0 | BSD-3-Clause | https://api.nuget.org/v3-flatcontainer/nsubstitute/6.2.0/nsubstitute.nuspec |
| Testcontainers.PostgreSQL | 4.15.0 | nuspec dep groups incl. net10.0 | MIT | https://api.nuget.org/v3-flatcontainer/testcontainers.postgresql/4.15.0/testcontainers.postgresql.nuspec |
| Respawn | 7.0.0 | nuspec dep group netstandard2.1 | Apache-2.0 | https://api.nuget.org/v3-flatcontainer/respawn/7.0.0/respawn.nuspec |

npm packages (direct dependencies/devDependencies, root + workspaces):

| Package | Version | Engines evidence | Licence | Evidence |
|---|---|---|---|---|
| typescript | 6.0.3 | `engines.node >=14.17`; pinned exact for `>=26 <27` repo engines | Apache-2.0 | https://registry.npmjs.org/typescript/6.0.3 |

Planned (not yet referenced) — DECISIONS §4.1–§4.3 choices that no project file references yet; rows here also count as registered:

| Package | Planned version | Licence | Evidence |
|---|---|---|---|
| Microsoft.AspNetCore.OpenApi | 10.0.12 | MIT | https://api.nuget.org/v3-flatcontainer/microsoft.aspnetcore.openapi/10.0.12/microsoft.aspnetcore.openapi.nuspec — nuspec dep group net10.0; emits the served OpenAPI document (DECISIONS §4.1) |
| Microsoft.AspNetCore.DataProtection | in-box (ASP.NET Core shared framework: `Microsoft.AspNetCore.DataProtection.dll` ships in `Microsoft.AspNetCore.App` 10.0.x) | MIT | https://api.nuget.org/v3-flatcontainer/microsoft.aspnetcore.dataprotection/10.0.12/microsoft.aspnetcore.dataprotection.nuspec — NuGet package exists (net10.0/net462/netstandard2.0) but is not referenced; the shared-framework build is used (DECISIONS §4.1) |
| Microsoft.Extensions.Compliance.Redaction | 10.10.0 | MIT | https://api.nuget.org/v3-flatcontainer/microsoft.extensions.compliance.redaction/10.10.0/microsoft.extensions.compliance.redaction.nuspec — nuspec dep groups incl. net10.0; log redaction (DECISIONS §4.1) |
| @angular/core | 22.2.0 | MIT | https://registry.npmjs.org/@angular/core/22.2.0 — `engines.node >=26.0.0` covers Node 26 (DECISIONS §4.2) |
| @angular/cli | 22.2.0 | MIT | https://registry.npmjs.org/@angular/cli/22.2.0 (DECISIONS §4.2) |
| @angular/build | 22.2.0 | MIT | https://registry.npmjs.org/@angular/build/22.2.0 — peer `typescript >=6.0 <6.1` (DECISIONS §4.2) |
| @angular/cdk | 22.2.0 | MIT | https://registry.npmjs.org/@angular/cdk/22.2.0 (DECISIONS §4.2) |
| @angular/localize | 22.2.0 | MIT | https://registry.npmjs.org/@angular/localize/22.2.0 — first-party i18n, vi + en builds (DECISIONS §4.2) |
| @ngrx/signals | 22.0.1 | MIT | https://registry.npmjs.org/@ngrx/signals/22.0.1 — SignalStore for feature state (DECISIONS §4.2) |
| @hey-api/openapi-ts | 0.99.0 | MIT | https://registry.npmjs.org/@hey-api/openapi-ts/0.99.0 — generates `packages/sdk` from `contracts/openapi.yaml` (DECISIONS §4.2) |
| angular-eslint | 22.5.0 | MIT | https://registry.npmjs.org/angular-eslint/22.5.0 (DECISIONS §4.2) |
| vitest | 5.0.1 | MIT | https://registry.npmjs.org/vitest/5.0.1 — web + customer-side unit tests (DECISIONS §4.2–§4.3) |
| @playwright/test | 1.63.0 | Apache-2.0 | https://registry.npmjs.org/@playwright/test/1.63.0 — web E2E + runner engine (DECISIONS §4.2–§4.3) |
| appium | 3.7.0 | Apache-2.0 | https://registry.npmjs.org/appium/3.7.0 — mobile engine with UiAutomator2/XCUITest drivers (DECISIONS §4.3) |

"Registered" means the name appears as the first cell of a matrix-table row in this Decision section (including the Planned table); rows in *Alternatives rejected* below are **not** registrations — a dependency whose name matches a rejected row fails `check-adr-matrix.sh` with "rejected in ADR-0002".

## Alternatives rejected

| Package | Version checked | Rejection reason (DECISIONS §4 licence rule) |
|---|---|---|
| MediatR | 14.2.0 | RPL-1.5 or paid commercial licence (LICENSE.md in nupkg) — fails the licence rule; replaced by `Mediator` 3.0.2 (MIT) |
| JsonSchema.Net | 9.4.0 | Binary release under a paid "Open Source Maintenance Fee" EULA (OSMFEULA.txt in nupkg) — fails the licence rule; replaced by Corvus.Json.Validator 5.6.1 (Apache-2.0) |
| FluentAssertions | 8.11.0 | Xceed Community Licence is non-commercial (LICENSE in nupkg) — fails the licence rule; replaced by Shouldly 4.3.0 (BSD-3-Clause) |
| WolverineFx | 6.40.0 | MIT (licence OK) — rejected on scope: runtime code generation and its own envelope tables are more machinery than needed (DECISIONS §4.1) |
| Hangfire | 1.8.25 | LGPL-3.0 + extra storage schema — fails the licence rule without a dedicated ADR; replaced by own outbox + `task` queue + Cronos |
| Quartz.NET | 4.1.1 | Apache-2.0 (licence OK) — rejected on scope: a second scheduler model where Cronos + a unique `(schedule_name, fire_at)` key suffices |
| NJsonSchema | 11.6.1 | MIT (licence OK) — rejected on capability: JSON Schema 2020-12 support is partial; Corvus.Json.Validator covers 2020-12 |

## Consequences

- `global.json` uses `rollForward: latestFeature` rather than the plan's literal `latestPatch`: `latestPatch` only rolls within the same feature band (10.0.1xx → 10.0.103), so a machine carrying only SDK 10.0.401 would fail AC1. `latestFeature` from `10.0.100` accepts the whole 10.0.x line — including 10.0.401 — while `allowPrerelease: false` keeps 11.0 previews/RCs out, preserving AC4's wrong-line rejection.
- Every package in `Directory.Packages.props` and every direct npm dependency must carry a row above; `scripts/check-adr-matrix.sh` exits 1 otherwise, so adding a package without registering it fails the check.
- A version bump that changes a DECISIONS §4 row is a CR in DECISIONS §10, not a silent edit here.
- Licences were read from nuspec `<license>` tags or licence files inside the nupkg/registry record on 2026-09-25; a re-release under a different licence is caught at the next bump re-check.

## Evidence

- nuget.org flat container: nuspecs and (for Mediator.*, AWSSDK.S3) unpacked nupkgs, fetched 2026-09-25 — links per row above.
- registry.npmjs.org: `typescript` packument, `6.0.3` is the latest `6.0.x` (latest tag = 7.0.2), fetched 2026-09-25.
- DECISIONS.md §4.1–§4.3 (stack table, licence rule); plans/00-overview.md §5.2 (layout), §5.7 S6 (lock files); rulings T40 (single package source) and T47.
- `dotnet --version` → 10.0.401 with `global.json` `10.0.100` + `latestFeature` (verified on this repo).

## Revisit trigger

- Any new `PackageVersion`/`PackageReference` or npm dependency: add a row before merge (enforced by `check-adr-matrix.sh`).
- 01.1.1-T4: .NET 12 LTS GA and Angular/`typescript-eslint` peer ranges allowing TS 7 → bump `global.json`, CPM versions and `typescript`, re-run this matrix.
- Any dependency re-licensing event (e.g. the MediatR 14 pattern) → re-check the affected row immediately.
