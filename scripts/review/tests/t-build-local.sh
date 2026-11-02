#!/usr/bin/env bash
# --build-local exists for a host too small to build: the images are built on
# this machine for the host's own platform, from the exact sha, shipped as one
# compressed stream and only loaded there. The host must never build or pull.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup buildlocal

tag="${FX_SECOND_SHA:0:12}"
deploy="$FX_REPO/scripts/review/deploy.sh"

if ! out="$(bash "$deploy" review-host --build-local --no-dogfood 2>&1)"; then
  fx_fail "the build-local deploy failed: $out"
fi
fx_assert_log_has "$DOCKER_LOG" "buildx build --platform linux/arm64" "the images are built for the host platform"
for svc in migrator api worker web seed; do
  fx_assert_log_has "$DOCKER_LOG" "-t evidgate-review-$svc:$tag" "$svc is tagged with the short sha"
done
fx_assert_log_has "$DOCKER_LOG" "save evidgate-review-migrator:$tag" "the images are saved on this machine"
fx_assert_log_has "$DOCKER_LOG" " evidgate-review-seed:$tag" "the seed image is shipped too"
fx_assert_log_has "$DOCKER_LOG" "--build-arg BASE=evidgate-review-migrator:$tag" "the seed image is built on the migrator image"
fx_assert_log_has "$SSH_LOG" "docker load" "the images are loaded on the host"
fx_assert_log_has "$DOCKER_LOG" "EVIDGATE_REVIEW_TAG=$tag" "compose resolves the shipped tag"
# The gate asks compose for the web image, and the env file still holds the first
# deploy's full sha: only the passed tag keeps it inspecting what was loaded.
fx_assert_log_has "$GATE_LOG" "check-web-image.sh TAG=$tag" "the image gate inspects the loaded short tag"

# `compose ... build` would mean the host built something, which is the whole
# thing this flag exists to avoid.
if grep -F -- "-f" "$DOCKER_LOG" | grep -q " build$"; then
  fx_fail "the host built images under --build-local: $(grep -F -- '-f' "$DOCKER_LOG" | grep ' build$')"
fi
fx_ok "the host never built an image"

fx_assert_file "$HOME/evidgate-review/releases/$FX_SECOND_SHA/.release-sha"
fx_assert_eq "$(cat "$HOME/evidgate-review/releases/$FX_SECOND_SHA/.release-sha")" "$FX_SECOND_SHA" "the release tree still names the full sha"
