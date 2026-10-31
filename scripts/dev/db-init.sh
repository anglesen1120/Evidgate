#!/usr/bin/env bash
# 01.4.1-T2.3 — local database bootstrap for the dev stack (plans/01-foundation.md §6).
#
# Creates the migration database and its owner credential as superuser, brings it
# to head with Evidgate.Migrator (migration 0001 creates the three application
# roles), then grants LOGIN + a password to those roles as superuser and prints
# the EVIDGATE_* environment template. This mirrors the production separation
# (owner runs migrations, app serves traffic, operator reads, worker_sched
# sweeps) so an RLS mistake fails locally first.
#
# No credential is committed and none is passed on a command line: the four
# passwords are read from the environment, exported to the psql children as
# environment variables, and read inside psql with \getenv — so they never appear
# in `ps` output (00 §5.7 S3). The template printed at the end carries
# placeholders, never the values.
#
# usage: scripts/dev/db-init.sh [--with-token] [--help]
#
#   --with-token  after the role grants, run `Evidgate.Migrator seed-dev
#                 --with-token`: the dev tenant plus one dev ingest token and one
#                 sample policy revision, minted through the real commands on the
#                 app role. The token secret is written to this script's stdout
#                 only, once: it is never captured, tee'd, exported or logged.
#                 Requires EVIDGATE__CI__PEPPER in the environment.
#
# required environment:
#   EVIDGATE_OWNER_PASSWORD       password for the migration role
#   EVIDGATE_APP_PASSWORD         password for the application role
#   EVIDGATE_OPERATOR_PASSWORD    password for the audited read-only role
#   EVIDGATE_WORKER_SCHED_PASSWORD password for the sweep role
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

DB_HOST="${EVIDGATE_DB_HOST:-127.0.0.1}"
DB_PORT="${EVIDGATE_DB_PORT:-5432}"
DB_NAME="${EVIDGATE_DB_NAME:-evidgate}"
DB_SUPERUSER="${EVIDGATE_DB_SUPERUSER:-postgres}"
DB_SUPERUSER_PASSWORD="${EVIDGATE_DB_SUPERUSER_PASSWORD:-postgres}"
DB_CONTAINER="${EVIDGATE_DB_CONTAINER:-}"

# Password variables psql reads with \getenv; exported so both the host psql and
# `docker exec -e NAME` (which copies from this environment, never from argv) see
# them.
PASSWORD_VARS=(
  EVIDGATE_OWNER_PASSWORD
  EVIDGATE_APP_PASSWORD
  EVIDGATE_OPERATOR_PASSWORD
  EVIDGATE_WORKER_SCHED_PASSWORD
)

