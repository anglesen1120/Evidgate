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

# cloudflared prints its throwaway host on its own log, so the URL is read back
# from the container instead of guessed. The banner also mentions
# api.trycloudflare.com, which is the service endpoint and never the tunnel.
rv_quick_tunnel_url_now() {
  rv_run "docker logs $(rv_quote "$RV_QUICK_TUNNEL_CONTAINER") 2>&1 \
    | grep -oE 'https://[a-z0-9-]+\.trycloudflare\.com' | grep -v '^https://api\.' \
    | sort -u | tail -n 1" || true
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
