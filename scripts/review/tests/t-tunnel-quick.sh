#!/usr/bin/env bash
# A quick tunnel needs no token and no hostname: cloudflared prints a throwaway
# trycloudflare.com URL, which is the only address reviewers are given. Because
# that address is on the internet, the build behind it must always be behind
# HTTP Basic Auth, and those credentials are minted once and never rewritten.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup quick

root="$HOME/evidgate-review"
deploy="$FX_REPO/scripts/review/deploy.sh"

unset CLOUDFLARE_TUNNEL_TOKEN
if ! out="$(bash "$deploy" review-host --tunnel quick --no-dogfood 2>&1)"; then
  fx_fail "the quick deploy failed: $out"
fi
fx_assert_log_has "$DOCKER_LOG" "--profile tunnel-quick up -d cloudflared-quick" "the quick tunnel is the ingress started"
fx_assert_contains "$out" "https://bright-river-1234.trycloudflare.com" "the trycloudflare URL is read from the log and printed"
fx_assert_contains "$out" "user/password are in $root/.env.review" "the output says where the credentials are"
if grep -F -- "-f" "$DOCKER_LOG" | grep -q -- "--profile tunnel up"; then
  fx_fail "the token tunnel was started in quick mode"
fi
fx_ok "the token tunnel is not started"

content="$(cat "$root/.env.review")"
fx_assert_contains "$content" 'EVIDGATE_REVIEW_TUNNEL_MODE=quick' "the mode is recorded in the env file"
fx_assert_contains "$content" 'EVIDGATE_REVIEW_BASIC_AUTH_USER=reviewer' "the reviewer user is the fixed one"
grep -Eq '^EVIDGATE_REVIEW_BASIC_AUTH_PASSWORD=[0-9a-f]{48}$' "$root/.env.review" \
  || fx_fail "the password is not 48 hex characters"
fx_ok "the password is 48 hex characters"
grep -Eq '^EVIDGATE__SESSION__PEPPER=.{44}$' "$root/.env.review" \
  || fx_fail "the session pepper is missing: /v1/dev/session would answer 503"
fx_ok "the session pepper is minted as base64 32 bytes"
# Single quotes matter: compose's dotenv would read the $apr1 prefix as a
# variable reference and hand the container a mangled hash.
grep -Eq "^EVIDGATE_REVIEW_BASIC_AUTH_HASH='\\\$apr1\\\$[./A-Za-z0-9]+\\\$[./A-Za-z0-9]+'$" "$root/.env.review" \
  || fx_fail "the hash is not a single-quoted apr1 line: $(grep BASIC_AUTH_HASH "$root/.env.review")"
fx_ok "the hash is a single-quoted apr1 value"

password="$(sed -n 's/^EVIDGATE_REVIEW_BASIC_AUTH_PASSWORD=//p' "$root/.env.review")"
hash="$(sed -n 's/^EVIDGATE_REVIEW_BASIC_AUTH_HASH=//p' "$root/.env.review")"
hash="${hash#\'}"
hash="${hash%\'}"
fx_assert_not_contains "$out" "$password" "the password never reaches the output"
fx_assert_not_contains "$out" "$hash" "the hash never reaches the output"

before="$(fx_sha "$root/.env.review")"
if ! out="$(bash "$deploy" review-host --tunnel quick --no-dogfood 2>&1)"; then
  fx_fail "the second deploy failed: $out"
fi
fx_assert_eq "$(fx_sha "$root/.env.review")" "$before" "the credentials are minted once and never rewritten"
fx_assert_contains "$out" "kept existing" "a second deploy keeps the secrets file"
