#!/usr/bin/env bash
# 10.2.1-T1.1 + T1.2 (AC1, AC5): build the three Evidgate images from
# infra/docker/Dockerfile.{api,worker,migrator} and check what the acceptance
# criteria ask a reviewer to be able to see.
#
# Docker-gated and slow (the first run pulls the pinned SDK and runtime base and
# cross-publishes ReadyToRun assemblies), so it is not part of scripts/verify.sh
# by default: wire it as an optional stage or a separate CI job. Every check
# below fails the run with a non-zero exit.
#
# BuildKit is required (FROM --platform=$BUILDPLATFORM and the automatic
# TARGETARCH argument). A docker-container builder builds the other architecture
# on the host architecture, without emulating the SDK:
#   docker buildx create --name evidgate --driver docker-container --use
#
# Checks per image:
#   AC1  the built image's first layers ARE the pinned chiseled aspnet layers, so
#        the runtime base is the digest-pinned image and not something else (the
#        Api pins the `-extra` variant, Worker/Migrator the plain one);
#   AC1  `docker inspect .Config.User` is a non-root uid (the chiseled base's 1654);
#   P7   the published runtimeconfig.json carries Server GC plus the per-image
#        globalization setting (invariant for Worker, NOT invariant for the Api,
#        which needs ICU for NFC — 13.1.1-T2, D-S55-Q1), and (Api/Worker) the main
#        assembly has a ReadyToRun header;
#   06.3.6 the Worker image carries its parse child as a second published process
#        (/app/parser/Evidgate.Parser.dll with its own runtimeconfig.json and
#        deps.json, globalization-invariant like the Worker it serves), and
#        `dotnet /app/parser/Evidgate.Parser.dll` with no arguments answers the
#        child's usage contract — the fixed usage line on stderr, exit 2 — as the
#        image's own non-root user, so a Worker image that ships no runnable child
#        fails here and not at the first parse in production;
#   T7   every image's /app tree is root-owned (uid 0) with no group/other write
#        bit and 0755 on each directory the runtime launches from: /app, plus
#        /app/parser for the Worker and /app/migrations for the Migrator (which
#        must carry at least one *.sql), so the non-root runtime can read and
#        start its files but cannot unlink or replace an assembly or a script;
#        ownership is read from the exported image filesystem because the
#        chiseled base has no shell;
#   AC5  the Migrator image is its own image on the same pins and
#        `docker run <image> --help` still prints the CLI contract;
#   AC1/AC5 with --with-db: a throwaway PostgreSQL 18 (the pinned compose.dev.yml
#        image) is brought to head with the Migrator image, then `--status` is run
#        against it and the Api answers /internal/health/live + /ready.
#
# The checks themselves live in scripts/image-checks.sh (per image) and
# scripts/image-db-checks.sh (--with-db); this file is the CLI, the pin audit and
# the build/run driver.
#
# usage: scripts/build-images.sh [options]
#   --platform <linux/amd64|linux/arm64>  target platform (repeatable; default: host)
#   --image <api|worker|migrator>         restrict the run (repeatable; default: all)
#   --tag <tag>                           image tag (default: dev)
#   --no-cache                            pass --no-cache to the build
#   --check-pins                          no build: audit the pins, print them, exit
#   --check-registry                      with --check-pins: resolve the tags at
#                                         mcr.microsoft.com and compare (network)
#   --with-db                             add the database-backed checks
#   --keep                                keep the containers started by --with-db
#   --offline                             skip the registry lookups (pin freshness and
#                                         the runtime-base layer proof) and say so
#   -h, --help
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

PLATFORMS=""
IMAGES=""
TAG=dev
NO_CACHE=0
CHECK_PINS=0
CHECK_REGISTRY=0
WITH_DB=0
KEEP=0
OFFLINE=0

usage() {
  cat <<'EOF'
usage: scripts/build-images.sh [options]

  --platform <linux/amd64|linux/arm64>  target platform (repeatable; default: host)
  --image <api|worker|migrator>         restrict the run (repeatable; default: all)
  --tag <tag>                           image tag (default: dev)
  --no-cache                            pass --no-cache to the build
  --check-pins                          no build: audit the pins, print them, exit
  --check-registry                      with --check-pins: resolve the tags at
                                        mcr.microsoft.com and compare (network)
  --with-db                             add the database-backed checks (needs the
                                        migrator and api images in the same run)
  --keep                                keep the containers started by --with-db
  --offline                             skip the registry lookups (pin freshness and
                                        the runtime-base layer proof) and say so
  -h, --help                            this text
EOF
}

