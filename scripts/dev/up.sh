#!/usr/bin/env bash
# 01.8.1-T3.2 / FR-01-11 — one command from a clean checkout to a working local
# stack: PostgreSQL 18 + SeaweedFS from infra/compose.dev.yml (digest-pinned),
# the dev role split and migrations through scripts/dev/db-init.sh, the pending/
# 24 h lifecycle rule on the dev bucket (T18), then Evidgate.Api and
# Evidgate.Worker under `dotnet watch` plus the Angular app under `ng serve` on
# the host. It exits 0 only once both .NET processes answer
# GET /internal/health/ready with 200 (AC1) and the dev server answers 200. It
# refuses up front when another process already holds EVIDGATE_DB_PORT,
# EVIDGATE_S3_PORT or EVIDGATE_WEB_PORT, because a publish onto a taken port
# silently does not apply and the migrations would run against that other server.
#
# usage: scripts/dev/up.sh [--reset] [--with-token] [--observability]
#
#   --reset       stop the host processes and delete the named volumes pg-data and
#                 s3-data before starting again (AC4). Without it, data survives
#                 `down.sh` / `up.sh`.
#   --with-token  also run `Evidgate.Migrator seed-dev --with-token`: one dev
#                 ingest token (project-bound, scopes artifact:write, event:write,
#                 run:create) and one sample policy revision, minted through the
#                 real commands on the app role. A minted secret goes to this
#                 terminal only, once: never to a log file, `.env.dev` or a
#                 subprocess environment; a rerun that finds a live token prints
#                 no secret. Needs EVIDGATE__CI__PEPPER in .env.dev.
#
# Secrets: the first run copies .env.dev.template to .env.dev, fills every __...__
# placeholder with a fresh random value and chmods it 600. No password is ever
# printed by this script; .env.dev is git-ignored (00 §5.7 S3). The only secret
# this script prints is the dev ingest token, and only under --with-token.
#
# web: `ng serve` runs as the host process `web` (own pid/log like api/worker). It
# needs the npm workspaces installed and packages/sdk built, because the app
# imports the SDK from its git-ignored dist/; this script keeps both current
# (npm ci when the install is stale, `tsc -b` incremental on every run).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=scripts/dev/lib.sh
. "$SCRIPT_DIR/lib.sh"
# shellcheck source=scripts/env/lib-bucket.sh
. "$SCRIPT_DIR/../env/lib-bucket.sh"
# shellcheck source=scripts/dev/observability.sh
. "$SCRIPT_DIR/observability.sh"

# The bucket requests run inside the SeaweedFS container with its own curl, so no
# host S3 client is required and they target the gateway's own port rather than
# the host port EVIDGATE_S3_PORT publishes (they differ once it is overridden).
evg_s3_curl() { evg_compose exec -T seaweedfs curl "$@"; }

READY_TIMEOUT_SECONDS="${EVIDGATE_UP_READY_TIMEOUT_SECONDS:-300}"
START_SECONDS="$SECONDS"

