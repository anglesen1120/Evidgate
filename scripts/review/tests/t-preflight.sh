#!/usr/bin/env bash
# The preflight has to accept the compose a real host ships (its major is not 2
# everywhere) and must not demand node or python3 on a host that only loads
# images: under --build-local the release's static gate runs on this machine.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup preflight

# --- a newer compose major is accepted -------------------------------------
if ! out="$(FX_COMPOSE_VERSION=v5.6.1 bash "$FX_REPO/scripts/review/deploy.sh" review-host --build-local --no-dogfood 2>&1)"; then
  fx_fail "compose v5.6.1 was refused: $out"
fi
fx_assert_contains "$out" "docker compose v5.6.1" "report v5.6.1 in the preflight line"

# --- a compose 1.x is still refused ----------------------------------------
if out="$(FX_COMPOSE_VERSION=v1.29.2 bash "$FX_REPO/scripts/review/deploy.sh" review-host --no-dogfood 2>&1)"; then
  fx_fail "compose v1.29.2 was accepted"
fi
fx_assert_contains "$out" "docker compose v2 or newer" "compose 1.x is refused with the floor"

# --- a load-only host is never asked for node or python3 -------------------
fx_assert_log_lacks "$SSH_LOG" "command -v node" "the host is not asked for node"
fx_assert_log_lacks "$SSH_LOG" "command -v python3" "the host is not asked for python3"
# ... and the static gate ran here instead, over the extracted sha.
fx_assert_log_has "$GATE_LOG" "--static-only" "the static gate ran on this machine"
fx_assert_log_lacks "$SSH_LOG" "check.sh --static-only" "the host ran no gate"

# --- a building host still needs both tools --------------------------------
: >"$SSH_LOG"
if ! out="$(bash "$FX_REPO/scripts/review/deploy.sh" review-host --no-dogfood 2>&1)"; then
  fx_fail "the building deploy failed: $out"
fi
fx_assert_log_has "$SSH_LOG" "command -v node" "a building host is asked for node"
fx_assert_log_has "$SSH_LOG" "command -v python3" "a building host is asked for python3"
fx_assert_log_has "$SSH_LOG" "check.sh --static-only" "a building host runs the gate before the build"

fx_ok "the preflight matches what the host is asked to do"
