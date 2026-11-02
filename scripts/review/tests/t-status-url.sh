#!/usr/bin/env bash
# The host is restarted every morning, and a restarted cloudflared-quick asks for
# a NEW trycloudflare host while `docker logs` keeps every earlier session. The
# URL a reviewer is given is therefore the LAST one logged since the current
# start, never the first and never the alphabetically last.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup statusurl

# Three sessions in one log: an old host that sorts last, the service endpoint
# from the banner, an older host, and the current one at the end.
export FX_QUICK_LOG="$SANDBOX/cloudflared.log"
export FX_STARTED_AT="2026-11-02T08:00:00Z"
cat >"$FX_QUICK_LOG" <<'LOG'
2026-11-01T08:00:00Z INF Requesting new quick Tunnel on trycloudflare.com...
2026-11-01T08:00:01Z INF |  https://zebra-previous.trycloudflare.com  |
2026-11-02T08:00:00Z INF Requesting new quick Tunnel on trycloudflare.com...
2026-11-02T08:00:00Z INF +------------------------------------------------------------------------------+
2026-11-02T08:00:00Z INF |  https://api.trycloudflare.com/v1/tunnels/9f1c  |
2026-11-02T08:00:01Z INF |  https://aaa-older.trycloudflare.com  |
2026-11-02T08:00:02Z INF |  https://mmm-current.trycloudflare.com  |
LOG

if ! out="$(bash "$FX_REPO/scripts/review/status.sh" review-host 2>&1)"; then
  fx_fail "status.sh failed: $out"
fi

fx_assert_contains "$out" "https://mmm-current.trycloudflare.com" "the current URL is printed"
fx_assert_not_contains "$out" "zebra-previous" "an earlier session's URL is not printed"
fx_assert_not_contains "$out" "aaa-older" "an earlier URL of the current session is not printed"
fx_assert_not_contains "$out" "api.trycloudflare.com" "the service endpoint is never the tunnel"
fx_assert_contains "$out" "changes every time the host or the container starts" \
  "status says the URL does not survive a boot"

# The log is narrowed to the current start, so yesterday's session cannot win.
fx_assert_log_has "$DOCKER_LOG" "--since" "the log is read from the container's start"
fx_assert_log_has "$DOCKER_LOG" "$FX_STARTED_AT" "the start time is the one docker reported"

# A container that has not logged a URL yet says so instead of printing nothing.
: >"$FX_QUICK_LOG"
if ! out="$(bash "$FX_REPO/scripts/review/status.sh" review-host 2>&1)"; then
  fx_fail "status.sh failed on an empty log: $out"
fi
fx_assert_contains "$out" "no trycloudflare.com URL in its log since its last start" \
  "an empty log is reported as such"

fx_ok "the current quick URL is the last one logged since the start"
