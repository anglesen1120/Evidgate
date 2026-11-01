# Runbook: Pilot-0 dogfood run

Proves the Pilot-0 exit check (`plans/features/SOLO-PATH.md` §2) on a local dev stack: the
`apps/web` Playwright e2e reports through `packages/adapter-playwright` and the CLI, the
evaluator produces a decision, and `evidgate gate` exits with it. Three runs: green → PASS (exit
0), forced failure → FAIL (exit 1), missing shard → INCONCLUSIVE or pending (exit 2).

The decision is never computed by the script; it is read from `GET /v1/runs/{r}/decision`
through `evidgate gate --json`.

## 1. Prerequisites

- Docker (colima or Docker Desktop), .NET 10 SDK, Node 26, `curl`.
- Free ports for PostgreSQL, SeaweedFS, the Api, the Worker and the web dev server
  (`.env.dev.template`). `scripts/dev/lib.sh` uses fixed container names
  (`evidgate-dev-postgres`, `evidgate-dev-seaweedfs`): if a stack of the same name already runs,
  stop it first and restore it afterwards. Never delete its volumes.
- A fresh `npm ci` and a built Release solution in the checkout.

## 2. Bring the stack up

```sh
scripts/dev/up.sh --reset --with-token
```

Copy from the output, once (the token is shown only once and is stored nowhere):

| Output line                                                         | Variable                                              |
| ------------------------------------------------------------------- | ----------------------------------------------------- |
| `EVIDGATE_DEV_TOKEN=…`                                              | `EVIDGATE_DEV_TOKEN`                                  |
| `seed-dev: manifest inputs suite_revision_id=… case_revision_id=…`  | `PILOT0_SUITE_REVISION_ID`, `PILOT0_CASE_REVISION_ID` |
| the seeded environment revision (see `docs/dev-setup.md`)           | `PILOT0_ENVIRONMENT_REVISION_ID`                      |
| the case id of the seeded sample case (the project's case list)     | `PILOT0_CASE_ID`                                      |

A rerun without `--reset` prints no token when a live one exists; reset to mint a new one.

## 3. Run

```sh
export EVIDGATE_ENV=dev
export EVIDGATE_API_URL=http://127.0.0.1:8080
export EVIDGATE_DEV_TOKEN=…            # from step 2, never on a command line
export PILOT0_SUITE_REVISION_ID=… PILOT0_CASE_REVISION_ID=… \
       PILOT0_ENVIRONMENT_REVISION_ID=… PILOT0_CASE_ID=…
scripts/dev/pilot0-dogfood.sh
```

The script refuses a non-loopback API URL, keeps the token cache and all spools in a temporary
directory it removes on exit, never prints the token, and exits non-zero on any mismatch.

What it does per run: build the manifest (execution key computed with the server's
`ExecutionKey` formula and handed to the reporter through its identity resolver), `manifests
upload`, `runs create`, run the Playwright smoke test through the reporter, `events send`, `runs
finalize`, then `evidgate gate --wait`. Run B injects a failing spec from the temporary
directory only; run C declares two shards and finalizes only shard 1.

## 4. Expected output

One line per run and a final table `run | gate exit | expected | decision | completeness |
missing`. Exit 0 only when the three gate exits are 0, 1 and 2.

| Run | Gate exit | status    | decision        | completeness | missing           |
| --- | --------- | --------- | --------------- | ------------ | ----------------- |
| A   | 0         | evaluated | PASS            | complete     | `[]`              |
| B   | 1         | evaluated | FAIL            | complete     | `[]`              |
| C   | 2         | see §5    | INCONCLUSIVE or pending | incomplete | includes `missing_shard` |

## 5. Observed result

Filled in after the live run (run ids shown as 8-character prefixes).

## 6. Web view against the live API

Filled in after the live run (screenshots, vi + en, light).

## 7. Troubleshooting

| Symptom                                                       | Cause and action                                                                                                  |
| ------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------- |
| `missing required environment: …`                             | One of the five variables of §3 is unset. The script names each.                                                  |
| `dev_token_refused`                                           | `EVIDGATE_ENV` is not `dev` or the API URL is not loopback (`contracts/cli.md` §4).                               |
| `events send` exits 3 `unknown_execution_key`                 | A run-level event carries the adapter's placeholder key; only planned execution keys are accepted.                |
| `evidgate gate` exits 3 with 403                              | The CI token lacks `decision:read`; the role gains it with the decision route.                                    |
| Port already in use / container name conflict                 | See §1: another stack holds the port or the fixed container name.                                                 |
| Run C exits 3 instead of 2                                    | The decision request failed rather than answered `pending`; read the stderr `error.code`.                         |
