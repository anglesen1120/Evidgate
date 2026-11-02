#!/usr/bin/env bash
# A redeploy over an existing .env.review that has no tunnel token must tell the
# operator to APPEND the line: deleting that file mints new database and API
# passwords and locks every service out of its own data. The file is never
# rewritten by the script, so it must be unchanged after the refusal.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup tunnel-token-append

deploy="$FX_REPO/scripts/review/deploy.sh"
root="$HOME/evidgate-review"

# A quick deploy writes the file; the operator then wants the named tunnel.
if ! out="$(bash "$deploy" review-host --tunnel quick --no-dogfood 2>&1)"; then
  fx_fail "the quick deploy failed: $out"
fi
before="$(fx_sha "$root/.env.review")"

unset CLOUDFLARE_TUNNEL_TOKEN
if out="$(bash "$deploy" review-host --tunnel token --no-dogfood 2>&1)"; then
  fx_fail "a token deploy without a token was accepted"
fi
fx_assert_contains "$out" "has no CLOUDFLARE_TUNNEL_TOKEN" "the missing token is named"
fx_assert_contains "$out" "Append the line" "the operator is told to append"
fx_assert_contains "$out" "instead of deleting the file" "deleting the file is refused"
fx_assert_not_contains "$out" "delete that file on the host" "the old advice is gone"
fx_assert_eq "$(fx_sha "$root/.env.review")" "$before" "the secrets file is unchanged"

# --- a first deploy still refuses before it writes the file --------------
rm -f "$root/.env.review"
if out="$(bash "$deploy" review-host --tunnel token --no-dogfood 2>&1)"; then
  fx_fail "a first token deploy without a token was accepted"
fi
fx_assert_contains "$out" "on a first deploy needs CLOUDFLARE_TUNNEL_TOKEN" "a first deploy says why it cannot continue"
fx_assert_absent "$root/.env.review"
