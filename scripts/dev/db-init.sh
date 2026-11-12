#!/usr/bin/env bash
# 01.4.1-T2.3 — local database bootstrap for the dev stack (plans/01-foundation.md §6).
#
# Creates the migration database and its owner credential as superuser, brings it
# to head with Evidgate.Migrator (migration 0001 creates the three application
# roles, 0104 adds evidgate_worker and 0105 evidgate_platform_operator), then
# grants LOGIN + a password to those roles as superuser and prints the EVIDGATE_*
# environment template. This mirrors the production separation (owner runs
# migrations, app serves traffic, operator reads, worker_sched sweeps, worker
# drains the outbox and the task queue, platform_operator sets the platform
# switch from psql) so an RLS mistake fails locally first.
#
# No credential is committed and none is passed on a command line: the six
# passwords are read from the environment, exported to the psql children as
# environment variables, and read inside psql with \getenv — so they never appear
# in `ps` output (00 §5.7 S3). The template printed at the end carries
# placeholders, never the values.
#
# The superuser psql path and the two role statements are shared with
# scripts/env/bootstrap-db.sh through scripts/env/lib-db.sh, so the dev stack and
# an environment bootstrap cannot drift apart.
#
# usage: scripts/dev/db-init.sh [--with-token] [--help]
#
#   --with-token  after the role grants, run `Evidgate.Migrator seed-dev
#                 --with-token`: the dev tenant and the dev catalog suite plus one
#                 dev ingest token and one sample policy revision, minted through
#                 the real commands on the app role. A minted secret reaches this
#                 script's stdout only, once: never captured, tee'd, exported or
#                 logged, and a rerun that finds a live token prints no secret.
#                 Requires EVIDGATE__CI__PEPPER in the environment.
#
# The plain run also seeds through the app role (the catalog suite is written by
# the Catalog module), so both modes run after the grants.
#
# required environment:
#   EVIDGATE_OWNER_PASSWORD       password for the migration role
#   EVIDGATE_APP_PASSWORD         password for the application role
#   EVIDGATE_OPERATOR_PASSWORD    password for the audited read-only role
#   EVIDGATE_WORKER_SCHED_PASSWORD password for the sweep role
#   EVIDGATE_WORKER_PASSWORD      password for the outbox/task-queue worker role
# optional environment (defaults in brackets):
#   EVIDGATE_DB_HOST [127.0.0.1]          EVIDGATE_DB_PORT [5432]
#   EVIDGATE_DB_NAME [evidgate]           EVIDGATE_DB_SUPERUSER [postgres]
#   EVIDGATE_DB_SUPERUSER_PASSWORD [postgres]
#   EVIDGATE_DB_CONTAINER                  run psql through `docker exec -i <name>`
#                                          instead of a host psql (this host has none)
#   EVIDGATE_DB_INIT_SEED [1]              0 skips the dev seed (plain or --with-token)
#   EVIDGATE__CI__PEPPER                   base64 CI pepper; required with --with-token
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"

EVG_DB_LABEL=db-init
# shellcheck source=scripts/env/lib-db.sh
. "$ROOT/scripts/env/lib-db.sh"

DB_HOST="${EVIDGATE_DB_HOST:-127.0.0.1}"
DB_PORT="${EVIDGATE_DB_PORT:-5432}"
DB_NAME="${EVIDGATE_DB_NAME:-evidgate}"
DB_SUPERUSER="${EVIDGATE_DB_SUPERUSER:-postgres}"
DB_SUPERUSER_PASSWORD="${EVIDGATE_DB_SUPERUSER_PASSWORD:-postgres}"
DB_CONTAINER="${EVIDGATE_DB_CONTAINER:-}"

