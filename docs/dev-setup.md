# Local development setup (01.8.1 — one-command local stack)

Prerequisites, one command, and what to do when it does not come up. The story is
[`plans/features/01-foundation/01.8.1-one-command-local-stack.md`](../plans/features/01-foundation/01.8.1-one-command-local-stack.md);
the emulator decision is [ADR-0004](adr/0004-s3-emulator-local-dev.md).

## Prerequisites

| Need | Why | Note |
| --- | --- | --- |
| Docker daemon | PostgreSQL 18 and SeaweedFS run in compose | colima and Docker Desktop both work; `up.sh` picks up colima's socket when no default daemon answers |
| `docker compose` (v2) | every compose call goes through the plugin (`docker compose …`) | `docker compose version`; the standalone `docker-compose` script is not used |
| .NET 10 SDK | Api, Worker and `Evidgate.Migrator` | on `PATH`, or in `$HOME/.dotnet` (picked up automatically) |
| `curl`, `awk` | readiness polling, reading the SeaweedFS identity file | present on macOS and on every common Linux distribution |
| `pgrep` | `lib.sh` walks the process tree of each host it stops | ships with macOS and every common Linux distribution |
| Node 26 | the Angular dev server (`ng serve`) and the TypeScript tooling | `up.sh` runs `npm ci` at the repository root when `node_modules` is missing or older than `package-lock.json`, and refreshes `packages/sdk` with `tsc -b` (incremental) on every run |

## One command

```sh
scripts/dev/up.sh
```

