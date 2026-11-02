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
  (`.env.dev.template`). `scripts/dev/lib.sh` fixes the container names
  (`evidgate-dev-postgres`, `evidgate-dev-seaweedfs`) and `infra/compose.dev.yml` fixes the
  compose project `evidgate-dev`, so a second stack cannot run beside the first (§2).
- A fresh `npm ci` and a built Release solution in the checkout.

## 2. Bring the stack up

`up.sh --reset` runs `docker compose down -v` on the compose project (default `evidgate-dev`)
and deletes the `pg-data`/`s3-data` volumes, so use it only on a throwaway stack. Where a dev
stack you care about lives, run `scripts/dev/down.sh` instead (it keeps the volumes).

With an existing dev stack, stop it (`scripts/dev/down.sh`), then run the dogfood under its own
compose project so the two keep separate volumes:

```sh
COMPOSE_PROJECT_NAME=evidgate-pilot0 scripts/dev/up.sh --with-token
```

The container names `evidgate-dev-postgres` and `evidgate-dev-seaweedfs` are fixed in
`infra/compose.dev.yml` and ignore the project name, so the other stack's containers must be
stopped (renamed aside) first. DB port 5432 may be taken by a host Postgres: set
`EVIDGATE_DB_PORT` and the `Port=` values of the connection strings in `.env.dev` (e.g. 5433),
as `docs/dev-setup.md` explains. Stop this stack the way it was started:
`COMPOSE_PROJECT_NAME=evidgate-pilot0 scripts/dev/down.sh`.

Before the first `--with-token`, `.env.dev` needs two values that `.env.dev.template` does not
ship:

```sh
EVIDGATE__CI__PEPPER=$(openssl rand -base64 32)
EVIDGATE__CI__SIGNINGKEY=$(openssl rand -base64 48)
```

Without the signing key `/v1/ci/token` answers 503 `CI authentication is not configured`, and
the script fails at manifest upload. Never print either value.

Copy from the output, once (the token is shown only once and is stored nowhere):

| Output line                                                         | Variable                                              |
| ------------------------------------------------------------------- | ----------------------------------------------------- |
| `EVIDGATE_DEV_TOKEN=…`                                              | `EVIDGATE_DEV_TOKEN`                                  |
| `seed-dev: manifest inputs suite_revision_id=… case_revision_id=…`  | `PILOT0_SUITE_REVISION_ID`, `PILOT0_CASE_REVISION_ID` |
| `seed-dev: manifest inputs environment_revision_id=… environment_key=dev` | `PILOT0_ENVIRONMENT_REVISION_ID`                 |
| the case id of the seeded sample case (see below)                   | `PILOT0_CASE_ID`                                      |

`PILOT0_CASE_ID` cannot be read from a list: the dev user has no login, and the dev token
cannot read the catalog. Read the one seeded case from the throwaway dev database directly:

```sh
docker exec evidgate-dev-postgres psql -U postgres -d evidgate -At -c \
  "select id from test_case limit 1"
```

The container's superuser is fine here: it is the local throwaway dev database, with no
customer data.

A rerun without `--reset` prints no token when a live `dev-seed` token already exists; its
secret was shown once and cannot be shown again. `up.sh` suggests `--reset` to mint another,
which deletes the volumes, so do that on a throwaway stack only.

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
| C   | 2         | pending   | none (the run stays pending) | none | none |

## 5. Observed result

Live run on 2026-11-02 against `develop` after #390 (decision route), throwaway dev stack from `scripts/dev/up.sh --reset --with-token`, local Playwright chromium, dev token with scopes `artifact:write`, `decision:read`, `event:write`, `run:create`. Run ids are shortened to the first 13 characters.

Landed in #392 (merge a7c6d7c1).

| Run | run id     | what ran                                                    | gate exit | status    | decision | completeness | outcome      | missing |
| --- | ---------- | ----------------------------------------------------------- | --------- | --------- | -------- | ------------ | ------------ | ------- |
| A   | `01a199a1-0361` | apps/web `smoke.spec.ts` through the reporter and the CLI   | 0         | evaluated | PASS     | complete     | all_passed   | `[]`    |
| B   | `01a199a1-17e9` | one throwaway forced-failure spec (never committed)         | 1         | evaluated | FAIL     | complete     | has_failures | `[]`    |
| C   | `01a199a1-2970` | two-shard manifest, only shard 1 finalized                  | 2         | pending   | none     | none         | none         | none    |

Only the apps/web `smoke.spec.ts` (the vi boot test) runs through the reporter: the seeded
catalog holds exactly one case, so a planned execution exists for exactly one test. The other
apps/web specs (csp, shell, telemetry, static-server) have no planned execution and their
results would be refused.

Run C is not INCONCLUSIVE: the run is still collecting, so the gate reports `still pending after 20s` and exits 2, the contract's code for INCONCLUSIVE or pending (`contracts/cli.md` §2). The INCONCLUSIVE path (a deadline that closes the run with a missing shard) was not exercised live.

Gaps the run found, both fixed in the same PR:

