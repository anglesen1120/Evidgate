#!/usr/bin/env bash
# --local without EVIDGATE_REVIEW_HOST_ROOT must not invent a root: a dry run
# creates nothing, and status or down against an invented empty root would report
# a stack that does not exist and tear nothing down.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup local-root

deploy="$FX_REPO/scripts/review/deploy.sh"
status="$FX_REPO/scripts/review/status.sh"
down="$FX_REPO/scripts/review/down.sh"
mkdir -p "$SANDBOX/tmp"
export TMPDIR="$SANDBOX/tmp"
unset EVIDGATE_REVIEW_HOST_ROOT

# --- a dry run prints an illustrative root and creates nothing -------------
if ! out="$(bash "$deploy" --local --dry-run 2>&1)"; then
  fx_fail "a --local dry run died: $out"
fi
fx_assert_contains "$out" "$TMPDIR/evidgate-review-local.XXXXXXXX" "the plan shows a root of the same shape"
[ -z "$(ls -A "$TMPDIR")" ] || fx_fail "a dry run created $TMPDIR/$(ls -A "$TMPDIR")"
fx_ok "a dry run created no root"

# --- status and down refuse instead of inventing an empty root ------------
if out="$(bash "$status" --local 2>&1)"; then
  fx_fail "status --local invented a root"
fi
fx_assert_contains "$out" "EVIDGATE_REVIEW_HOST_ROOT" "status names the variable"
fx_assert_contains "$out" "never a new one" "status explains why it refuses"

if out="$(bash "$down" --local 2>&1)"; then
  fx_fail "down --local invented a root"
fi
fx_assert_contains "$out" "EVIDGATE_REVIEW_HOST_ROOT" "down names the variable"
[ -z "$(ls -A "$TMPDIR")" ] || fx_fail "a refused status or down created a root"
fx_ok "a refused status or down created no root"

# --- with the root named, status reads it --------------------------------
root="$SANDBOX/root"
mkdir -p "$root/releases/abc"
ln -s releases/abc "$root/current"
printf '%s\n' abc >"$root/releases/abc/.release-sha"
if ! out="$(EVIDGATE_REVIEW_HOST_ROOT="$root" bash "$status" --local 2>&1)"; then
  fx_fail "status --local with a root failed: $out"
fi
fx_assert_contains "$out" "current         : releases/abc" "status reads the named root"
fx_assert_contains "$out" "release marker  : abc (matches current)" "status proves the marker"
