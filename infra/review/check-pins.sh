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
fail=0

[ -f "$REVIEW_COMPOSE" ] || review_die "missing $REVIEW_COMPOSE"
[ -f "$DOCKERFILE_WEB" ] || review_die "missing $DOCKERFILE_WEB"

review_step 'compose image references'
while IFS= read -r ref; do
  case "$ref" in
    evidgate-review-*)
      case "$ref" in
        *"\${EVIDGATE_REVIEW_SHA"*) ;;
        *) echo "check-pins: built image '$ref' is not tagged with the release sha" >&2; fail=1 ;;
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
done < <(sed -nE 's/^[[:space:]]+image:[[:space:]]*([^[:space:]]+)[[:space:]]*$/\1/p' "$REVIEW_COMPOSE")

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
review_step 'ok: pins are immutable and share the dev stack digests'
