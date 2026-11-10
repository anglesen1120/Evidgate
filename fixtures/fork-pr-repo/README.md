# Fork-PR credential-reachability fixture

An example repository for the claim that a fork pull request cannot reach an Evidgate credential. It is not a `<domain>/<name>/` fixture corpus: it holds no `meta.json` and no `input.*` file, so the repository's fixture loaders skip this directory.

- `package.json`, `package-lock.json`, `playwright.config.ts`, `suite-env.global-setup.ts`, `src/`, `tests/` — the example suite: three browser-free Playwright tests over plain TypeScript, and a global setup that records the environment names the suite receives.
- `run-fork-context.sh` — runs the suite under `env -i` and fails unless it produced a non-skipped report from an environment holding no Evidgate variable; `run-fork-context.test.sh` holds the negative controls.
- `.github/workflows/fork-safe.yml` — the untrusted side: `pull_request` and `push`, no Evidgate setting and no secret.
- `.github/workflows/maintainer-upload.yml` — the trusted side: dispatch or push to `main`, an environment with no stored secret (the job can still mint a project-scoped `ci_producer` token, whose lifetime is capped at 60 minutes by option validation).

## What a run records

`./run-fork-context.sh` starts the suite through `env -i` with `PATH` and `HOME` only, so the caller's variables are not inherited. The suite's own global setup records `Object.keys(process.env)` to `test-results/suite-env.json`, and the runner reads that file and refuses the run when it holds any `EVIDGATE_*` variable or any secret-like name (`TOKEN`, `SECRET`, `PASSWORD`, `CREDENTIAL`, `API_KEY`, `PRIVATE_KEY`). The check therefore reads what the tests saw, not the environment of a helper process started next to them, so the "no credential" half is executed rather than asserted.

A successful run leaves four files:

- `test-results/report.json` — the Playwright JSON report (counts per status, suites, durations).
- `playwright-report/index.html` — the HTML report; both reports are what the workflows upload.
- `test-results/suite-env.json` — the environment names the suite process received, written by its global setup.
- `test-results/run-context.json` — the observed evidence: those names, `evidgate_vars: []`, `secret_vars: []`, the suite counts and the report digests.

`run-context.json` uses the fixture-local schema `fork-pr-fixture/1`. The script exits non-zero when the suite fails, when either report is missing, when the report records an unexpected or a skipped result, when it records no test at all, or when the environment check fails.

## Run it locally

```sh
cd fixtures/fork-pr-repo
./run-fork-context.sh
```

In an Evidgate checkout the root `npm ci` already provides the pinned `@playwright/test`, which Node resolves from this directory. A standalone copy of this directory installs it from the tracked lockfile with `npm ci --ignore-scripts`: one pinned devDependency, no browser download, and no lifecycle script runs. The script exits 2 when the package is missing.

The runner's negative controls run against the same suite:

```sh
cd fixtures/fork-pr-repo
./run-fork-context.test.sh
```

They prove that a caller-side `EVIDGATE_API_TOKEN` does not reach the suite, that the check fails when a suite does report an Evidgate variable, and that an all-skipped suite is refused.

## The two workflows