usage() {
  cat <<'USAGE'
Evidgate local database bootstrap (01.4.1-T2.3).

usage: scripts/dev/db-init.sh [--with-token] [--help]

Creates the database and its owner credential as superuser, applies the
migrations as evidgate_owner (migration 0001 creates evidgate_app,
evidgate_operator and evidgate_worker_sched, 0104 adds evidgate_worker, 0105
evidgate_platform_operator), grants those roles LOGIN + a password as superuser
and prints the EVIDGATE_* environment template.

  --with-token  after the grants, also run `seed-dev --with-token`: the dev
                tenant, one project-bound dev ingest token (artifact:write,
                event:write, run:create) and one sample policy revision. A minted
                token is printed here once, never to a log or a file; a rerun
                that finds a live token prints no secret.

required environment:
  EVIDGATE_OWNER_PASSWORD        password for the migration role
  EVIDGATE_APP_PASSWORD          password for the application role
  EVIDGATE_OPERATOR_PASSWORD     password for the audited read-only role
  EVIDGATE_WORKER_SCHED_PASSWORD password for the sweep role
  EVIDGATE_WORKER_PASSWORD       password for the outbox/task-queue worker role
  EVIDGATE_PLATFORM_OPERATOR_PASSWORD
                                 password for the platform-operator role that
                                 alone may execute the system healing-switch definer

optional environment (defaults in brackets):
  EVIDGATE_DB_HOST [127.0.0.1]           EVIDGATE_DB_PORT [5432]
  EVIDGATE_DB_NAME [evidgate]            EVIDGATE_DB_SUPERUSER [postgres]
  EVIDGATE_DB_SUPERUSER_PASSWORD [postgres]
  EVIDGATE_DB_CONTAINER                  run psql through `docker exec -i <name>`
                                         instead of a host psql (this host has none)
  EVIDGATE_DB_INIT_SEED [1]              0 skips the dev seed
  EVIDGATE__CI__PEPPER                   required with --with-token (base64)

Passwords never appear on a command line: they are exported to the psql children
and read there with \getenv.
USAGE
}

# A connection-string value, quoted and escaped so a password that contains ';'
# or '"' still yields a valid DSN (Npgsql accepts the quoted form).
dsn_quote() {
  local value="$1"
  printf '"%s"' "${value//\"/\"\"}"
}

with_token=false
for arg in "$@"; do
  case "$arg" in
    --with-token) with_token=true ;;
    -h | --help)
      usage
      exit 0
      ;;
    *) evg_db_die "unexpected argument '$arg' (try --help)" ;;
  esac
done

evg_db_require_env "${EVG_DB_PASSWORD_VARS[@]}"

# Exported so both a host psql and `docker exec -e NAME` (which copies from this
# environment, never from argv) can read them with \getenv.
evg_db_export_passwords "${EVG_DB_PASSWORD_VARS[@]}"

# psql lives in the PostgreSQL container on this host: prefer the container path
# when told which container to use, otherwise require a host psql.
evg_db_psql_init "$DB_CONTAINER" "$DB_HOST" "$DB_PORT" "$DB_SUPERUSER" "$DB_SUPERUSER_PASSWORD"

printf 'db-init: preparing %s on %s:%s as superuser %s\n' "$DB_NAME" "$DB_HOST" "$DB_PORT" "$DB_SUPERUSER"

# 1. Migration role + database (scripts/env/lib-db.sh, shared with the
#    per-environment bootstrap).
evg_db_create_owner_and_database "$DB_NAME"

# 2. Bring the database to head as evidgate_owner; migration 0001 creates
#    evidgate_app, evidgate_operator and evidgate_worker_sched without LOGIN, and
#    0104 / 0105 add evidgate_worker and evidgate_platform_operator.
printf 'db-init: applying migrations as evidgate_owner\n'
migrator_connection_string="Host=$DB_HOST;Port=$DB_PORT;Database=$DB_NAME;Username=evidgate_owner;Password=$(dsn_quote "$EVIDGATE_OWNER_PASSWORD")"
# The status probe is the first connect as evidgate_owner: on success its output is
# noise, but on failure it is the only evidence (wrong server, missing role) — the
# migrations that follow would fail with something less specific.
if ! status_output="$(EVIDGATE_MIGRATOR__CONNECTIONSTRING="$migrator_connection_string" \
  dotnet run --project "$ROOT/src/Evidgate.Migrator" -- --status 2>&1)"; then
  printf '%s\n' "$status_output" >&2
  evg_db_die "Evidgate.Migrator --status failed against $DB_HOST:$DB_PORT/$DB_NAME (output above)"
fi
EVIDGATE_MIGRATOR__CONNECTIONSTRING="$migrator_connection_string" \
  dotnet run --project "$ROOT/src/Evidgate.Migrator"

# 3. Credentials for the application roles created by the migrations: LOGIN +
#    password are granted here, by the superuser bootstrap, never in a migration
#    (00 §5.7 S3). The statements live in scripts/env/lib-db.sh.
evg_db_grant_application_logins