`scripts/dev/up.sh --with-token` does the same and additionally mints the dev
ingest token and the sample policy revision ([The dev ingest token](#the-dev-ingest-token-seed-dev---with-token));
everything else, including the data in the volumes, is identical.

From a clean checkout it does, in order:

1. creates `.env.dev` from [`.env.dev.template`](../.env.dev.template) on first run
   (mode 600, fresh random passwords for the superuser and the four database roles);
2. `docker compose -f infra/compose.dev.yml up -d --wait` — PostgreSQL 18 and
   SeaweedFS, both digest-pinned, healthy before anything else runs (no sleeps);
   `up.sh` refuses first when another process already holds a port the stack must
   publish ([Troubleshooting](#troubleshooting));
3. [`scripts/dev/db-init.sh`](../scripts/dev/db-init.sh) against the compose
   PostgreSQL: superuser bootstrap → `evidgate_owner` migrates the database →
   migration 0001 creates the application roles → superuser grants them `LOGIN` +
   password (01.4.1-T2.3; the role split mirrors production, so an RLS mistake
   fails locally first), then `Evidgate.Migrator seed-dev` writes the synthetic dev
   tenant and the dev project's sample case, suite and suite revision ([The dev
   tenant](#the-dev-tenant-seed-dev); `EVIDGATE_DB_INIT_SEED=0` skips it). The seed
   runs after those grants because it writes through the application role — the
   catalog rows always, and under `--with-token` the ingest token and the sample
   policy revision as well;
4. creates the dev bucket and puts the `pending/` 24 h lifecycle rule on it (T18);
5. installs the npm workspaces (`npm ci`) when `node_modules` is missing or older
   than `package-lock.json`, rebuilds `packages/sdk` with `tsc -b` (incremental, so
   a changed SDK recompiles and an unchanged one is skipped) for the web app's
   `dist/` import, builds `Evidgate.Api` and `Evidgate.Worker` once each (two cold
   `dotnet watch` restores would race on the shared `obj/` files), then starts them
   with `dotnet watch` and the
   Angular app with `ng serve` (`127.0.0.1:4200`) on the host, logging to
   `.local/dev/logs/` with pids under `.local/dev/pids/`;
6. polls `GET /internal/health/ready` on Api and Worker and `GET /` on the dev
   server until each answers `200`, then prints the URLs and the elapsed time and
   exits 0. The 300 s budget (`EVIDGATE_UP_READY_TIMEOUT_SECONDS`) applies per
   service and they are polled in order, so a cold first run can wait up to three
   times that budget before it fails and prints the log tail.

The host tooling (`npm ci`, `tsc -b`, `ng serve`, `dotnet build`) runs without the
`.env.dev` credentials in its environment: `lib.sh`'s `evg_run_scrubbed` drops every
exported variable whose name matches
`PASSWORD|SECRET|CONNECTIONSTRING|ACCESS_?KEY|S3_CONFIG|PEPPER|DEV_TOKEN`. Only the
compose stack, `db-init.sh` and the Api and Worker processes get them.

Compose publishes both stateful ports on `127.0.0.1` only, and the dev stack's
Worker probe binds loopback as well: `.env.dev.template` sets
`EVIDGATE__WORKER__HEALTHADDRESS=127.0.0.1`. Only the option's own default is
`0.0.0.0`, so a deployment that leaves that key unset listens on every interface
and must keep 8081 behind the host firewall.

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
| web (`ng serve`) | `http://127.0.0.1:4200` | the Angular 22 app from `apps/web`; the `development` configuration localizes to `vi` only and the dev server serves that single locale flat, so the app answers on `/` (no `/vi/` prefix). It imports `@evidgate/sdk` from `packages/sdk/dist` |

## Everyday commands

```sh
scripts/dev/logs.sh              # compose + host logs in one stream
scripts/dev/logs.sh worker       # api | worker | web | postgres | seaweedfs | all
scripts/dev/down.sh              # stop hosts + compose, keep volumes (AC4)
scripts/dev/up.sh --reset        # stop hosts, delete the volumes, start clean
scripts/dev/up.sh --with-token   # start, then mint the dev ingest token (printed once)
scripts/dev/up.sh --observability  # also start the observability profile (docs/runbooks/telemetry.md §5)
```

Data lives in the named volumes `pg-data` and `s3-data`: `down.sh` then `up.sh`
keeps everything (the database comes back at the same migration head, so the
migrations print `nothing to apply` and the seed writes nothing); `up.sh --reset`
deletes both volumes.

## Configuration (`.env.dev`)

`.env.dev` is git-ignored (00 §5.7 S3) and is the only place dev credentials live.
It is sourced and exported by the dev scripts, and inherited by compose, the Api,
the Worker and the Migrator:

| Setting | Used by | Meaning |
| --- | --- | --- |
| `EVIDGATE_DB_PORT`, `EVIDGATE_S3_PORT` | compose | host ports published on `127.0.0.1` |
| `EVIDGATE_WEB_PORT` | `up.sh` (`ng serve`) | port of the Angular dev server on `127.0.0.1`; not in `.env.dev.template`, `up.sh` defaults it to `4200` |
| `EVIDGATE_DB_SUPERUSER_PASSWORD` | compose, `db-init.sh` | bootstrap-only superuser password |
| `EVIDGATE_OWNER_PASSWORD`, `EVIDGATE_APP_PASSWORD`, `EVIDGATE_OPERATOR_PASSWORD`, `EVIDGATE_WORKER_SCHED_PASSWORD` | `db-init.sh` | the four role passwords |
| `EVIDGATE__DATABASE__CONNECTIONSTRING` | Api, Worker; `db-init.sh --with-token` builds the same value from the role password and port for the seed | `evidgate_app` — subject to RLS |
| `EVIDGATE__TASKS__SWEEPCONNECTIONSTRING` | Worker | `evidgate_worker_sched` — the scheduled owner `SECURITY DEFINER` sweeps (01.5.1-T6.2, TL T80) |
| `EVIDGATE__RUNS__CANCEL__SOFTSTOPDELAY`, `EVIDGATE__RUNS__CANCEL__KILLDELAY` | Api, Worker | the two windows of an accepted run cancel, as `TimeSpan` literals, measured from the request; the kill deadline is also the `available_at` of the durable kill task. Both are required at boot — there is no option default — and `KILLDELAY` must be at least `SOFTSTOPDELAY`. The `db-init.sh --with-token` seed does not read them |
| `EVIDGATE_MIGRATOR__CONNECTIONSTRING` | `Evidgate.Migrator` | `evidgate_owner` — the only writer of `public.schema_migration` |
| `EVIDGATE__CI__PEPPER` | `db-init.sh --with-token` (the seed) | base64 HMAC pepper for ingest tokens and binding challenges (`EVIDGATE:CI:PEPPER`), at least 32 bytes after decoding; the seed refuses and writes nothing when it is missing or invalid. Not in `.env.dev.template`: generate one with `openssl rand -base64 32` and paste it as `EVIDGATE__CI__PEPPER=<the value>` in `.env.dev` before the first `--with-token` run |
| `EVIDGATE__CI__SIGNINGKEY` | Api | HS256 key of the `ci_producer` tokens `POST /v1/ci/token` mints and every CI route accepts; base64, at least 32 bytes after decoding. Not in `.env.dev.template`: generate one with `openssl rand -base64 48`, paste it as `EVIDGATE__CI__SIGNINGKEY=<the value>` in `.env.dev` and restart the Api, otherwise `/ci/token` answers 503 `CI authentication is not configured` |
| `ASPNETCORE_URLS`, `EVIDGATE__WORKER__HEALTHPORT`, `EVIDGATE__WORKER__HEALTHADDRESS` | Api, Worker | HTTP endpoint, worker probe port, and the address that probe binds (`127.0.0.1` in the template; the option default is `0.0.0.0`) |
| `DOTNET_ENVIRONMENT`, `ASPNETCORE_ENVIRONMENT` | Api, Worker | `Development` for the dev stack |
| `EVIDGATE_S3_ENDPOINT`, `EVIDGATE_S3_REGION`, `EVIDGATE_S3_BUCKET`, `EVIDGATE_S3_ACCESS_KEY`, `EVIDGATE_S3_SECRET_KEY` | `up.sh` | local emulator coordinates and identity |
| `EVIDGATE__OBJECT__ENDPOINT`, `EVIDGATE__OBJECT__REGION`, `EVIDGATE__OBJECT__BUCKET`, `EVIDGATE__OBJECT__FORCEPATHSTYLE`, `EVIDGATE__OBJECT__ACCESSKEY`, `EVIDGATE__OBJECT__SECRETKEY` | Api, Worker | the object store they bind (`EVIDGATE:Object`, 06.1.2-T1); same endpoint, region, bucket and identity as the `EVIDGATE_S3_*` block above |

Changing a port means changing the matching connection string or endpoint in the
same file; `up.sh` does not rewrite URLs, and it refuses a port value that is not
digits only. The Api and the Worker read their object store from
`EVIDGATE__OBJECT__*`: those six keys must carry the same endpoint, region, bucket
and identity as the `EVIDGATE_S3_*` block, which is the dev stack's own view of the
emulator (`up.sh` asserts the identity against
`infra/seaweedfs/s3-config.json`). No `EVIDGATE__STORAGE__*` key is bound.

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

## The dev ingest token (`seed-dev --with-token`)

`scripts/dev/up.sh --with-token` (or `scripts/dev/db-init.sh --with-token` on its own)
mints one dev ingest token and writes one sample policy revision, activated advisory,
for the dev project — all through the real commands on the application role:

- token `dev-seed`, bound to `dev-project`, scopes `artifact:write`, `decision:read`,
  `event:write` and `run:create`, no expiry (local only, gone with the volume);
- one sample `policy-revision.v1` revision for `dev-project` (`CreatePolicyRevisionCommand`
  appends a **draft**) and, right after it, its activation through
  `ActivatePolicyCommand` in mode **advisory**. A run created afterwards pins that
  revision instead of falling back to the `EVIDGATE:Runs` defaults, so its evaluation
  has a policy to apply instead of failing on a run that pins none.

The seed stays idempotent: rerunning it returns the existing policy revision
(`sha256:<hash>`), replays its activation (no second pointer row, no second
`policy.activate` audit row) and mints no second token. Two concurrent first runs
cannot both mint either: the seed holds a session-level PostgreSQL advisory lock
across the check and the mint, so the second waits, then finds the live `dev-seed`
token and mints nothing.

**The secret is printed once.** The seed writes the single line
`EVIDGATE_DEV_TOKEN=egt_...` to the terminal that ran the script. Neither script
captures that line, tees it, stores it in `.env.dev`, exports it, or hands it to the
Api, the Worker or the web tooling. Treat the terminal scrollback as the secret: do
not paste it into a file, a chat or a commit, and do not run these scripts under
`bash -x` while minting.

Export it in the shell that runs the CLI (never in `.env.dev`, which every host
process inherits):

```sh
export EVIDGATE_ENV=dev
export EVIDGATE_API_URL=http://127.0.0.1:8080
export EVIDGATE_PROJECT=0198a000-0000-7000-8000-00000000d002
export EVIDGATE_DEV_TOKEN='egt_...'   # the line the seed printed
```

The seed prints `EVIDGATE_DEV_TOKEN=egt_...`; that value is an ingest token. With
`EVIDGATE_ENV=dev` and a loopback `EVIDGATE_API_URL` the CLI exchanges it at
`POST /v1/ci/token` for a short-lived `ci_producer` token, the same path as
`EVIDGATE_INGEST_TOKEN`, which stays the CI variable. The Api accepts only the
minted token on the CI routes and never the raw one; with any other
`EVIDGATE_ENV` or a non-loopback URL the CLI refuses the dev token (exit 3).

The exchange needs `EVIDGATE__CI__SIGNINGKEY` in `.env.dev` (see
[Configuration](#configuration-envdev)); without it `/ci/token` answers 503
`CI authentication is not configured`. One producer token is cached per API URL and
project under `EVIDGATE_CACHE_DIR` (default `~/.cache/evidgate`), so after
`up.sh --reset` the seeded token is a new secret: use a fresh cache directory or
delete `<cache>/ci-token.*.json` before running the CLI.

**Rerunning with a live token** exits 0, mints nothing and prints no secret; the
scripts say so and name the one recovery:

```text
a live 'dev-seed' ingest token already exists; its secret was shown once and cannot be shown again. To get a new one run: scripts/dev/down.sh && scripts/dev/up.sh --reset --with-token
```

That reset is the only recovery in dev: the dev user has no login, so there is no
session to revoke a token with, and a stored secret cannot be shown a second time.

```sh
scripts/dev/down.sh && scripts/dev/up.sh --reset --with-token
```

`--reset` deletes the `pg-data`/`s3-data` volumes, so the dev data is recreated from
scratch (AC4).

**Smoke it with the CLI.** Build the workspace package once, then use the two
project-scoped commands the token covers:

```sh
npm run build -w @evidgate/cli
node packages/cli/dist/bin.js manifests upload --file m.json
node packages/cli/dist/bin.js runs create --manifest-id <manifest_id> --manifest-hash <manifest_hash>
```

Add `--json` for `{manifest_id, manifest_hash}` and `{run_id, manifest_hash, state}`.

`m.json` is a `manifest.v1` document and must name a suite revision that exists in
`dev-project`. The seed creates exactly one, and prints the two ids a manifest
needs (they are the database's, so they are stable per volume and change only on
`--reset`):

```text
seed-dev: catalog suite dev-seed-smoke revision 1 sha256:<64 hex> (created)
seed-dev: manifest inputs suite_revision_id=<uuid> case_revision_id=<uuid>
seed-dev: environment dev revision 1 sha256:<64 hex> (created)
seed-dev: manifest inputs environment_revision_id=<uuid> environment_key=dev
```

The environment line is printed by the same run: the seed writes one `dev`
environment revision for `dev-project` through the Orchestrator command, because
`runs create` refuses a manifest that names an unknown or null environment revision
and the seeded ingest token has no `environment:write` to create one itself.

Read them from the output of the run that seeded the stack (`scripts/dev/up.sh`
and `scripts/dev/db-init.sh` print the seed's stdout), or ask for them again at any
time — a rerun writes nothing and repeats the same two lines with `(already
present)`:

```sh
# Both roles: the seed writes the tenant as the owner and the catalog half as the app role.
EVIDGATE_MIGRATOR__CONNECTIONSTRING='Host=127.0.0.1;Port=5432;Database=evidgate;Username=evidgate_owner;Password=<EVIDGATE_OWNER_PASSWORD>' \
EVIDGATE__DATABASE__CONNECTIONSTRING='Host=127.0.0.1;Port=5432;Database=evidgate;Username=evidgate_app;Password=<EVIDGATE_APP_PASSWORD>' \
DOTNET_ENVIRONMENT=Development dotnet run --project src/Evidgate.Migrator -- seed-dev
```

Both `Port=` values must be the `EVIDGATE_DB_PORT` of `.env.dev` (the default is
`5432`). Or run [`scripts/dev/db-init.sh`](../scripts/dev/db-init.sh), which builds
the two connection strings from `.env.dev` and prints the same lines.

Take the shape from the contract vector
[`fixtures/contracts/vectors/manifest.v1/acc-01-golden.json`](../fixtures/contracts/vectors/manifest.v1/acc-01-golden.json)
and change three things, because the dev project holds one case and one suite
revision:

- `suite_revision_id` → the `suite_revision_id` the seed printed;
- every `case_revision_id` (in `planned_executions` and in
  `matrix.case_revision_ids`) → the `case_revision_id` the seed printed, with
  `planned_executions`, `matrix` and `shard_plan` trimmed to that one case the way
  the vector repeats its entries;
- `environment_revision_id` → the `environment_revision_id` the seed printed (the
  one `dev` revision of `dev-project`, whose `base_urls.local` is
  `http://127.0.0.1:8080`). The upload only checks that the revision exists in the
  project; `env_needs.target_envs` and each `planned_executions[].target_env` are
  free-form labels at this milestone, so leave the vector's `staging` as it is, or
  use `dev` to read consistently.

`runs create` answers 201 with a `run_id` when the token, the project and the
manifest line up.

Without `--with-token`, no token is seeded: the seed writes the tenant, the
catalog suite and the environment revision only, and a token has to come from
`POST /v1/projects/{p}/ingest-tokens` once a session exists (02.3.x) or from the
seeded CI binding with a GitHub OIDC token.

## The dev tenant (`seed-dev`)

`scripts/dev/db-init.sh` runs `Evidgate.Migrator seed-dev` after the migrations and
after the application-role grants, because the seed writes through that role
(skip it with `EVIDGATE_DB_INIT_SEED=0`). It writes, idempotently and in one
transaction as the owner role, a synthetic tenant: org `dev-org`, project
`dev-project`, a dev user (`0198a000-0000-7000-8000-00000000d003`, signed in through
the development sign-in below) holding `tenant_admin` and `release_approver`, and one
**active** CI binding for
the synthetic repository id `9000000000001` (`evidgate-dev/sample`). It refuses, writing
nothing, unless `DOTNET_ENVIRONMENT=Development` and both database hosts are local or
bare compose service names. The data is fully synthetic.

The same run then writes the dev project's catalog sample, on the application role,
through the Catalog module's own service and commands (never hand-written SQL on
catalog tables): one case `dev seed smoke` and its case revision, one suite
`dev-seed-smoke` and one **draft** suite revision holding that case, so
`manifests upload` has a suite revision to name. Rerunning finds all four and adds
nothing: the sample's content is fixed, so the ids and the revision hash stay the
same, and the seed prints them again ([Smoke it with the
CLI](#the-dev-ingest-token-seed-dev---with-token)). The same run then writes one
`dev` environment revision for the project, through the Orchestrator module's own
command, so a manifest and a run have a real `environment_revision_id`; its content
is fixed too (a loopback base URL, no dependency versions, no secret references), so
a rerun finds it by the module's content hash, adds nothing and prints the same id
with `(already present)`.

Every lookup comes first and is paged: the case by its annotation id, the suite by
its slug, and the revision by its content hash (the revision preview). A dev project
the seed did not create is protected twice: it stops when other **active** cases
exist, because the registration service marks every active case absent from its
one-file sample tree missing, and if one arrives while it registers, the registration
is rolled back instead of committed. The seed also never resubmits the sample
content to the create command, which would move the suite's head back to that older
revision and off a newer one.

Under `--with-token` the same seed also mints the ingest token and writes the sample
policy revision and its advisory activation through the application role;
`EVIDGATE_DB_INIT_SEED=0` skips it either way.

## Signing in to the web app against the real API (`POST /v1/dev/session`)

The dev user the seed writes has no login: there is no OIDC provider configured in the
local stack, so the federated login answers 503 and the web development build, which
needs a session cookie, cannot open any screen. The development-only sign-in fills that
gap — it gives the seeded dev user a real session through the same audit and session
machinery a federated login uses, and it exists **only** in a Development host that asked
for it.

Enable it in `.env.dev` (never in a deployed environment):

```sh
# Generate each value once in your shell, then paste it below: a command substitution
# written here is re-run every time up.sh sources the file, so the secret would change
# on every restart (and a reader that parses plain KEY=VALUE lines would see the
# expression itself).
openssl rand -base64 32          # the dev-login secret
openssl rand -base64 32          # the session pepper (a different value)
```

```sh
# .env.dev — the switch and the shared secret of the local sign-in.
EVIDGATE__DEVLOGIN__ENABLED=true
EVIDGATE__DEVLOGIN__SECRET=<the value generated above>
# The session pepper every issued session is hashed with (base64, at least 32
# bytes); `openssl rand -base64 32` and paste. The local stack ships none.
EVIDGATE__SESSION__PEPPER=<the other value generated above>
```

The session pepper is not optional for this route: `session_create` stores the HMAC of
the cookie value, so a host without `EVIDGATE__SESSION__PEPPER` answers **503
`unavailable`** to a correct secret as well (the audit row is written and no session
row follows). The dev-login pair alone is therefore not enough to sign in.

`ENABLED` must be set to `true` explicitly — it defaults to false, and no environment
sets it, so the route stays unmapped until you write the line. Use at least 16 random
characters for the secret (the base64 of 32 random bytes is 44): the route only requires
a non-empty value, so the strength is entirely yours to choose. Only the Api host acts on
these two keys — the Worker and the Migrator compile the same module but map no route —
and neither key is required at boot. The route is mapped
when **all three** conditions hold — the host environment is `Development`,
`EVIDGATE__DEVLOGIN__ENABLED` is true, and `EVIDGATE__DEVLOGIN__SECRET` is set — and in
every other case the path is not registered at all, so the answer is the host's ordinary
**404**, never a 401: a deployment cannot be talked into a development sign-in by
configuration alone. Restart the stack after adding the keys (`scripts/dev/up.sh`),
because the mapping is decided while the host builds its routes.

The sign-in needs **HTTPS**, even locally: the session cookie is `__Host-` and the
antiforgery pair is `CookieSecurePolicy.Always`, so a plaintext request cannot mint them
and the route answers `503 unavailable` before writing anything (no half-made session).
The dev stack's `ASPNETCORE_URLS=http://127.0.0.1:8080` endpoint therefore cannot sign
anyone in; run the Api with a certificate instead and point the web dev build's proxy at
it:

```sh
dotnet dev-certs https --trust                       # once per machine
ASPNETCORE_URLS=https://localhost:8443 \
  EVIDGATE__DEVLOGIN__ENABLED=true \
  EVIDGATE__DEVLOGIN__SECRET="$EVIDGATE__DEVLOGIN__SECRET" \
  EVIDGATE__SESSION__PEPPER="$EVIDGATE__SESSION__PEPPER" \
  dotnet run --project src/Evidgate.Api
```

Then sign in with the secret and keep the cookie jar (`-k` only because the dev
certificate is local to this machine):

```sh
curl -sS -k -i -c /tmp/evidgate-dev.cookies \
  -H 'Content-Type: application/json' \
  --data "{\"secret\":\"$EVIDGATE__DEVLOGIN__SECRET\"}" \
  https://localhost:8443/v1/dev/session
```

A success is a bodiless `204` whose `Set-Cookie` headers carry
`__Host-evidgate_session` plus the antiforgery pair (`XSRF-TOKEN`, `__Host-evidgate_af`);
a cookie-authenticated mutation then echoes the `XSRF-TOKEN` value in `X-XSRF-TOKEN`.
The session always belongs to the seeded dev user (`0198a000-0000-7000-8000-00000000d003`)
and to nobody else — the body carries the secret and nothing more — and the sign-in writes
the `auth.login` row of the dev org **before** the session row, so a sign-in that cannot
be audited issues nothing (`503 unavailable`). A wrong, absent or blank secret is `401
token_invalid` with no session row and no cookie; the value is compared in constant time
and never logged. Without an active membership the answer is `403 no_membership`. The
session is an ordinary row: the logout and revoke routes end it, and signing in again
replaces the previous one. If the web app reports a stale session after a reseed, rerun
the curl above.

Behind a TLS-terminating proxy (a tunnel or a load balancer) the Api sees plaintext unless the
proxy is listed (`EVIDGATE__FORWARDEDHEADERS__KNOWNPROXIES`, see `docs/runbooks/forwarded-headers.md`)
and sends `X-Forwarded-Proto: https`, and then this route refuses with the same `503`: the
deployment has to terminate TLS at the Api or trust the proxy that does. It is never solved by
relaxing the cookie flags.

The route is throttled per client address: 30 attempts per 60-second window by default
(`EVIDGATE__CI__EXCHANGEBURSTLIMIT` and `EVIDGATE__CI__EXCHANGEWINDOWSECONDS`), counted
before anything else, so a successful sign-in spends one unit exactly like a wrong guess.
The key is the address the Api sees — every caller behind one proxy or tunnel shares a
single key unless forwarded headers are trusted — and a spent window answers `429
rate_limited` with `Retry-After` until it resets.

## Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| `cannot reach a docker daemon (try: colima start)` | no daemon answers: start one (`colima start`, or Docker Desktop). With colima, `export DOCKER_HOST=unix://$HOME/.colima/default/docker.sock` (or let `up.sh` detect it). |
| `port <n> on 127.0.0.1 is already in use` (`up.sh`), `port is already allocated` (compose) | a host PostgreSQL/Homebrew service, another stack or another `ng serve` holds the port. `up.sh` refuses before compose starts when no container of this stack owns that service, because a publish onto a taken port would silently not apply and `db-init.sh` would then talk to that other server; it also warns when this stack's container exists but is not running and something answers on that port, because starting that container would then fail. Stop the listener, or change the port in `.env.dev` — for the database also the `Port=` values of the connection strings, for S3 also `EVIDGATE_S3_ENDPOINT` and `EVIDGATE__OBJECT__ENDPOINT` (both carry the S3 port). |
| `EVIDGATE_DB_PORT must be a number of digits only` | a port value in `.env.dev` carries something other than digits (`5432 `, `localhost:5432`, `58333abc`): fix the variable the message names; `up.sh` checks `EVIDGATE_DB_PORT`, `EVIDGATE_S3_PORT` and `EVIDGATE_WEB_PORT` before anything starts. An empty value is not an error: each of the three falls back to its default (`5432`, `8333`, `4200`). |
| `EVIDGATE__CI__PEPPER is not set` (`up.sh --with-token`), or the seed refuses with a pepper message | the token seed resolves the CI pepper like the Api does. Generate one with `openssl rand -base64 32` in your shell, paste it as `EVIDGATE__CI__PEPPER=<the value>` in `.env.dev` (base64, at least 32 bytes after decoding) and rerun; `up.sh` refuses before compose starts, and the seed refuses before writing anything. |
| `EVIDGATE_S3_ACCESS_KEY/SECRET_KEY in .env.dev do not match …` | the identity in `.env.dev` and in `infra/seaweedfs/s3-config.json` (read by the 01.8.1-T1 parity suite) drifted apart; keep them equal. |
| `api exited during startup` | the last 15 log lines are printed; the full log is `.local/dev/logs/api.log`. Usual causes: a mutated `.env.dev`, or the database missing a migration head. |
| `ready` answers `503` with `journal_behind` | the running build expects a later migration than the database has: restart with `scripts/dev/up.sh` (it re-applies migrations through `db-init.sh`). |
| A stack left over from a previous branch | `scripts/dev/down.sh` then `scripts/dev/up.sh --reset`. |