usage() {
  cat <<'USAGE'
Evidgate local dev stack (01.8.1).

usage: scripts/dev/up.sh [--reset] [--with-token] [--observability]

Brings up PostgreSQL 18 + SeaweedFS (compose, digest-pinned images), applies the
migrations as evidgate_owner, configures the dev bucket, then starts the Api and
the Worker with `dotnet watch` and the Angular app with `ng serve` on the host,
and waits for both .NET readiness endpoints and the dev server.

It refuses before compose starts when another process already holds one of the
published ports (EVIDGATE_DB_PORT, EVIDGATE_S3_PORT) or EVIDGATE_WEB_PORT, and when
one of the three is not digits only; see docs/dev-setup.md, Troubleshooting.

  --reset       stop the host processes and delete the pg-data/s3-data volumes (and the
                observability volumes) first
  --observability
                also start the compose profile `observability` (collector, Prometheus,
                VictoriaLogs, VictoriaTraces; loopback ports, retention per ADR-0012) and
                point the Api and Worker at the collector. Off by default; run
                scripts/dev/observability-smoke.sh to prove it on a throwaway project.
  --with-token  also mint the dev ingest token and the sample policy revision
                (seed-dev --with-token). A minted token is printed here once and
                never to a log or .env.dev, and a rerun that finds a live token
                prints no secret. Needs EVIDGATE__CI__PEPPER in .env.dev (base64,
                >= 32 bytes; docs/dev-setup.md).
  -h, --help    this text

Environment read from .env.dev (created from .env.dev.template on first run):
  EVIDGATE_DB_PORT, EVIDGATE_S3_PORT, EVIDGATE_WEB_PORT, ASPNETCORE_URLS,
  EVIDGATE__WORKER__HEALTHPORT, EVIDGATE__WORKER__HEALTHADDRESS,
  EVIDGATE__DATABASE__CONNECTIONSTRING, ...
  EVIDGATE__CI__PEPPER                      required with --with-token
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
  for name in EVIDGATE_DB_SUPERUSER_PASSWORD EVIDGATE_OWNER_PASSWORD EVIDGATE_APP_PASSWORD \
    EVIDGATE_OPERATOR_PASSWORD EVIDGATE_WORKER_SCHED_PASSWORD EVIDGATE_WORKER_PASSWORD \
    EVIDGATE_PLATFORM_OPERATOR_PASSWORD; do
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

# Bucket + the pending/ 24 h lifecycle, through the same helpers
# scripts/env/bootstrap-bucket.sh uses, so the two cannot drift apart.
configure_dev_bucket() {
  local endpoint="http://127.0.0.1:8333"

  evg_s3_configure "$EVIDGATE_S3_ACCESS_KEY" "$EVIDGATE_S3_SECRET_KEY" \
    "${EVIDGATE_S3_REGION:-us-east-1}"
  evg_s3_create_bucket "$EVIDGATE_S3_BUCKET" "$endpoint"
  evg_s3_put_lifecycle "$EVIDGATE_S3_BUCKET" "$endpoint" "$EVG_S3_LIFECYCLE_FILE"

  info "bucket $EVIDGATE_S3_BUCKET ready with the pending/ 24 h lifecycle rule ($EVG_S3_LIFECYCLE_FILE)"
}

# True when something accepts TCP on 127.0.0.1:$1. A loopback listener answers at
# once, so the watchdog only bounds a pathological case (macOS has no timeout(1)
# and no extra tool is wanted here).
port_is_open() {
  local port="$1" probe watchdog
  ( exec 3<>"/dev/tcp/127.0.0.1/$port" ) 2>/dev/null &
  probe=$!
  ( sleep 2; kill -9 "$probe" 2>/dev/null ) >/dev/null 2>&1 &
  watchdog=$!
  if wait "$probe"; then
    { kill "$watchdog" 2>/dev/null; wait "$watchdog"; } 2>/dev/null || true
    return 0
  fi
  { kill "$watchdog" 2>/dev/null; wait "$watchdog"; } 2>/dev/null || true
  return 1
}

# True when another process holds 127.0.0.1:$2 and the service that should own it
# ($1, `-` for a host process) has no container: a publish onto a taken port
# silently does not apply and db-init would then talk to that other server.
port_conflict() {
  local service="$1" port="$2" container=""
  if [ "$service" != "-" ]; then
    container="$(evg_compose ps -a -q "$service" 2>/dev/null || true)"
  fi
  if [ -n "$container" ]; then
    # Only a container that is not running reaches this branch; one that runs but
    # fails its healthcheck still counts as running, so the message must not
    # claim an unhealthy container.
    if [ -z "$(evg_compose ps --status running -q "$service" 2>/dev/null || true)" ] \
      && port_is_open "$port"; then
      evg_warn "the '$service' container exists but is not running while something answers on port $port — if another process owns that port, starting the container fails; stop the listener or change the port"
    fi
    return 1
  fi
  port_is_open "$port"
}

# Refuse before compose starts, so the failure names the port and the variable
# instead of surfacing later as a misleading database error.
preflight_ports() {
  if port_conflict postgres "$EVIDGATE_DB_PORT"; then
    die "port $EVIDGATE_DB_PORT on 127.0.0.1 is already in use, so PostgreSQL 18 cannot be
     published there (another server answers instead, and the migrations would run
     against it). Set EVIDGATE_DB_PORT in .env.dev and the Port= value of the
     connection strings in that file, or stop the process holding the port
     (docs/dev-setup.md, Troubleshooting)."
  fi
  if port_conflict seaweedfs "$EVIDGATE_S3_PORT"; then
    die "port $EVIDGATE_S3_PORT on 127.0.0.1 is already in use, so SeaweedFS cannot be
     published there. Set EVIDGATE_S3_PORT, EVIDGATE_S3_ENDPOINT and
     EVIDGATE__OBJECT__ENDPOINT in .env.dev, or stop the process holding the port
     (docs/dev-setup.md, Troubleshooting)."
  fi
  if ! evg_host_is_running web && port_conflict - "$EVIDGATE_WEB_PORT"; then
    die "port $EVIDGATE_WEB_PORT on 127.0.0.1 is already in use, so ng serve cannot bind
     it. Set EVIDGATE_WEB_PORT in .env.dev, or stop the process holding the port
     (docs/dev-setup.md, Troubleshooting)."
  fi
}

# Api and Worker share project references; two `dotnet watch` processes that both
# restore on a cold checkout race on the shared obj/*.nuget.g.targets files
# ("already exists"). Building each once, in sequence, makes the two restores no-ops.
# The build is third-party tooling that runs no dev code, so it gets no credential
# (evg_run_scrubbed); the two `dotnet watch` hosts that follow keep the full
# environment because they need the connection strings.
build_hosts() {
  local project
  for project in Evidgate.Api Evidgate.Worker; do
    info "building $project"
    evg_run_scrubbed dotnet build "$EVG_ROOT/src/$project" --nologo -v q \
      || die "dotnet build of $project failed — run: dotnet build src/$project"
  done
}

# apps/web imports @evidgate/sdk, whose main/types point into packages/sdk/dist —
# git-ignored, so absent on a clean checkout. npm ci is needed when the install is
# absent or older than package-lock.json; `tsc -b` is incremental, so running it
# every time is what rebuilds a stale dist/. Both are third-party tooling and run
# without the dev credentials.
ensure_web_prerequisites() {
  command -v node >/dev/null 2>&1 || die "node is required for ng serve (Node 26, docs/dev-setup.md)"
  command -v npm >/dev/null 2>&1 || die "npm is required for ng serve (Node 26, docs/dev-setup.md)"
  local modules="$EVG_ROOT/node_modules" install_marker="$EVG_ROOT/node_modules/.package-lock.json"
  if [ ! -d "$modules" ] || [ ! -f "$install_marker" ] \
    || [ "$EVG_ROOT/package-lock.json" -nt "$install_marker" ]; then
    info "installing the npm workspaces (npm ci)"
    ( cd "$EVG_ROOT" && evg_run_scrubbed npm ci --no-progress --loglevel=error ) \
      || die "npm ci failed — fix what it printed and re-run"
  fi
  info "building packages/sdk (tsc -b, the web app imports it from dist/)"
  ( cd "$EVG_ROOT" && evg_run_scrubbed npx tsc -b packages/sdk ) \
    || die "building packages/sdk failed — run: npx tsc -b packages/sdk"
}

start_host() {
  local name="$1" scrub=false
  shift
  if [ "${1:-}" = "--scrub" ]; then
    scrub=true
    shift
  fi
  local pid_file="$EVG_PID_DIR/$name.pid" log_file="$EVG_LOG_DIR/$name.log" pid

  if evg_host_is_running "$name"; then
    info "$name is already running (pid $(cat "$pid_file"))"
    return 0
  fi

  mkdir -p "$EVG_LOG_DIR" "$EVG_PID_DIR"
  printf '\n===== %s: started %s =====\n' "$name" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >>"$log_file"
  (
    cd "$EVG_ROOT"
    # --scrub drops every credential-shaped variable; the .NET hosts keep the
    # full environment because they need the connection strings.
    if [ "$scrub" = true ]; then
      evg_scrub_env_args
    else
      EVG_SCRUB_ARGS=()
    fi
    if [ "${#EVG_SCRUB_ARGS[@]}" -gt 0 ]; then
      nohup env "${EVG_SCRUB_ARGS[@]}" "$@" >>"$log_file" 2>&1 &
    else
      nohup "$@" >>"$log_file" 2>&1 &
    fi
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
with_token=false
observability=false
for arg in "$@"; do
  case "$arg" in
    --reset) reset=true ;;
    --with-token) with_token=true ;;
    --observability) observability=true ;;
    -h | --help)
      usage
      exit 0
      ;;
    *) die "unexpected argument '$arg' (try --help)" ;;
  esac
