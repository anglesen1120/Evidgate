#!/usr/bin/env bash
# Every third-party image this stack pulls must be digest-pinned, the two shared
# with the dev stack must be the very same pin, and every image this stack builds
# must be tagged with the release sha. The images it derives (contracts/ on top of
# an infra/docker image) must name their build-only base, and those bases must be
# the raw Dockerfiles in the `images` profile and nothing else.
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
DERIVED_DOCKERFILE="$REVIEW_DIR/Dockerfile.contracts"
fail=0

[ -f "$REVIEW_COMPOSE" ] || review_die "missing $REVIEW_COMPOSE"
[ -f "$DOCKERFILE_WEB" ] || review_die "missing $DOCKERFILE_WEB"
[ -f "$DERIVED_DOCKERFILE" ] || review_die "missing $DERIVED_DOCKERFILE"

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

review_step 'derived images: contracts/ on top of an infra/docker image'
DERIVED_DOCKERFILE="$REVIEW_DIR/Dockerfile.contracts"
DERIVED_IGNORE="$DERIVED_DOCKERFILE.dockerignore"
[ -f "$DERIVED_DOCKERFILE" ] || review_die "missing $DERIVED_DOCKERFILE"
[ -f "$DERIVED_IGNORE" ] || review_die "missing $DERIVED_IGNORE"

froms="$(grep -cE '^FROM ' "$DERIVED_DOCKERFILE" || true)"
[ "$froms" = 1 ] || {
  echo "check-pins: Dockerfile.contracts must have exactly one FROM line (found $froms)" >&2
  fail=1
}
derived_base="$(sed -nE 's/^FROM[[:space:]]+(.*[^[:space:]])[[:space:]]*$/\1/p' "$DERIVED_DOCKERFILE")"
# shellcheck disable=SC2016 # the Dockerfile's own ${BASE}, not a shell value
[ "$derived_base" = '${BASE}' ] || {
  echo "check-pins: Dockerfile.contracts must take its base as ARG BASE (found '$derived_base'): it is a local derivative, not a registry image" >&2
  fail=1
}
grep -qE '^ARG BASE[[:space:]]*$' "$DERIVED_DOCKERFILE" || {
  echo "check-pins: Dockerfile.contracts declares no 'ARG BASE'" >&2
  fail=1
}
# The path is contract, not style: the runtime resolves contracts/schemas and
# contracts/adapter above the binary (/app/contracts, then /contracts) and answers
# 500 on a manifest submission when neither is there.
grep -qF 'COPY --chmod=a=rX contracts/ /app/contracts/' "$DERIVED_DOCKERFILE" || {
  echo "check-pins: Dockerfile.contracts must COPY contracts/ to /app/contracts/ (found no such line)" >&2
  fail=1
}
grep -qE '^!contracts' "$DERIVED_IGNORE" || {
  echo "check-pins: Dockerfile.contracts.dockerignore does not keep contracts/ in the build context" >&2
  fail=1
}

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
review_split_services "$work"

# service:base pairs. api-debug runs the very same image as api, so it repeats the
# derivation rather than inventing a second one.
for pair in api:api worker:worker seed:migrator api-debug:api; do
  svc="${pair%%:*}"
  want="${pair##*:}"
  block="$work/$svc"
  if [ ! -f "$block" ]; then
    echo "check-pins: '$svc' is missing from compose.review.yml" >&2
    fail=1
    continue
  fi
  grep -qE '^      dockerfile: infra/review/Dockerfile\.contracts$' "$block" || {
    echo "check-pins: '$svc' does not build from infra/review/Dockerfile.contracts" >&2
    fail=1
  }
  grep -qE "^        BASE: evidgate-review-${want}-base:\\\$\{EVIDGATE_REVIEW_TAG" "$block" || {
    echo "check-pins: '$svc' does not derive from evidgate-review-${want}-base:<release tag>" >&2
    fail=1
  }
done

review_step 'build-only base images come from infra/docker and carry the release tag'
for raw in migrator api worker; do
  block="$work/$raw-base"
  dockerfile="$REVIEW_DIR/../docker/Dockerfile.$raw"
  [ -f "$dockerfile" ] || {
    echo "check-pins: $dockerfile is missing; '$raw-base' cannot be built from it" >&2
    fail=1
  }
  if [ ! -f "$block" ]; then
    echo "check-pins: '$raw-base' is missing from compose.review.yml" >&2
    fail=1
    continue
  fi
  grep -qE "^    image: evidgate-review-${raw}-base:\\\$\{EVIDGATE_REVIEW_TAG" "$block" || {
    echo "check-pins: '$raw-base' is not tagged with the release tag" >&2
    fail=1
  }
  grep -qE "^      dockerfile: infra/docker/Dockerfile\.${raw}$" "$block" || {
    echo "check-pins: '$raw-base' does not build from infra/docker/Dockerfile.${raw}" >&2
    fail=1
  }
  grep -qE "^    profiles: \['images'\]$" "$block" || {
    echo "check-pins: '$raw-base' is build-only, so it must stay in the 'images' profile (found none)" >&2
    fail=1
  }
done

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
review_step 'ok: pins are immutable, derived images carry contracts/, digests match the dev stack'
