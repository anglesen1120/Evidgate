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
| YamlDotNet | 18.1.0 | nuspec dep groups incl. net10.0, net8.0, netstandard2.0 | MIT | https://api.nuget.org/v3-flatcontainer/yamldotnet/18.1.0/yamldotnet.nuspec |
| NSubstitute | 6.2.0 | nuspec dep groups net8.0, netstandard2.0 | BSD-3-Clause | https://api.nuget.org/v3-flatcontainer/nsubstitute/6.2.0/nsubstitute.nuspec |
| Testcontainers.PostgreSQL | 4.15.0 | nuspec dep groups incl. net10.0 | MIT | https://api.nuget.org/v3-flatcontainer/testcontainers.postgresql/4.15.0/testcontainers.postgresql.nuspec |
| Respawn | 7.0.0 | nuspec dep group netstandard2.1 | Apache-2.0 | https://api.nuget.org/v3-flatcontainer/respawn/7.0.0/respawn.nuspec |
| Microsoft.VisualStudio.Threading.Analyzers | 18.7.23 | analyzer package (no runtime dep groups); build-time only via `GlobalPackageReference` | MIT | https://api.nuget.org/v3-flatcontainer/microsoft.visualstudio.threading.analyzers/18.7.23/microsoft.visualstudio.threading.analyzers.nuspec — VSTHRD async rules (01.2.1-T1) |
| Testcontainers | 4.15.0 | nuspec dep groups incl. net10.0 | MIT | https://api.nuget.org/v3-flatcontainer/testcontainers/4.15.0/testcontainers.nuspec — generic containers (SeaweedFS parity, 01.8.1-T1); already transitive via Testcontainers.PostgreSQL |
| Microsoft.Extensions.Options.DataAnnotations | 10.0.12 | nuspec dep groups incl. net10.0 | MIT | https://api.nuget.org/v3-flatcontainer/microsoft.extensions.options.dataannotations/10.0.12/microsoft.extensions.options.dataannotations.nuspec — `ValidateDataAnnotations` + `ValidateOnStart` for `EVIDGATE__*` options (01.1.2-T1.1) |
| Microsoft.Extensions.Diagnostics.HealthChecks | 10.0.12 | nuspec dep groups incl. net10.0 | MIT | https://api.nuget.org/v3-flatcontainer/microsoft.extensions.diagnostics.healthchecks/10.0.12/microsoft.extensions.diagnostics.healthchecks.nuspec — Worker-side health checks (01.1.2-T1.3); the Api uses the shared-framework copy |
| Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions | 10.0.12 | nuspec dep groups incl. net10.0 | MIT | https://api.nuget.org/v3-flatcontainer/microsoft.extensions.diagnostics.healthchecks.abstractions/10.0.12/microsoft.extensions.diagnostics.healthchecks.abstractions.nuspec — `IHealthCheck` in BuildingBlocks (01.1.2-T1.3) |
| Microsoft.AspNetCore.OpenApi | 10.0.12 | nuspec dep group net10.0 | MIT | https://api.nuget.org/v3-flatcontainer/microsoft.aspnetcore.openapi/10.0.12/microsoft.aspnetcore.openapi.nuspec — emits the served OpenAPI document (DECISIONS §4.1; 01.1.2 AC7) |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.12 | nuspec dep group net10.0 | MIT | https://api.nuget.org/v3-flatcontainer/microsoft.aspnetcore.mvc.testing/10.0.12/microsoft.aspnetcore.mvc.testing.nuspec — `WebApplicationFactory` for in-process Api tests (01.1.2 test plan) |
| Microsoft.Extensions.Compliance.Redaction | 10.10.0 | nuspec dep groups incl. net10.0 | MIT | https://api.nuget.org/v3-flatcontainer/microsoft.extensions.compliance.redaction/10.10.0/microsoft.extensions.compliance.redaction.nuspec — log redaction (DECISIONS §4.1; 01.1.2-T1.2) |
| Microsoft.Extensions.Telemetry | 10.10.0 | nuspec dep groups incl. net10.0 | MIT | https://api.nuget.org/v3-flatcontainer/microsoft.extensions.telemetry/10.10.0/microsoft.extensions.telemetry.nuspec — `EnableRedaction` log enrichment that applies the redactors (01.1.2-T1.2) |
| OpenTelemetry.Instrumentation.AspNetCore | 1.19.0 | nuspec dep groups incl. net10.0 | Apache-2.0 | https://api.nuget.org/v3-flatcontainer/opentelemetry.instrumentation.aspnetcore/1.19.0/opentelemetry.instrumentation.aspnetcore.nuspec — inbound request spans (01.1.2-T1.2) |
| OpenTelemetry.Exporter.OpenTelemetryProtocol | 1.19.1 | nuspec dep groups incl. net10.0 | Apache-2.0 | https://api.nuget.org/v3-flatcontainer/opentelemetry.exporter.opentelemetryprotocol/1.19.1/opentelemetry.exporter.opentelemetryprotocol.nuspec — OTLP egress to the collector (plan 10 §3 decision) |
| OpenTelemetry.Exporter.Prometheus.AspNetCore | 1.19.1-beta.1 | nuspec dep groups incl. net10.0 | Apache-2.0 | https://api.nuget.org/v3-flatcontainer/opentelemetry.exporter.prometheus.aspnetcore/1.19.1-beta.1/opentelemetry.exporter.prometheus.aspnetcore.nuspec — `GET /metrics` scrape endpoint (00 §9.1, 01.1.2 §5). [WARN] pre-release: no stable version exists on nuget.org (every listed version is `-beta`, checked 2026-09-26); revisit when a stable release ships or if plan 10 drops `/metrics` for OTLP-only |
| OpenTelemetry.Exporter.InMemory | 1.19.1 | nuspec dep groups incl. net10.0 | Apache-2.0 | https://api.nuget.org/v3-flatcontainer/opentelemetry.exporter.inmemory/1.19.1/opentelemetry.exporter.inmemory.nuspec — test-only span capture (01.1.2 AC6) |

