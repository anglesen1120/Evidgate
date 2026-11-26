#!/usr/bin/env bash
# An env file written before the invite keys existed gains them once on the next
# deploy; every existing value stays byte for byte, and a declaration that is
# already there is never replaced.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup env-upgrade

env_file="$HOME/evidgate-review/.env.review"
deploy="$FX_REPO/scripts/review/deploy.sh"
key_a=EVIDGATE__INVITE__PEPPER
key_b=EVIDGATE__INVITE__PEPPERVERSION

env_val() { grep "^$1=" "$env_file" | cut -d= -f2-; }
without_invite() { grep -v '^EVIDGATE__INVITE__' "$env_file" >"$env_file.old"; }
deploy_ok() { bash "$deploy" review-host --no-dogfood >"$SANDBOX/deploy.out" 2>&1 || fx_fail "$1 deploy failed: $(cat "$SANDBOX/deploy.out")"; }

deploy_ok "the first"

# Rewind to a pre-invite file with no trailing newline, to prove the guard.
without_invite
printf '%s' "$(cat "$env_file.old")" >"$env_file"
rm -f "$env_file.old"
chmod 600 "$env_file"
others_before="$(cat "$env_file")"

deploy_ok "the upgrade"
content="$(cat "$env_file")"
fx_assert_eq "$(env_val "$key_b")" "1" "the missing version was added"
added="$(env_val "$key_a")"
grep -Eq '^[A-Za-z0-9+/]{43}=$' <<<"$added" || fx_fail "the added value is not 32 base64 bytes"
fx_ok "the missing key was generated"
fx_assert_eq "$(grep -c "^$key_a=" "$env_file")" "1" "the key is declared once"
fx_assert_contains "$content" "$others_before" "every existing value is unchanged"
fx_assert_not_contains "$(cat "$SANDBOX/deploy.out")" "$added" "the generated value never reaches the output"
fx_assert_eq "$(fx_mode "$env_file")" "600" "the file stays mode 600"

# A later deploy keeps both keys exactly as they are.
before="$(fx_sha "$env_file")"
deploy_ok "the third"
fx_assert_eq "$(fx_sha "$env_file")" "$before" "an existing declaration is kept, not rotated"

# An operator-chosen version is never reset to 1.
sed -i.bak "s/^$key_b=.*/$key_b=7/" "$env_file"
rm -f "$env_file.bak"
deploy_ok "the fourth"
fx_assert_eq "$(env_val "$key_b")" "7" "an existing version is kept"

# Another dotenv spelling or an empty value still counts as declared: only the
# truly absent key is added.
without_invite
printf '%s = kept-as-is\n' "$key_a" >>"$env_file.old"
cat "$env_file.old" >"$env_file"
rm -f "$env_file.old"
deploy_ok "the spelling"
fx_assert_eq "$(grep -c "^$key_a " "$env_file")" "1" "the spaced declaration stays the only one"
fx_assert_eq "$(grep -c "^$key_a=" "$env_file")" "0" "no second declaration was appended"
fx_assert_eq "$(grep -c "^$key_b=" "$env_file")" "1" "the missing partner was added beside it"

without_invite
printf '%s=\n' "$key_a" >>"$env_file.old"
cat "$env_file.old" >"$env_file"
rm -f "$env_file.old"
deploy_ok "the empty"
fx_assert_eq "$(grep -c "^$key_a=\$" "$env_file")" "1" "an empty declaration is kept for compose to refuse, not replaced"
