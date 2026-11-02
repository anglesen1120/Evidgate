#!/usr/bin/env bash
# Reports what the review stack on <ssh-host> is serving: the release recorded
# in `current`, whether the shipped tree still matches it, which containers run,
# and which host ports are published. Read-only: it never creates, starts or
# stops anything, so it is safe to run from anywhere, any time.
#
# usage: scripts/review/status.sh <ssh-host> [-i <ssh-keyfile>]
#        scripts/review/status.sh --local
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=scripts/review/lib.sh
. "$SCRIPT_DIR/lib.sh"

HOST_ARG=""
KEY_ARG=""

usage() {
  awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$0"
}

parse_args() {
  while [ $# -gt 0 ]; do
    case "$1" in
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
  local current sha marker env_state containers ports origin auth_user public_url

  parse_args "$@"
  rv_resolve_ssh_key "$KEY_ARG"
  rv_probe_host

  printf 'review stack on %s (%s)\n' "$(rv_target)" "$RV_HOST_ROOT"
  printf '  compose project : %s\n' "$RV_PROJECT"

  if ! rv_run "test -e $(rv_quote "$RV_HOST_ROOT/current")"; then
    printf '  current         : none — nothing has been deployed here yet\n'
  else
    current="$(rv_run "readlink $(rv_quote "$RV_HOST_ROOT/current")")"
    sha="${current#releases/}"
    printf '  current         : %s\n' "$current"
    marker="$(rv_run "cat $(rv_quote "$RV_HOST_ROOT/$current/.release-sha") 2>/dev/null || true")"
    if [ "$marker" = "$sha" ]; then
      printf '  release marker  : %s (matches current)\n' "$marker"
    else
      printf '  release marker  : %s (does NOT match current %s)\n' "${marker:-missing}" "$sha"
    fi
  fi

  if rv_run "test -f $(rv_quote "$RV_ENV_FILE")"; then
    env_state="$(rv_run "ls -l $(rv_quote "$RV_ENV_FILE") | cut -c1-10")"
    printf '  secrets         : %s (%s, content never shown)\n' "$RV_ENV_FILE" "$env_state"
    origin="$(rv_env_get EVIDGATE_REVIEW_PUBLIC_ORIGIN)"
    if [ -n "$origin" ]; then
      printf '  public origin   : %s\n' "$origin"
    else
      printf '  public origin   : none recorded (no token tunnel hostname)\n'
    fi
    auth_user="$(rv_env_get EVIDGATE_REVIEW_BASIC_AUTH_USER)"
    if [ -n "$auth_user" ]; then
      printf '  basic auth      : user %s, password in %s\n' "$auth_user" "$RV_ENV_FILE"
    else
      printf '  basic auth      : no credentials recorded in the env file\n'
    fi
  else
    printf '  secrets         : %s missing — deploy has not run\n' "$RV_ENV_FILE"
  fi

  # Which ingress is live is a property of the running containers, not of the
  # env file: the mode is passed per deploy, so the env file can be stale.
  if rv_run "docker inspect $(rv_quote "$RV_QUICK_TUNNEL_CONTAINER") >/dev/null 2>&1"; then
    public_url="$(rv_quick_tunnel_url_now)"
    if [ -n "$public_url" ]; then
      # Reviewers are handed this URL, and it does not survive a reboot.
      printf '  tunnel          : quick, %s\n' "$public_url"
      printf '                    this URL changes every time the host or the container starts: print it again after a boot\n'
    else
      printf '  tunnel          : quick, no trycloudflare.com URL in its log since its last start\n'
    fi
  elif rv_run "docker inspect $(rv_quote "evidgate-review-cloudflared") >/dev/null 2>&1"; then
    printf '  tunnel          : token\n'
  else
    printf '  tunnel          : none (nothing is published)\n'
  fi

  containers="$(rv_run "docker ps -a --filter label=com.docker.compose.project=$(rv_quote "$RV_PROJECT") --format '{{.Names}}\t{{.State}}\t{{.Status}}'")"
  if [ -z "$containers" ]; then
    printf '  containers      : none\n'
  else
    printf '  containers      :\n'
    printf '%s\n' "$containers" | sed 's/^/    /'
  fi

  # Same rule deploy enforces after the final up: with Cloudflare Tunnel as the
  # only ingress, a published port is a second door.
  ports="$(rv_run "docker ps --filter label=com.docker.compose.project=$(rv_quote "$RV_PROJECT") --format '{{.Names}} {{.Ports}}' | grep -F -- '->' || true")"
  if [ -z "$ports" ]; then
    printf '  host ports      : none published\n'
  else
    printf '  host ports      :\n'
    printf '%s\n' "$ports" | sed 's/^/    /'
  fi
}

main "$@"