npm packages (direct dependencies/devDependencies, root + workspaces):

| Package | Version | Engines evidence | Licence | Evidence |
|---|---|---|---|---|
| typescript | 6.0.3 | `engines.node >=14.17`; pinned exact for `>=26 <27` repo engines | Apache-2.0 | https://registry.npmjs.org/typescript/6.0.3 |
| eslint | 10.11.0 | `engines.node ^20.19.0 \|\| ^22.13.0 \|\| >=24` | MIT | https://registry.npmjs.org/eslint/10.11.0 — web/package lint (01.2.1-T1, DECISIONS §4.2) |
| @eslint/js | 10.0.1 | `engines.node ^20.19.0 \|\| ^22.13.0 \|\| >=24`; peer `eslint ^10.0.0` | MIT | https://registry.npmjs.org/@eslint/js/10.0.1 |
| typescript-eslint | 8.70.1 | peer `typescript >=4.8.4 <6.1.0` (covers 6.0.3), `eslint ^10.0.0` | MIT | https://registry.npmjs.org/typescript-eslint/8.70.1 — `no-floating-promises` (01.2.1 AC3) |
| prettier | 3.9.9 | `engines.node >=14` | MIT | https://registry.npmjs.org/prettier/3.9.9 — TS/JSON/YAML format gate (01.2.1-T2) |
| husky | 9.1.7 | `engines.node >=18` | MIT | https://registry.npmjs.org/husky/9.1.7 — pre-commit hook (01.2.1-T4) |
| lint-staged | 17.5.1 | `engines.node >=22.22.1` | MIT | https://registry.npmjs.org/lint-staged/17.5.1 — staged-file linters (01.2.1-T4) |
| @stoplight/spectral-cli | 6.16.3 | `engines.node ^16.20 \|\| ^18.18 \|\| >= 20.17` | Apache-2.0 | https://registry.npmjs.org/@stoplight%2fspectral-cli/6.16.3 — OpenAPI lint (01.3.1 AC1); transitive `@scarf/scarf` postinstall telemetry is disabled by `scarfSettings.enabled=false` in the root package.json (honoured by @scarf/scarf's report.js), and `SCARF_ANALYTICS=false` is set by the lint script as belt-and-braces |
| ajv | 8.20.0 | no `engines` field | MIT | https://registry.npmjs.org/ajv/8.20.0 — JSON Schema 2020-12 checks in `scripts/` (01.3.1 AC4/AC6) |
| ajv-formats | 3.0.1 | no `engines` field | MIT | https://registry.npmjs.org/ajv-formats/3.0.1 |
| vitest | 5.0.1 | `engines.node ^22.12.0 \|\| ^24.0.0 \|\| >=26.0.0`; brings `vite` 8.3.1 transitively (peer range `^6.4.0 \|\| ^7.0.0 \|\| ^8.0.0`) | MIT | https://registry.npmjs.org/vitest/5.0.1 — web + customer-side unit tests (01.6.1-T1, DECISIONS §4.2–§4.3) |
| canonicalize | 5.1.0 | `engines.node >=22`; no dependencies | Apache-2.0 | https://registry.npmjs.org/canonicalize/5.1.0 — RFC 8785 canonical JSON in `packages/cli` (01.3.2-T4, DECISIONS §4.3). [WARN] Node 24.x and 26.x warm-process `JSON.parse` mis-decode escaped member names — V8 regression from v8/v8@f2f3b3f7a22d, fixed upstream in v8/v8@93cd21e8254b (2026-09-17) but in no Node release yet. Re-measured 2026-09-26 with a throwaway deterministic generator of 27,027 documents (15,000 random docs whose member names use control/quote/backslash escapes, 12,000 number literals, wide and 64-deep docs): a cold first pass corrupts 142 documents on Node 24.21.0 (V8 13.6) and 26.10.0 (V8 14.6), later passes in the same process range 137–208, and Node 22.23.3 (V8 12.4) stays at 0 in every pass; the same document parsed in a fresh process is correct, so the divergence is process-history state, e.g. `{"\ue000\"":5e-324,"\t":[...]}` decodes its third key as `"\\"` (U+005C) instead of U+0009. Upstream reports: nodejs/node#63785 (open, labelled v8 engine) → Chromium 521080746. On the wave-3 corpus the guard withholds 64–78 of 27,146 documents in-process; it stays in place as a partial mitigation; revisit on Node upgrades |
| yaml | 2.9.1 | `engines.node >= 14.6`; no dependencies | ISC | https://registry.npmjs.org/yaml/2.9.1 — runtime dependency of `packages/adapter-playwright`, which parses the sidecar `evidgate.cases.yaml` as plain data (core schema, no custom tags) (04.3.2-T1); test-only devDependency of `packages/cli` for `quickstart.test.ts` (04.2.4-T2), which parses the rendered workflow; root devDependency (same exact pin) for `scripts/lint-ci-recipe.mjs`, which parses customer workflows as YAML 1.2 core schema with unique keys, bounded aliases and explicit tags refused (11.2.1-T1); exact pin |
| @playwright/test | 1.63.0 | `engines.node >=20`; depends on `playwright` 1.63.0 | Apache-2.0 | https://registry.npmjs.org/@playwright/test/1.63.0 — web E2E runner in `apps/web` (01.6.1-T5, DECISIONS §4.2–§4.3) |
| @hey-api/openapi-ts | 0.99.0 | `engines.node >=22.18.0` | MIT | https://registry.npmjs.org/@hey-api/openapi-ts/0.99.0 — generates `packages/sdk` from `contracts/openapi.yaml` (01.3.2-T1, DECISIONS §4.2); moved here from Planned when T1 landed it |
| license-checker-rseidelsohn | 5.0.1 | `engines.node >=24` | BSD-3-Clause | https://registry.npmjs.org/license-checker-rseidelsohn/5.0.1 — npm licence scanner for `scripts/licence-scan.sh` (01.7.1-T2); the maintained fork: `license-checker` upstream is archived (last release 2022), a detail the plan row predates |
| @cyclonedx/cyclonedx-npm | 6.0.1 | `engines.node >=20.18.0` | Apache-2.0 | https://registry.npmjs.org/@cyclonedx/cyclonedx-npm/6.0.1 — npm CycloneDX SBOM for CI artifacts (01.7.1-T4.2) |
| @evidgate/sdk | 0.0.0 | first-party workspace (private) | First-party (unpublished) | `packages/sdk` — generated from `contracts/openapi.yaml` by `@hey-api/openapi-ts` (01.3.2-T1); ships no third-party code of its own |
| @evidgate/cli | 0.0.0 | first-party workspace (private) | First-party (unpublished) | `packages/cli` — the `evidgate` CLI (04.2.1) and its Ajv 8 contract validator (01.3.2-T2); listed because `packages/test-fixtures` depends on it for the 04.7.1-T3 protocol matrix spec; its third-party dependencies have their own rows |
| @evidgate/test-fixtures | 0.0.0 | first-party workspace (private) | First-party (unpublished) | `packages/test-fixtures` — `fixtures/` loaders + golden-file helper (01.6.1-T3); dependencies: `canonicalize` (row above) and the first-party `@evidgate/cli` (04.7.1-T3 protocol matrix spec, row above) |

