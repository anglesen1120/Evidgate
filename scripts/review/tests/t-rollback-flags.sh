#!/usr/bin/env bash
# The rollback hint must be a command that reproduces the release: without
# --build-local it rebuilds on a host that cannot build, and without --tunnel it
# comes back with no ingress at all.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup rollback-flags

deploy="$FX_REPO/scripts/review/deploy.sh"

if ! out="$(bash "$deploy" review-host --build-local --tunnel quick --no-dogfood 2>&1)"; then
  fx_fail "the first deploy failed: $out"
fi
# A second release, so there is a previous one to roll back to.
printf 'third release\n' >"$FX_REPO/VERSION"
git -C "$FX_REPO" -c user.name=test -c user.email=test@example.com commit -qam three
if ! out="$(bash "$deploy" review-host --build-local --tunnel quick --no-dogfood 2>&1)"; then
  fx_fail "the second deploy failed: $out"
fi

fx_assert_contains "$out" "previous release: releases/$FX_SECOND_SHA" "the previous release is named"
fx_assert_contains "$out" "--sha $FX_SECOND_SHA" "the rollback names the sha"
fx_assert_contains "$out" "--build-local" "the rollback keeps --build-local"
fx_assert_contains "$out" "--tunnel quick" "the rollback keeps the ingress"