usage() {
  cat <<'USAGE'
Evidgate local database bootstrap (01.4.1-T2.3).

usage: scripts/dev/db-init.sh [--with-token] [--help]

Creates the database and its owner credential as superuser, applies the
migrations as evidgate_owner (migration 0001 creates evidgate_app,
evidgate_operator and evidgate_worker_sched), grants those roles LOGIN +
a password as superuser and prints the EVIDGATE_* environment template.

  --with-token  after the grants, also run `seed-dev --with-token`: the dev
                tenant, one project-bound dev ingest token (artifact:write,
                event:write, run:create) and one sample policy revision. The token
                is printed here ONCE; it is never written to a log or a file.

required environment:
  EVIDGATE_OWNER_PASSWORD        password for the migration role
  EVIDGATE_APP_PASSWORD          password for the application role
  EVIDGATE_OPERATOR_PASSWORD     password for the audited read-only role
  EVIDGATE_WORKER_SCHED_PASSWORD password for the sweep role

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

die() {
  printf 'db-init: %s\n' "$*" >&2
  exit 1
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
    *) die "unexpected argument '$arg' (try --help)" ;;
  esac
done

missing=""
for name in "${PASSWORD_VARS[@]}"; do
  if [[ -z "${!name:-}" ]]; then
    missing="$missing $name"
  fi
done
if [[ -n "$missing" ]]; then
  die "missing required environment:$missing (see --help)"
fi

# Exported so both a host psql and `docker exec -e NAME` (which copies from this
# environment, never from argv) can read them with \getenv.
export EVIDGATE_OWNER_PASSWORD
export EVIDGATE_APP_PASSWORD
export EVIDGATE_OPERATOR_PASSWORD
export EVIDGATE_WORKER_SCHED_PASSWORD

# psql lives in the PostgreSQL container on this host: prefer the container path
# when told which container to use, otherwise require a host psql.
if [[ -n "$DB_CONTAINER" ]]; then
  command -v docker >/dev/null 2>&1 || die "docker is required with EVIDGATE_DB_CONTAINER"
  psql_super() {
    docker exec -i \
      -e EVIDGATE_OWNER_PASSWORD -e EVIDGATE_APP_PASSWORD \
      -e EVIDGATE_OPERATOR_PASSWORD -e EVIDGATE_WORKER_SCHED_PASSWORD \
      "$DB_CONTAINER" psql -X -q -v ON_ERROR_STOP=1 -U "$DB_SUPERUSER" -d postgres "$@"
  }
elif command -v psql >/dev/null 2>&1; then
  psql_super() {
    PGPASSWORD="$DB_SUPERUSER_PASSWORD" psql -X -q -v ON_ERROR_STOP=1 \
      -h "$DB_HOST" -p "$DB_PORT" -U "$DB_SUPERUSER" -d postgres "$@"
  }
else
  die "no psql on PATH — install it, or set EVIDGATE_DB_CONTAINER to a running PostgreSQL container
     (e.g. EVIDGATE_DB_CONTAINER=\$(docker ps --filter ancestor=postgres --format '{{.Names}}' | head -1))"
fi

printf 'db-init: preparing %s on %s:%s as superuser %s\n' "$DB_NAME" "$DB_HOST" "$DB_PORT" "$DB_SUPERUSER"

# 1. Migration role + database. The owner needs LOGIN (it connects to migrate) and
#    CREATEROLE (migration 0001 creates the three application roles as the owner,
#    because a non-superuser migration role cannot grant SUPERUSER/BYPASSRLS flags).
psql_super -v db_name="$DB_NAME" <<'SQL'
\getenv owner_password EVIDGATE_OWNER_PASSWORD

do $$
begin
  if not exists (select 1 from pg_catalog.pg_roles where rolname = 'evidgate_owner') then
    create role evidgate_owner login createrole;
  end if;
end
$$;

alter role evidgate_owner login createrole nosuperuser nocreatedb nobypassrls password :'owner_password';

select format('create database %I owner evidgate_owner', :'db_name')
 where not exists (select 1 from pg_catalog.pg_database where datname = :'db_name')
\gexec
SQL

# 2. Bring the database to head as evidgate_owner; migration 0001 creates
#    evidgate_app, evidgate_operator and evidgate_worker_sched without LOGIN.
printf 'db-init: applying migrations as evidgate_owner\n'
migrator_connection_string="Host=$DB_HOST;Port=$DB_PORT;Database=$DB_NAME;Username=evidgate_owner;Password=$(dsn_quote "$EVIDGATE_OWNER_PASSWORD")"
# The status probe is the first connect as evidgate_owner: on success its output is
# noise, but on failure it is the only evidence (wrong server, missing role) — the
# migrations that follow would fail with something less specific.
if ! status_output="$(EVIDGATE_MIGRATOR__CONNECTIONSTRING="$migrator_connection_string" \
  dotnet run --project "$ROOT/src/Evidgate.Migrator" -- --status 2>&1)"; then
  printf '%s\n' "$status_output" >&2
  die "Evidgate.Migrator --status failed against $DB_HOST:$DB_PORT/$DB_NAME (output above)"
fi
EVIDGATE_MIGRATOR__CONNECTIONSTRING="$migrator_connection_string" \
  dotnet run --project "$ROOT/src/Evidgate.Migrator"

# 2b. The synthetic dev tenant (01.8.1-T3.1): org, project, dev user with its roles and a
#     CI binding. Idempotent; the command itself refuses anything but a local Development
#     database, and `--no-seed` skips it. Under --with-token it runs in step 3b instead,
#     because that invocation also mints through the app role, which needs the LOGIN
#     granted in step 3.
if [ "${EVIDGATE_DB_INIT_SEED:-1}" != "0" ] && [ "$with_token" = false ]; then
  printf 'db-init: seeding the dev tenant (seed-dev)\n'
  DOTNET_ENVIRONMENT=Development EVIDGATE_MIGRATOR__CONNECTIONSTRING="$migrator_connection_string" \
    dotnet run --project "$ROOT/src/Evidgate.Migrator" -- seed-dev
fi

# 3. Credentials for the application roles created by migration 0001: LOGIN +
#    password are granted here, by the superuser bootstrap, never in a migration
#    (00 §5.7 S3).
psql_super <<'SQL'
\getenv app_password EVIDGATE_APP_PASSWORD
\getenv operator_password EVIDGATE_OPERATOR_PASSWORD
\getenv worker_sched_password EVIDGATE_WORKER_SCHED_PASSWORD

alter role evidgate_app login password :'app_password';
alter role evidgate_operator login password :'operator_password';
alter role evidgate_worker_sched login password :'worker_sched_password';
SQL

# 3b. The token seed (01.8.1-T4.1): the tenant above plus one ingest token and one
#     sample policy revision, minted through the real commands on the app role, so it
#     follows the grants above (that role needs LOGIN) and both credentials travel as
#     environment variables, never as arguments. The token secret reaches this script's
#     stdout only: it is not captured in a variable, tee'd, or written to a log, because
#     any copy would outlive the run in a file the developer did not mean to create.
if [ "${EVIDGATE_DB_INIT_SEED:-1}" != "0" ] && [ "$with_token" = true ]; then
  printf 'db-init: seeding the dev tenant and the dev ingest token (seed-dev --with-token)\n'
  app_connection_string="Host=$DB_HOST;Port=$DB_PORT;Database=$DB_NAME;Username=evidgate_app;Password=$(dsn_quote "$EVIDGATE_APP_PASSWORD")"
  DOTNET_ENVIRONMENT=Development \
    EVIDGATE_MIGRATOR__CONNECTIONSTRING="$migrator_connection_string" \
    EVIDGATE__DATABASE__CONNECTIONSTRING="$app_connection_string" \
    EVIDGATE__CI__PEPPER="${EVIDGATE__CI__PEPPER:-}" \
    dotnet run --project "$ROOT/src/Evidgate.Migrator" -- seed-dev --with-token
  printf 'db-init: the token above is shown once and is not stored anywhere; export it in your shell as EVIDGATE_DEV_TOKEN (docs/dev-setup.md)\n'
fi

cat <<EOF
db-init: done — $DB_NAME is at head with evidgate_owner, evidgate_app, evidgate_operator, evidgate_worker_sched.

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
# A password containing ';' or '"' must be wrapped in double quotes inside the
# connection string (Npgsql's quoted value form), e.g. Password="a;b".
EOF