.NET local tools (`.config/dotnet-tools.json`; 01.7.1 — scanned out-of-process, never linked):

| Package | Version | Tool | Licence | Evidence |
|---|---|---|---|---|
| nuget-license | 4.0.18 | `nuget-license` | Apache-2.0 | https://api.nuget.org/v3-flatcontainer/nuget-license/4.0.18/nuget-license.nuspec — NuGet licence scanner chosen by 01.7.1-T2 (T40): reads project licences without linking |
| CycloneDX | 6.2.0 | `dotnet-CycloneDX` | Apache-2.0 | https://api.nuget.org/v3-flatcontainer/cyclonedx/6.2.0/cyclonedx.nuspec — NuGet CycloneDX SBOM for CI artifacts (01.7.1-T4.2) |

Container image pins (`tests/Shared/ContainerImages.cs`, `infra/compose.dev.yml`, `infra/docker/Dockerfile.*`, `infra/README.md`; 01.6.1-T2.1, 01.8.1 and 10.2.1-T1 — multi-arch image-index digests):

| Image | Pin | Licence | Evidence |
|---|---|---|---|
| `postgres` | `18.6@sha256:5a5a84b19854a9ffaa54082c166ff4ec27473a361e496e5ea167f298f2da9722` | PostgreSQL licence | https://hub.docker.com/_/postgres — digest resolves to a multi-arch OCI index (amd64 + arm64); `docker manifest inspect` verified 2026-09-26 |
| `chrislusf/seaweedfs` | `4.47@sha256:ce9e796f1fe6f06968f4c04bdaf8f678dad9c8acdfef3d244133d71bfa6bf882` | Apache-2.0 | https://hub.docker.com/r/chrislusf/seaweedfs — same verification |
| `mcr.microsoft.com/dotnet/sdk` | `10.0.100@sha256:c7445f141c04f1a6b454181bd098dcfa606c61ba0bd213d0a702489e5bd4cd71` | MIT (.NET); Ubuntu content under the MCR OSS notice | https://github.com/dotnet/dotnet-docker/blob/main/LICENSE — builder stage only; tag = `global.json` `sdk.version`; index covers amd64/arm/arm64, `docker manifest inspect` verified 2026-09-27 |
| `mcr.microsoft.com/dotnet/aspnet` | `10.0.12-noble-chiseled-extra@sha256:00e0ad6a7ef8c0c1391b87f05c7ac757a15740455688f2bfcd146a3f4b987efd` | MIT (.NET); Ubuntu 24.04 chiseled content under the MCR OSS notice; the added ICU (`libicu74`) under the **Unicode License V3** (SPDX `Unicode-3.0`, MIT-style — the shipped Ubuntu package labels it MIT, © 1991-2022 Unicode, Inc.) and tzdata in the public domain | https://github.com/dotnet/dotnet-docker/blob/main/README.md#license (aka.ms/mcr/osslegalnotice), https://github.com/dotnet/dotnet-docker/blob/main/documentation/image-variants.md (`extra` = default image + `icu` + `tzdata`), https://github.com/unicode-org/icu/blob/main/LICENSE, and the image's own `usr/share/doc/libicu74/copyright` (`License: MIT`) / `usr/share/doc/tzdata/copyright` (`License: public-domain`) — runtime base of the **api** image only (NFC text needs ICU, 13.1.1-T2 / D-S55-Q1), non-root UID 1654; `docker buildx imagetools inspect` verified 2026-10-23, index covers amd64/arm64/arm/v7 |
| `mcr.microsoft.com/dotnet/aspnet` | `10.0.12-noble-chiseled@sha256:9651fa59abcdf177c30392cb44a820605ca5d618429ab37acbf6e7c644510b02` | MIT (.NET); Ubuntu 24.04 chiseled content under the MCR OSS notice | https://github.com/dotnet/dotnet-docker/blob/main/README.md#license (aka.ms/mcr/osslegalnotice) — runtime base of the **worker/migrator** images (globalization-invariant: it sets `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true` and ships no `libicu`), non-root UID 1654; `docker manifest inspect` verified 2026-09-27 |

