# `--with-db` checks for scripts/build-images.sh (AC1/AC5, 10.2.1-T1.1/T1.2).
#
# Sourced by scripts/build-images.sh after it creates the throwaway containers'
# names (network, db_container, api_container) and installs its cleanup trap: the
# driver owns those and the container lifecycle, this file owns what is run
# against them. The images under test are the built ones, so every check fails
# the run instead of skipping.
#
# The dev stack's PostgreSQL pin (infra/compose.dev.yml, tests/Shared/ContainerImages.cs).
# The dev stack's PostgreSQL pin (infra/compose.dev.yml, tests/Shared/ContainerImages.cs).
POSTGRES_IMAGE='postgres:18.6@sha256:5a5a84b19854a9ffaa54082c166ff4ec27473a361e496e5ea167f298f2da9722'
OWNER_PASSWORD='imagecheck-owner'
APP_PASSWORD='imagecheck-app'

# --- --with-db ----------------------------------------------------------------

db_begin() {
  local deadline
  echo "== starting a throwaway $POSTGRES_IMAGE"
  docker network create "$network" >/dev/null
  docker run -d --name "$db_container" --network "$network" --network-alias db \
    -e POSTGRES_PASSWORD=imagecheck \
    -e POSTGRES_USER=postgres \
    -e POSTGRES_DB=postgres \
    -e POSTGRES_INITDB_ARGS='--auth-host=scram-sha-256' \
    "$POSTGRES_IMAGE" >/dev/null
  deadline=$((SECONDS + 120))
  until docker exec "$db_container" pg_isready -h 127.0.0.1 -p 5432 -U postgres -d postgres >/dev/null 2>&1; do
    [[ $SECONDS -lt $deadline ]] || die "the throwaway PostgreSQL did not become ready within 120s"
    sleep 1
  done
  docker exec -i "$db_container" psql -U postgres -d postgres -v ON_ERROR_STOP=1 -q <<'SQL' >/dev/null
create role evidgate_owner login createrole nosuperuser nocreatedb nobypassrls password 'imagecheck-owner';
create database evidgate owner evidgate_owner;
SQL
  echo "   ready: database evidgate owned by evidgate_owner"
}

db_connection_string() { echo "Host=db;Port=5432;Database=evidgate;Username=evidgate_owner;Password=$OWNER_PASSWORD"; }

check_migrator_status() {
  local reference="$1" output status
  set +e
  output="$(docker run --rm --platform "$CHECK_PLATFORM" --network "$network" \
    -e "EVIDGATE_MIGRATOR__CONNECTIONSTRING=$(db_connection_string)" \
    "$reference" --status 2>&1)"
  status=$?
  set -e
  printf '%s\n' "$output" | sed 's/^/   | /'
  [[ "$status" -eq 0 ]] || die "'--status' exited $status against the database, expected 0"
  printf '%s\n' "$output" | grep -qiE 'pending' \
    || die "'--status' printed no applied/pending summary"
  echo "   --status: exit 0"
}

check_migrator_apply() {
  local reference="$1"
  echo "-- applying the image's migration set to the throwaway database"
  docker run --rm --platform "$CHECK_PLATFORM" --network "$network" \
    -e "EVIDGATE_MIGRATOR__CONNECTIONSTRING=$(db_connection_string)" \
    "$reference" | sed 's/^/   | /'
}

check_api_health() {
  local reference="$1" host_port deadline live ready
  echo "-- starting the Api image against the migrated database"
  # 0001_foundation_roles.sql deliberately creates evidgate_app with nologin and
  # no password (a password in a migration would live in git history); the deploy
  # bootstrap grants them, and so does this check (scripts/dev/db-init.sh line 178).
  # The Api refuses to serve as any other role (00 §5.4.3), which its own log shows.
  docker exec "$db_container" psql -U postgres -d evidgate -v ON_ERROR_STOP=1 -q \
    -c "alter role evidgate_app login password '$APP_PASSWORD';" >/dev/null
  docker run -d --platform "$CHECK_PLATFORM" --name "$api_container" --network "$network" \
    -p 127.0.0.1::8080 \
    -e "EVIDGATE__DATABASE__CONNECTIONSTRING=Host=db;Port=5432;Database=evidgate;Username=evidgate_app;Password=$APP_PASSWORD" \
    "$reference" >/dev/null
  host_port="$(docker port "$api_container" 8080/tcp | head -1)"
  host_port="${host_port##*:}"
  [[ -n "$host_port" ]] || die "the Api container published no host port"
  deadline=$((SECONDS + 60))
  live=""
  until live="$(curl -fsS --max-time 2 "http://127.0.0.1:$host_port/internal/health/live" 2>/dev/null)"; do
    [[ $SECONDS -lt $deadline ]] || {
      docker logs "$api_container" 2>&1 | tail -20 | sed 's/^/   | /'
      die "the Api image did not answer /internal/health/live within 60s"
    }
    sleep 1
  done
  printf '%s\n' "$live" | sed 's/^/   live:  /'
  printf '%s\n' "$live" | grep -q '"status"' \
    || die "the live endpoint did not return the 00 §9.1 health envelope"
  ready="$(curl -fsS --max-time 5 "http://127.0.0.1:$host_port/internal/health/ready" 2>/dev/null || true)"
  printf '%s\n' "$ready" | sed 's/^/   ready: /'
  printf '%s\n' "$ready" | grep -q '"status"' \
    || die "the ready endpoint did not return the 00 §9.1 health envelope"
  echo "   /internal/health/live + /ready served by the built Api image"
}
