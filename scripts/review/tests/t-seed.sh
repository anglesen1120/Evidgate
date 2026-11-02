#!/usr/bin/env bash
# The api, worker and seed images need the repository's contracts/ beside the
# binary: the infra/docker images compile it in the build stage and ship only the
# published output, while the runtime resolves contracts/schemas and
# contracts/adapter from /app/contracts (a manifest submission answers 500
# without them). Each of the three is therefore an infra/docker image plus one
# COPY, taken as ARG BASE, and each base must be built first.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup seed

assert_file_has() { # FILE NEEDLE LABEL
  if grep -qF -- "$2" "$1"; then fx_ok "$3"; else fx_fail "$3: '$2' is missing from ${1##*/}"; fi
}

fx_assert_log_lacks "$SSH_LOG" "seed-dev" "nothing runs before the deploy"

# --- the derived image itself (the fixture repo carries no images) -----------
real_repo="$(cd "$FX_SCRIPTS/../.." && pwd)"
dockerfile="$real_repo/infra/review/Dockerfile.contracts"
ignore="$real_repo/infra/review/Dockerfile.contracts.dockerignore"
fx_assert_file "$dockerfile"
fx_assert_file "$ignore"

fx_assert_eq "$(grep -cE '^FROM ' "$dockerfile")" "1" "the contracts image is one stage"
assert_file_has "$dockerfile" "ARG BASE" "the base image is an argument"
# shellcheck disable=SC2016 # ${BASE} is Dockerfile syntax, not a shell variable
assert_file_has "$dockerfile" 'FROM ${BASE}' "the contracts image derives from that base"
assert_file_has "$dockerfile" "COPY --chmod=a=rX contracts/ /app/contracts/" \
  "contracts/ lands where the runtime resolves it"
# The runtime user of every base is non-root, so the COPY has to stay readable by
# it: a=rX is what makes the directory 0555 and the files 0444.
assert_file_has "$ignore" '!contracts' "the build context keeps only contracts/"

# --- compose wires three derivations and three bases ------------------------
compose="$real_repo/infra/review/compose.review.yml"
if grep -qF 'Dockerfile.seed' "$compose"; then
  fx_fail "compose still references Dockerfile.seed"
fi
fx_ok "no service builds from the removed seed Dockerfile"

for svc in api worker seed; do
  assert_file_has "$compose" "image: evidgate-review-$svc:" "$svc is tagged with the release"
done
for pair in api:api worker:worker seed:migrator; do
  svc="${pair%%:*}"
  base="${pair##*:}"
  assert_file_has "$compose" "BASE: evidgate-review-$base-base:" "$svc derives from the $base base"
done
assert_file_has "$compose" "dockerfile: infra/review/Dockerfile.contracts" \
  "the derived services build from the contracts Dockerfile"
for raw in migrator api worker; do
  assert_file_has "$compose" "image: evidgate-review-$raw-base:" "the $raw base has its own tag"
  assert_file_has "$compose" "dockerfile: infra/docker/Dockerfile.$raw" "$raw base is the raw image"
done
fx_assert_eq "$(grep -cF "profiles: ['images']" "$compose")" "3" \
  "the three bases are only built, never started"

# --- the plan says the same thing as the run --------------------------------
if ! plan="$(bash "$FX_REPO/scripts/review/deploy.sh" review-host --dry-run 2>&1)"; then
  fx_fail "the dry run failed: $plan"
fi
# The plan prints compose argv quoted, so the service and its arguments are the
# needles, not the whole phrase.
fx_assert_contains "$plan" "'seed' 'seed-dev' '--with-token'" "the plan seeds through the seed service"
fx_assert_not_contains "$plan" "'migrator' 'seed-dev'" "the plan no longer seeds through the migrator"
fx_assert_contains "$plan" "'migrator-base' 'migrator' 'api-base' 'worker-base'" "the plan builds the bases first"

# --- the real (stubbed) run: two build passes, then the seed call -----------
if ! out="$(bash "$FX_REPO/scripts/review/deploy.sh" review-host --no-dogfood 2>&1)"; then
  fx_fail "the deploy failed: $out"
fi
fx_assert_log_has "$DOCKER_LOG" "--profile images build migrator-base migrator api-base worker-base" \
  "the build-only bases are built first"
fx_assert_log_has "$DOCKER_LOG" "build api worker seed web" "the derived images and web follow"
fx_assert_log_has "$DOCKER_LOG" "run --rm -T seed seed-dev --with-token" "seed-dev runs on the seed service"
fx_assert_log_lacks "$DOCKER_LOG" "run --rm -T migrator seed-dev" "seed-dev never runs on the migrator image"

bases_line="$(grep -nF "build migrator-base migrator api-base worker-base" "$DOCKER_LOG" | head -1 | cut -d: -f1 || true)"
derived_line="$(grep -nF "build api worker seed web" "$DOCKER_LOG" | head -1 | cut -d: -f1 || true)"
[ -n "$bases_line" ] && [ -n "$derived_line" ] || fx_fail "the build order is not recorded"
[ "$bases_line" -lt "$derived_line" ] \
  || fx_fail "a derived image was built before its base (line $derived_line < $bases_line)"
fx_ok "the derived images are built after their bases"

# The remote command is one shell string, so compose's arguments appear quoted.
fx_assert_log_has "$SSH_LOG" "'seed' 'seed-dev' '--with-token'" "the seed call reaches the host"
fx_ok "the seed job runs from the derived image"