Planned (not yet referenced) — DECISIONS §4.1–§4.3 choices that no project file references yet; rows here also count as registered:

| Package | Planned version | Licence | Evidence |
|---|---|---|---|
| Microsoft.AspNetCore.DataProtection | in-box (ASP.NET Core shared framework: `Microsoft.AspNetCore.DataProtection.dll` ships in `Microsoft.AspNetCore.App` 10.0.x) | MIT | https://api.nuget.org/v3-flatcontainer/microsoft.aspnetcore.dataprotection/10.0.12/microsoft.aspnetcore.dataprotection.nuspec — NuGet package exists (net10.0/net462/netstandard2.0) but is not referenced; the shared-framework build is used (DECISIONS §4.1) |
| @angular/core | 22.2.0 | MIT | https://registry.npmjs.org/@angular/core/22.2.0 — `engines.node >=26.0.0` covers Node 26 (DECISIONS §4.2) |
| @angular/cli | 22.2.0 | MIT | https://registry.npmjs.org/@angular/cli/22.2.0 (DECISIONS §4.2) |
| @angular/build | 22.2.0 | MIT | https://registry.npmjs.org/@angular/build/22.2.0 — peer `typescript >=6.0 <6.1` (DECISIONS §4.2) |
| @angular/cdk | 22.2.0 | MIT | https://registry.npmjs.org/@angular/cdk/22.2.0 (DECISIONS §4.2) |
| @angular/localize | 22.2.0 | MIT | https://registry.npmjs.org/@angular/localize/22.2.0 — first-party i18n, vi + en builds (DECISIONS §4.2) |
| @ngrx/signals | 22.0.1 | MIT | https://registry.npmjs.org/@ngrx/signals/22.0.1 — SignalStore for feature state (DECISIONS §4.2) |
| angular-eslint | 22.5.0 | MIT | https://registry.npmjs.org/angular-eslint/22.5.0 (DECISIONS §4.2) |
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
