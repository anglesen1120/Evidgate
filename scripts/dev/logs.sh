#!/usr/bin/env bash
# 01.8.1-T2.2 — follows the local dev stack's logs: the compose services and the
# host logs up.sh writes under .local/dev/logs/ (api, worker and web).
#
# usage: scripts/dev/logs.sh [api|worker|web|postgres|seaweedfs|all]
#
# `all` (the default) follows compose in the background and the host logs in the
# foreground, so a single terminal shows both. Ctrl-C stops following, not the
# stack.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=scripts/dev/lib.sh
. "$SCRIPT_DIR/lib.sh"

usage() {
  cat <<'USAGE'
Evidgate local dev stack — logs (01.8.1).

usage: scripts/dev/logs.sh [target]

  api        follow .local/dev/logs/api.log (dotnet watch)
  worker     follow .local/dev/logs/worker.log (dotnet watch)
  web        follow .local/dev/logs/web.log (ng serve)
  postgres   docker compose logs -f postgres
  seaweedfs  docker compose logs -f seaweedfs
  all        everything (default); Ctrl-C stops following only
USAGE
}

target="${1:-all}"
case "$target" in
  -h | --help)
    usage
    exit 0
    ;;
  api | worker | web)
    log_file="$EVG_LOG_DIR/$target.log"
    [ -f "$log_file" ] || evg_die "no $target log yet — is the stack up? (scripts/dev/up.sh)"
    exec tail -n 100 -F "$log_file"
    ;;
  postgres | seaweedfs)
    evg_load_env || true
    exec docker compose -f "$EVG_COMPOSE_FILE" logs -f --tail=100 "$target"
    ;;
  all) ;;
  *) evg_die "unknown target '$target' (try --help)" ;;
esac

evg_load_env || true

host_logs=()
for name in "${EVG_HOST_NAMES[@]}"; do
  [ -f "$EVG_LOG_DIR/$name.log" ] && host_logs+=("$EVG_LOG_DIR/$name.log")
done

# compose services and host files both in the background: the follow ends when
# either stream ends (or on Ctrl-C) and the trap reaps the other one, so no
# follower survives the script.
tail_pid=""
evg_compose logs -f --tail=50 &
compose_pid=$!
if [ ${#host_logs[@]} -eq 0 ]; then
  evg_info "no host logs yet in $EVG_LOG_DIR (up.sh writes api.log/worker.log/web.log) — following compose only"
  tail_pid="$compose_pid"
else
  tail -n 50 -F "${host_logs[@]}" &
  tail_pid=$!
fi

# `docker compose logs -f` runs the compose CLI plugin as a child of the driver,
# so the cleanup kills the subtree, not just the pid it was handed.
cleanup() {
  evg_kill_tree "$compose_pid" TERM
  [ "$tail_pid" = "$compose_pid" ] || evg_kill_tree "$tail_pid" TERM
}
trap cleanup EXIT INT TERM

# `wait -n` ends the script as soon as one side stops; without it a dead
# follower would leave the terminal hanging on the other.
wait -n "$compose_pid" "$tail_pid" || true