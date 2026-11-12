#!/usr/bin/env bash
# Per-environment database bootstrap: the PostgreSQL 18 database,
# the five Evidgate roles and the four application credentials, one environment
# at a time. The dev stack's scripts/dev/db-init.sh runs the same statements
# through scripts/env/lib-db.sh, so the two cannot drift.
#
# The steps are separate because the migrator runs between the two halves: the
# superuser creates evidgate_owner and the database, the deploy's migration job
# runs Evidgate.Migrator as that owner (migration 0001 creates evidgate_app,
# evidgate_operator and evidgate_worker_sched without LOGIN, and 0104 adds
# evidgate_worker), and only then can the superuser grant those four LOGIN and a
# password.
#
# No credential is passed on a command line and none is printed: the passwords
# are read from the environment by name, exported to the psql child, and read
# inside psql with \getenv. A missing variable is reported by name only.
#
# Only the deploy identity runs `database` and `credentials`: they create or
# alter roles as the database superuser, and no runtime process holds that
# credential (the admin port is reachable from the deploy identity only).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"

EVG_DB_LABEL=bootstrap-db
EVG_DB_ROOT="$ROOT"
# shellcheck source=scripts/env/lib-db.sh
. "$ROOT/scripts/env/lib-db.sh"

usage() {
  cat <<'USAGE'
Evidgate per-environment database bootstrap.

usage: scripts/env/bootstrap-db.sh <dev|staging|prod> <database|credentials|verify> [--help]

steps, one per run, in this order:
  database      as superuser: create evidgate_owner (LOGIN + CREATEROLE, never
                SUPERUSER/BYPASSRLS/CREATEDB) and the database it owns. Then run
                Evidgate.Migrator against the owner connection: migration 0001
                creates the three application roles, 0104 evidgate_worker.
  credentials   as superuser: LOGIN + a password for evidgate_app,
                evidgate_operator, evidgate_worker_sched and evidgate_worker.
                Refuses, naming the missing roles, until the migrator created
                them.
  verify        read-only assertions as superuser against the database:
                * the non-built-in roles are exactly the five evidgate_* roles
                  (anything else is named; pg_* and the bootstrap superuser are
                  allowed);
                * all five may log in, and none carries SUPERUSER, BYPASSRLS,
                  REPLICATION, CREATEDB or CREATEROLE; only evidgate_owner
                  carries CREATEROLE;
                * no application role is a member of any other role, apart from
                  the owner's admin-only record of the roles it creates
                  (no INHERIT, no SET);
                * evidgate_app owns no object in the database;
                * every public table is not an exceptions entry of kind
                  `mechanism` in contracts/rls-exceptions.txt and carries both
                  ENABLE + FORCE row level security.
                Prints PASS/FAIL lines and exits non-zero on any failure.

required environment, per step (values are never printed):
  database     EVIDGATE_OWNER_PASSWORD
  credentials  EVIDGATE_APP_PASSWORD, EVIDGATE_OPERATOR_PASSWORD,
               EVIDGATE_WORKER_SCHED_PASSWORD, EVIDGATE_WORKER_PASSWORD
  verify       none (the superuser connection only)

per-environment variables (a bracketed value is the dev-only default; staging
and prod have no host, superuser or superuser-password default):
  EVIDGATE_DB_HOST [127.0.0.1]           staging/prod: required; dev: loopback
                                         only unless EVIDGATE_DB_CONTAINER is set
  EVIDGATE_DB_PORT [5432]
  EVIDGATE_DB_NAME [evidgate, evidgate_staging, evidgate_prod]
                                         ^[a-z_][a-z0-9_]{0,62}$; staging refuses
                                         evidgate_prod and prod refuses
                                         evidgate_staging, because the roles are
                                         cluster-wide and the two environments
                                         need their own server
  EVIDGATE_DB_SUPERUSER [postgres]       staging/prod: required
  EVIDGATE_DB_SUPERUSER_PASSWORD [postgres]
                                         staging/prod: required
  EVIDGATE_DB_CONTAINER                  dev: run psql via `docker exec -i <name>`;
                                         staging/prod: refused (the environment's
                                         own server, never a local container)
  PGSSLMODE                              staging/prod: set to require when unset
  EVIDGATE_BOOTSTRAP_CONFIRM_ENV         prod: must be exactly prod
USAGE
}

