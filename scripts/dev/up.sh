#!/usr/bin/env bash
# 01.8.1-T3.2 / FR-01-11 — one command from a clean checkout to a working local
# stack: PostgreSQL 18 + SeaweedFS from infra/compose.dev.yml (digest-pinned),
# the dev role split and migrations through scripts/dev/db-init.sh, the pending/
# 24 h lifecycle rule on the dev bucket (T18), then Evidgate.Api and
# Evidgate.Worker under `dotnet watch` on the host. It exits 0 only once both
# processes answer GET /internal/health/ready with 200 (AC1).
#
# usage: scripts/dev/up.sh [--reset]
#
#   --reset   stop the host processes and delete the named volumes pg-data and
#             s3-data before starting again (AC4). Without it, data survives
#             `down.sh` / `up.sh`.
#
# Secrets: the first run copies .env.dev.template to .env.dev, fills every __...__
# placeholder with a fresh random value and chmods it 600. No password is ever
# printed by this script; .env.dev is git-ignored (00 §5.7 S3).
#
# web: `ng serve` is deliberately not started — apps/web is still the 00 §5.2
# placeholder and the Angular app arrives with 09.1.1.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=scripts/dev/lib.sh
. "$SCRIPT_DIR/lib.sh"

READY_TIMEOUT_SECONDS="${EVIDGATE_UP_READY_TIMEOUT_SECONDS:-300}"
START_SECONDS="$SECONDS"

usage() {
  cat <<'USAGE'
Evidgate local dev stack (01.8.1).

usage: scripts/dev/up.sh [--reset]

Brings up PostgreSQL 18 + SeaweedFS (compose, digest-pinned images), applies the
migrations as evidgate_owner, configures the dev bucket, then starts the Api and
the Worker with `dotnet watch` and waits for both readiness endpoints.

  --reset   stop the host processes and delete the pg-data/s3-data volumes first
  -h, --help  this text

Environment read from .env.dev (created from .env.dev.template on first run):
  EVIDGATE_DB_PORT, EVIDGATE_S3_PORT, ASPNETCORE_URLS,
  EVIDGATE__WORKER__HEALTHPORT, EVIDGATE__DATABASE__CONNECTIONSTRING, ...
  EVIDGATE_UP_READY_TIMEOUT_SECONDS overrides the 300 s readiness budget.
USAGE
}

die() { evg_die "$@"; }

info() { evg_info "$@"; }

# A fresh dev password. `openssl` when present (macOS/Linux), else /dev/urandom
# through od; never printed, only written into .env.dev.
random_secret() {
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -hex 24
  else
    od -An -tx1 -N24 /dev/urandom | tr -d ' \n'
  fi
}

# AC1 from a clean checkout: .env.dev is generated once and then kept, so the
# generated credentials survive every later run.
ensure_env_file() {
  [ -f "$EVG_ENV_FILE" ] && return 0
  [ -f "$EVG_ENV_TEMPLATE" ] || die "missing $EVG_ENV_TEMPLATE"

  local content name
  content="$(<"$EVG_ENV_TEMPLATE")"
  for name in EVIDGATE_DB_SUPERUSER_PASSWORD EVIDGATE_OWNER_PASSWORD \
    EVIDGATE_APP_PASSWORD EVIDGATE_OPERATOR_PASSWORD EVIDGATE_WORKER_SCHED_PASSWORD; do
    content="${content//__${name}__/$(random_secret)}"
  done

  umask 077
  printf '%s\n' "$content" >"$EVG_ENV_FILE"
  chmod 600 "$EVG_ENV_FILE"
  info "created .env.dev from .env.dev.template (mode 600, fresh random role passwords)"
}

# dotnet installed outside PATH (the SDK tarball layout: $HOME/.dotnet) is picked
# up automatically; DOTNET_ROOT keeps the apphosts resolvable.
ensure_dotnet_on_path() {
  if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then
    export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
    PATH="$HOME/.dotnet:$PATH"
    export PATH
    info "using the .NET SDK at $DOTNET_ROOT"
  fi
  command -v dotnet >/dev/null 2>&1 \
    || die "the .NET 10 SDK is required on PATH (docs/dev-setup.md)"
}

