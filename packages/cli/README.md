# @evidgate/cli

The `evidgate` command-line client (04.2.1–04.2.4). It exchanges a CI OIDC
identity for a short-lived producer token, uploads a run manifest and renders a
CI recipe. The process-level contract — exit codes, configuration, token
sources, error codes and output rules — is
[`contracts/cli.md`](../../contracts/cli.md).

## Install

The package is not published yet (04.9.2-T1); until then run it from the
workspace build. Once published, pin the exact version:

```sh
npx --yes --package @evidgate/cli@<version> -- evidgate --help
```

## Configuration

- `EVIDGATE_API_URL` — the API root **without** `/v1` (the client appends it).
- `EVIDGATE_PROJECT` — the project slug the command acts on.
- `EVIDGATE_SPOOL_DIR` — the spool root `events send`, `runs finalize` and
  `artifacts upload` read (required by the two spool commands only; optional for
  the upload, which then has no resume state).
- `EVIDGATE_UPLOAD_DEADLINE` — one deadline for a whole `artifacts upload`, as
  seconds or with an `s`/`m`/`h` suffix; default 30 minutes, ceiling 24 hours.

Both of the first two have `--api-url` / `--project` flag equivalents; an
explicit flag wins over the environment (`contracts/cli.md` §3).

## Artifacts upload

```sh
evidgate artifacts upload --run <run_id> --file test-results/trace.zip \
  --kind trace --execution-key <64-hex> [--producer shard-1] [--attempt 1]
```

One file per invocation. `--execution-key` is **required**: the 64-hex digest of
the planned execution the artifact belongs to (the manifest decides it, the CLI
cannot derive it). The file's real path must sit under `test-results/`,
`playwright-report/` or a `--dir` root — a root that is itself a symlink is
refused — and a kind's static cap is checked before the token exchange. The CLI
hashes the file, PUTs it to the presigned URL with exactly the signed headers,
then calls `complete` and verifies the byte count the server reports.

A rejected `complete` is terminal for that artifact row: a re-run replays
`complete` and is rejected again, so fix the cause and re-run with a changed file
(a new digest mints a new row) or delete
`<spool_dir>/<run_id>/<producer_id>/uploads/<identity>-<artifact_id>.json` to start over.
Nothing is retried on a deterministic 4xx; a 5xx, `408`, `429` or transport
failure is retried inside `EVIDGATE_UPLOAD_DEADLINE`, and an expired presigned URL
re-mints at most three times in total per invocation (each dead URL costs one
mint).

## Quickstart

`evidgate quickstart` renders a CI recipe for the project:

```sh
evidgate quickstart                    # GitHub Actions workflow to stdout
evidgate quickstart --format generic   # POSIX sh script to stdout
evidgate quickstart --write .github/workflows/evidgate.yml
```

`--format` is `github` (default) or `generic`; any other value exits 3.
`--write <path>` writes the template inside the current working directory
(creating parent directories, never overwriting an existing file) and prints
`wrote <path>`.

The rendered examples are committed and kept equal to the renderer by a test:

- [GitHub Actions workflow](../../docs/quickstart/github-actions.yml)
- [generic shell script](../../docs/quickstart/generic.sh)

The GitHub template stores no long-lived secret. `EVIDGATE_API_URL` and
`EVIDGATE_EXECUTION` (the planned execution's 64-hex digest, passed to
`--execution-key`) are repository **variables**, not secrets. The workflow grants
nothing at the top level: the credential-free `test` job runs on
`pull_request`/`push`, and the `upload` job requests `id-token: write` only on
`workflow_dispatch` or a push to
the default branch, so GitHub mints a short-lived OIDC JWT; the CLI exchanges
that JWT at `POST /v1/ci/token` for a producer token that goes only to the 0600
cache and is never printed. The generic template names its credential only
(`EVIDGATE_OIDC_TOKEN`, else a masked `EVIDGATE_INGEST_TOKEN`).

Current state: the pinned `@evidgate/cli@<version>` is not installable until
04.9.2-T1 publishes the package; every step in the templates is a real command —
`runs create` (04.2.2-T2), `events send` and `runs finalize` (04.2.3-T1/T2) and
`artifacts upload` (04.2.4-T1). The GitHub template keeps the manifest ids and the
run id as step outputs and passes them through step env; the generic one captures
the `--json` output in shell variables. `artifacts upload` sends one file per
invocation from `test-results/` or `playwright-report/` (or a `--dir` root) and
verifies the size and checksum the server reports at `complete`.
