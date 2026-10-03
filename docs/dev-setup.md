# Local development setup (01.8.1 — one-command local stack)

Prerequisites, one command, and what to do when it does not come up. The story is
[`plans/features/01-foundation/01.8.1-one-command-local-stack.md`](../plans/features/01-foundation/01.8.1-one-command-local-stack.md);
the emulator decision is [ADR-0004](adr/0004-s3-emulator-local-dev.md).

## Prerequisites

| Need | Why | Note |
| --- | --- | --- |
| Docker daemon | PostgreSQL 18 and SeaweedFS run in compose | colima and Docker Desktop both work; `up.sh` picks up colima's socket when no default daemon answers |
| .NET 10 SDK | Api, Worker and `Evidgate.Migrator` | on `PATH`, or in `$HOME/.dotnet` (picked up automatically) |
| `curl`, `awk` | readiness polling, reading the SeaweedFS identity file | present on macOS and on every common Linux distribution |
| Node 26 | TypeScript tooling and, later, `ng serve` | `npm ci` once, per the repo root `README`/CI gate |

## One command

```sh
scripts/dev/up.sh
```

From a clean checkout it does, in order:

1. creates `.env.dev` from [`.env.dev.template`](../.env.dev.template) on first run
   (mode 600, fresh random passwords for the superuser and the four database roles);
2. `docker compose -f infra/compose.dev.yml up -d --wait` — PostgreSQL 18 and
   SeaweedFS, both digest-pinned, healthy before anything else runs (no sleeps);
3. [`scripts/dev/db-init.sh`](../scripts/dev/db-init.sh) against the compose
   PostgreSQL: superuser bootstrap → `evidgate_owner` migrates the database →
   migration 0001 creates the application roles → superuser grants them `LOGIN` +
   password (01.4.1-T2.3; the role split mirrors production, so an RLS mistake
   fails locally first);
4. creates the dev bucket and puts the `pending/` 24 h lifecycle rule on it (T18);
5. starts `Evidgate.Api` and `Evidgate.Worker` with `dotnet watch` on the host,
   logging to `.local/dev/logs/` with pids under `.local/dev/pids/`;
6. polls `GET /internal/health/ready` on both until each answers `200`, then prints
   the URLs and the elapsed time and exits 0.

Compose publishes both stateful ports on `127.0.0.1` only. The Worker's own probe
listener binds all interfaces (`TcpListener(IPAddress.Any)`, 01.1.2-T1.3), so on a
shared machine keep 8081 behind the host firewall; the database and the emulator
are the surfaces that matter and neither leaves loopback.