# colima publishes the daemon on a socket that is not the default one; use it
# only when no other daemon answers, so Docker Desktop setups are untouched.
ensure_docker_host() {
  command -v docker >/dev/null 2>&1 || die "docker is required (docs/dev-setup.md)"
  if [ -z "${DOCKER_HOST:-}" ] && [ -S "$HOME/.colima/default/docker.sock" ] \
    && ! docker info >/dev/null 2>&1; then
    export DOCKER_HOST="unix://$HOME/.colima/default/docker.sock"
    info "no default docker daemon answered — using colima ($DOCKER_HOST)"
  fi
  docker info >/dev/null 2>&1 || die "cannot reach a docker daemon (try: colima start)"
  command -v curl >/dev/null 2>&1 || die "curl is required"
}

# The S3 identity in .env.dev and the one the 01.8.1-T1 parity suite reads from
# infra/seaweedfs/s3-config.json must be the same credential; a drift here would
# make the parity suite prove something about a store the dev stack is not using.
assert_s3_identity_matches_file() {
  local file_identity access secret
  [ -f "$EVG_S3_CONFIG_FILE" ] || die "missing $EVG_S3_CONFIG_FILE"
  file_identity="$(awk -F'"' '
    /"accessKey"[[:space:]]*:/ { access = $4 }
    /"secretKey"[[:space:]]*:/ { secret = $4 }
    END { print access, secret }' "$EVG_S3_CONFIG_FILE")"
  read -r access secret <<<"$file_identity"
  [ -n "$access" ] && [ -n "$secret" ] \
    || die "could not read an identity from $EVG_S3_CONFIG_FILE"

  if [ "$access" != "${EVIDGATE_S3_ACCESS_KEY:-}" ] \
    || [ "$secret" != "${EVIDGATE_S3_SECRET_KEY:-}" ]; then
    die "EVIDGATE_S3_ACCESS_KEY/SECRET_KEY in .env.dev do not match $EVG_S3_CONFIG_FILE
     (01.8.1-T1 reads that file; keep the two in step)"
  fi
}

# The identity file reaches the SeaweedFS container through this variable: the
# container writes it to /etc/seaweedfs/s3-config.json before starting the S3
# gateway (infra/compose.dev.yml explains why it is not a host bind mount).
export_s3_config_for_compose() {
  EVIDGATE_SEAWEEDFS_S3_CONFIG="$(<"$EVG_S3_CONFIG_FILE")"
  export EVIDGATE_SEAWEEDFS_S3_CONFIG
}

# Bucket + the pending/ 24 h lifecycle (T18). The request runs inside the
# SeaweedFS container with its own curl, so no host S3 client is required.
configure_dev_bucket() {
  local bucket="${EVIDGATE_S3_BUCKET}" endpoint="${EVIDGATE_S3_ENDPOINT}" code
  local credentials="${EVIDGATE_S3_ACCESS_KEY}:${EVIDGATE_S3_SECRET_KEY}"
  local sigv4='aws:amz:us-east-1:s3'

  code="$(evg_compose exec -T seaweedfs curl -s -o /dev/null -w '%{http_code}' \
    --aws-sigv4 "$sigv4" --user "$credentials" -X PUT "$endpoint/$bucket")"
  # 409 is the emulator's "bucket already exists" (up.sh is re-runnable).
  case "$code" in
    200 | 409) ;;
    *) die "creating bucket '$bucket' failed (HTTP ${code:-none})" ;;
  esac

  code="$(evg_compose exec -T seaweedfs curl -s -o /dev/null -w '%{http_code}' \
    --aws-sigv4 "$sigv4" --user "$credentials" \
    -X PUT --data-binary @- "$endpoint/$bucket?lifecycle=" <"$EVG_S3_LIFECYCLE_FILE")"
  [ "$code" = "200" ] || die "putting the lifecycle rule on '$bucket' failed (HTTP ${code:-none})"

  info "bucket $bucket ready with the pending/ 24 h lifecycle rule ($EVG_S3_LIFECYCLE_FILE)"
}

start_host() {
  local name="$1"
  shift
  local pid_file="$EVG_PID_DIR/$name.pid" log_file="$EVG_LOG_DIR/$name.log" pid

  if evg_host_is_running "$name"; then
    info "$name is already running (pid $(cat "$pid_file"))"
    return 0
  fi

  mkdir -p "$EVG_LOG_DIR" "$EVG_PID_DIR"
  printf '\n===== %s: started %s =====\n' "$name" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >>"$log_file"
  (
    cd "$EVG_ROOT"
    nohup "$@" >>"$log_file" 2>&1 &
    echo $! >"$pid_file"
  )
  pid="$(cat "$pid_file")"
  info "started $name (pid $pid, log $log_file)"
}