die() { echo "build-images: $*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --platform) PLATFORMS="${PLATFORMS}${PLATFORMS:+ }${2:?--platform needs a value}"; shift 2 ;;
    --image)    IMAGES="${IMAGES}${IMAGES:+ }${2:?--image needs a value}"; shift 2 ;;
    --tag)      TAG="${2:?--tag needs a value}"; shift 2 ;;
    --no-cache) NO_CACHE=1; shift ;;
    --check-pins) CHECK_PINS=1; shift ;;
    --check-registry) CHECK_REGISTRY=1; shift ;;
    --with-db) WITH_DB=1; shift ;;
    --keep) KEEP=1; shift ;;
    --offline) OFFLINE=1; shift ;;
    -h | --help) usage; exit 0 ;;
    *) die "unknown argument '$1' (try --help)" ;;
  esac
done

# --- image identity -----------------------------------------------------------

prefix() {
  case "$1" in
    api | worker | migrator) echo "$1" ;;
    *) die "unknown image '$1' (api, worker, migrator)" ;;
  esac
}

project_of() {
  case "$(prefix "$1")" in
    api) echo src/Evidgate.Api/Evidgate.Api.csproj ;;
    worker) echo src/Evidgate.Worker/Evidgate.Worker.csproj ;;
    migrator) echo src/Evidgate.Migrator/Evidgate.Migrator.csproj ;;
  esac
}

assembly_of() {
  case "$(prefix "$1")" in
    api) echo Evidgate.Api.dll ;;
    worker) echo Evidgate.Worker.dll ;;
    migrator) echo Evidgate.Migrator.dll ;;
  esac
}

# 06.3.6-T1.3: the Worker image publishes two entry points. The parse child
# (06.3.6 §5) is the second one, in its own /app/parser directory — the two
# publish outputs carry the same dependency assemblies, so a shared directory
# would let one publish overwrite the other's runtimeconfig.json/deps.json.
# No other image ships a child.
child_project_of() {
  case "$(prefix "$1")" in
    worker) echo src/Evidgate.Parser/Evidgate.Parser.csproj ;;
    *) echo "" ;;
  esac
}

child_assembly_of() {
  case "$(prefix "$1")" in
    worker) echo Evidgate.Parser.dll ;;
    *) echo "" ;;
  esac
}

child_directory_of() {
  case "$(prefix "$1")" in
    worker) echo parser ;;
    *) echo "" ;;
  esac
}

# 00 §5.7 P7 names the long-lived Api/Worker processes for ReadyToRun.
is_r2r() { case "$(prefix "$1")" in api | worker) echo 1 ;; *) echo 0 ;; esac; }

dockerfile_for() { echo "infra/docker/Dockerfile.$(prefix "$1")"; }
image_ref() { echo "evidgate-$1:${TAG}-${2##*/}"; }

host_platform() {
  case "$(uname -m)" in
    x86_64 | amd64) echo linux/amd64 ;;
    arm64 | aarch64) echo linux/arm64 ;;
    *) die "cannot map host architecture '$(uname -m)' to a container platform" ;;
  esac
}

[[ -n "$PLATFORMS" ]] || PLATFORMS="$(host_platform)"
if [[ -z "$IMAGES" ]]; then IMAGES="api worker migrator"; else
  for image in $IMAGES; do prefix "$image" >/dev/null; done
fi
for platform in $PLATFORMS; do
  case "$platform" in
    linux/amd64 | linux/arm64) ;;
    *) die "unsupported platform '$platform': 00 §11 supports linux/amd64 and linux/arm64" ;;
  esac
done

# --- pins ---------------------------------------------------------------------
# The Dockerfiles are the pin's single source; these readers only audit them.

builder_pin() { sed -n 's/^FROM --platform=\$BUILDPLATFORM \([^ ]*\) AS build$/\1/p' "$(dockerfile_for "$1")"; }
runtime_pin() { sed -n 's/^FROM \([^ ]*\) AS runtime$/\1/p' "$(dockerfile_for "$1")"; }
declared_sdk_version() { python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["sdk"]["version"])' global.json; }