PostgreSQL's initdb defaults would leave loopback connections unauthenticated
(`host … 127.0.0.1/32 trust`), which would make the owner/app/operator split a
convention instead of a control. The compose service therefore initialises with
`POSTGRES_INITDB_ARGS=--auth-host=scram-sha-256`: everything that connects over
TCP from the host — Api, Worker, Migrator and the isolation suite — authenticates
with its role password. Inside the container the unix socket stays `trust` (the
image's own convention), which is the path `db-init.sh` uses as superuser.

## What you get

| Surface | Default endpoint | Notes |
| --- | --- | --- |
| `Evidgate.Api` | `http://127.0.0.1:8080` | readiness: `/internal/health/ready`, liveness: `/internal/health/live`, metrics: `/metrics` |
| `Evidgate.Worker` | readiness `http://127.0.0.1:8081/internal/health/ready` | the worker serves only the probe listener; its work happens in the database-backed queue |
| PostgreSQL 18 | `127.0.0.1:5432`, database `evidgate` | roles `evidgate_owner` (migrations), `evidgate_app` (Api/Worker), `evidgate_operator`, `evidgate_worker_sched` |
| SeaweedFS S3 | `http://127.0.0.1:8333`, bucket `evidgate-dev` | static local-only identity, same one the 01.8.1-T1 parity suite uses |

## Everyday commands

```sh
scripts/dev/logs.sh              # compose + host logs in one stream
scripts/dev/logs.sh worker       # api | worker | postgres | seaweedfs | all
scripts/dev/down.sh              # stop hosts + compose, keep volumes (AC4)
scripts/dev/up.sh --reset        # stop hosts, delete the volumes, start clean
```

Data lives in the named volumes `pg-data` and `s3-data`: `down.sh` then `up.sh`
keeps everything (the database comes back at the same migration head, so
`db-init.sh` has nothing to do); `up.sh --reset` deletes both volumes.

## Configuration (`.env.dev`)

`.env.dev` is git-ignored (00 §5.7 S3) and is the only place dev credentials live.
It is sourced and exported by the dev scripts, and inherited by compose, the Api,
the Worker and the Migrator:

| Setting | Used by | Meaning |
| --- | --- | --- |
| `EVIDGATE_DB_PORT`, `EVIDGATE_S3_PORT` | compose | host ports published on `127.0.0.1` |
| `EVIDGATE_DB_SUPERUSER_PASSWORD` | compose, `db-init.sh` | bootstrap-only superuser password |
| `EVIDGATE_OWNER_PASSWORD`, `EVIDGATE_APP_PASSWORD`, `EVIDGATE_OPERATOR_PASSWORD`, `EVIDGATE_WORKER_SCHED_PASSWORD` | `db-init.sh` | the four role passwords |
| `EVIDGATE__DATABASE__CONNECTIONSTRING` | Api, Worker | `evidgate_app` — subject to RLS |
| `EVIDGATE__TASKS__SWEEPCONNECTIONSTRING` | Worker | `evidgate_worker_sched` — the scheduled owner `SECURITY DEFINER` sweeps (01.5.1-T6.2, TL T80) |
| `EVIDGATE_MIGRATOR__CONNECTIONSTRING` | `Evidgate.Migrator` | `evidgate_owner` — the only writer of `public.schema_migration` |
| `ASPNETCORE_URLS`, `EVIDGATE__WORKER__HEALTHPORT` | Api, Worker | HTTP endpoint, worker probe port |
| `DOTNET_ENVIRONMENT`, `ASPNETCORE_ENVIRONMENT` | Api, Worker | `Development` for the dev stack |
| `EVIDGATE_S3_ENDPOINT`, `EVIDGATE_S3_REGION`, `EVIDGATE_S3_BUCKET`, `EVIDGATE_S3_ACCESS_KEY`, `EVIDGATE_S3_SECRET_KEY` | `up.sh` | local emulator coordinates and identity |

Changing a port means changing the matching connection string or endpoint in the
same file; `up.sh` does not rewrite URLs. The storage module's own
`EVIDGATE__STORAGE__*` options land with 06.1.2 (ADR-0004) and will point at the
`EVIDGATE_S3_*` coordinates above.

## Running the integration suites against the dev stack

The isolation suite (01.6.1-T4 / AC5) normally starts its own PostgreSQL
container. Point it at the running dev database with
`EVIDGATE_ISOLATION__SUPERUSERCONNECTIONSTRING` instead:

```sh
set -a; . ./.env.dev; set +a
export EVIDGATE_ISOLATION__SUPERUSERCONNECTIONSTRING="Host=127.0.0.1;Port=${EVIDGATE_DB_PORT};Database=postgres;Username=postgres;Password=${EVIDGATE_DB_SUPERUSER_PASSWORD}"
dotnet test tests/Isolation.Tests
```

It then creates one scratch database per test on that server, migrates each with
the real Migrator and runs the RLS assertions as `evidgate_app` — i.e. against the
same image and the same role split the dev stack uses. Two things to know:

- it rotates the `evidgate_owner`, `evidgate_app`, `evidgate_operator` and
  `evidgate_worker_sched` passwords (roles are global to a server), so restart the
  stack afterwards — `scripts/dev/down.sh && scripts/dev/up.sh`, which re-grants
  the `.env.dev` passwords through `db-init.sh`;
- it leaves the scratch databases behind only if the run is aborted; a normal run
  drops them as the test assembly finishes.

The S3 parity suite (01.8.1-T1) runs against its own SeaweedFS container, so it
needs no dev-stack endpoint; to point the same assertions at the dev bucket, use
`EVIDGATE_S3PARITY__ENDPOINT`, `__REGION`, `__ACCESSKEY`, `__SECRETKEY`,
`__BUCKET` (the same three assertions 10.6.1-T2 will run against the provider).

## Not started yet

- **web** — `apps/web` is the 00 §5.2 placeholder; `ng serve` arrives with 09.1.1,
  and `up.sh` says so instead of pretending to start it.
- **Seed part 2** (01.8.1-T4) — the dev ingest token (`seed-dev --with-token`) and the
  sample policy revision need the ingest-token command of 02.4.2 wired into the seed
  and the policy tables of 07.1.1. Until then, create a token with
  `POST /v1/projects/{p}/ingest-tokens` once a session exists (02.3.x), or use the
  seeded CI binding with a GitHub OIDC token.

## The dev tenant (`seed-dev`)

`scripts/dev/db-init.sh` runs `Evidgate.Migrator seed-dev` after the migrations
(skip it with `EVIDGATE_DB_INIT_SEED=0`). It writes, idempotently and in one
transaction as the owner role, a synthetic tenant: org `dev-org`, project
`dev-project`, a dev user (`0198a000-0000-7000-8000-00000000d003`, no login exists
yet) holding `tenant_admin` and `release_approver`, and one **active** CI binding for
the synthetic repository id `1000001` (`evidgate-dev/sample`). It refuses, writing
nothing, unless `DOTNET_ENVIRONMENT=Development` and the database host is local or a
bare compose service name. The data is fully synthetic.

## Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| `Cannot connect to the Docker daemon` | daemon not running: `colima start`, or start Docker Desktop. With colima, `export DOCKER_HOST=unix://$HOME/.colima/default/docker.sock` (or let `up.sh` detect it). |
| `port is already allocated` | another PostgreSQL/SeaweedFS (or a previous stack) holds 5432/8333: change `EVIDGATE_DB_PORT`/`EVIDGATE_S3_PORT` in `.env.dev` **and** the matching URLs in it. |
| `EVIDGATE_S3_ACCESS_KEY/SECRET_KEY in .env.dev do not match …` | the identity in `.env.dev` and in `infra/seaweedfs/s3-config.json` (read by the 01.8.1-T1 parity suite) drifted apart; keep them equal. |
| `api exited during startup` | the last 15 log lines are printed; the full log is `.local/dev/logs/api.log`. Usual causes: a mutated `.env.dev`, or the database missing a migration head. |
| `ready` answers `503` with `journal_behind` | the running build expects a later migration than the database has: restart with `scripts/dev/up.sh` (it re-applies migrations through `db-init.sh`). |
| A stack left over from a previous branch | `scripts/dev/down.sh` then `scripts/dev/up.sh --reset`. |
