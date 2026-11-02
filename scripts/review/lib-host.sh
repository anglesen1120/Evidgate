#!/usr/bin/env bash
# Host facts and the --build-local path. Sourced by lib.sh; never executed.
#
# A 4 GiB review host cannot build three .NET images and run them at the same
# time, so --build-local builds on the deploying machine for the host's platform
# and ships the images as a compressed `docker save` stream: the host only loads
# them and never builds or pulls. Without the flag the host builds, which needs
# real headroom, hence the swap check.

RV_MIN_SWAP_KB=$((1024 * 1024))
# web-local and api-debug reuse the web and api images, so these five tags cover
# every image compose runs; saving more than that only costs transfer time. The
# three `*-base` images are build scaffolding and stay on whichever machine built
# them: the host never builds, so it never needs them.
RV_BUILT_SERVICES="migrator api worker web seed"

# Build order, and the reason it is an order: compose cannot sequence one
# service's build after another's, so a base is built first and the image derived
# from it second. migrator-base carries the migrator tag too (one Dockerfile, one
# cache, two names) because the seed derives from the base while the stack runs
# the plain tag.
RV_BUILD_ORDER="migrator-base api-base worker-base api worker seed web"
RV_BUILD_BASES_ARGS=(--profile images build migrator-base migrator api-base worker-base)
RV_BUILD_DERIVED_ARGS=(build api worker seed web)

# The Dockerfile a build name is made from, and the build-only base it derives
# from (empty for a base itself, for the migrator and for web).
rv_build_dockerfile() { # NAME
  case "$1" in
    web) printf 'infra/review/Dockerfile.web' ;;
    api | worker | seed) printf 'infra/review/Dockerfile.contracts' ;;
    *-base) printf 'infra/docker/Dockerfile.%s' "${1%-base}" ;;
    *) printf 'infra/docker/Dockerfile.%s' "$1" ;;
  esac
}

rv_build_base_arg() { # NAME
  case "$1" in
    api) printf 'evidgate-review-api-base' ;;
    worker) printf 'evidgate-review-worker-base' ;;
    seed) printf 'evidgate-review-migrator-base' ;;
    *) printf '' ;;
  esac
}

# The platform comes from the host, not from this machine: a laptop on arm64
# can deploy to an amd64 VM. rv_preflight sets RV_HOST_ARCH from the same answer.

# The host build, in the only order that works: the build-only bases first (the
# `images` profile, so `up` can never start one), then the images derived from
# them. The migrator is in the first pass with its base: same Dockerfile, and
# BuildKit serves the second tag from cache.
rv_build_host_images() {
  rv_compose "${RV_BUILD_BASES_ARGS[@]}"
  rv_compose "${RV_BUILD_DERIVED_ARGS[@]}"
}

rv_report_memory() {
  local mem_kb swap_kb
  # shellcheck disable=SC2016 # $2 must reach the host's awk, not this shell
  mem_kb="$(rv_run "awk '/^MemTotal:/ {print \$2}' /proc/meminfo 2>/dev/null || true")"
  # shellcheck disable=SC2016 # same: the field is awk's, not a shell variable
  swap_kb="$(rv_run "awk '/^SwapTotal:/ {print \$2}' /proc/meminfo 2>/dev/null || true")"
  case "$mem_kb" in
    '' | *[!0-9]*) rv_warn "cannot read memory from $(rv_target); skipping the swap check"; return 0 ;;
  esac
  case "$swap_kb" in
    '' | *[!0-9]*) swap_kb=0 ;;
  esac
  rv_info "memory on $(rv_target): $((mem_kb / 1024)) MiB RAM, $((swap_kb / 1024)) MiB swap"
  [ "$swap_kb" -ge "$RV_MIN_SWAP_KB" ] && return 0
  if [ "$RV_SETUP_SWAP" = 1 ]; then
    rv_setup_swap
    return 0
  fi
  if [ "$RV_BUILD_LOCAL" = 1 ]; then
    rv_info "swap is under 1 GiB, which is fine for --build-local: the host only loads images"
    return 0
  fi
  rv_warn "swap is under 1 GiB on a host that builds images; two builds in a row can take a 4 GiB machine out of memory"
  if [ "$RV_MODE" = local ]; then
    rv_warn "deploy with --build-local (build here, ship the images) instead"
  else
    rv_warn "either deploy with --build-local, or add a 2 GiB swap file; --setup-swap does this:"
    printf '    %s\n' 'sudo fallocate -l 2G /swapfile || sudo dd if=/dev/zero of=/swapfile bs=1M count=2048'
    printf '    %s\n' 'sudo chmod 600 /swapfile && sudo mkswap /swapfile && sudo swapon /swapfile'
    printf '    %s\n' "echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab"
  fi
}

