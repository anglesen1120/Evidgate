#!/usr/bin/env bash
# The seed job needs contracts/schemas beside /app/migrations, which the migrator
# image does not ship, so it runs from a derived image: Dockerfile.seed takes the
# already-built migrator image as ARG BASE. deploy.sh must therefore build that
# image after the migrator, ship it with --build-local, and run seed-dev on it.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup seed

assert_file_has() { # FILE NEEDLE LABEL
  if grep -qF -- "$2" "$1"; then fx_ok "$3"; else fx_fail "$3: '$2' is missing from ${1##*/}"; fi
}

fx_assert_log_lacks "$SSH_LOG" "seed-dev" "nothing runs before the deploy"

# --- the files themselves (the fixture repo carries no images) ---------------
real_repo="$(cd "$FX_SCRIPTS/../.." && pwd)"
dockerfile="$real_repo/infra/review/Dockerfile.seed"
fx_assert_file "$dockerfile"
fx_assert_file "$real_repo/infra/review/Dockerfile.seed.dockerignore"

fx_assert_eq "$(grep -cE '^FROM ' "$dockerfile")" "1" "the seed image is one stage"
assert_file_has "$dockerfile" "ARG BASE" "the base image is an argument"
# shellcheck disable=SC2016 # ${BASE} is Dockerfile syntax, not a shell variable
assert_file_has "$dockerfile" 'FROM ${BASE}' "the seed image derives from that base"
assert_file_has "$dockerfile" "COPY --chmod=a=rX contracts/ /app/contracts/" \
  "contracts/schemas lands where DevSeedRun looks for it"
# The runtime user of the base image is non-root, so the COPY has to stay
# readable by it: a=rX is what makes the directory 0555 and the files 0444.
assert_file_has "$real_repo/infra/review/Dockerfile.seed.dockerignore" '!contracts' \
  "the build context keeps only contracts/"

assert_file_has "$real_repo/infra/review/compose.review.yml" 'dockerfile: infra/review/Dockerfile.seed' \
  "compose builds the seed service from that Dockerfile"
assert_file_has "$real_repo/infra/review/compose.review.yml" 'BASE: evidgate-review-migrator:' \
  "the seed build names the migrator image as its base"
assert_file_has "$real_repo/infra/review/compose.review.yml" 'image: evidgate-review-seed:' \
  "the seed image is tagged with the release"

# --- the plan says the same thing as the run --------------------------------
if ! plan="$(bash "$FX_REPO/scripts/review/deploy.sh" review-host --dry-run 2>&1)"; then
  fx_fail "the dry run failed: $plan"
fi
# The plan prints compose argv quoted, so the service and its arguments are the
# needles, not the whole phrase.
fx_assert_contains "$plan" "'seed' 'seed-dev' '--with-token'" "the plan seeds through the seed service"
fx_assert_not_contains "$plan" "'migrator' 'seed-dev'" "the plan no longer seeds through the migrator"

# --- the real (stubbed) run: compose build order and the seed call ----------
if ! out="$(bash "$FX_REPO/scripts/review/deploy.sh" review-host --no-dogfood 2>&1)"; then
  fx_fail "the deploy failed: $out"
fi
fx_assert_log_has "$DOCKER_LOG" "build migrator api worker web" "the migrator is built in the first pass"
fx_assert_log_has "$DOCKER_LOG" "build seed" "the seed image is built separately"
fx_assert_log_has "$DOCKER_LOG" "run --rm -T seed seed-dev --with-token" "seed-dev runs on the seed service"
fx_assert_log_lacks "$DOCKER_LOG" "run --rm -T migrator seed-dev" "seed-dev never runs on the migrator image"

migrator_line="$(grep -nF "build migrator api worker web" "$DOCKER_LOG" | head -1 | cut -d: -f1)"
seed_line="$(grep -nF "build seed" "$DOCKER_LOG" | head -1 | cut -d: -f1)"
[ -n "$migrator_line" ] && [ -n "$seed_line" ] || fx_fail "the build order is not recorded"
[ "$migrator_line" -lt "$seed_line" ] \
  || fx_fail "the seed image was built before its base (line $seed_line < $migrator_line)"
fx_ok "the seed image is built after the migrator image"

# The remote command is one shell string, so compose's arguments appear quoted.
fx_assert_log_has "$SSH_LOG" "'seed' 'seed-dev' '--with-token'" "the seed call reaches the host"
fx_ok "the seed job runs from the derived image"
