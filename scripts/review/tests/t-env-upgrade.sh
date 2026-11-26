#!/usr/bin/env bash
# An env file written before the invite pepper existed gains the two keys once on
# the next deploy; every existing value stays byte for byte, and a pepper that is
# already there is never rotated.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup env-upgrade

env_file="$HOME/evidgate-review/.env.review"
deploy="$FX_REPO/scripts/review/deploy.sh"

bash "$deploy" review-host --no-dogfood >/dev/null 2>&1 || fx_fail "the first deploy failed"

# Rewind to a pre-pepper file, with no trailing newline to prove the guard.
grep -v '^EVIDGATE__INVITE__' "$env_file" >"$env_file.old"
printf '%s' "$(cat "$env_file.old")" >"$env_file"
rm -f "$env_file.old"
chmod 600 "$env_file"
others_before="$(cat "$env_file")"

if ! out="$(bash "$deploy" review-host --no-dogfood 2>&1)"; then
  fx_fail "the upgrade deploy failed: $out"
fi
content="$(cat "$env_file")"
fx_assert_contains "$content" 'EVIDGATE__INVITE__PEPPERVERSION=1' "the missing version was added"
pepper="$(grep '^EVIDGATE__INVITE__PEPPER=' "$env_file" | cut -d= -f2-)"
grep -Eq '^[A-Za-z0-9+/]{43}=$' <<<"$pepper" || fx_fail "the added pepper is not 32 base64 bytes"
fx_ok "the missing pepper was generated"
fx_assert_eq "$(grep -c '^EVIDGATE__INVITE__PEPPER=' "$env_file")" "1" "the pepper is declared once"
fx_assert_contains "$content" "$others_before" "every existing value is unchanged"
fx_assert_not_contains "$out" "$pepper" "the generated pepper never reaches the output"
fx_assert_eq "$(fx_mode "$env_file")" "600" "the file stays mode 600"

# A third deploy keeps both keys exactly as they are.
before="$(fx_sha "$env_file")"
bash "$deploy" review-host --no-dogfood >/dev/null 2>&1 || fx_fail "the third deploy failed"
fx_assert_eq "$(fx_sha "$env_file")" "$before" "an existing pepper is kept, not rotated"

# An operator-chosen version is never reset to 1.
sed -i.bak 's/^EVIDGATE__INVITE__PEPPERVERSION=.*/EVIDGATE__INVITE__PEPPERVERSION=7/' "$env_file"
rm -f "$env_file.bak"
bash "$deploy" review-host --no-dogfood >/dev/null 2>&1 || fx_fail "the fourth deploy failed"
fx_assert_contains "$(cat "$env_file")" 'EVIDGATE__INVITE__PEPPERVERSION=7' "an existing version is kept"

# Partial and unusual states: a declaration in another dotenv spelling, or with an
# empty value, still counts as declared, so only the truly absent key is added.
grep -v '^EVIDGATE__INVITE__' "$env_file" >"$env_file.old"
printf 'EVIDGATE__INVITE__PEPPER = spaced-pepper-kept\n' >>"$env_file.old"
cat "$env_file.old" >"$env_file"
rm -f "$env_file.old"
bash "$deploy" review-host --no-dogfood >/dev/null 2>&1 || fx_fail "the partial-state deploy failed"
fx_assert_eq "$(grep -c 'EVIDGATE__INVITE__PEPPER ' "$env_file")" "1" "the spaced pepper stays the only pepper"
fx_assert_eq "$(grep -c '^EVIDGATE__INVITE__PEPPER=' "$env_file")" "0" "no second pepper was appended"
fx_assert_eq "$(grep -c '^EVIDGATE__INVITE__PEPPERVERSION=' "$env_file")" "1" "the missing version was added beside it"

sed -i.bak 's/^EVIDGATE__INVITE__PEPPER = .*/EVIDGATE__INVITE__PEPPER=/' "$env_file"
rm -f "$env_file.bak"
bash "$deploy" review-host --no-dogfood >/dev/null 2>&1 || fx_fail "the empty-value deploy failed"
fx_assert_eq "$(grep -c '^EVIDGATE__INVITE__PEPPER=$' "$env_file")" "1" "an empty declaration is kept for compose to refuse, not replaced"
