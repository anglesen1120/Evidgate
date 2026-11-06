#!/usr/bin/env bash
# Shared superuser access and the role/database statements of the two
# bootstraps: the dev stack (scripts/dev/db-init.sh) and the per-environment
# script (scripts/env/bootstrap-db.sh). Sourced, never executed.
#
# No credential is passed on a command line. The host path hands psql the
# superuser password as PGPASSWORD in its environment; the container path
# forwards the role passwords with `docker exec -e NAME`, which copies them
# from this process's environment; every statement reads them inside psql with
# \getenv. A missing variable is reported by name, never by value.
#
# The caller sets EVG_DB_LABEL (the error prefix) and, for the verifier,
# EVG_DB_ROOT (the repository root holding contracts/rls-exceptions.txt).

# The four role passwords. Exported only for the action that needs them, so a
# docker exec -e NAME copies a value instead of a command-line argument.
EVG_DB_PASSWORD_VARS=(
  EVIDGATE_OWNER_PASSWORD
  EVIDGATE_APP_PASSWORD
  EVIDGATE_OPERATOR_PASSWORD
  EVIDGATE_WORKER_SCHED_PASSWORD
)

evg_db_die() {
  printf '%s: %s\n' "${EVG_DB_LABEL:-bootstrap-db}" "$*" >&2
  exit 1
}

# evg_db_require_env <NAME>... — fail naming every missing variable, never its
# value. The message matches db-init.sh's, so both scripts read the same.
evg_db_require_env() {
  local missing="" name
  for name in "$@"; do
    if [[ -z "${!name:-}" ]]; then
      missing="$missing $name"
    fi
  done
  if [[ -n "$missing" ]]; then
    evg_db_die "missing required environment:$missing (see --help)"
  fi
}

evg_db_export_passwords() {
  local name
  for name in "$@"; do
    export "$name"
  done
}

# evg_db_psql_init <container> <host> <port> <superuser> <superuser_password>
# Picks the psql path once: the PostgreSQL container when one is named (this
# host, the dev stack and the test hosts carry no psql), otherwise a host psql.
evg_db_psql_init() {
  EVG_DB_CONTAINER="$1"
  EVG_DB_HOST="$2"
  EVG_DB_PORT="$3"
  EVG_DB_SUPERUSER="$4"
  EVG_DB_SUPERUSER_PASSWORD="$5"

  if [[ -n "$EVG_DB_CONTAINER" ]]; then
    command -v docker >/dev/null 2>&1 || evg_db_die "docker is required with EVIDGATE_DB_CONTAINER"
  elif ! command -v psql >/dev/null 2>&1; then
    evg_db_die "no psql on PATH — install it, or set EVIDGATE_DB_CONTAINER to a running PostgreSQL container
     (e.g. EVIDGATE_DB_CONTAINER=\$(docker ps --filter ancestor=postgres --format '{{.Names}}' | head -1))"
  fi
}

# evg_db_psql <database> [psql arguments...] — superuser psql in quiet mode with
# ON_ERROR_STOP, so a failed statement fails the bootstrap.
evg_db_psql() {
  local database="$1"
  shift
  if [[ -n "${EVG_DB_CONTAINER:-}" ]]; then
    docker exec -i \
      -e EVIDGATE_OWNER_PASSWORD -e EVIDGATE_APP_PASSWORD \
      -e EVIDGATE_OPERATOR_PASSWORD -e EVIDGATE_WORKER_SCHED_PASSWORD \
      "$EVG_DB_CONTAINER" psql -X -q -v ON_ERROR_STOP=1 -U "$EVG_DB_SUPERUSER" -d "$database" "$@"
  else
    PGPASSWORD="$EVG_DB_SUPERUSER_PASSWORD" psql -X -q -v ON_ERROR_STOP=1 \
      -h "$EVG_DB_HOST" -p "$EVG_DB_PORT" -U "$EVG_DB_SUPERUSER" -d "$database" "$@"
  fi
}

# Step 1: the migration role and the database, as superuser. The owner needs
# LOGIN (it connects to migrate) and CREATEROLE (migration 0001 creates the
# three application roles as the owner, because a non-superuser migration role
# cannot grant SUPERUSER/BYPASSRLS flags). Both statements are conditional, so a
# rerun changes no attribute.
evg_db_create_owner_and_database() {
  evg_db_psql postgres -v db_name="$1" <<'SQL'
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
}

# Step 3: LOGIN + a password for the three roles migration 0001 created, as
# superuser and never in a migration (00 §5.7 S3).
evg_db_grant_application_logins() {
  evg_db_psql postgres <<'SQL'
\getenv app_password EVIDGATE_APP_PASSWORD
\getenv operator_password EVIDGATE_OPERATOR_PASSWORD
\getenv worker_sched_password EVIDGATE_WORKER_SCHED_PASSWORD

alter role evidgate_app login password :'app_password';
alter role evidgate_operator login password :'operator_password';
alter role evidgate_worker_sched login password :'worker_sched_password';
SQL
}

# The superuser bootstrap runs between two jobs: migration 0001 creates the
# application roles, so a grant before it has nothing to alter. Refuse first and
# name every missing role instead of failing on the first ALTER.
evg_db_require_application_roles() {
  evg_db_psql postgres <<'SQL'
do $$
declare
  missing text;
begin
  select string_agg(candidate, ', ' order by candidate)
    into missing
    from unnest(array['evidgate_app', 'evidgate_operator', 'evidgate_worker_sched']) as candidate
   where not exists (select 1 from pg_catalog.pg_roles where rolname = candidate);

  if missing is not null then
    raise exception 'application roles missing (%): run Evidgate.Migrator before the credentials step', missing;
  end if;
end
$$;
SQL
}