ENVIRONMENT=""
STEP=""
for arg in "$@"; do
  case "$arg" in
    -h | --help)
      usage
      exit 0
      ;;
    dev | staging | prod)
      if [[ -n "$ENVIRONMENT" ]]; then
        evg_db_die "environment given twice ('$ENVIRONMENT' then '$arg')"
      fi
      ENVIRONMENT="$arg"
      ;;
    database | credentials | verify)
      if [[ -n "$STEP" ]]; then
        evg_db_die "step given twice ('$STEP' then '$arg')"
      fi
      STEP="$arg"
      ;;
    *)
      evg_db_die "unexpected argument '$arg' (try --help)"
      ;;
  esac
done

if [[ -z "$ENVIRONMENT" || -z "$STEP" ]]; then
  usage >&2
  evg_db_die "expected an environment and a step (see --help)"
fi

# A prod bootstrap creates or changes the production roles: make it deliberate
# rather than a mistyped environment name, and before anything connects.
if [[ "$ENVIRONMENT" == prod && "${EVIDGATE_BOOTSTRAP_CONFIRM_ENV:-}" != prod ]]; then
  evg_db_die "refusing the prod bootstrap: EVIDGATE_BOOTSTRAP_CONFIRM_ENV must be set to 'prod'"
fi

# dev keeps the local defaults the dev stack already uses. staging and prod take
# the server and its superuser from the environment, so a wrong or missing value
# can never silently bootstrap the developer's local server.
case "$ENVIRONMENT" in
  dev)
    DB_HOST="${EVIDGATE_DB_HOST:-127.0.0.1}"
    DB_PORT="${EVIDGATE_DB_PORT:-5432}"
    DB_NAME="${EVIDGATE_DB_NAME:-evidgate}"
    DB_SUPERUSER="${EVIDGATE_DB_SUPERUSER:-postgres}"
    DB_SUPERUSER_PASSWORD="${EVIDGATE_DB_SUPERUSER_PASSWORD:-postgres}"
    ;;
  staging | prod)
    evg_db_require_env EVIDGATE_DB_HOST EVIDGATE_DB_SUPERUSER EVIDGATE_DB_SUPERUSER_PASSWORD
    DB_HOST="$EVIDGATE_DB_HOST"
    DB_PORT="${EVIDGATE_DB_PORT:-5432}"
    DB_NAME="${EVIDGATE_DB_NAME:-evidgate_$ENVIRONMENT}"
    DB_SUPERUSER="$EVIDGATE_DB_SUPERUSER"
    DB_SUPERUSER_PASSWORD="$EVIDGATE_DB_SUPERUSER_PASSWORD"
    ;;
esac
DB_CONTAINER="${EVIDGATE_DB_CONTAINER:-}"

# Every guard below runs before anything connects.
if [[ ! "$DB_NAME" =~ ^[a-z_][a-z0-9_]{0,62}$ ]]; then
  evg_db_die "refusing EVIDGATE_DB_NAME '$DB_NAME': the name must match ^[a-z_][a-z0-9_]{0,62}\$"
fi

# The four roles are cluster-wide, so two environments on one server would share
# them: prod would carry whatever staging granted, and the other way round.
case "$ENVIRONMENT:$DB_NAME" in
  staging:evidgate_prod | prod:evidgate_staging)
    evg_db_die "refusing EVIDGATE_DB_NAME '$DB_NAME' for $ENVIRONMENT: staging and prod need
     separate clusters, because evidgate_owner and the application roles are cluster-wide"
    ;;
esac

# A managed environment reaches its own server, never a local container.
if [[ "$ENVIRONMENT" != dev && -n "$DB_CONTAINER" ]]; then
  evg_db_die "refusing EVIDGATE_DB_CONTAINER '$DB_CONTAINER' for $ENVIRONMENT: unset it, or use the dev environment for a local container"
fi

# Container mode ignores the host, so this is the only case where a dev
# bootstrap connects to whatever EVIDGATE_DB_HOST names.
if [[ "$ENVIRONMENT" == dev && -z "$DB_CONTAINER" ]]; then
  case "$DB_HOST" in
    127.0.0.1 | localhost | ::1) ;;
    *)
      evg_db_die "refusing EVIDGATE_DB_HOST '$DB_HOST' for dev: dev bootstraps the local stack —
       use a loopback host, or set EVIDGATE_DB_CONTAINER to run psql inside the container"
      ;;
  esac
