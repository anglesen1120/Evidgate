#!/usr/bin/env bash
# Tunnel helpers. Sourced by lib.sh; never executed.
#
# Two ways to publish the stack: a named tunnel the owner created (a token, a
# hostname they control) and cloudflared's throwaway `quick` tunnel, which needs
# no account and gets a random trycloudflare.com host that changes on every
# restart. Both are the only ingress: the stack itself publishes nothing.

rv_tunnel_mode_valid() { # MODE
  case "$1" in
    none | quick | token) return 0 ;;
  esac
  return 1
}

# A connector container that exists but is stopped publishes nothing, so status
# and the deploy must ask about its running state, never about its existence.
rv_connector_running() { # CONTAINER
  [ "$(rv_run "docker inspect -f '{{.State.Running}}' $(rv_quote "$1") 2>/dev/null || true")" = true ]
}

# The tunnel publishes one door, so what an unauthenticated request gets is the
# proof it is locked. A quick tunnel has only Basic Auth (exactly 401). A named
# tunnel is locked either by Cloudflare Access (an edge redirect) or, if Access
# is not attached yet, by the local Basic Auth (401); a 200 is never a pass while
# a hash is configured.
rv_ingress_codes() {
  if [ "$RV_TUNNEL_MODE" = quick ]; then
    printf '401'
  elif [ -n "$(rv_env_get EVIDGATE_REVIEW_BASIC_AUTH_HASH)" ]; then
    printf '301 302 303 307 308 401'
  else
    printf '200 301 302 303 307 308 401'
  fi
}

# cloudflared prints its throwaway host on its own log, so the URL is read back
# from the container instead of guessed. The banner also mentions
# api.trycloudflare.com, which is the service endpoint and never the tunnel.
#
# The container is restarted on every host boot and each start asks Cloudflare for
# a NEW host, while `docker logs` keeps the previous sessions. The current URL is
# therefore the LAST match logged since the current start, not the first or the
# alphabetically last: `--since` is the container's StartedAt and `tail -n 1`
# keeps log order.
rv_quick_tunnel_url_now() {
  local since logs
  since="$(rv_run "docker inspect -f '{{.State.StartedAt}}' $(rv_quote "$RV_QUICK_TUNNEL_CONTAINER") 2>/dev/null || true")"
  logs="docker logs"
  if [ -n "$since" ]; then
    logs="docker logs --since $(rv_quote "$since")"
  fi
  rv_run "$logs $(rv_quote "$RV_QUICK_TUNNEL_CONTAINER") 2>&1 \
    | grep -oE 'https://[a-z0-9-]+\.trycloudflare\.com' | grep -v '^https://api\.' \
    | tail -n 1" || true
}

# A quick tunnel that dies leaves no URL and no reason, so the wait reports the
# container state instead of running out the clock.
rv_quick_tunnel_url() { # TIMEOUT_SECONDS
  local timeout="$1" deadline url="" running
  deadline=$((timeout + SECONDS))
  while [ "$SECONDS" -lt "$deadline" ]; do
    url="$(rv_quick_tunnel_url_now)"
    if [ -n "$url" ]; then
      printf '%s' "$url"
      return 0
    fi
    running="$(rv_run "docker inspect -f '{{.State.Running}}' $(rv_quote "$RV_QUICK_TUNNEL_CONTAINER") 2>/dev/null || true")"
    [ "$running" = true ] \
      || rv_die "$RV_QUICK_TUNNEL_CONTAINER is not running on $(rv_target); its log says: $(rv_run "docker logs --tail 20 $(rv_quote "$RV_QUICK_TUNNEL_CONTAINER") 2>&1 | tail -n 5" || true)"
    sleep 2
  done
  rv_die "cloudflared did not report a trycloudflare.com URL within $timeout s on $(rv_target); check: docker logs $RV_QUICK_TUNNEL_CONTAINER"
}
