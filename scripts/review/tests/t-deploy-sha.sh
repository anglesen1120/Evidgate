#!/usr/bin/env bash
# A deploy ships exactly the commit it was asked for: a dirty tree is refused
# before anything leaves this machine, and `--sha <older>` ships that tree, not
# HEAD.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup sha

root="$HOME/evidgate-review"
deploy="$FX_REPO/scripts/review/deploy.sh"

printf 'uncommitted edit\n' >>"$FX_REPO/VERSION"
if out="$(bash "$deploy" review-host --no-dogfood 2>&1)"; then
  fx_fail "a dirty tree must be refused, but the deploy succeeded"
fi
fx_assert_contains "$out" "dirty" "a dirty tree is refused by name"
fx_assert_log_lacks "$DOCKER_LOG" "build" "the refusal built nothing"
fx_assert_log_lacks "$DOCKER_LOG" "--project-name" "the refusal ran no compose command"
fx_assert_log_lacks "$SSH_LOG" "mkdir" "the refusal made no change on the host"
git -C "$FX_REPO" checkout -- VERSION

if ! out="$(bash "$deploy" review-host --sha "$FX_FIRST_SHA" --no-dogfood 2>&1)"; then
  fx_fail "deploying the older sha failed: $out"
fi
release="$root/releases/$FX_FIRST_SHA"
fx_assert_file "$release/.release-sha"
fx_assert_eq "$(cat "$release/.release-sha")" "$FX_FIRST_SHA" "the release marker is the asked sha"
fx_assert_eq "$(cat "$release/VERSION")" "first release" "the shipped tree is the asked sha"
fx_assert_absent "$root/releases/$FX_SECOND_SHA"
fx_assert_eq "$(readlink "$root/current")" "releases/$FX_FIRST_SHA" "current points at the shipped release"
fx_assert_eq "$(sed -n 's/^EVIDGATE_REVIEW_SHA=//p' "$root/.env.review")" "$FX_FIRST_SHA" "the env file tags images with the release sha"
fx_assert_log_has "$DOCKER_LOG" "$FX_FIRST_SHA" "every compose call tags the release sha"

out="$(bash "$FX_REPO/scripts/review/status.sh" review-host 2>&1)"
fx_assert_contains "$out" "$FX_FIRST_SHA" "status reports the shipped sha"
fx_assert_contains "$out" "matches current" "status proves the marker matches current"
