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
EVG_HOST_NAMES=(api worker web)
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

# Exported names whose value is a credential. A new secret is covered by this
# pattern instead of an edit at every call site. ACCESS_?KEY covers both spellings
# a local identity uses (EVIDGATE_S3_ACCESS_KEY, EVIDGATE__OBJECT__ACCESSKEY);
# PEPPER, SIGNING_?KEY and DEV_TOKEN cover the credentials the token seed and the CI exchange rely on, so a
# shell that exported them does not hand them to npm, ng serve or dotnet build.
EVG_SECRET_ENV_PATTERN='PASSWORD|SECRET|CONNECTIONSTRING|ACCESS_?KEY|S3_CONFIG|PEPPER|SIGNING_?KEY|DEV_TOKEN'

# Fills EVG_SCRUB_ARGS with one `-u NAME` pair per exported variable matching
# the pattern above, read from the live environment. Process substitution, not a
# command substitution inside a here-document: this has to expand identically on
# macOS /bin/bash 3.2, and the loop then runs in this shell rather than a
# subshell, so EVG_SCRUB_ARGS survives it.
evg_scrub_env_args() {
  local name
  EVG_SCRUB_ARGS=()
  while IFS= read -r name; do
    [ -n "$name" ] || continue
    EVG_SCRUB_ARGS+=(-u "$name")
  done < <(env | sed -n 's/^\([A-Za-z_][A-Za-z0-9_]*\)=.*/\1/p' | grep -Ei "$EVG_SECRET_ENV_PATTERN" || true)
}

# Runs a third-party command (npm, npx tsc, ng serve, dotnet build) with every
# credential removed from its environment: it runs external code, and only the
# Api and the Worker need the dev secrets. Args: CMD [ARG...].
evg_run_scrubbed() {
  evg_scrub_env_args
  if [ "${#EVG_SCRUB_ARGS[@]}" -gt 0 ]; then
    env "${EVG_SCRUB_ARGS[@]}" "$@"
  else
    env "$@"
  fi
}

evg_compose() { docker compose -f "$EVG_COMPOSE_FILE" "$@"; }

# Stop/teardown must see the observability containers and volumes too: compose
# ignores a service whose profile is not selected, so `down` would leave them up.
evg_compose_all() { COMPOSE_PROFILES=observability evg_compose "$@"; }

# True when up.sh started the host and it is still alive.
evg_host_is_running() {
  local pid
  pid="$(cat "$EVG_PID_DIR/$1.pid" 2>/dev/null || true)"
  [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null
}

# Ports reach compose interpolation and `ng serve` as strings, so a value with a
# letter becomes a bad publish spec or silently publishes nothing. Args: NAME VALUE.
evg_require_port() {
  case "$2" in
    '' | *[!0-9]*) evg_die "$1 must be a number of digits only (got '$2') — check .env.dev" ;;
  esac
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

# Stops a host process started by up.sh. The tree is snapshotted before SIGTERM,
# so the final SIGKILL reaches every pid seen then; the wait is for the deepest
# descendant (the app), because `dotnet watch` ignores SIGTERM. Safe when idle.
evg_stop_host() {
  local name="$1" pid_file pid app_pid deadline tree pid_to_kill
  pid_file="$EVG_PID_DIR/$name.pid"
  [ -f "$pid_file" ] || return 0
  pid="$(cat "$pid_file" 2>/dev/null || true)"
  rm -f "$pid_file"
  [ -n "$pid" ] || return 0
  kill -0 "$pid" 2>/dev/null || return 0

  evg_info "stopping $name (pid $pid)"
  tree="$(evg_process_tree "$pid")"
  app_pid="$(printf '%s\n' "$tree" | head -n 1)"
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

  # The snapshot, not a fresh tree walk: a child that left the tree is exactly
  # the process SIGKILL must still reach.
  while IFS= read -r pid_to_kill; do
    [ -n "$pid_to_kill" ] || continue
    if kill -0 "$pid_to_kill" 2>/dev/null; then
      kill -s KILL "$pid_to_kill" 2>/dev/null || true
    fi
  done <<EOF
$tree
EOF
}

evg_stop_hosts() {
  local name
  for name in "${EVG_HOST_NAMES[@]}"; do
    evg_stop_host "$name"
  done
}