#!/usr/bin/env bash
# The tunnel is the only door, so the post-deploy check must prove the door is
# locked: with Basic Auth in front, an unauthenticated request gets exactly 401,
# and a 200 there would mean the review build is public. Only a hostname behind a
# Cloudflare Access policy (no local hash) may redirect first.
#
# The accepted set is decided by rv_ingress_codes, which both tunnel branches
# call; the matrix is checked here against that helper so the suite does not wait
# out a real timeout for each combination, and one end-to-end case proves the
# deploy actually refuses a 200 on a public host.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup ingress-lock

deploy="$FX_REPO/scripts/review/deploy.sh"
root="$HOME/evidgate-review"

# --- quick mode: a 200 from the throwaway host is refused -----------------
if out="$(FX_HTTP_CODE=200 bash "$deploy" review-host --tunnel quick --no-dogfood 2>&1)"; then
  fx_fail "a quick tunnel that answered 200 was accepted"
fi
fx_assert_contains "$out" "one of [401]" "quick mode accepts exactly 401"
fx_assert_contains "$out" "the quick tunnel" "the failure names the quick tunnel"

# --- the matrix behind the helper ----------------------------------------
codes_for() { # MODE HASH
  (
    # shellcheck source=/dev/null
    . "$FX_REPO/scripts/review/lib.sh"
    # shellcheck disable=SC2034 # read by rv_ingress_codes in the sourced lib
    RV_SSH_HOST="review-host"
    # shellcheck disable=SC2034 # read by rv_ingress_codes in the sourced lib
    RV_TUNNEL_MODE="$1"
    RV_ENV_FILE="$SANDBOX/env.$1"
    printf 'EVIDGATE_REVIEW_BASIC_AUTH_HASH=%s\n' "$2" >"$RV_ENV_FILE"
    rv_ingress_codes
  )
}

fx_assert_eq "$(codes_for quick '')" "401" "quick mode requires a 401 even with no hash recorded"
# shellcheck disable=SC2016 # the apr1 prefix is a literal, not an expansion
fx_assert_eq "$(codes_for token '\$apr1\$x\$y')" "401" "token mode with a hash requires exactly 401"
fx_assert_eq "$(codes_for token '')" "200 301 302 303 307 308 401" "token mode without a hash allows the Access edge to answer"

# --- a 401 is the pass it must be, end to end ----------------------------
if ! out="$(FX_HTTP_CODE=401 bash "$deploy" review-host --tunnel quick --no-dogfood 2>&1)"; then
  fx_fail "a quick tunnel that answered 401 was refused: $out"
fi
fx_ok "a 401 is accepted as the locked door"

# --- an Access-fronted origin redirects, and that is the pass -------------
# A token deploy writes the token into a fresh file; the operator then removes
# the local hash because the edge holds the policy.
rm -f "$root/.env.review"
if ! out="$(FX_HTTP_CODE=401 CLOUDFLARE_TUNNEL_TOKEN=fixture-tunnel-token \
  bash "$deploy" review-host --tunnel token --public-origin https://review.example.com --no-dogfood 2>&1)"; then
  fx_fail "the first token deploy failed: $out"
fi
grep -v '^EVIDGATE_REVIEW_BASIC_AUTH_HASH=' "$root/.env.review" >"$root/.env.review.tmp"
mv "$root/.env.review.tmp" "$root/.env.review"
chmod 600 "$root/.env.review"
: >"$DOCKER_LOG"
if ! out="$(FX_HTTP_CODE=302 CLOUDFLARE_TUNNEL_TOKEN=fixture-tunnel-token \
  bash "$deploy" review-host --tunnel token --no-dogfood 2>&1)"; then
  fx_fail "an Access-fronted origin that redirected was refused: $out"
fi
fx_ok "a redirect is accepted when no local hash is configured"
fx_assert_log_has "$DOCKER_LOG" "--profile tunnel up -d --wait cloudflared" "the token tunnel is the ingress started"

# --- the tunnel token never reaches a child process ----------------------
fx_assert_log_lacks "$DOCKER_LOG" "LEAK " "no docker call inherits CLOUDFLARE_TUNNEL_TOKEN"
