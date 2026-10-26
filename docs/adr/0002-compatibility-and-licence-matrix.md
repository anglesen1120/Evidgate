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
| OpenTelemetry.Instrumentation.Http | 1.19.0 | nuspec dep groups incl. net10.0 | Apache-2.0 | https://api.nuget.org/v3-flatcontainer/opentelemetry.instrumentation.http/1.19.0/opentelemetry.instrumentation.http.nuspec — outbound `HttpClient` client spans, so the current activity's W3C `traceparent` travels with every call (10.3.1-T1.1) |
| OpenTelemetry.Exporter.OpenTelemetryProtocol | 1.19.1 | nuspec dep groups incl. net10.0 | Apache-2.0 | https://api.nuget.org/v3-flatcontainer/opentelemetry.exporter.opentelemetryprotocol/1.19.1/opentelemetry.exporter.opentelemetryprotocol.nuspec — OTLP egress to the collector (plan 10 §3 decision) |
| OpenTelemetry.Exporter.Prometheus.AspNetCore | 1.19.1-beta.1 | nuspec dep groups incl. net10.0 | Apache-2.0 | https://api.nuget.org/v3-flatcontainer/opentelemetry.exporter.prometheus.aspnetcore/1.19.1-beta.1/opentelemetry.exporter.prometheus.aspnetcore.nuspec — `GET /metrics` scrape endpoint (00 §9.1, 01.1.2 §5). [WARN] pre-release: no stable version exists on nuget.org (every listed version is `-beta`, checked 2026-09-26); revisit when a stable release ships or if plan 10 drops `/metrics` for OTLP-only |
| OpenTelemetry.Exporter.InMemory | 1.19.1 | nuspec dep groups incl. net10.0 | Apache-2.0 | https://api.nuget.org/v3-flatcontainer/opentelemetry.exporter.inmemory/1.19.1/opentelemetry.exporter.inmemory.nuspec — test-only span capture (01.1.2 AC6) |

npm packages (direct dependencies/devDependencies, root + workspaces):

