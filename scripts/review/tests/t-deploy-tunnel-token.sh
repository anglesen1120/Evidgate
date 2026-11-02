#!/usr/bin/env bash
# --public-origin without CLOUDFLARE_TUNNEL_TOKEN must stop before the secrets
# file is created: that file is written once and never rewritten, so a first
# deploy that recorded an empty token could never be given a tunnel later.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup tunnel-token

root="$HOME/evidgate-review"
deploy="$FX_REPO/scripts/review/deploy.sh"

unset CLOUDFLARE_TUNNEL_TOKEN
if out="$(bash "$deploy" review-host --public-origin https://review.example.com --no-dogfood 2>&1)"; then
  fx_fail "a tunnel deploy without a token must be refused, but it succeeded"
fi
fx_assert_contains "$out" "CLOUDFLARE_TUNNEL_TOKEN" "the refusal names the missing token"
fx_assert_absent "$root/.env.review"
fx_assert_log_lacks "$DOCKER_LOG" "build" "the refusal built nothing"
fx_assert_log_lacks "$DOCKER_LOG" "--project-name" "the refusal ran no compose command"
fx_assert_log_lacks "$SSH_LOG" "mkdir" "the refusal made no change on the host"

export CLOUDFLARE_TUNNEL_TOKEN="fixture-tunnel-token-value"
if ! out="$(bash "$deploy" review-host --public-origin https://review.example.com --no-dogfood 2>&1)"; then
  fx_fail "the tunnel deploy with a token failed: $out"
fi
fx_assert_file "$root/.env.review"
fx_assert_contains "$(cat "$root/.env.review")" 'EVIDGATE_REVIEW_PUBLIC_ORIGIN=https://review.example.com' "the origin is recorded for the CSP and the tunnel"
fx_assert_contains "$(cat "$root/.env.review")" 'CLOUDFLARE_TUNNEL_TOKEN=fixture-tunnel-token-value' "the token reaches the env file"
fx_assert_not_contains "$out" "fixture-tunnel-token-value" "the token never reaches the output"
fx_assert_log_has "$DOCKER_LOG" "--profile tunnel up -d --wait cloudflared" "the tunnel is the last ingress started"
fx_assert_log_lacks "$DOCKER_LOG" "0.0.0.0" "nothing is published on all interfaces"