`fork-safe.yml` runs on `pull_request` (including a fork's) and on a push to `main`. The workflow grants no permission at workflow level and `contents: read` to the only job, which checks out with `persist-credentials: false`, installs the one pinned package, runs `./run-fork-context.sh`, and uploads `fork-pr-report` as artifact data: the two reports plus the customer's `evidgate-manifest.json` when their own tooling writes it at the repository root. It holds no Evidgate setting, no secret and no token. A fork's copy of this workflow runs the same steps; GitHub withholds every repository variable and secret from it, so the job still produces a report.

`maintainer-upload.yml` never runs for a pull request. A new report reaches Evidgate two ways:

- on `workflow_dispatch` the maintainer names the tested run id; its artifact is downloaded into its own directory under `$RUNNER_TEMP`, outside the workspace, and only the expected regular files are copied out of it — the HTML report and the manifest. Nothing in the artifact is executed, and no artifact file sits on the path the CLI is resolved from;
- on a push to `main`, the same job runs the suite itself, because the fork-safe workflow is a separate run whose artifact cannot be depended on across workflows.

The upload job runs in the `evidgate-upload` environment, which holds no stored secret, and elevates for itself: `contents: read`, `actions: read` (another run's artifact) and `id-token: write` (the exchange). The environment is not a sandbox: because the job can mint a token, a compromised step could exfiltrate it until it expires, and required reviewers plus the main-only deployment branch rule bound who starts the job at all. The pinned CLI is installed with `--ignore-scripts` into `$RUNNER_TEMP` before the artifact is downloaded, and every later step calls it by absolute path, so no downloaded file can be resolved as the CLI or as one of its dependencies. It mints the token with `evidgate auth login --github-env`, which prints `::add-mask::` and appends `EVIDGATE_CI_TOKEN` to `$GITHUB_ENV`; no credential is passed as an argument. The token is a `ci_producer` token scoped to one project, valid for at most 60 minutes and never refreshed (`contracts/cli.md` §7). The last step uploads the manifest (`§8`), creates the run for it with `evidgate runs create` (`§9`), and uploads the HTML report against that run (`§12`).

Both workflows carry the same pinned action SHAs as [`../ci-recipes/github-fork-safe.yml`](../ci-recipes/github-fork-safe.yml) and can be checked with the same lint:

```sh
scripts/lint-ci-recipe.sh fixtures/fork-pr-repo/.github/workflows/*.yml
```

## Repository setup (once)

1. Bind the repository to the project (`POST /v1/projects/{p}/ci-bindings`) with the repository, the allowed ref `refs/heads/main` and the allowed workflow ref of this workflow file on `main`; the header of the published recipe shows the same setup.
2. Create the environment `evidgate-upload` with **no secrets**, required reviewers, and deployment branches limited to `main`.
3. Set the repository variables `EVIDGATE_API_URL` and `EVIDGATE_EXECUTION` (the 64-hex digest of the planned execution). Add `EVIDGATE_BINDING_CHALLENGE` only until the first exchange has activated the binding, then delete it.
4. Provide the manifest your tooling produces at `evidgate-manifest.json` in the tested run's workspace, so the artifact carries it (this fixture does not synthesise one, so its upload steps need a manifest from your own tooling); on a dispatch, pass the artifact-relative path to the `manifest_path` input.

When the binding allowlist does not cover a context, the exchange is refused with `403 forbidden` (`binding_mismatch`); an OIDC token whose claims fail validation, including an `event_name` outside the server's allowlist, is refused with `401 token_invalid` before any binding is read. Both refusals are why the untrusted job never holds a token.

## Negative proofs, and the security-event row that is still open

The fixture records the positive half in full: a run with no Evidgate variable at all produces a normal report (AC2). The negative half has three parts:

- **AC3 — an ingest token is never a bearer.** A real ingest token presented as `Authorization: Bearer` on the manifest route answers `401 unauthenticated` and writes no credential row, on its own project and on another project of the same org; the closing exchange in that test shows the secret itself is genuine (`ForkContextCredentialReachabilityTests.IngestToken_AsBearer_OnManifestUpload_Is401Unauthenticated`), and a revoked secret is refused the same way (`RevokedIngestToken_AsBearer_Is401Unauthenticated`). It is not `403`: the request resolves no caller at all.
- **AC4 — a minted `ci_producer` token cannot mint credentials.** The token is refused with `403 forbidden` on the ingest-token and project API-token creation routes, on its own project and on another project of the same org, and writes no credential row (`MintedCiProducer_OnTokenCreation_Is403Forbidden`, which also shows an admin still mints on that route and that the JWT authenticates on the manifest route). The org credential write is the membership invite: `403` on the token's own org (`CiProducerScopeTests.Real_Minted_CiProducer_Token_Is_403_On_Membership_Write`) and `404 not_found` when the token names another org, so it cannot probe org ids (`MintedCiProducer_OnForeignOrgMembership_Is404NotFound`). A fork pull request cannot obtain a tenant-level credential either: Evidgate has no tenant-level CI credential, and both grants at `POST /v1/ci/token` are project-scoped.
- **A refused fork exchange is not one status.** Claims that fail validation are `401 token_invalid`; an `event_name` outside the server allowlist is one such case (`GitHubOidc.cs:132-136`; `CiAuthTests.Exchange_ClaimMatrix_RefusesEveryInvalidToken`). A `ref` or `job_workflow_ref` outside the binding allowlist is `403 forbidden` with `binding_mismatch` (`ExchangeCommands.cs:88-93`; `CiAuthTests.Exchange_BindingMatching_RefusesUnboundForkedAndUnlistedWorkflows_AndMintsNothing`). An ingest token that _is_ stored in a fork-triggered workflow would still be exchangeable at `POST /v1/ci/token`, so the control is never storing one there (recipe §5).

The `security_event` row of type `auth_failure` that a refusal should write is **not** produced today: no production emitter exists for that type yet (`docs/security/alert-rules.md` §3). That part of AC3 is open with owner 02.4.1-T1, is not pinned by this fixture, and no test here asserts on `security_event` rows.

The recorded outputs for AC2, AC3 and AC4 live in `docs/security/evidence/fork-pr-fixture-outputs.md`, one section per criterion.

## Limits

- The suite is browser-free and network-free by design; a real repository replaces `src/` and `tests/` with its own suite and keeps the runner and the two workflows.
- The GitHub runs are archived evidence for the story, not part of this repository's gates. The two workflows are linted by hand with `scripts/lint-ci-recipe.sh fixtures/fork-pr-repo/.github/workflows/*.yml`; wiring them into the `check-ci-recipe` stage is an open item. `run-fork-context.test.sh` runs locally only; `run-fork-context.sh` also runs inside the maintainer workflow, on a push to `main` before the staging step.
- The manifest the maintainer workflow uploads is the customer's own planning artifact; the fixture does not synthesise one.
