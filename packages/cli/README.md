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

Both have `--api-url` / `--project` flag equivalents; an explicit flag wins over
the environment (`contracts/cli.md` §3).

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

The GitHub template stores no long-lived secret. `EVIDGATE_API_URL` is a
repository **variable**, not a secret, and the job requests `id-token: write` so
GitHub mints a short-lived OIDC JWT; the CLI exchanges that JWT at
`POST /v1/ci/token` for a producer token that goes only to the 0600 cache and is
never printed. The generic template names its credential only
(`EVIDGATE_OIDC_TOKEN`, else a masked `EVIDGATE_INGEST_TOKEN`).

Current state: the pinned `@evidgate/cli@<version>` is not installable until
04.9.2-T1 publishes the package, and the steps for `runs create` (04.2.2-T2),
events and `runs finalize` (04.2.3-T1/T2) and `artifacts upload` (04.2.4-T1)
appear in the templates as comments naming their owning task.
