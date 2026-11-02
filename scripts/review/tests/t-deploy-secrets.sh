#!/usr/bin/env bash
# The secrets file is generated once with mode 600 and a later deploy keeps it
# byte for byte: a rewrite would lock every running service out of its own
# database, and the reviewers out of the only account they can sign in with.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup secrets

env_file="$HOME/evidgate-review/.env.review"
deploy="$FX_REPO/scripts/review/deploy.sh"

if ! out="$(bash "$deploy" review-host --no-dogfood 2>&1)"; then
  fx_fail "the first deploy failed: $out"
fi
fx_assert_file "$env_file"
fx_assert_eq "$(fx_mode "$env_file")" "600" "the secrets file is mode 600"

if grep -Eq '=__[A-Za-z0-9_]+__$' "$env_file"; then
  fx_fail "the secrets file still holds a placeholder: $(grep -E '=__[A-Za-z0-9_]+__$' "$env_file")"
fi
fx_ok "every placeholder was replaced"

content="$(cat "$env_file")"
fx_assert_not_contains "$content" 'EVIDGATE_SEAWEEDFS_S3_CONFIG' "the object-store identity is not duplicated into the env file"
fx_assert_contains "$content" 'EVIDGATE__DEVLOGIN__ENABLED=true' "the Development-only sign-in is enabled"
fx_assert_not_contains "$content" 'evidgate-local-test-only' "the committed dev identity is not reused"
fx_assert_not_contains "$out" "$(grep '^EVIDGATE__DEVLOGIN__SECRET=' "$env_file" | cut -d= -f2-)" "the sign-in secret never reaches the output"

# Lengths matter: the Api rejects a short pepper or sign-in secret at startup,
# and a mismatch here only shows up as a 503 on the first ingest.
while IFS='|' read -r key pattern; do
  grep -Eq "^$key=$pattern$" "$env_file" || fx_fail "$key does not match $pattern"
  fx_ok "$key has the shape its consumer validates"
done <<'SHAPES'
EVIDGATE_DB_SUPERUSER_PASSWORD|[0-9a-f]{48}
EVIDGATE_S3_ACCESS_KEY|[0-9a-f]{48}
EVIDGATE_S3_SECRET_KEY|[0-9a-f]{48}
EVIDGATE__CI__PEPPER|[A-Za-z0-9+/]{43}=
EVIDGATE__CI__SIGNINGKEY|[A-Za-z0-9+/]{64}
EVIDGATE__DEVLOGIN__SECRET|[A-Za-z0-9+/]{43}=
SHAPES
fx_assert_contains "$content" 'Password='"$(grep '^EVIDGATE_APP_PASSWORD=' "$env_file" | cut -d= -f2-)" "the connection string repeats the app password, so the roles match"

before="$(fx_sha "$env_file")"

if ! out="$(bash "$deploy" review-host --no-dogfood 2>&1)"; then
  fx_fail "the second deploy failed: $out"
fi
fx_assert_contains "$out" "kept existing" "a second deploy reports the file kept"
fx_assert_eq "$(fx_sha "$env_file")" "$before" "a second deploy does not rewrite the secrets file"