# 3b. The dev seed: the synthetic tenant, the dev project's sample case, suite
#     and suite revision (written by the Catalog module, so a manifest has a suite
#     revision to name), and with --with-token one ingest token and one sample
#     policy revision. Both paths write through the application role, so both
#     follow the grants above. A minted secret reaches this script's stdout once
#     and nowhere else.
if [ "${EVIDGATE_DB_INIT_SEED:-1}" != "0" ]; then
  app_connection_string="Host=$DB_HOST;Port=$DB_PORT;Database=$DB_NAME;Username=evidgate_app;Password=$(dsn_quote "$EVIDGATE_APP_PASSWORD")"
  if [ "$with_token" = true ]; then
    printf 'db-init: seeding the dev tenant, the dev catalog suite, the ingest token and the policy (seed-dev --with-token)\n'
    seed_args=(seed-dev --with-token)
    pepper="${EVIDGATE__CI__PEPPER:-}"
  else
    printf 'db-init: seeding the dev tenant and the dev catalog suite (seed-dev)\n'
    seed_args=(seed-dev)
    pepper=""
  fi
  DOTNET_ENVIRONMENT=Development \
    EVIDGATE_MIGRATOR__CONNECTIONSTRING="$migrator_connection_string" \
    EVIDGATE__DATABASE__CONNECTIONSTRING="$app_connection_string" \
    EVIDGATE__CI__PEPPER="$pepper" \
    dotnet run --project "$ROOT/src/Evidgate.Migrator" -- "${seed_args[@]}"
  if [ "$with_token" = true ]; then
    # The note must hold on a rerun too, and it cannot tell which case happened
    # without capturing the seed's stdout, the one thing this path must not do.
    printf 'db-init: if a line EVIDGATE_DEV_TOKEN=... was printed above, its secret is shown once and stored nowhere: export it in your shell (docs/dev-setup.md).\n'
    printf "db-init: if none was printed, a live 'dev-seed' ingest token already exists; its secret was shown once and cannot be shown again. To get a new one run: scripts/dev/down.sh && scripts/dev/up.sh --reset --with-token\n"
  fi
fi

cat <<EOF
db-init: done — $DB_NAME is at head with evidgate_owner, evidgate_app, evidgate_operator, evidgate_worker_sched, evidgate_worker, evidgate_platform_operator.

# Evidgate dev environment template (placeholders; fill in the passwords you exported).
# Keep this out of version control: .env* is git-ignored (00 §5.7 S3).
#
# Api/Worker (01.1.2-T1.2) — application role, subject to RLS:
export EVIDGATE__DATABASE__CONNECTIONSTRING='Host=$DB_HOST;Port=$DB_PORT;Database=$DB_NAME;Username=evidgate_app;Password=<EVIDGATE_APP_PASSWORD>'
# Migrator (01.4.1) — owner role, the only writer of public.schema_migration:
export EVIDGATE_MIGRATOR__CONNECTIONSTRING='Host=$DB_HOST;Port=$DB_PORT;Database=$DB_NAME;Username=evidgate_owner;Password=<EVIDGATE_OWNER_PASSWORD>'
# Audited support path (TL T76) and owner SECURITY DEFINER sweeps (TL T80):
#   evidgate_operator      Password=<EVIDGATE_OPERATOR_PASSWORD>   (SELECT only)
#   evidgate_worker_sched  Password=<EVIDGATE_WORKER_SCHED_PASSWORD> (EXECUTE per function)
#
# Platform operator (08.6.3) — psql only and no host holds it: this role is the
# one grantee of evidgate.healing_switch_set_system, so the platform-wide row is
# set by a human, never by an Api request.
#   evidgate_platform_operator Password=<EVIDGATE_PLATFORM_OPERATOR_PASSWORD> (the switch definer only)
#
# Worker (01.1.2) — the outbox dispatcher and the task queue machinery. The Api
# never holds this one: a tenant read of the queue goes through the two scoped
# views instead.
export EVIDGATE__TASKS__WORKERCONNECTIONSTRING='Host=$DB_HOST;Port=$DB_PORT;Database=$DB_NAME;Username=evidgate_worker;Password=<EVIDGATE_WORKER_PASSWORD>'
# A password containing ';' or '"' must be wrapped in double quotes inside the
# connection string (Npgsql's quoted value form), e.g. Password="a;b".
EOF
