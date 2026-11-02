#!/usr/bin/env bash
# A 30 GB host holds one image set per release at most. After the new release
# serves, every `evidgate-review-*` tag except the current and the previous one
# is removed, and the `*-base` build scaffolding goes in every case: the host
# never builds when images are shipped.
set -euo pipefail

# shellcheck disable=SC1091 # the fixture path depends on the caller
. "$(dirname "$0")/lib-fixture.sh"
fx_setup prune-images

cat >"$SANDBOX/images" <<'IMAGES'
evidgate-review-api:aaaaaaaaaaaa
evidgate-review-web:aaaaaaaaaaaa
evidgate-review-api:bbbbbbbbbbbb
evidgate-review-web:bbbbbbbbbbbb
evidgate-review-api:cccccccccccc
evidgate-review-migrator-base:cccccccccccc
evidgate-review-api-base:cccccccccccc
IMAGES
export FX_IMAGES="$SANDBOX/images"

(
  # shellcheck source=/dev/null
  . "$FX_REPO/scripts/review/lib.sh"
  # shellcheck disable=SC2034 # read by the ssh stub and rv_run in the sourced lib
  RV_SSH_HOST="review-host"
  # shellcheck disable=SC2034 # read by rv_prune_images in the sourced lib
  RV_IMAGE_TAG="aaaaaaaaaaaa"
  rv_prune_images "bbbbbbbbbbbb"
)

fx_assert_log_has "$DOCKER_LOG" "rmi evidgate-review-api:cccccccccccc" "a tag older than the previous release is removed"
fx_assert_log_has "$DOCKER_LOG" "rmi evidgate-review-migrator-base:cccccccccccc" "the build-only base image is removed"
fx_assert_log_has "$DOCKER_LOG" "rmi evidgate-review-api-base:cccccccccccc" "the api base image is removed"
fx_assert_log_lacks "$DOCKER_LOG" "rmi evidgate-review-api:aaaaaaaaaaaa" "the serving tag is kept"
fx_assert_log_lacks "$DOCKER_LOG" "rmi evidgate-review-web:aaaaaaaaaaaa" "the serving web tag is kept"
fx_assert_log_lacks "$DOCKER_LOG" "rmi evidgate-review-api:bbbbbbbbbbbb" "the rollback tag is kept"
fx_assert_log_lacks "$DOCKER_LOG" "rmi evidgate-review-web:bbbbbbbbbbbb" "the rollback web tag is kept"