fi

# The TLS policy of the environment's server is not this script's to relax, so
# require TLS unless the deploy already chose a mode.
if [[ "$ENVIRONMENT" != dev && -z "${PGSSLMODE:-}" ]]; then
  export PGSSLMODE=require
fi

case "$STEP" in
  database)
    evg_db_require_env EVIDGATE_OWNER_PASSWORD
    evg_db_export_passwords EVIDGATE_OWNER_PASSWORD
    ;;
  credentials)
    evg_db_require_env EVIDGATE_APP_PASSWORD EVIDGATE_OPERATOR_PASSWORD EVIDGATE_WORKER_SCHED_PASSWORD \
      EVIDGATE_WORKER_PASSWORD
    evg_db_export_passwords \
      EVIDGATE_APP_PASSWORD EVIDGATE_OPERATOR_PASSWORD EVIDGATE_WORKER_SCHED_PASSWORD EVIDGATE_WORKER_PASSWORD
    ;;
  verify)
    # The superuser connection is the only credential verify needs.
    ;;
esac

evg_db_psql_init "$DB_CONTAINER" "$DB_HOST" "$DB_PORT" "$DB_SUPERUSER" "$DB_SUPERUSER_PASSWORD"

printf 'bootstrap-db: %s %s: database %s on %s:%s as superuser %s\n' \
  "$ENVIRONMENT" "$STEP" "$DB_NAME" "$DB_HOST" "$DB_PORT" "$DB_SUPERUSER"

# --- verify ------------------------------------------------------------------

verify_failures=0

verify_pass() {
  printf 'PASS %s: %s\n' "$1" "$2"
}

verify_fail() {
  printf 'FAIL %s: %s\n' "$1" "$2"
  verify_failures=$((verify_failures + 1))
}

# The role catalogue is cluster-wide, so this reads it through the target
# database and holds every role that is neither PostgreSQL's own (pg_*) nor the
# bootstrap superuser to the five Evidgate roles.
evg_db_verify_roles() {
  local before="$verify_failures" rows name extra="" missing="" seen=""
  local expected="evidgate_owner evidgate_app evidgate_operator evidgate_worker_sched evidgate_worker"

  rows="$(evg_db_psql "$DB_NAME" -t -A <<'SQL'
select rolname from pg_catalog.pg_roles order by rolname
SQL
)"

  while IFS= read -r name; do
    if [[ -z "$name" ]]; then
      continue
    fi
    case "$name" in
      pg_*) continue ;;
    esac
    if [[ "$name" == "$DB_SUPERUSER" ]]; then
      continue
    fi
    case " $expected " in
      *" $name "*) seen="$seen $name" ;;
      *) extra="$extra $name" ;;
    esac
  done <<<"$rows"

  for name in $expected; do
    case " $seen " in
      *" $name "*) ;;
      *) missing="$missing $name" ;;
    esac
  done

  if [[ -n "$extra" ]]; then
    verify_fail "roles" "unexpected role(s):$extra (allowed: pg_*, $DB_SUPERUSER, $expected)"
  fi
  if [[ -n "$missing" ]]; then
    verify_fail "roles" "missing role(s):$missing — run the migrator (migration 0001 creates them) and the credentials step"
  fi
  if [[ "$verify_failures" == "$before" ]]; then
    verify_pass "roles" "exactly $expected, plus pg_* and $DB_SUPERUSER"
  fi
}