# Polls until the endpoint answers 200, failing early when the process died
# (a config error must not look like a 5-min wait). No fixed sleeps.
wait_for_ready() {
  local name="$1" url="$2" deadline=$((SECONDS + READY_TIMEOUT_SECONDS)) status
  while [ "$SECONDS" -lt "$deadline" ]; do
    if ! evg_host_is_running "$name"; then
      printf '\n' >&2
      tail -n 15 "$EVG_LOG_DIR/$name.log" >&2 || true
      die "$name exited during startup — full log: $EVG_LOG_DIR/$name.log"
    fi
    status="$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 "$url" || true)"
    if [ "$status" = "200" ]; then
      info "$name is ready ($url)"
      return 0
    fi
    sleep 1
  done
  # The developer needs the reason, not a path: `dotnet watch` stays alive after
  # its app crashes (it waits for a file change), so the failure is in the log.
  printf '\n' >&2
  tail -n 15 "$EVG_LOG_DIR/$name.log" >&2 || true
  die "$name was not ready at $url within ${READY_TIMEOUT_SECONDS}s — log: $EVG_LOG_DIR/$name.log"
}

reset=false
case "${1:-}" in
  --reset) reset=true ;;
  -h | --help)
    usage
    exit 0
    ;;
  "") ;;
  *) die "unexpected argument '$1' (try --help)" ;;
esac
[ $# -le 1 ] || die "unexpected argument '$2' (try --help)"

ensure_env_file
evg_load_env || die "$EVG_ENV_FILE is missing"
ensure_dotnet_on_path
ensure_docker_host
assert_s3_identity_matches_file
export_s3_config_for_compose

# Ports have defaults in compose and in the hosts, so an older .env.dev stays usable.
: "${EVIDGATE_DB_PORT:=5432}"
: "${EVIDGATE_S3_PORT:=8333}"
: "${ASPNETCORE_URLS:=http://127.0.0.1:8080}"
: "${EVIDGATE__WORKER__HEALTHPORT:=8081}"
: "${EVIDGATE_S3_ENDPOINT:=http://127.0.0.1:${EVIDGATE_S3_PORT}}"
: "${EVIDGATE_S3_BUCKET:=evidgate-dev}"
export EVIDGATE_DB_PORT EVIDGATE_S3_PORT EVIDGATE_S3_ENDPOINT EVIDGATE_S3_BUCKET

if [ "$reset" = true ]; then
  info "--reset: stopping the host processes and deleting the pg-data/s3-data volumes"
  evg_stop_hosts
  evg_compose down -v
fi

info "starting PostgreSQL 18 and SeaweedFS (docker compose up -d --wait)"
evg_compose up -d --wait

# 01.4.1-T2.3: the superuser bootstrap, migrations as evidgate_owner and the
# LOGIN+password grant for the app roles. Reused, never duplicated here.
EVIDGATE_DB_HOST=127.0.0.1 \
  EVIDGATE_DB_PORT="$EVIDGATE_DB_PORT" \
  EVIDGATE_DB_NAME=evidgate \
  EVIDGATE_DB_SUPERUSER=postgres \
  EVIDGATE_DB_CONTAINER="$EVG_PG_CONTAINER" \
  "$SCRIPT_DIR/db-init.sh"

configure_dev_bucket

start_host api dotnet watch --project "$EVG_ROOT/src/Evidgate.Api" --non-interactive run
start_host worker dotnet watch --project "$EVG_ROOT/src/Evidgate.Worker" --non-interactive run

wait_for_ready api "${ASPNETCORE_URLS}/internal/health/ready"
wait_for_ready worker "http://127.0.0.1:${EVIDGATE__WORKER__HEALTHPORT}/internal/health/ready"

info "dev stack up in $((SECONDS - START_SECONDS))s"
cat <<EOF

  Api                 ${ASPNETCORE_URLS}   (GET ${ASPNETCORE_URLS}/internal/health/ready)
  Worker              readiness http://127.0.0.1:${EVIDGATE__WORKER__HEALTHPORT}/internal/health/ready
  PostgreSQL 18       127.0.0.1:${EVIDGATE_DB_PORT}   database evidgate (roles: owner/app/operator/worker_sched)
  SeaweedFS S3        ${EVIDGATE_S3_ENDPOINT}   bucket ${EVIDGATE_S3_BUCKET}
  web (ng serve):     not started — apps/web arrives with 09.1.1

  logs   scripts/dev/logs.sh        stop   scripts/dev/down.sh
  reset  scripts/dev/up.sh --reset  (deletes the pg-data/s3-data volumes)
EOF