#!/usr/bin/env bash
# Status reports which ingress is live, and a stopped connector publishes
# nothing: it must read as "none", not as a live tunnel that reviewers cannot
# reach.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup status-connector

deploy="$FX_REPO/scripts/review/deploy.sh"
status="$FX_REPO/scripts/review/status.sh"

if ! out="$(bash "$deploy" review-host --tunnel quick --no-dogfood 2>&1)"; then
  fx_fail "the quick deploy failed: $out"
fi

# --- running connector: quick is reported --------------------------------
if ! out="$(FX_RUNNING=true bash "$status" review-host 2>&1)"; then
  fx_fail "status failed: $out"
fi
fx_assert_contains "$out" "tunnel          : quick" "a running connector is reported"

# --- stopped connector: nothing is published -----------------------------
if ! out="$(FX_RUNNING=false bash "$status" review-host 2>&1)"; then
  fx_fail "status failed on a stopped connector: $out"
fi
fx_assert_contains "$out" "tunnel          : none" "a stopped connector is not reported as live"
fx_assert_not_contains "$out" "tunnel          : quick" "a stopped connector is never called quick"