done

ensure_env_file
evg_load_env || die "$EVG_ENV_FILE is missing"

# The seed resolves the CI pepper through ISecretProvider like the Api and refuses
# before writing anything when it is absent; checking first keeps a missing pepper
# from bringing the whole compose stack up. The seed still validates the value.
if [ "$with_token" = true ] && [ "${EVIDGATE_DB_INIT_SEED:-1}" != "0" ] \
  && [ -z "${EVIDGATE__CI__PEPPER:-}" ]; then
  die "EVIDGATE__CI__PEPPER is not set — add it to .env.dev first (base64, >= 32 bytes:
     openssl rand -base64 32); docs/dev-setup.md, The dev ingest token"
fi

ensure_dotnet_on_path
ensure_docker_host
assert_s3_identity_matches_file
export_s3_config_for_compose

# Ports have defaults in compose and in the hosts, so an older .env.dev stays usable.
: "${EVIDGATE_DB_PORT:=5432}"
: "${EVIDGATE_S3_PORT:=8333}"
: "${EVIDGATE_WEB_PORT:=4200}"
evg_require_port EVIDGATE_DB_PORT "$EVIDGATE_DB_PORT"
evg_require_port EVIDGATE_S3_PORT "$EVIDGATE_S3_PORT"
evg_require_port EVIDGATE_WEB_PORT "$EVIDGATE_WEB_PORT"
: "${ASPNETCORE_URLS:=http://127.0.0.1:8080}"
: "${EVIDGATE__WORKER__HEALTHPORT:=8081}"
: "${EVIDGATE__WORKER__HEALTHADDRESS:=127.0.0.1}"
export EVIDGATE__WORKER__HEALTHADDRESS
: "${EVIDGATE_S3_ENDPOINT:=http://127.0.0.1:${EVIDGATE_S3_PORT}}"
: "${EVIDGATE_S3_BUCKET:=evidgate-dev}"
export EVIDGATE_DB_PORT EVIDGATE_S3_PORT EVIDGATE_WEB_PORT
export EVIDGATE_S3_ENDPOINT EVIDGATE_S3_BUCKET

