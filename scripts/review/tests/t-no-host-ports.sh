#!/usr/bin/env bash
# The tunnel is the only ingress, so the deploy must refuse to start a connector
# on a stack that already publishes a host port. The check has to run BEFORE the
# connector starts: a connector that is already up when the check fails leaves a
# second door open while the operator reads the error.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup no-host-ports

deploy="$FX_REPO/scripts/review/deploy.sh"

# --- a published port stops the deploy before the connector starts ---------
cat >"$SANDBOX/published" <<'PORTS'
evidgate-review-api-1 0.0.0.0:18080->8080/tcp
PORTS
export FX_PORTS="$SANDBOX/published"

if out="$(bash "$deploy" review-host --tunnel quick --no-dogfood 2>&1)"; then
  fx_fail "a stack publishing a host port was accepted"
fi
fx_assert_contains "$out" "must publish nothing on the host" "the published port is named"
fx_assert_contains "$out" "18080->8080" "the offending port is quoted"
fx_assert_log_lacks "$DOCKER_LOG" "tunnel-quick up" "the connector was never started"

# --- and the check runs again after the connector is up --------------------
: >"$DOCKER_LOG"
: >"$SANDBOX/published"
if ! out="$(bash "$deploy" review-host --tunnel quick --no-dogfood 2>&1)"; then
  fx_fail "a clean stack was refused: $out"
fi
fx_assert_log_has "$DOCKER_LOG" "tunnel-quick up -d cloudflared-quick" "the connector started on a clean stack"
count="$(grep -c 'ps --filter' "$DOCKER_LOG" || true)"
[ "$count" -ge 2 ] || fx_fail "the port check ran $count time(s), expected before and after the connector"
fx_ok "the port check ran before and after the connector"

# --- the local smoke may publish its documented loopback bind only ---------
# web-local publishes 127.0.0.1:8088->8080 by design (the local profile's whole
# point), so a blanket "no ->" rule would break every real --local deploy. Any
# other mapping, and any mapping outside loopback, is still a second door.
cat >"$SANDBOX/published" <<'PORTS'
evidgate-review-web-local 127.0.0.1:8088->8080/tcp
PORTS
# A local deploy without EVIDGATE_REVIEW_HOST_ROOT only prints the plan, so the
# root is set here: the check has to run for real against the reported mapping.
export EVIDGATE_REVIEW_HOST_ROOT="$SANDBOX/root"
if ! out="$(bash "$deploy" --local --no-dogfood 2>&1)"; then
  fx_fail "the local smoke was refused for its own loopback bind: $out"
fi
fx_assert_contains "$out" "no tunnel: nothing is published beyond the loopback binds" \
  "the local smoke accepts its own loopback bind"

cat >"$SANDBOX/published" <<'PORTS'
evidgate-review-web-local 127.0.0.1:8088->8080/tcp
evidgate-review-postgres 0.0.0.0:5432->5432/tcp
PORTS
if out="$(bash "$deploy" --local --no-dogfood 2>&1)"; then
  fx_fail "the local smoke accepted a second published port"
fi
fx_assert_contains "$out" "the local smoke may publish only 127.0.0.1:8088->8080/tcp" \
  "the unexpected local mapping is named"
fx_assert_contains "$out" "5432->5432" "the offending local port is quoted"
fx_ok "the local smoke rejects every mapping but its own loopback bind"