# Idempotent: an existing /swapfile is reused, an active one is not re-enabled
# and fstab keeps a single line, so a second --setup-swap changes nothing.
rv_setup_swap() {
  rv_info "creating a 2 GiB swap file on $(rv_target) (this needs passwordless sudo)"
  rv_mutate "set -eu; if [ ! -f /swapfile ]; then sudo fallocate -l 2G /swapfile || sudo dd if=/dev/zero of=/swapfile bs=1M count=2048; sudo chmod 600 /swapfile; sudo mkswap /swapfile >/dev/null; fi; if ! swapon --show=NAME --noheadings 2>/dev/null | grep -qx /swapfile; then sudo swapon /swapfile; fi; if ! grep -q '^/swapfile ' /etc/fstab; then echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab >/dev/null; fi"
  rv_run "grep '^SwapTotal:' /proc/meminfo" || true
}

# The build runs from an extraction of the sha, never the working tree: the same
# bytes that `git archive` ships to the host are what the image is built from.
rv_build_local_images() {
  local tmp svc dockerfile base_arg failed=0
  tmp="$(mktemp -d "${TMPDIR:-/tmp}/evidgate-review-build.XXXXXX")" \
    || rv_die "cannot create a temporary build directory"
  RV_SCRATCH+=("$tmp")
  rv_info "building $RV_BUILT_SERVICES on this machine for linux/$RV_HOST_ARCH from $RV_RELEASE_SHA"
  git -C "$RV_REPO_ROOT" archive --format=tar "$RV_RELEASE_SHA" \
    | tar -xf - -C "$tmp" \
    || rv_die "cannot extract $RV_RELEASE_SHA for the local build"

  docker buildx version >/dev/null 2>&1 \
    || rv_die "docker buildx is not available on this machine, and --build-local needs it for the host platform"
  if [ "$(docker version --format '{{.Server.Os}}/{{.Server.Arch}}' 2>/dev/null || true)" != "linux/$RV_HOST_ARCH" ]; then
    rv_info "this machine is not linux/$RV_HOST_ARCH: buildx needs qemu emulation for the target platform"
  fi

  # The host does not run this under --build-local, so the release's static gate
  # runs here, on the same extracted sha the images are built from.
  ( cd "$tmp" && bash infra/review/check.sh --static-only ) \
    || rv_die "--static-only failed on the extracted $RV_RELEASE_SHA; nothing was built"

  # Bases first, then the images derived from them, in the same order the host
  # would build them.
  for svc in $RV_BUILD_ORDER; do
    dockerfile="$(rv_build_dockerfile "$svc")"
    base="$(rv_build_base_arg "$svc")"
    tags="-t evidgate-review-$svc:$RV_IMAGE_TAG"
    base_arg=""
    [ -z "$base" ] || base_arg="--build-arg BASE=$base:$RV_IMAGE_TAG"
    # The migrator's build-only base is the migrator image: tag both from one
    # build instead of compiling the same Dockerfile twice.
    [ "$svc" != migrator-base ] || tags="$tags -t evidgate-review-migrator:$RV_IMAGE_TAG"
    # Unquoted $base_arg and $tags: this is a plain docker build, and both values
    # are arguments this script controls.
    # shellcheck disable=SC2086
    if ! ( cd "$tmp" && docker buildx build \
      --platform "linux/$RV_HOST_ARCH" --load \
      --build-arg "EVIDGATE_REVIEW_PUBLIC_ORIGIN=$RV_PUBLIC_ORIGIN" \
      $base_arg $tags \
      -f "$dockerfile" . ); then
      failed=1
      rv_warn "building evidgate-review-$svc failed"
    fi
  done
  [ "$failed" = 0 ] \
    || rv_die "a local image build failed; if the failure mentions the platform, this machine cannot build linux/$RV_HOST_ARCH (buildx/qemu), so deploy from a machine on that platform instead"
}

# One stream, no intermediate tarball on the host: gzip keeps a .NET image set
# transferable over a laptop uplink.
rv_ship_images() {
  local svc images=""
  for svc in $RV_BUILT_SERVICES; do
    images="$images evidgate-review-$svc:$RV_IMAGE_TAG"
  done
  rv_info "shipping images$images to $(rv_target)"
  # shellcheck disable=SC2086 # the list is built from fixed names and a sha
  docker save $images \
    | gzip -c \
    | rv_mutate_stdin "set -eu; gunzip -c | docker load" \
    || rv_die "shipping the images failed"
  # Proof the host now holds them: a load that silently did nothing would fail
  # later, at `up`, with a much worse message.
  for svc in $RV_BUILT_SERVICES; do
    rv_run "docker image inspect evidgate-review-$svc:$RV_IMAGE_TAG >/dev/null 2>&1" \
      || rv_die "evidgate-review-$svc:$RV_IMAGE_TAG is missing on $(rv_target) after the load"
  done
}
