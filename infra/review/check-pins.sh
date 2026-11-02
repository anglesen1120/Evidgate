#!/usr/bin/env bash
# Every third-party image this stack pulls must be digest-pinned, the two shared
# with the dev stack must be the very same pin, and the three images this stack
# builds must be tagged with the release sha.
#
# scripts/build-images.sh --check-pins audits the .NET Dockerfiles only, so this
# is the check that keeps the review stack's pins from drifting. It reads
# infra/compose.dev.yml and .nvmrc; it never edits them.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=infra/review/lib-check.sh
. "$SCRIPT_DIR/lib-check.sh"

DEV_COMPOSE="$REVIEW_DIR/../compose.dev.yml"
NVMRC="$REVIEW_DIR/../../.nvmrc"
DOCKERFILE_WEB="$REVIEW_DIR/Dockerfile.web"
DOCKERFILE_SEED="$REVIEW_DIR/Dockerfile.seed"
fail=0

[ -f "$REVIEW_COMPOSE" ] || review_die "missing $REVIEW_COMPOSE"
[ -f "$DOCKERFILE_WEB" ] || review_die "missing $DOCKERFILE_WEB"
[ -f "$DOCKERFILE_SEED" ] || review_die "missing $DOCKERFILE_SEED"

review_step 'compose image references'
while IFS= read -r ref; do
  case "$ref" in
    evidgate-review-*)
      case "$ref" in
        *"\${EVIDGATE_REVIEW_TAG"*) ;;
        *) echo "check-pins: built image '$ref' is not tagged with the release tag" >&2; fail=1 ;;
      esac
      ;;
    *@sha256:*)
      if ! printf '%s' "${ref##*@}" | grep -Eq '^sha256:[0-9a-f]{64}$'; then
        echo "check-pins: '$ref' has a malformed digest" >&2
        fail=1
      fi
      ;;
    *)
      echo "check-pins: '$ref' is a mutable tag: every pulled image needs @sha256:<64 hex>" >&2
      fail=1
      ;;
  esac
done < <(sed -nE 's/^[[:space:]]+image:[[:space:]]*(.+[^[:space:]])[[:space:]]*$/\1/p' "$REVIEW_COMPOSE")

review_step 'web Dockerfile FROM lines'
stages=0
while IFS= read -r line; do
  stages=$((stages + 1))
  ref="${line##* }"
  case "$ref" in
    *@sha256:*) ;;
    *) echo "check-pins: Dockerfile.web FROM '$ref' is not digest-pinned" >&2; fail=1 ;;
  esac
done < <(grep -E '^FROM ' "$DOCKERFILE_WEB" | sed -E 's/[[:space:]]+AS[[:space:]]+[A-Za-z0-9_.-]+$//')
[ "$stages" -ge 2 ] || { echo "check-pins: Dockerfile.web must have a build and a runtime stage" >&2; fail=1; }

review_step 'shared pins match the dev stack'
if [ -f "$DEV_COMPOSE" ]; then
  for repo in postgres chrislusf/seaweedfs; do
    dev="$(sed -nE "s|^[[:space:]]+image:[[:space:]]*${repo}:[^@]*@(sha256:[0-9a-f]{64})[[:space:]]*\$|\\1|p" "$DEV_COMPOSE" | head -1)"
    review="$(sed -nE "s|^[[:space:]]+image:[[:space:]]*${repo}:[^@]*@(sha256:[0-9a-f]{64})[[:space:]]*\$|\\1|p" "$REVIEW_COMPOSE" | head -1)"
    if [ -z "$review" ]; then
      echo "check-pins: compose.review.yml does not pin $repo by digest" >&2
      fail=1
    elif [ "$review" != "$dev" ]; then
      echo "check-pins: $repo is pinned to $review but the dev stack uses $dev" >&2
      fail=1
    fi
  done
else
  echo "check-pins: $DEV_COMPOSE is missing; cannot compare the shared pins" >&2
  fail=1
fi

review_step 'the seed image derives from the migrator image'
froms="$(grep -cE '^FROM ' "$DOCKERFILE_SEED" || true)"
[ "$froms" = 1 ] || {
  echo "check-pins: Dockerfile.seed must have exactly one FROM line (found $froms)" >&2
  fail=1
}
base="$(sed -nE 's/^FROM[[:space:]]+(.*[^[:space:]])[[:space:]]*$/\1/p' "$DOCKERFILE_SEED")"
# shellcheck disable=SC2016 # the Dockerfile's own ${BASE}, not a shell value
[ "$base" = '${BASE}' ] || {
  echo "check-pins: Dockerfile.seed must take its base as ARG BASE (found '$base'): it is a local derivative, not a registry image" >&2
  fail=1
}
grep -qE '^ARG BASE[[:space:]]*$' "$DOCKERFILE_SEED" || {
  echo "check-pins: Dockerfile.seed declares no 'ARG BASE'" >&2
  fail=1
}
seed_image="$(sed -nE 's/^[[:space:]]+image:[[:space:]]*(evidgate-review-seed:.+[^[:space:]])[[:space:]]*$/\1/p' "$REVIEW_COMPOSE")"
[ -n "$seed_image" ] || {
  echo "check-pins: compose.review.yml has no seed image" >&2
  fail=1
}
seed_base="$(sed -nE 's/^[[:space:]]+BASE:[[:space:]]*(evidgate-review-migrator:.+[^[:space:]])[[:space:]]*$/\1/p' "$REVIEW_COMPOSE")"
[ -n "$seed_base" ] || {
  echo "check-pins: the seed build takes no BASE=evidgate-review-migrator:<tag> argument" >&2
  fail=1
}
case "$seed_base" in
  *'EVIDGATE_REVIEW_TAG'*) ;;
  *) echo "check-pins: the seed BASE '$seed_base' is not tagged with the release tag" >&2; fail=1 ;;
esac

review_step 'node pin matches .nvmrc'
if [ -f "$NVMRC" ]; then
  wanted="$(tr -d '[:space:]' <"$NVMRC")"
  tag="$(grep -E '^FROM .*node:[0-9]+' "$DOCKERFILE_WEB" | head -1 | grep -oE 'node:[0-9]+' | cut -d: -f2)"
  if [ "$tag" != "$wanted" ]; then
    echo "check-pins: Dockerfile.web builds on node $tag but .nvmrc pins $wanted" >&2
    fail=1
  fi
else
  echo "check-pins: $NVMRC is missing; cannot compare the node pin" >&2
  fail=1
fi

if [ "$fail" -ne 0 ]; then
  review_die 'check-pins: FAILED'
fi
review_step 'ok: pins are immutable, the seed image derives from the migrator, digests match the dev stack'
