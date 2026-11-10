# Fork-PR credential-reachability fixture

An example repository for the claim that a fork pull request cannot reach an Evidgate credential. It is not a `<domain>/<name>/` fixture corpus: it holds no `meta.json` and no `input.*` file, so the repository's fixture loaders skip this directory.

- `package.json`, `playwright.config.ts`, `src/`, `tests/` — the example suite: three browser-free Playwright tests over plain TypeScript.
- `run-fork-context.sh` — runs the suite under `env -i` and fails unless a report was produced.
- `.github/workflows/fork-safe.yml` — the untrusted side: `pull_request` and `push`, no Evidgate setting and no secret.
- `.github/workflows/maintainer-upload.yml` — the trusted side: dispatch or push to `main`, secretless environment, a short-lived project-scoped `ci_producer` token.

## What a run records

`./run-fork-context.sh` starts the suite through `env -i` with a two-name allow-list (`PATH`, `HOME`). It then reads the suite's environment back and refuses the run when any `EVIDGATE_*` variable or any secret-like name (`TOKEN`, `SECRET`, `PASSWORD`, `CREDENTIAL`, `API_KEY`, `PRIVATE_KEY`) is present. Nothing from the caller's shell can reach the suite, so the "no credential" half is executed rather than asserted.

A successful run leaves three files:

- `test-results/report.json` — the Playwright JSON report (counts per status, suites, durations).
- `playwright-report/index.html` — the HTML report; both reports are what the workflows upload.
- `test-results/run-context.json` — the observed evidence: the environment allow-list, `evidgate_vars: 0`, `secret_vars: 0`, the suite counts and the report digests.

`run-context.json` uses the fixture-local schema `fork-pr-fixture/1`. The script exits non-zero when the suite fails, when either report is missing, when the report records an unexpected result, or when the environment check fails.

## Run it locally

```sh
cd fixtures/fork-pr-repo
./run-fork-context.sh
```

In an Evidgate checkout the root `npm ci` already provides the pinned `@playwright/test`. A standalone copy of this directory needs `npm install` first: one pinned devDependency, no lockfile, no browser download. The script exits 2 when the package is missing.

## The two workflows

`fork-safe.yml` runs on `pull_request` (including a fork's) and on a push to `main`. The workflow grants no permission at workflow level and `contents: read` to the only job, which checks out with `persist-credentials: false`, installs the one pinned package, runs `./run-fork-context.sh`, and uploads `fork-pr-report` as artifact data: the two reports plus the customer's `evidgate-manifest.json` when their own tooling writes it at the repository root. It holds no Evidgate setting, no secret and no token. A fork's copy of this workflow runs the same steps; GitHub withholds every repository variable and secret from it, so the job still produces a report.

`maintainer-upload.yml` never runs for a pull request. A new report reaches Evidgate two ways:

- on `workflow_dispatch` the maintainer names the tested run id; its artifact is downloaded into `test-results/` and `playwright-report/` as data, never executed;
- on a push to `main`, the same job runs the suite itself, because the fork-safe workflow is a separate run whose artifact cannot be depended on across workflows.

The upload job runs in the secretless environment `evidgate-upload` and elevates for itself: `contents: read`, `actions: read` (another run's artifact) and `id-token: write` (the exchange). It mints the token with `evidgate auth login --github-env`, which prints `::add-mask::` and appends `EVIDGATE_CI_TOKEN` to `$GITHUB_ENV`; no credential is passed as an argument. The token is a `ci_producer` token scoped to one project, valid for at most 60 minutes and never refreshed (`contracts/cli.md` §7). The last step uploads the manifest (`§8`), creates the run for it with `evidgate runs create` (`§9`), and uploads the HTML report against that run (`§12`).

Both workflows carry the same pinned action SHAs as [`../ci-recipes/github-fork-safe.yml`](../ci-recipes/github-fork-safe.yml) and can be checked with the same lint:

```sh
scripts/lint-ci-recipe.sh fixtures/fork-pr-repo/.github/workflows/*.yml
```

## Repository setup (once)

1. Bind the repository to the project (`POST /v1/projects/{p}/ci-bindings`) with the repository, the allowed ref `refs/heads/main` and the allowed workflow ref of this workflow file on `main`; the header of the published recipe shows the same setup.
2. Create the environment `evidgate-upload` with **no secrets**, required reviewers, and deployment branches limited to `main`.
3. Set the repository variables `EVIDGATE_API_URL` and `EVIDGATE_EXECUTION` (the 64-hex digest of the planned execution). Add `EVIDGATE_BINDING_CHALLENGE` only until the first exchange has activated the binding, then delete it.
4. Provide the manifest your tooling produces at `evidgate-manifest.json` in the tested run's workspace, so the artifact carries it (this fixture does not synthesise one, so its upload steps need a manifest from your own tooling); on a dispatch, pass the artifact-relative path to the `manifest_path` input.

When the binding allowlist does not cover a context, the exchange is refused with `403 forbidden`; that refusal is why the untrusted job never holds a token.

## Negative proofs, and the security-event row that is still open

The fixture records the positive half in full: a run with no Evidgate variable at all produces a normal report (AC2). The negative half has two parts:

- **Refusal.** A credential use from a fork context is refused with `403 forbidden`; that status is the subject of the identity integration test `tests/Identity.IntegrationTests/ForkContextCredentialReachabilityTests.cs` (AC3/AC4). A fork pull request cannot obtain a tenant-level credential either: Evidgate has no tenant-level CI credential, and both grants at `POST /v1/ci/token` are project-scoped.
- **Logging.** The `security_event` row of type `auth_failure` that the refusal should write is **not** produced today: no production emitter exists for that type yet (`docs/security/alert-rules.md` §3). That part of AC3 is open with owner 02.4.1-T1 and is not pinned by this fixture.

The recorded outputs for AC2, AC3 and AC4 live in `docs/security/evidence/fork-pr-fixture-outputs.md`, one section per criterion.

## Limits

- The suite is browser-free and network-free by design; a real repository replaces `src/` and `tests/` with its own suite and keeps the runner and the two workflows.
- The GitHub runs are archived evidence for the story, not part of this repository's gates; only `run-fork-context.sh` runs anywhere automatically.
- The manifest the maintainer workflow uploads is the customer's own planning artifact; the fixture does not synthesise one.