if [ "$observability" = true ]; then
  evg_obs_configure
fi

if [ "$reset" = true ]; then
  info "--reset: stopping the host processes and deleting the pg-data/s3-data and observability volumes"
  evg_stop_hosts
  evg_compose_all down -v
fi

if [ "$observability" = true ]; then
  evg_obs_preflight
else
  # An inherited profile would start services whose config this path never loads.
  unset COMPOSE_PROFILES
fi
preflight_ports

if [ "$observability" = true ]; then
  info "starting PostgreSQL 18, SeaweedFS and the observability profile (docker compose up -d --wait)"
else
  info "starting PostgreSQL 18 and SeaweedFS (docker compose up -d --wait)"
fi
evg_compose up -d --wait

# 01.4.1-T2.3: the superuser bootstrap, migrations as evidgate_owner and the
# LOGIN+password grant for the app roles. Reused, never duplicated here.
db_init() {
  EVIDGATE_DB_HOST=127.0.0.1 \
    EVIDGATE_DB_PORT="$EVIDGATE_DB_PORT" \
    EVIDGATE_DB_NAME=evidgate \
    EVIDGATE_DB_SUPERUSER=postgres \
    EVIDGATE_DB_CONTAINER="$EVG_PG_CONTAINER" \
    "$SCRIPT_DIR/db-init.sh" "$@"
}

if [ "$with_token" = true ]; then
  db_init --with-token
else
  db_init
fi

configure_dev_bucket

ensure_web_prerequisites
build_hosts