evg_db_verify_role_attributes() {
  local before="$verify_failures" rows name is_super bypass createdb createrole replication canlogin count=0

  rows="$(evg_db_psql "$DB_NAME" -t -A -F '|' <<'SQL'
select rolname, rolsuper, rolbypassrls, rolcreatedb, rolcreaterole, rolreplication, rolcanlogin
  from pg_catalog.pg_roles
 where rolname in ('evidgate_owner', 'evidgate_app', 'evidgate_operator', 'evidgate_worker_sched',
                   'evidgate_worker')
 order by rolname
SQL
)"

  while IFS='|' read -r name is_super bypass createdb createrole replication canlogin; do
    if [[ -z "$name" ]]; then
      continue
    fi
    count=$((count + 1))
    if [[ "$is_super" != f ]]; then
      verify_fail "attributes" "$name: rolsuper must be false (found true)"
    fi
    if [[ "$bypass" != f ]]; then
      verify_fail "attributes" "$name: rolbypassrls must be false (found true)"
    fi
    if [[ "$replication" != f ]]; then
      verify_fail "attributes" "$name: rolreplication must be false (found true)"
    fi
    if [[ "$createdb" != f ]]; then
      verify_fail "attributes" "$name: rolcreatedb must be false (found true)"
    fi
    # verify runs after `credentials`, so every role has to carry LOGIN by now.
    if [[ "$canlogin" != t ]]; then
      verify_fail "attributes" "$name: rolcanlogin must be true (found false) — run the credentials step"
    fi
    if [[ "$name" == evidgate_owner ]]; then
      if [[ "$createrole" != t ]]; then
        verify_fail "attributes" "evidgate_owner: rolcreaterole must be true (found false)"
      fi
    elif [[ "$createrole" != f ]]; then
      verify_fail "attributes" "$name: rolcreaterole must be false (found true)"
    fi
  done <<<"$rows"

  if [[ "$count" -ne 5 ]]; then
    verify_fail "attributes" "found $count of the five roles, so their attributes cannot all be checked (see the roles check)"
    return 0
  fi
  if [[ "$verify_failures" == "$before" ]]; then
    verify_pass "attributes" "all five may log in; no SUPERUSER, BYPASSRLS, REPLICATION or CREATEDB on any role; CREATEROLE only on evidgate_owner"
  fi
}

# Any membership of an application role in another role makes the app role that
# role through SET ROLE, so every such edge fails except the admin-only record
# PostgreSQL writes when evidgate_owner creates the application roles.
evg_db_verify_role_memberships() {
  local before="$verify_failures" offenders

  offenders="$(evg_db_psql "$DB_NAME" -t -A <<'SQL'
with recursive members(oid, rolname) as (
  select oid, rolname
    from pg_catalog.pg_roles
   where rolname in ('evidgate_owner', 'evidgate_app', 'evidgate_operator', 'evidgate_worker_sched',
                     'evidgate_worker')
), walk(origin, reached) as (
  select oid, oid from members
  union
  select w.origin, am.roleid
    from walk w
    join pg_catalog.pg_auth_members am on am.member = w.reached
   where am.inherit_option or am.set_option
), offender(role_oid, target_oid) as (
  select am.member, am.roleid
    from pg_catalog.pg_auth_members am
   where am.member in (select oid from members)
     and not (
       -- The owner created the application roles, and PG records that as an
       -- admin-only edge: it lets the owner administer them, not act as them.
       am.member = (select oid from pg_catalog.pg_roles where rolname = 'evidgate_owner')
       and am.roleid in (select oid from pg_catalog.pg_roles
                          where rolname in ('evidgate_app', 'evidgate_operator', 'evidgate_worker_sched',
                                            'evidgate_worker'))
       and am.admin_option
       and not am.inherit_option
       and not am.set_option
     )
  union
  -- Authority reached through a chain of SET or INHERIT edges, whatever the hop.
  select w.origin, w.reached
    from walk w
   where w.reached <> w.origin
)
select string_agg(hit, '; ' order by hit)
  from (
    select distinct format('%s is a member of %s', m.rolname, g.rolname) as hit
      from offender o
      join pg_catalog.pg_roles m on m.oid = o.role_oid
      join pg_catalog.pg_roles g on g.oid = o.target_oid
  ) hits
SQL
)"

  if [[ -n "$offenders" ]]; then
    verify_fail "memberships" "$offenders"
  elif [[ "$verify_failures" == "$before" ]]; then
    verify_pass "memberships" "no application role is a member of the owner, of another application role, of a pg_* role or of $DB_SUPERUSER"
  fi
}

