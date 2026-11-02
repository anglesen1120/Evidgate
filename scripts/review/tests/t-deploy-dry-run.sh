#!/usr/bin/env bash
# --dry-run prints the plan and performs only read-only preflight: no secret in
# the output, no docker call, no write on the host.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup dryrun

export CLOUDFLARE_TUNNEL_TOKEN="fixture-tunnel-token-value"

if ! out="$(bash "$FX_REPO/scripts/review/deploy.sh" review-host --dry-run \
  --public-origin https://review.example.com 2>&1)"; then
  fx_fail "the dry run failed: $out"
fi

fx_assert_contains "$out" "dry run: nothing is created" "the dry run says what it does"
fx_assert_contains "$out" "cloudflared" "the plan shows the tunnel step"
fx_assert_not_contains "$out" "fixture-tunnel-token-value" "the tunnel token never reaches the output"
fx_assert_log_lacks "$DOCKER_LOG" "build" "the dry run built nothing"
fx_assert_log_lacks "$DOCKER_LOG" "--project-name" "the dry run ran no compose command"
fx_assert_absent "$HOME/evidgate-review"

for needle in mkdir "cat >" "tar -xf" chmod "mv " "--project-name" "up -d" "run --rm" "seed-dev" "ln -sfn"; do
  fx_assert_log_lacks "$SSH_LOG" "$needle" "the dry run sent no '$needle'"
done
