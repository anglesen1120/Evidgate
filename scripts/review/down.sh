#!/usr/bin/env bash
# Stops the review stack on <ssh-host>. The database and bucket volumes are kept,
# so the next deploy finds its data; --purge deletes them together with every
# shipped release, which is the only way to get a new dev ingest token out of the
# seed (it prints one once per database). The secrets file is never deleted: a
# later deploy reuses the credentials the services and the reviewers already know.
#
# usage: scripts/review/down.sh <ssh-host> [-i <ssh-keyfile>] [--purge] [--yes]
#        scripts/review/down.sh --local [--purge] [--yes]
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=scripts/review/lib.sh
. "$SCRIPT_DIR/lib.sh"

HOST_ARG=""
KEY_ARG=""
PURGE=0
CONFIRMED=0

usage() {
  awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$0"
}

parse_args() {
  while [ $# -gt 0 ]; do
    case "$1" in
      --purge)
        PURGE=1
        shift
        ;;
      --yes)
        CONFIRMED=1
        shift
        ;;
      --local)
        RV_MODE=local
        shift
        ;;
      -i)
        [ $# -ge 2 ] || rv_die "-i needs an ssh key file"
        KEY_ARG="$2"
        shift 2
        ;;
      -i?*)
        KEY_ARG="${1#-i}"
        shift
        ;;
      -h | --help)
        usage
        exit 0
        ;;
      -*)
        rv_die "unknown option '$1' (try --help)"
        ;;
      *)
        [ -z "$HOST_ARG" ] || rv_die "expected one host, got '$HOST_ARG' and '$1'"
        HOST_ARG="$1"
        shift
        ;;
    esac
  done
  if [ "$RV_MODE" = local ]; then
    [ -z "$HOST_ARG" ] || rv_die "--local takes no host (got '$HOST_ARG')"
  else
    [ -n "$HOST_ARG" ] || rv_die "the ssh host is required (or --local; try --help)"
    RV_SSH_HOST="$HOST_ARG"
  fi
}

main() {
  parse_args "$@"
  rv_resolve_ssh_key "$KEY_ARG"
  rv_probe_host

  if [ "$PURGE" = 1 ] && [ "$CONFIRMED" != 1 ]; then
    rv_die "--purge deletes the database volume, the bucket volume and every release under $RV_HOST_ROOT; it only runs with an explicit --yes"
  fi
  if [ "$PURGE" = 1 ]; then
    rv_info "stopping the review stack and deleting its volumes on $(rv_target)"
    rv_teardown down -v --remove-orphans
    rv_mutate "set -eu; rm -rf $(rv_quote "$RV_HOST_ROOT/releases") $(rv_quote "$RV_HOST_ROOT/current")"
    rv_info "volumes, releases and current removed; $RV_ENV_FILE kept — delete it by hand to mint new credentials"
    return 0
  fi

  rv_info "stopping the review stack on $(rv_target), volumes kept"
  rv_teardown down --remove-orphans
  rv_info "stopped — the database and bucket volumes are kept; the next deploy serves the same data"
}

main "$@"