# Ownership is the other half of tenant isolation: the app role may hold DML
# grants but must own nothing, because an owner keeps its privileges through a
# REVOKE and can drop the table a policy protects.
evg_db_verify_app_ownership() {
  local before="$verify_failures" owned

  owned="$(evg_db_psql "$DB_NAME" -t -A <<'SQL'
-- pg_shdepend records an ownership row for every object class (relation,
-- routine, type, collation, publication, schema, ...), so one query covers the
-- classes a hand-written list would miss. dbid 0 is shared with the cluster.
select string_agg(owned, ', ' order by owned)
  from (
    select distinct pg_describe_object(classid, objid, objsubid) as owned
      from pg_catalog.pg_shdepend
     where refclassid = 'pg_catalog.pg_authid'::regclass
       and refobjid = (select oid from pg_catalog.pg_roles where rolname = 'evidgate_app')
       and deptype = 'o'
       and classid <> 'pg_catalog.pg_database'::regclass
       and dbid in (0, (select oid from pg_catalog.pg_database where datname = current_database()))
    union all
    select 'database ' || datname
      from pg_catalog.pg_database
     where datname = current_database()
       and datdba = (select oid from pg_catalog.pg_roles where rolname = 'evidgate_app')
  ) owned
SQL
)"

  if [[ -n "$owned" ]]; then
    verify_fail "ownership" "evidgate_app owns: $owned"
  elif [[ "$verify_failures" == "$before" ]]; then
    verify_pass "ownership" "evidgate_app owns no object (database, schema, relation, routine, type, collation, publication)"
  fi
}

# The exception list is the single source for tables outside the rule set, so the
# verifier parses it rather than keeping a copy. Only `mechanism` entries are
# outside row level security entirely; `platform` and `catalogue` tables still
# need ENABLE + FORCE.
evg_db_mechanism_tables() {
  local file="$EVG_DB_ROOT/contracts/rls-exceptions.txt"
  if [[ ! -f "$file" ]]; then
    evg_db_die "missing $file (the RLS exception source)"
  fi
  awk '
    { sub(/#.*/, "") }
    NF >= 2 && $2 == "mechanism" && $1 ~ /^public\./ {
      sub(/^public\./, "", $1)
      print $1
    }
  ' "$file"
}

evg_db_verify_force_rls() {
  local before="$verify_failures" mechanism rows table enabled forced
  local offenders="" tables=0 skipped=0

  mechanism="$(evg_db_mechanism_tables | tr '\n' ' ')"

  rows="$(evg_db_psql "$DB_NAME" -t -A -F '|' <<'SQL'
select c.relname, c.relrowsecurity, c.relforcerowsecurity
  from pg_catalog.pg_class c
  join pg_catalog.pg_namespace n on n.oid = c.relnamespace
 where n.nspname = 'public'
   and c.relkind in ('r', 'p')
 order by c.relname
SQL
)"

  if [[ -z "$rows" ]]; then
    verify_pass "rls" "no public tables exist yet, so there is nothing to check"
    return 0
  fi

  while IFS='|' read -r table enabled forced; do
    if [[ -z "$table" ]]; then
      continue
    fi
    tables=$((tables + 1))
    case " $mechanism " in
      *" $table "*)
        skipped=$((skipped + 1))
        continue
        ;;
    esac
    if [[ "$enabled" != t || "$forced" != t ]]; then
      offenders="$offenders $table(rowsecurity=$enabled,forcerowsecurity=$forced)"
    fi
  done <<<"$rows"

  if [[ -n "$offenders" ]]; then
    verify_fail "rls" "ENABLE + FORCE row level security missing on:$offenders"
  elif [[ "$verify_failures" == "$before" ]]; then
    verify_pass "rls" "$((tables - skipped)) public tables with ENABLE + FORCE row level security, $skipped mechanism exception(s) skipped"
  fi
}

evg_db_verify() {
  evg_db_verify_roles
  evg_db_verify_role_attributes
  evg_db_verify_role_memberships
  evg_db_verify_app_ownership
  evg_db_verify_force_rls

  if ((verify_failures > 0)); then
    printf 'bootstrap-db: verify failed — %s check(s) above name the offender\n' "$verify_failures" >&2
    exit 1
  fi
  printf 'bootstrap-db: verify passed\n'
}

# --- steps -------------------------------------------------------------------

case "$STEP" in
  database)
    evg_db_create_owner_and_database "$DB_NAME"
    printf 'bootstrap-db: evidgate_owner and %s are ready — run Evidgate.Migrator against the owner connection next\n' "$DB_NAME"
    ;;
  credentials)
    evg_db_require_application_roles
    evg_db_grant_application_logins
    printf 'bootstrap-db: evidgate_app, evidgate_operator, evidgate_worker_sched and evidgate_worker may log in to %s\n' "$DB_NAME"
    ;;
  verify)
    evg_db_verify
    ;;
esac
