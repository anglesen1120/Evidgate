#!/usr/bin/env bash
# A release tree per sha fills a 30 GB host, so only the three newest and the one
# `current` points at are kept. The move into the release directory must be a
# single rename: `mv` without -T moves INTO a directory that appeared
# concurrently and leaves a half tree behind.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup release-retention

deploy="$FX_REPO/scripts/review/deploy.sh"
root="$HOME/evidgate-review"

if ! out="$(bash "$deploy" review-host --no-dogfood 2>&1)"; then
  fx_fail "the first deploy failed: $out"
fi

# Five older releases, oldest first, with distinct mtimes so -t is meaningful.
i=1
for sha in 1111111111111111111111111111111111111111 \
  2222222222222222222222222222222222222222 \
  3333333333333333333333333333333333333333 \
  4444444444444444444444444444444444444444; do
  mkdir -p "$root/releases/$sha"
  touch -t "20260101010$i" "$root/releases/$sha"
  i=$((i + 1))
done

# current is the newest, as a real deploy leaves it.
target="$(readlink "$root/current")"
[ -n "$target" ] || fx_fail "current is not a symlink"
touch "$root/releases/${target#releases/}"

# shellcheck disable=SC1091 # the fixture path depends on the caller
(
  # shellcheck source=/dev/null
  . "$FX_REPO/scripts/review/lib.sh"
  # shellcheck disable=SC2034 # read by the ssh stub and rv_run in the sourced lib
  RV_SSH_HOST="review-host"
  # shellcheck disable=SC2034 # read by rv_prune_releases in the sourced lib
  RV_HOST_ROOT="$root"
  # shellcheck disable=SC2034 # same
  RV_ENV_FILE="$root/.env.review"
  rv_prune_releases
)

kept="$(find "$root/releases" -mindepth 1 -maxdepth 1 -type d | wc -l | tr -d ' ')"
[ "$kept" = 3 ] || fx_fail "kept $kept release(s), expected 3: $(ls -1 "$root/releases")"
fx_ok "three releases are kept"
[ -L "$root/current" ] || fx_fail "current stopped being a symlink"
[ -d "$root/$target" ] || fx_fail "the release current points at was deleted"
fx_ok "the release current points at survives the prune"
[ -f "$root/releases/1111111111111111111111111111111111111111/.release-sha" ] \
  && fx_fail "the oldest release was not pruned"
fx_ok "the oldest release is gone"

# --- a release directory with a foreign marker is refused, never merged ----
mkdir -p "$root/releases/$FX_FIRST_SHA"
printf '%s\n' "0000000000000000000000000000000000000000" >"$root/releases/$FX_FIRST_SHA/.release-sha"
if out="$(bash "$deploy" review-host --sha "$FX_FIRST_SHA" --no-dogfood 2>&1)"; then
  fx_fail "a deploy over an existing release directory was accepted"
fi
fx_assert_contains "$out" "exists but its .release-sha reads" "the foreign directory is reported"