# One builder and one context ignore list across all three Dockerfiles: drift here
# is how a pin silently stops applying to an image. 13.1.1-T2 (D-S55-Q1): the Api
# composes the authoring module, which stores NFC text and refuses to start without
# ICU, so its runtime base is the `-extra` chiseled variant while Worker/Migrator
# stay on the plain invariant-only base — and the two bases must remain one aspnet
# version (`<version>-noble-chiseled` plus its `-extra` sibling) or the images
# diverge.
check_pin_identity() {
  local fail=0 builder runtime worker_runtime migrator_runtime other_builder image
  local runtime_tag base_tag
  builder="$(builder_pin api)"
  runtime="$(runtime_pin api)"
  [[ -n "$builder" ]] || { echo "check-pins: no builder FROM in $(dockerfile_for api)" >&2; fail=1; }
  [[ -n "$runtime" ]] || { echo "check-pins: no runtime FROM in $(dockerfile_for api)" >&2; fail=1; }
  for image in api worker migrator; do
    other_builder="$(builder_pin "$image")"
    if [[ "$other_builder" != "$builder" ]]; then
      echo "check-pins: $(dockerfile_for "$image") builder '$other_builder' differs from $(dockerfile_for api) '$builder'" >&2
      fail=1
    fi
  done
  worker_runtime="$(runtime_pin worker)"
  migrator_runtime="$(runtime_pin migrator)"
  [[ -n "$worker_runtime" ]] || { echo "check-pins: no runtime FROM in $(dockerfile_for worker)" >&2; fail=1; }
  [[ -n "$migrator_runtime" ]] || { echo "check-pins: no runtime FROM in $(dockerfile_for migrator)" >&2; fail=1; }
  if [[ "$worker_runtime" != "$migrator_runtime" ]]; then
    echo "check-pins: $(dockerfile_for migrator) runtime '$migrator_runtime' differs from $(dockerfile_for worker) '$worker_runtime'" >&2
    fail=1
  fi
  runtime_tag="${worker_runtime%%@*}"
  runtime_tag="${runtime_tag##*:}"
  base_tag="${runtime%%@*}"
  base_tag="${base_tag##*:}"
  if [[ "$base_tag" != "${runtime_tag}-extra" ]]; then
    echo "check-pins: the api runtime '$runtime' is not the -extra chiseled variant of the" \
      "worker/migrator base '$worker_runtime': the Api must ship ICU to compose NFC text" \
      "(13.1.1-T2, D-S55-Q1)" >&2
    fail=1
  fi
  return "$fail"
}

# Context hygiene and pin strength: the ignore list must keep the git-ignored host
# state out of the build context (`.env.dev` is the one that matters — scripts/dev/up.sh
# writes real per-role passwords into it, 00 §5.7 S3 — and a regenerated list dropping
# it is invisible until a builder holds the credentials), the builder tag must equal
# the SDK CI installs, and no mutable tag may reach a build.
check_pin_sources() {
  local fail=0 sdk builder runtime worker_runtime migrator_runtime builder_tag pattern ref image
  sdk="$(declared_sdk_version)"
  builder="$(builder_pin api)"
  runtime="$(runtime_pin api)"
  worker_runtime="$(runtime_pin worker)"
  migrator_runtime="$(runtime_pin migrator)"
  for image in api worker migrator; do
    if ! cmp -s infra/docker/Dockerfile.api.dockerignore "infra/docker/Dockerfile.$image.dockerignore"; then
      echo "check-pins: infra/docker/Dockerfile.$image.dockerignore differs from Dockerfile.api.dockerignore" >&2
      fail=1
    fi
  done
  for pattern in '.env*' '.local' '**/playwright-report' '**/test-results' '**/*.tsbuildinfo'; do
    if ! grep -qxF "$pattern" infra/docker/Dockerfile.api.dockerignore; then
      echo "check-pins: infra/docker/Dockerfile.api.dockerignore no longer excludes '$pattern' from the build context" >&2
      fail=1
    fi
  done
  builder_tag="${builder%@*}"
  builder_tag="${builder_tag##*:}"
  if [[ "$builder_tag" != "$sdk" ]]; then
    echo "check-pins: builder tag '$builder_tag' is not global.json sdk.version '$sdk'" >&2
    fail=1
  fi
  for ref in "$builder" "$runtime" "$worker_runtime" "$migrator_runtime"; do
    case "$ref" in
      *@sha256:*) ;;
      *) echo "check-pins: '$ref' is not digest-pinned (@sha256:<64 hex>): a mutable tag must not reach a build or a deploy file" >&2; fail=1 ;;
    esac
  done
  return "$fail"
}