# The Api never holds the worker credential, and neither host holds the platform
# operator's: `env -u` drops them from the child env, so only a human's psql has
# the role that may execute the system healing-switch definer.
start_host api env -u EVIDGATE__TASKS__WORKERCONNECTIONSTRING -u EVIDGATE_WORKER_PASSWORD \
  -u EVIDGATE_PLATFORM_OPERATOR_PASSWORD dotnet watch --project "$EVG_ROOT/src/Evidgate.Api" --non-interactive run
start_host worker env -u EVIDGATE_PLATFORM_OPERATOR_PASSWORD dotnet watch --project "$EVG_ROOT/src/Evidgate.Worker" --non-interactive run
# CI=1 and NG_CLI_ANALYTICS=false keep `ng serve` non-interactive under nohup (a
# first run would otherwise ask about usage-data); --scrub drops every credential.
start_host web --scrub env CI=1 NG_CLI_ANALYTICS=false \
  npm run start -w apps/web -- --host 127.0.0.1 --port "$EVIDGATE_WEB_PORT"

wait_for_ready api "${ASPNETCORE_URLS}/internal/health/ready"
wait_for_ready worker "http://127.0.0.1:${EVIDGATE__WORKER__HEALTHPORT}/internal/health/ready"
# The development configuration localizes to `vi` only, and `ng serve` serves a
# single locale flat (forceI18nFlatOutput), so the app keeps its own base href `/`:
# a 200 from the root is what proves the workspace compiled. 300 s covers that.
wait_for_ready web "http://127.0.0.1:${EVIDGATE_WEB_PORT}/"

info "dev stack up in $((SECONDS - START_SECONDS))s"
cat <<EOF

  Api                 ${ASPNETCORE_URLS}   (GET ${ASPNETCORE_URLS}/internal/health/ready)
  Worker              readiness http://127.0.0.1:${EVIDGATE__WORKER__HEALTHPORT}/internal/health/ready
  web (ng serve)      http://127.0.0.1:${EVIDGATE_WEB_PORT}/
  PostgreSQL 18       127.0.0.1:${EVIDGATE_DB_PORT}   database evidgate (roles: owner/app/operator/worker_sched/worker/platform_operator)
  SeaweedFS S3        ${EVIDGATE_S3_ENDPOINT}   bucket ${EVIDGATE_S3_BUCKET}

  logs   scripts/dev/logs.sh        stop   scripts/dev/down.sh
  reset  scripts/dev/up.sh --reset  (deletes the pg-data/s3-data volumes)
EOF

if [ "$observability" = true ]; then
  cat <<EOF

  OTLP collector      grpc 127.0.0.1:${EVIDGATE_OBS_OTLP_GRPC_PORT}  http 127.0.0.1:${EVIDGATE_OBS_OTLP_HTTP_PORT}  (Api and Worker export here)
  Prometheus          http://127.0.0.1:${EVIDGATE_OBS_PROMETHEUS_PORT}/   (metrics)
  VictoriaLogs        http://127.0.0.1:${EVIDGATE_OBS_LOGS_PORT}/select/vmui/   (logs)
  VictoriaTraces      http://127.0.0.1:${EVIDGATE_OBS_TRACES_PORT}/select/vmui/   (traces)
EOF
fi

if [ "$with_token" = true ]; then
  if [ "${EVIDGATE_DB_INIT_SEED:-1}" = "0" ]; then
    cat <<'EOF'

  dev ingest token    not minted: EVIDGATE_DB_INIT_SEED=0 skipped the seed and the
                      existing-token check. Set it to 1 in the environment or in
                      .env.dev (that file wins over the shell) and re-run with
                      --with-token to seed; a live token is then reported, not
                      re-shown.
EOF
  else
    cat <<'EOF'

  dev ingest token    if a line EVIDGATE_DEV_TOKEN=... was printed above, it is
                      shown once and stored nowhere: export it in your shell.
                      If none was printed, a live 'dev-seed' ingest token already exists; its secret was shown once and cannot be shown again. To get a new one run: scripts/dev/down.sh && scripts/dev/up.sh --reset --with-token
EOF
  fi
fi
