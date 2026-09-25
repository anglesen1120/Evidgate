#!/usr/bin/env bash
# 01.8.1-T2.2 — stops the local dev stack started by up.sh: the two host
# processes (pid files under .local/dev/) first, then the compose services.
# The named volumes pg-data and s3-data are kept, so the database and the bucket
# contents survive the next `up.sh` (AC4); `up.sh --reset` is the way to wipe.
#
# usage: scripts/dev/down.sh [--help]
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=scripts/dev/lib.sh
. "$SCRIPT_DIR/lib.sh"

usage() {
  cat <<'USAGE'
Evidgate local dev stack — stop (01.8.1).

usage: scripts/dev/down.sh [--help]

Stops the Api and Worker started by up.sh, then `docker compose down`.
Volumes pg-data and s3-data are kept: the next up.sh finds the same data.
Use `scripts/dev/up.sh --reset` to delete the volumes instead.
USAGE
}

case "${1:-}" in
  -h | --help)
    usage
    exit 0
    ;;
  "") ;;
  *) evg_die "unexpected argument '$1' (try --help)" ;;
esac

# down.sh only needs the compose file; .env.dev is loaded when it exists so that
# compose interpolates the same ports/passwords the running stack used.
evg_load_env || evg_warn "$EVG_ENV_FILE not found — using the compose defaults"

evg_stop_hosts

evg_info "stopping the compose services (volumes kept)"
evg_compose down

evg_info "dev stack stopped — volumes pg-data and s3-data kept (up.sh --reset deletes them)"