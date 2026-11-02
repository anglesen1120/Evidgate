#!/usr/bin/env bash
# down.sh keeps the volumes unless --purge, and --purge never runs without an
# explicit --yes: it deletes the database, the bucket and every release.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup down

root="$HOME/evidgate-review"
down="$FX_REPO/scripts/review/down.sh"

if ! out="$(bash "$FX_REPO/scripts/review/deploy.sh" review-host --no-dogfood 2>&1)"; then
  fx_fail "the deploy failed: $out"
fi
fx_assert_file "$root/releases/$FX_SECOND_SHA/.release-sha"

: >"$DOCKER_LOG"
if ! out="$(bash "$down" review-host 2>&1)"; then
  fx_fail "a plain down failed: $out"
fi
fx_assert_log_has "$DOCKER_LOG" "down" "a plain down stops the stack"
fx_assert_log_lacks "$DOCKER_LOG" "-v" "a plain down keeps the volumes"
fx_assert_file "$root/releases/$FX_SECOND_SHA/.release-sha"

: >"$DOCKER_LOG"
if out="$(bash "$down" review-host --purge 2>&1)"; then
  fx_fail "--purge without --yes must refuse, but it ran"
fi
fx_assert_contains "$out" "--yes" "the refusal names the missing confirmation"
fx_assert_log_lacks "$DOCKER_LOG" "down" "a refused purge runs no teardown"
fx_assert_file "$root/releases/$FX_SECOND_SHA/.release-sha"

: >"$DOCKER_LOG"
if ! out="$(bash "$down" review-host --purge --yes 2>&1)"; then
  fx_fail "a confirmed purge failed: $out"
fi
fx_assert_log_has "$DOCKER_LOG" "down -v" "a confirmed purge deletes the volumes"
fx_assert_absent "$root/releases"
fx_assert_absent "$root/current"
fx_assert_file "$root/.env.review"