| Package | Version | Engines evidence | Licence | Evidence |
|---|---|---|---|---|
| typescript | 6.0.3 | `engines.node >=14.17`; pinned exact for `>=26 <27` repo engines | Apache-2.0 | https://registry.npmjs.org/typescript/6.0.3 |
| eslint | 10.11.0 | `engines.node ^20.19.0 \|\| ^22.13.0 \|\| >=24` | MIT | https://registry.npmjs.org/eslint/10.11.0 — web/package lint (01.2.1-T1, DECISIONS §4.2) |
| @eslint/js | 10.0.1 | `engines.node ^20.19.0 \|\| ^22.13.0 \|\| >=24`; peer `eslint ^10.0.0` | MIT | https://registry.npmjs.org/@eslint/js/10.0.1 |
| @eslint/css | 2.0.1 | `engines.node ^20.19.0 \|\| ^22.13.0 \|\| >=24` (covers the repo's Node 26); no `peerDependencies` (eslint appears only as its own devDependency `^10.4.0`, so it consumes the root eslint 10.11.0 as a plain runtime); ESM-only, loaded by the CommonJS `eslint.config.js` through `require('@eslint/css').default` (Node's require(esm), >=22.12); brings `@eslint/css-tree` 4.1.2 (MIT) and a nested `mdn-data` 2.37.2 (CC0-1.0) transitively | Apache-2.0 | https://registry.npmjs.org/@eslint/css/2.0.1 — the `css/css` language of the `style-tokens` lint rule that bans raw colour/spacing literals in component styles (09.1.1-T11.4, AC9) |
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
| @angular/common | 22.2.3 | `engines.node ^22.22.3 \|\| ^24.15.0 \|\| >=26.0.0`; peers `@angular/core` 22.2.3, `rxjs ^6.5.3 \|\| ^7.4.0` | MIT | https://registry.npmjs.org/@angular/common/22.2.3 — runtime dependency of `apps/web` (09.1.1-T1, DECISIONS §4.2) |
| @angular/compiler | 22.2.3 | `engines.node ^22.22.3 \|\| ^24.15.0 \|\| >=26.0.0`; no peers | MIT | https://registry.npmjs.org/@angular/compiler/22.2.3 — runtime dependency of `apps/web` (09.1.1-T1, DECISIONS §4.2) |
| @angular/compiler-cli | 22.2.3 | `engines.node ^22.22.3 \|\| ^24.15.0 \|\| >=26.0.0`; peers `@angular/compiler` 22.2.3, `typescript >=6.0 <6.1` (covers 6.0.3) | MIT | https://registry.npmjs.org/@angular/compiler-cli/22.2.3 — AOT/template type-check for `apps/web` builds (09.1.1-T1, DECISIONS §4.2) |
| @angular/platform-browser | 22.2.3 | `engines.node ^22.22.3 \|\| ^24.15.0 \|\| >=26.0.0`; peers `@angular/core` and `@angular/common` 22.2.3 | MIT | https://registry.npmjs.org/@angular/platform-browser/22.2.3 — `bootstrapApplication` for `apps/web` (09.1.1-T1, DECISIONS §4.2) |
| rxjs | 7.8.2 | no `engines` field; peer of `@angular/common` | Apache-2.0 | https://registry.npmjs.org/rxjs/7.8.2 — Angular runtime peer; `apps/web` code uses signals first (W2), rxjs only where Angular APIs require it (09.1.1-T1) |
| tslib | 2.8.1 | no `engines` field | 0BSD | https://registry.npmjs.org/tslib/2.8.1 — TypeScript helper runtime required by Angular packages (09.1.1-T1) |
| jsdom | 30.1.3 | `engines.node ^22.22.2 \|\| ^24.15.0 \|\| >=26.0.0`; optional peer `canvas` not installed | MIT | https://registry.npmjs.org/jsdom/30.1.3 — DOM environment of the `@angular/build:unit-test` Vitest runner in `apps/web` (09.1.1-T1, 01.6.1-T1 hand-over) |
| rollup | 4.65.0 | `engines.node >=18.0.0`; platform binaries arrive as optional `@rollup/rollup-*` packages (all MIT) | MIT | https://registry.npmjs.org/rollup/4.65.0 — declared by `apps/web` because `@angular/build` 22.2.3 lists `rollup ^4.0.0` as a peer; without it npm resolves the peer to the hoisted `rollup` 2.80.0 of `@stoplight/spectral-ruleset-bundler`, `npm ls` reports it invalid and `cyclonedx-npm` (`sbom` stage) fails (09.1.1-T1) |
| @hey-api/openapi-ts | 0.99.0 | `engines.node >=22.18.0` | MIT | https://registry.npmjs.org/@hey-api/openapi-ts/0.99.0 — generates `packages/sdk` from `contracts/openapi.yaml` (01.3.2-T1, DECISIONS §4.2); moved here from Planned when T1 landed it |
| @angular/cdk | 22.2.3 | peers `@angular/core`/`common`/`forms`/`platform-browser` `^22.0.0 \|\| ^23.0.0`, `rxjs`; no `engines` field | MIT | https://registry.npmjs.org/@angular/cdk/22.2.3 — `@angular/cdk/table` + `@angular/cdk/scrolling` for the shared list component (ADR-0008 rule 8, 09.1.1-T7); moved here from Planned when T7 landed it. Its runtime dependencies `parse5` ^8 (MIT) and `entities` ^8 (BSD-2-Clause) become production transitives (both allowed by `scripts/licence-policy.json`) |
| @angular/forms | 22.2.3 | exact lockstep with the other `@angular/*` packages (peers `@angular/common`/`core`/`platform-browser` 22.2.3) | MIT | https://registry.npmjs.org/@angular/forms/22.2.3 — declared by `apps/web` only because `@angular/cdk` 22.2.3 lists it as a non-optional peer, kept at the exact lockstep version so npm never resolves a newer `@angular/forms` whose peers demand a newer `@angular/common` (the ERESOLVE seen in 09.1.1-T7). No form code uses it. `@angular/forms` 22.2.3 depends on `zod` `^4.0.10` and the root `package.json` `overrides` entry `"@angular/forms": { "zod": "4.6.7" }` (MIT, https://registry.npmjs.org/zod/4.6.7) gives forms a patched copy (GHSA-mrhx-c7xc-m3w5, high, fixed in 4.6.6). It was added when `@angular/cli` 22.2.3 pinned `zod` 4.6.5; `@angular/cli` 22.2.4 (S70) pins 4.6.6, which is patched, so the override is no longer needed for the advisory and may be dropped in a later lock refresh (it is harmless meanwhile) |
| @angular/router | 22.2.3 | exact lockstep with the other `@angular/*` packages (`engines.node ^22.22.3 \|\| ^24.15.0 \|\| >=26.0.0`; peers `@angular/common`/`core`/`platform-browser` 22.2.3, `rxjs` ^6.5.3 \|\| ^7.4.0); single runtime dependency `tslib` (0BSD, already pinned) | MIT | https://registry.npmjs.org/@angular/router/22.2.3 — `provideRouter`, `CanMatchFn`/`CanActivateFn` guards and lazy `loadChildren` for the app shell (00 §5.6 W1, 09.1.1-T4). ADR-0008 line 65 closed a third-party router/data-fetching library, not Angular's own router; the spike bundle already measured the router. Owner review (non-blocking, recorded): D-S68-Q5 (orchestrator, 09.1.1-T4, S68) — a first-party package the plan's route/guard contract (plan 09.1.1 §4, §5) cannot be met without. Advisories GHSA-62vg-58rm-qff7 and GHSA-57xq-rjx2-v5xh (range `>=22.0.0 <22.2.1`, server-side-rendering denial of service) were exempted until 2026-11-30; closed in S70 by the lockstep bump of every `@angular/*` row to 22.2.3, and both exceptions were deleted from `scripts/exceptions.json`. |
| axe-core | 4.14.0 | `engines.node >=4`; no runtime dependencies | MPL-2.0 | https://registry.npmjs.org/axe-core/4.14.0 — accessibility rule engine run by the `apps/web` component specs in jsdom (AC8/AC9 a11y evidence, 09.1.1-T7). devDependency, test-time only, never bundled into `dist`; MPL-2.0 is file-level weak copyleft and unmodified, so closed-source SaaS use is unaffected; `scripts/licence-policy.json` exception keyed `ADR-0002` (same reasoning as `lightningcss`). Owner review (non-blocking, recorded): COORDINATOR-DECISION S64 / D-S64-Q2 |
| license-checker-rseidelsohn | 5.0.1 | `engines.node >=24` | BSD-3-Clause | https://registry.npmjs.org/license-checker-rseidelsohn/5.0.1 — npm licence scanner for `scripts/licence-scan.sh` (01.7.1-T2); the maintained fork: `license-checker` upstream is archived (last release 2022), a detail the plan row predates |
| @cyclonedx/cyclonedx-npm | 6.0.1 | `engines.node >=20.18.0` | Apache-2.0 | https://registry.npmjs.org/@cyclonedx/cyclonedx-npm/6.0.1 — npm CycloneDX SBOM for CI artifacts (01.7.1-T4.2) |
| @opentelemetry/api | 1.9.1 | `engines.node >=8.0.0`; no `peerDependencies`, no runtime dependencies | Apache-2.0 | https://registry.npmjs.org/@opentelemetry/api/1.9.1 — the trace API the web telemetry facade codes against (`apps/web/src/app/core/telemetry/`, 09.1.1-T9.1). Pinned 1.9.1 rather than the newest 1.10.0 because `@opentelemetry/sdk-trace` 2.12.0 and `@opentelemetry/resources` 2.12.0 peer `@opentelemetry/api >=1.3.0 <1.10.0` (verified with `npm view @opentelemetry/api versions` / `npm view @opentelemetry/sdk-trace@2.12.0 peerDependencies`, 2026-10-24) |
| @opentelemetry/sdk-trace | 2.12.0 | `engines.node ^18.19.0 \|\| >=20.6.0` (covers the repo's Node 26); peer `@opentelemetry/api >=1.3.0 <1.10.0` (satisfied by 1.9.1); depends on `@opentelemetry/core` 2.12.0, `@opentelemetry/resources` 2.12.0, `@opentelemetry/semantic-conventions` `^1.29.0` (1.43.0 installed) | Apache-2.0 | https://registry.npmjs.org/@opentelemetry/sdk-trace/2.12.0 — `TracerProvider`, `BatchSpanProcessor` and the samplers of the lazy telemetry chunk in `apps/web` (09.1.1-T9.1). Not `@opentelemetry/sdk-trace-base` 2.12.0: the registry marks it `deprecated: "Use @opentelemetry/sdk-trace package"`. The 2.x line, not 3.0.0: 3.0.0 peers `@opentelemetry/api >=1.10.0 <1.11.0` with `engines.node >=22.15.0`, its exporter release is 0.300.0 (which depends on `sdk-trace` 3.0.0), and `@opentelemetry/sdk-trace-web` — the browser package future route instrumentation would use — has no stable 3.x release (newest 2.12.0, peer `api >=1.0.0 <1.10.0`; only `3.0.0-development.0` exists). Picking 2.12.0 everywhere keeps one coherent `@opentelemetry/*` line on the one api 1.9.1 |
| @opentelemetry/resources | 2.12.0 | `engines.node ^18.19.0 \|\| >=20.6.0`; peer `@opentelemetry/api >=1.3.0 <1.10.0`; depends on `@opentelemetry/core` 2.12.0, `@opentelemetry/semantic-conventions` `^1.29.0` | Apache-2.0 | https://registry.npmjs.org/@opentelemetry/resources/2.12.0 — `resourceFromAttributes` for the `service.name=evidgate-web` resource (09.1.1-T9.1); listed as a direct dependency because the runtime imports it by name, while `sdk-trace` and the OTLP exporter also pin it transitively |
| @opentelemetry/exporter-trace-otlp-http | 0.223.0 | `engines.node ^18.19.0 \|\| >=20.6.0`; peer `@opentelemetry/api ^1.3.0`; depends on `@opentelemetry/sdk-trace` 2.12.0, `@opentelemetry/otlp-transformer` 0.223.0, `@opentelemetry/otlp-exporter-base` 0.223.0 | Apache-2.0 | https://registry.npmjs.org/@opentelemetry/exporter-trace-otlp-http/0.223.0 — `OTLPTraceExporter` POSTing spans to the OTLP/HTTP traces endpoint from `apps/web` (09.1.1-T9.1); 0.223.0 is the 0.x release that pins `sdk-trace` 2.12.0, whereas the 0.300.0 line moves to `sdk-trace` 3.0.0 (api >=1.10). Its runtime transitives (`@opentelemetry/core` 2.12.0, `otlp-transformer` 0.223.0, `otlp-exporter-base` 0.223.0, `api-logs`/`sdk-logs` 0.223.0, `sdk-metrics` 2.12.0, `semantic-conventions` 1.43.0) are all Apache-2.0, allowed by `scripts/licence-policy.json` |
| web-vitals | 6.2.3 | no `engines` field; no dependencies; ESM (`module` `dist/web-vitals.js`) plus a UMD build (`main` `dist/web-vitals.umd.cjs`) | Apache-2.0 | https://registry.npmjs.org/web-vitals/6.2.3 — `onLCP`/`onINP`/`onCLS` collection behind the `web_vital` spans (09.1.1-T9.1); imported only by the lazy telemetry chunk, never by the initial bundle |
| @angular/router | 22.2.3 | exact lockstep with the other `@angular/*` packages (peers `@angular/core`/`common`/`platform-browser` 22.2.3, `rxjs` `^6.5.3 \|\| ^7.4.0`) | MIT | https://registry.npmjs.org/@angular/router/22.2.3 — `Router.events` and `ActivatedRouteSnapshot` for the route-change spans of `core/telemetry/` (09.1.1-T9.1); the app shell routes (09.1.1-T4) use the same package |
| @evidgate/sdk | 0.0.0 | first-party workspace (private) | First-party (unpublished) | `packages/sdk` — generated from `contracts/openapi.yaml` by `@hey-api/openapi-ts` (01.3.2-T1); ships no third-party code of its own |
| @evidgate/cli | 0.0.0 | first-party workspace (private) | First-party (unpublished) | `packages/cli` — the `evidgate` CLI (04.2.1) and its Ajv 8 contract validator (01.3.2-T2); listed because `packages/test-fixtures` depends on it for the 04.7.1-T3 protocol matrix spec, and `packages/adapter-playwright` depends on it for the 04.3.3-T2 attachment upload facade (`createAttachmentUploader`, which wraps `uploadFile()`) — one lazy dynamic `import()` in `src/attach.ts`, type-only imports elsewhere, and never the reverse direction; its third-party dependencies have their own rows |
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
| @angular/core | 22.2.3 | MIT | https://registry.npmjs.org/@angular/core/22.2.3 — `engines.node >=26.0.0` covers Node 26 (DECISIONS §4.2) |
| @angular/cli | 22.2.4 | MIT | https://registry.npmjs.org/@angular/cli/22.2.4 (DECISIONS §4.2) |
| @angular/build | 22.2.3 | MIT | https://registry.npmjs.org/@angular/build/22.2.3 — peer `typescript >=6.0 <6.1` (DECISIONS §4.2) |
| @angular/localize | 22.2.3 | MIT | https://registry.npmjs.org/@angular/localize/22.2.3 — first-party i18n, vi + en builds (DECISIONS §4.2) |
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