- `seed-dev --with-token` created the sample policy as a draft and never activated it. Runs then pinned no policy, `orchestrator.evidence.revision_frozen` failed on every retry (`run pins no policy revision`) and the decision route stayed pending. The seed now activates it (advisory).
- The sample policy required the suites `smoke` and `regression`, which the seeded catalog does not contain (its suite slug is `dev-seed-smoke`), so every decision was INCONCLUSIVE `missing_result`. The sample now requires `dev-seed-smoke`.

Two script-side workarounds remain (carried, see the PR): the reporter stamps its run-level markers with a placeholder execution key, so the script rewrites that key in the spool before `events send`; and nothing yet builds a `manifest.v1` for a customer project, so the script builds it.

## 6. Web view against the live API

Pilot-0 exit check (e): **sign-in met, shell blocked on 09.1.1-T4.2 (S190)**. The screenshots below
used a local front door that injects `X-Evidgate-Org`; the app itself does not send it yet.

Exercised on 2026-11-02 against the stack of §2 on branch `feature/dev-login-s187`: the
development bundle of `apps/web`, both locales, against the real API — no fixture
backend, no mock decision. What it takes beyond §2:

1. The dev-login pair **and** a session pepper in `.env.dev` ("Signing in to the web app
   against the real API" in `docs/dev-setup.md`): without `EVIDGATE__SESSION__PEPPER` a
   correct secret answers `503 unavailable`, because `session_create` stores the cookie's
   HMAC and no session row can be written without the pepper.
2. Both locales, from the `development` configuration, so the review routes and the
   dev sign-in component are present: `npx ng build --configuration development
   --localize` (`apps/web` is the CLI workspace root) writes `en/` and `vi/` under
   `dist/browser`, each with its own base href.
3. One HTTPS origin for the browser: the bundle served by `apps/web/e2e/static-server.mjs`
   (`EVIDGATE_WEB_E2E_ROOT=<dist>/browser`, port 9555) behind a small HTTPS front door
   that forwards `/v1` to the Api's own HTTPS listener (`ASPNETCORE_URLS=http://127.0.0.1:8080;https://localhost:8443`,
   Kestrel `Certificates:Default:Path/KeyPath` with a local PEM pair). The session cookie
   is `__Host-`, so the web origin and the API origin must be the same host and scheme.

Result of that run:

| Step | Observed |
| --- | --- |
| `POST /v1/dev/session` with the secret, over HTTPS | `204`, `Set-Cookie` for `__Host-evidgate_session`, `__Host-evidgate_af` and `XSRF-TOKEN` |
| same secret over plaintext | `503` (the route refuses before the secret is judged) |
| a wrong secret over HTTPS | `401` |
| the route with `EVIDGATE__DEVLOGIN__ENABLED` absent from a fresh host | `404` (path never mapped) |
| authenticated reads with that cookie (`GET /v1/orgs`) | `200` |
| run A / run B detail | live decision `PASS` / `FAIL` with the evidence panel (`evaluated`, `live`, completeness complete), in `en` and `vi`, light theme |

The runs product route is phase G2, so the detail pages were opened on the review
address the development configuration adds, `/dev/run-detail/orgs/{orgId}/projects/{projectId}/runs/{runId}`
(`apps/web/src/app/core/dev-routes/dev-routes.fixtures.ts`), under the product guards.

Two gaps this exercise found (the first is carried as 'Web active-org context missing (S187)', receiver 09.1.1-T4.2):

- The web client sends no `X-Evidgate-Org` header on its tenant reads. The Api requires
  one and answers `422 request_invalid` (`name the organization: use /orgs/{o} or the
  X-Evidgate-Org header`) to `GET /v1/permissions`, which `SessionStore.readSession`
  reads at bootstrap: the shell then reports the workspace as unavailable and the run
  detail renders "Something went wrong". For the screenshots above the header was added
  by the local front door (the header the Api itself documents), not by the app.
- `docs/dev-setup.md` listed only the dev-login pair; the session pepper it now names is
  what turned the `503` into a session.

No screenshot is committed; the four run-detail captures (run A and run B, `en` and `vi`,
light) and one login-page capture with the Dev sign-in disclosure live outside the
repository.

## 7. Troubleshooting

| Symptom                                                       | Cause and action                                                                                                  |
| ------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------- |
| `missing required environment: …`                             | One of the five variables of §3 is unset. The script names each.                                                  |
| `dev_token_refused`                                           | `EVIDGATE_API_URL` is not loopback; the script forces `EVIDGATE_ENV=dev` on every CLI call (`contracts/cli.md` §4). |
| `events send` exits 3 `unknown_execution_key`                 | The script rewrites the reporter's placeholder key before `events send`; after that rewrite the error means the planned key formula drifted from `src/Modules/Evidgate.Modules.Orchestrator/Domain/ExecutionKey.cs`. |
| `evidgate gate` exits 3 with 403                              | The minted CI token lacks `decision:read`. `ci_producer` holds it since the decision route landed, so check the token's scopes at the `/v1/ci/token` exchange rather than waiting for a later grant. |
| Port already in use / container name conflict                 | See §2: another stack holds the port or one of the fixed container names.                                         |
| Run C exits 3 instead of 2                                    | The decision request failed rather than answered `pending`; read the stderr `error.code`.                         |