# The no-egress probe image (scripts/parser-no-egress.sh) is the non-chiseled sibling of the Api runtime (the probe
# needs bash) and no updater covers it, so it must be digest-pinned and stay on the Api runtime's aspnet version.
probe_pin() { sed -n 's/^IMAGE="\([^"]*\)"$/\1/p' scripts/parser-no-egress.sh; }
check_probe_pin() {
  local probe api_tag probe_tag
  probe="$(probe_pin)"
  api_tag="$(runtime_pin api)"; api_tag="${api_tag%@*}"; api_tag="${api_tag##*:}"
  probe_tag="${probe%@*}"; probe_tag="${probe_tag##*:}"
  case "$probe" in
    mcr.microsoft.com/dotnet/aspnet:*@sha256:*) ;;
    *) echo "check-pins: scripts/parser-no-egress.sh IMAGE '$probe' is not a digest-pinned aspnet image" >&2; return 1 ;;
  esac
  if [[ "${probe_tag%%-*}" != "${api_tag%%-*}" ]]; then
    echo "check-pins: scripts/parser-no-egress.sh aspnet version '${probe_tag%%-*}' differs from the api runtime '${api_tag%%-*}'" >&2
    return 1
  fi
}

check_pins() {
  local fail=0 sdk builder runtime worker_runtime
  check_pin_identity || fail=1
  check_pin_sources || fail=1
  check_probe_pin || fail=1
  sdk="$(declared_sdk_version)"
  builder="$(builder_pin api)"
  runtime="$(runtime_pin api)"
  worker_runtime="$(runtime_pin worker)"
  printf 'builder  %s\n' "$builder"
  printf 'runtime  %s (api: -extra, ships ICU + tzdata for the authoring NFC rule)\n' "$runtime"
  printf 'runtime  %s (worker/migrator: plain -chiseled, globalization-invariant)\n' "$worker_runtime"
  printf 'runtime  %s (parser no-egress probe: non-chiseled, bash)\n' "$(probe_pin)"
  printf 'sdk      %s (global.json: the same SDK CI installs, so the image and the test build compile with one toolchain)\n' "$sdk"
  return "$fail"
}

# --check-registry: resolve the pinned tags at mcr.microsoft.com and compare.
# A moved tag is informational (upstream published a patch under the same tag;
# the pin still points at what was reviewed), a tag that no longer resolves is fatal.
check_registry() {
  python3 - "$(builder_pin api)" "$(runtime_pin api)" "$(runtime_pin worker)" "$(probe_pin)" <<'PYEOF'
import sys, urllib.request

ACCEPT = ", ".join([
    "application/vnd.docker.distribution.manifest.list.v2+json",
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.v2+json",
])

def digest_of(reference):
    registry, _, rest = reference.partition("/")
    image, _, _pinned = rest.partition("@")
    repository, _, tag = image.partition(":")
    request = urllib.request.Request("https://%s/v2/%s/manifests/%s" % (registry, repository, tag))
    request.add_header("Accept", ACCEPT)
    with urllib.request.urlopen(request, timeout=60) as response:
        return response.headers.get("Docker-Content-Digest")

fail = 0
for reference in sys.argv[1:]:
    name, _, pinned = reference.partition("@")
    try:
        live = digest_of(reference)
    except Exception as error:
        print("check-registry: cannot resolve %s: %s" % (reference, error), file=sys.stderr)
        fail = 1
        continue
    if live == pinned:
        print("check-registry: %s still resolves to the pinned %s" % (name, pinned))
    else:
        print("check-registry: %s now resolves to %s; the build pins %s "
              "(upstream moved the tag: review before bumping)" % (name, live, pinned))
sys.exit(fail)
PYEOF
}

# --- docker helpers -----------------------------------------------------------

require_buildx() {
  docker buildx version >/dev/null 2>&1 \
    || die "docker buildx is required (BuildKit: FROM --platform=\$BUILDPLATFORM, automatic TARGETARCH)"
}

