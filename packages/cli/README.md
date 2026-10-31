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

## Contract schemas

The spool commands validate every envelope against the same JSON Schemas the
server uses, and the package cannot read `contracts/` at runtime: the texts are
embedded into `src/contracts.generated.ts` by `scripts/embed-contracts.mjs`
(`npm run embed:contracts -w @evidgate/cli`, also part of
`npm run build -w @evidgate/cli`, but not the root `npm run build`).
The committed module is generated and guarded by the parity test
`src/packagedSchemas.test.ts`. After editing `contracts/schemas` (or another
embedded contract directory), run `npm run build -w @evidgate/cli` to
regenerate it rather than editing the module.

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

## Gate

`evidgate gate` reads one run's decision and turns it into the exit code a CI step
branches on:

```sh
evidgate gate --run <run_id>                 # one poll
evidgate gate --run <run_id> --wait 5m       # poll until the run decides
evidgate gate --run <run_id> --json          # one JSON object line on stdout
```

Only a literal `PASS` exits 0:

- `PASS` → 0;
- `FAIL` → 1;
- `INCONCLUSIVE` → 2;
- `decision: null` (still pending) → 2, both without `--wait` and when the wait
  runs out.

Everything else exits 3 with `evidgate: error <code>`: a transport failure, a
`401` that a freshly minted token does not fix (one refresh is attempted), a
`403` or `404`, a `5xx`/`429` that still fails after the bounded retries, a
refused redirect, a decision body larger than 256 KiB, a body that is not JSON, a
body that is not an object, a body without a `decision` member, and any
`decision` value outside the four above (`"pass"`, `""`, a number).

`--wait` takes the same grammar as `EVIDGATE_SEND_DEADLINE`: bare seconds or an
`s`/`m`/`h` suffix, greater than zero and at most 24 hours; anything else exits 3
before any request. It polls with an exponential backoff and full jitter (1 s
floor, 10 s cap) and honours `Retry-After` on `429`/`503` up to the same 60 s
ceiling the rest of the client uses, so a server-named wait can be longer than
the backoff cap. Neither ever sleeps past the deadline, and the last poll starts
on it, so a decision that lands in the final moment still counts. Without
`--wait` the command makes one poll; the poll's own request may be replayed (up
to 3 attempts) for a retryable answer.

A server that answers only after the deadline cannot decide the run: no request is
granted more time than the wait has left, its abort is a transport failure, and the
command exits 3 — never 0. A token refresh keeps the same deadline, so a slow
refresh cannot buy the replay a fresh window. The last poll is the exception: it
starts on the deadline with the 1 ms minimum budget, so a decision that is already
waiting there still counts. When that last request is instead cut off before it
answers, the abort is not a verdict: a run that answered pending earlier exits 2,
and only a run that never answered at all exits 3.

Human mode prints `gate: <DECISION> (run <run_id>)` and one `  - <reason>` line per
reason; a pending or timed-out run explains itself on stderr as
`evidgate: gate: run <run_id> …`. Server text is
redacted, control characters are escaped, each reason line is capped at 200
characters, and at most 50 reasons are printed (the rest collapse into one
`… (k more)` line), so one response cannot flood a CI log. `--json` prints
exactly one object line on stdout, with these keys in this order:

```json
{
  "decision": "PASS",
  "completeness": null,
  "evaluation_id": null,
  "mode": null,
  "url": null
}
```

`completeness`, `evaluation_id`, `mode` and `url` are **fields pending contract**:
`contracts/openapi.yaml` declares only `decision`, `evaluation_revision` and
`reasons` today, so each of the four is printed when the response carries it as a
string and is `null` otherwise. Nothing is derived from the API URL and no value is
guessed. `decision` is `null` on a pending timeout. The run id must be a UUID; any
other value exits 3 before a token exchange.
