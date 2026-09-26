#!/usr/bin/env bash
# Shared helpers for the dev-stack scripts (01.8.1-T2.2): up.sh, down.sh and
# logs.sh all need the same paths, .env.dev loading and host-process control.
# Sourced, never executed.

# Shared constants; up.sh/down.sh/logs.sh use them, this file only defines them.
# shellcheck disable=SC2034
EVG_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
EVG_COMPOSE_FILE="$EVG_ROOT/infra/compose.dev.yml"
EVG_ENV_FILE="$EVG_ROOT/.env.dev"
EVG_ENV_TEMPLATE="$EVG_ROOT/.env.dev.template"
EVG_S3_CONFIG_FILE="$EVG_ROOT/infra/seaweedfs/s3-config.json"
EVG_S3_LIFECYCLE_FILE="$EVG_ROOT/infra/seaweedfs/pending-lifecycle.xml"
EVG_PG_CONTAINER="evidgate-dev-postgres"
EVG_HOST_NAMES=(api worker)
EVG_LOG_DIR="$EVG_ROOT/.local/dev/logs"
EVG_PID_DIR="$EVG_ROOT/.local/dev/pids"

# Script name prefix, so three scripts sharing one file still name themselves.
evg_info() { printf '%s: %s\n' "${0##*/}" "$*"; }

evg_warn() { printf '%s: %s\n' "${0##*/}" "$*" >&2; }

evg_die() {
  printf '%s: %s\n' "${0##*/}" "$*" >&2
  exit 1
}

# Sources .env.dev and exports every assignment, so the Api, the Worker and
# compose inherit them. Returns 1 when the file does not exist (down.sh does not
# need it; compose interpolates its own defaults).
evg_load_env() {
  [ -f "$EVG_ENV_FILE" ] || return 1
  set -a
  # shellcheck source=/dev/null
  . "$EVG_ENV_FILE"
  set +a
  return 0
}

evg_compose() { docker compose -f "$EVG_COMPOSE_FILE" "$@"; }

# True when up.sh started the host and it is still alive.
evg_host_is_running() {
  local pid
  pid="$(cat "$EVG_PID_DIR/$1.pid" 2>/dev/null || true)"
  [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null
}

# Terminates one process and its descendants, deepest first: `dotnet watch`
# forwards the signal to the app it owns, and the app's children (if any) must
# not survive it. `kill -s` keeps the signal out of the option-parsing rules.
evg_kill_tree() {
  local pid="$1" signal="$2" child
  for child in $(pgrep -P "$pid" 2>/dev/null || true); do
    evg_kill_tree "$child" "$signal"
  done
  kill -s "$signal" "$pid" 2>/dev/null || true
}

# The process tree below (and including) $1, deepest first.
evg_process_tree() {
  local pid="$1" child
  for child in $(pgrep -P "$pid" 2>/dev/null || true); do
    evg_process_tree "$child"
  done
  printf '%s\n' "$pid"
}

# Stops a host process started by up.sh. `dotnet watch` itself never exits on
# SIGTERM — it logs "Waiting for a file to change" and stays — so the wait is for
# the app it owns (the deepest descendant), which is allowed its graceful
# shutdown budget of up to 10 s (Evidgate.Api/Worker ShutdownTimeout); the
# watchers are then reaped with SIGKILL when still present. Safe when nothing runs.
evg_stop_host() {
  local name="$1" pid_file pid app_pid deadline
  pid_file="$EVG_PID_DIR/$name.pid"
  [ -f "$pid_file" ] || return 0
  pid="$(cat "$pid_file" 2>/dev/null || true)"
  rm -f "$pid_file"
  [ -n "$pid" ] || return 0
  kill -0 "$pid" 2>/dev/null || return 0

  evg_info "stopping $name (pid $pid)"
  app_pid="$(evg_process_tree "$pid" | head -n 1)"
  evg_kill_tree "$pid" TERM

  # A watcher shim (or a watch that has not started its app yet) is not an app:
  # there is nothing to wait for.
  if [ -n "$app_pid" ] && ps -o command= -p "$app_pid" 2>/dev/null | grep -q 'dotnet-watch'; then
    app_pid=""
  fi

  deadline=$((SECONDS + 15))
  while [ -n "$app_pid" ] && [ "$SECONDS" -lt "$deadline" ] && kill -0 "$app_pid" 2>/dev/null; do
    sleep 0.2
  done
  if [ -n "$app_pid" ] && kill -0 "$app_pid" 2>/dev/null; then
    evg_warn "$name did not stop within 15 s — sending SIGKILL"
  fi

  evg_kill_tree "$pid" KILL
}

evg_stop_hosts() {
  local name
  for name in "${EVG_HOST_NAMES[@]}"; do
    evg_stop_host "$name"
  done
}