# Set by the build loop: the platform the helpers below must run against, so a
# cross-platform check does not print Docker's "platform does not match the host"
# warning (and so the log says which platform each check covered).
CHECK_PLATFORM=""

run_id="$$"
network="evidgate-image-check-$run_id"
api_container="evidgate-image-check-api-$run_id"
db_container="evidgate-image-check-db-$run_id"

cleanup() {
  local status=$?
  if [[ "$KEEP" != "1" ]]; then
    docker rm -f "$api_container" >/dev/null 2>&1 || true
    docker rm -f "$db_container" >/dev/null 2>&1 || true
    docker network rm "$network" >/dev/null 2>&1 || true
  fi
  exit "$status"
}
trap cleanup EXIT

image_size() {
  docker image inspect "$1" --format '{{.Size}}' \
    | python3 -c 'import sys; print("%.1f MiB" % (int(sys.stdin.read().strip()) / 1048576))'
}

# --- checks (sourced) ---------------------------------------------------------
# The per-image checks and the --with-db checks are separate responsibilities with
# their own file: see scripts/image-checks.sh and scripts/image-db-checks.sh.

. "$ROOT/scripts/image-checks.sh"
. "$ROOT/scripts/image-db-checks.sh"

# --- build --------------------------------------------------------------------

built_api=""
built_worker=""
built_migrator=""

build_image() {
  local name="$1" platform="$2" dockerfile reference started elapsed
  name="$(prefix "$name")"
  dockerfile="$(dockerfile_for "$name")"
  reference="$(image_ref "$name" "$platform")"
  echo "== building $reference from $dockerfile for $platform"
  started=$SECONDS
  if [[ "$NO_CACHE" == "1" ]]; then
    docker buildx build --platform "$platform" --file "$dockerfile" --load \
      --tag "$reference" --provenance=false --sbom=false --no-cache .
  else
    docker buildx build --platform "$platform" --file "$dockerfile" --load \
      --tag "$reference" --provenance=false --sbom=false .
  fi
  elapsed=$((SECONDS - started))
  echo "   built in ${elapsed}s, size $(image_size "$reference") for $platform"
}

# --- run ----------------------------------------------------------------------

if [[ "$CHECK_PINS" == "1" ]]; then
  check_pins
  if [[ "$CHECK_REGISTRY" == "1" ]]; then check_registry; fi
  exit 0
fi

require_buildx
echo "== pins"
check_pins

for platform in $PLATFORMS; do
  for image in $IMAGES; do
    build_image "$image" "$platform"
    reference="$(image_ref "$image" "$platform")"
    CHECK_PLATFORM="$platform"
    echo "-- AC1/AC5 checks ($reference)"
    check_runtime_base "$reference" "$platform" "$(runtime_pin "$image")"
    check_user "$reference"
    check_app_tree_ownership "$reference" "$image"
    if [[ "$image" == "migrator" ]]; then
      check_migrator_cli "$reference"
      built_migrator="$reference"
    else
      check_p7 "$reference" "$image"
      check_migration_head "$reference"
      if [[ "$image" == "worker" ]]; then
        check_parse_child "$reference" "$image"
        check_parse_child_runs "$reference" "$image"
      fi
      if [[ "$image" == "api" ]]; then built_api="$reference"; else built_worker="$reference"; fi
    fi
  done
done

if [[ "$WITH_DB" == "1" ]]; then
  [[ -n "$built_migrator" ]] || die "--with-db needs the migrator image in the same run (--image migrator,api)"
  db_begin
  echo "-- AC5/AC1 checks against the throwaway database"
  check_migrator_status "$built_migrator"
  check_migrator_apply "$built_migrator"
  check_migrator_status "$built_migrator"
  if [[ -n "$built_api" ]]; then
    check_api_health "$built_api"
  else
    echo "   (api image not in this run: use --image api --with-db for the health check)"
  fi
fi

echo "== built images"
for reference in "$built_api" "$built_worker" "$built_migrator"; do
  [[ -n "$reference" ]] || continue
  printf '   %-34s %s\n' "$reference" \
    "$(docker image inspect "$reference" --format 'user={{.Config.User}} size={{.Size}} id={{.Id}}')"
done
echo "== build-images: ok"
