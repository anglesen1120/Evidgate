# Fork-PR fixture: recorded outputs

Recorded 2026-11-10 for the fork-PR credential-reachability proof described in
[`../customer-ci-recipe.md`](../customer-ci-recipe.md) §12. The code under test is
unchanged between runs; the commands are the procedure of
[`fixtures/fork-pr-repo/README.md`](../../../fixtures/fork-pr-repo/README.md).

## AC2: a fork-context run with no credential produces a normal report

Command (repository root, macOS, Node v26.10.0, `@playwright/test` 1.64.1):

```sh
cd fixtures/fork-pr-repo && ./run-fork-context.sh
```

Output:

```text
run-fork-context: 3 passed with 7 environment names and no EVIDGATE_* variable; report sha256:2b462690b4291a35357fab47b4b029cf0f9c6af104c3ed82be329f1be67a6b44
```

`test-results/run-context.json` (the suite's own `process.env` names, recorded by the
suite's global setup, not by the runner):

```json
{
  "suite": { "expected": 3, "unexpected": 0, "skipped": 0, "flaky": 0 },
  "environment": {
    "recorded_by": "suite global setup (tests see these names)",
    "names": ["HOME", "OLDPWD", "PATH", "PLAYWRIGHT_TEST", "PWD", "SHLVL", "__CF_USER_TEXT_ENCODING"],
    "evidgate_vars": [],
    "secret_vars": []
  },
  "artifacts": {
    "report": { "path": "test-results/report.json", "sha256": "sha256:2b462690b4291a35357fab47b4b029cf0f9c6af104c3ed82be329f1be67a6b44", "bytes": 6643 },
    "html_report": { "path": "playwright-report/index.html", "sha256": "sha256:6bf8c28a0b04497b0dd8d21aee8429bc84265c08c4ffb105e585a9a72fbd0413", "bytes": 518864 }
  }
}
```

Negative controls of the runner (`bash fixtures/fork-pr-repo/run-fork-context.test.sh`):
`run-fork-context.test: all negative controls hold` (a caller-held `EVIDGATE_API_TOKEN` is not
inherited; a suite that reports an Evidgate variable is refused; an all-skipped suite is
refused).

## AC3 and AC4: the refusals on the real API

Run on the LAN host (real PostgreSQL via Testcontainers), head `ee5089862a32`, build
`0 Warning(s) 0 Error(s)`:

```text
dotnet test (Identity.IntegrationTests) Test run summary: Passed!  total: 16  failed: 0  succeeded: 16  skipped: 0
dotnet test (Tenancy.IntegrationTests)  Test run summary: Passed!  total: 35  failed: 0  succeeded: 35  skipped: 0
[lan] slice summary: 2 project(s), 3 class(es), repeats=1, wall=89s rc=0
[lan] GATE_RC=0 sha ee5089862a32 run lan-fork-pr-fixture-1794274250-2198
```

The 16 Identity tests are `ForkContextCredentialReachabilityTests` and
`CiProducerScopeTests`; the 35 Tenancy tests are `CiAuthTests`.

| Criterion | Test (`tests/Identity.IntegrationTests/ForkContextCredentialReachabilityTests.cs`) | Exact outcome pinned |
|---|---|---|
| AC3 | `IngestToken_AsBearer_OnManifestUpload_Is401Unauthenticated` (own and another project) | `401` `unauthenticated`; no credential row written; the same secret still exchanges (control) |
| AC3 | `RevokedIngestToken_AsBearer_Is401Unauthenticated` | `401` `unauthenticated` |
| AC3 | `Manifest_WithoutACredential_Is401Unauthenticated` | `401` `unauthenticated`; no rows |
| AC4 | `MintedCiProducer_OnTokenCreation_Is403Forbidden` (project A and project B) | `403` `forbidden` on `POST /v1/projects/{p}/ingest-tokens` and `/api-tokens`; no credential row; admin control `200`, producer on the manifest route neither `401`/`403` nor `5xx` |
| AC4 | `MintedCiProducer_OnForeignOrgMembership_Is404NotFound` | `404` `not_found` (org mismatch resolves before the permission gate); own-org admin invite `201` (control); own-org producer denial is `CiProducerScopeTests` `403` |
| AC4 | `MintedCiProducer_Lifetime_IsAtMostOneHour_AndNotExpired`, `ExpiredMintedCiProducer_Is401Unauthenticated` | `exp - iat <= 3600`; `401` past `exp` |

## Open: the `auth_failure` security event

AC3 also asks for a `security_event` row of type `auth_failure` on the refused use. No
production code writes one (`docs/security/alert-rules.md` §3;
`SecurityEventEmitterCoverageTests` lists `auth_failure` as a known gap; owner 02.4.1-T1 /
OI-02-31). This proof records the status only and does not pin the absence of the row.
