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
#        the runtime base is the digest-pinned image and not something else;
#   AC1  `docker inspect .Config.User` is a non-root uid (the chiseled base's 1654);
#   P7   the published runtimeconfig.json carries InvariantGlobalization and
#        Server GC, and (Api/Worker) the main assembly has a ReadyToRun header;
#   AC5  the Migrator image is its own image on the same pins and
#        `docker run <image> --help` still prints the CLI contract;
#   AC1/AC5 with --with-db: a throwaway PostgreSQL 18 (the pinned compose.dev.yml
#        image) is brought to head with the Migrator image, then `--status` is run
#        against it and the Api answers /internal/health/live + /ready.
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

# The dev stack's PostgreSQL pin (infra/compose.dev.yml, tests/Shared/ContainerImages.cs).
POSTGRES_IMAGE='postgres:18.6@sha256:5a5a84b19854a9ffaa54082c166ff4ec27473a361e496e5ea167f298f2da9722'
OWNER_PASSWORD='imagecheck-owner'
APP_PASSWORD='imagecheck-app'

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

check_pins() {
  local fail=0 sdk builder runtime image other_builder other_runtime builder_tag ref
  sdk="$(declared_sdk_version)"
  builder="$(builder_pin api)"
  runtime="$(runtime_pin api)"
  [[ -n "$builder" ]] || { echo "check-pins: no builder FROM in $(dockerfile_for api)" >&2; fail=1; }
  [[ -n "$runtime" ]] || { echo "check-pins: no runtime FROM in $(dockerfile_for api)" >&2; fail=1; }

  # One builder and one runtime across all three Dockerfiles, and one context
  # ignore list: drift here is how a pin silently stops applying to an image.
  for image in api worker migrator; do
    other_builder="$(builder_pin "$image")"
    other_runtime="$(runtime_pin "$image")"
    if [[ "$other_builder" != "$builder" ]]; then
      echo "check-pins: $(dockerfile_for "$image") builder '$other_builder' differs from $(dockerfile_for api) '$builder'" >&2
      fail=1
    fi
    if [[ "$other_runtime" != "$runtime" ]]; then
      echo "check-pins: $(dockerfile_for "$image") runtime '$other_runtime' differs from $(dockerfile_for api) '$runtime'" >&2
      fail=1
    fi
    if ! cmp -s infra/docker/Dockerfile.api.dockerignore "infra/docker/Dockerfile.$image.dockerignore"; then
      echo "check-pins: infra/docker/Dockerfile.$image.dockerignore differs from Dockerfile.api.dockerignore" >&2
      fail=1
    fi
  done

  # Context hygiene: the ignore list must keep the git-ignored host state out of
  # the build context. `.env.dev` is the one that matters — scripts/dev/up.sh
  # writes real per-role passwords into it (00 §5.7 S3) — and a regenerated list
  # dropping it is invisible until a builder holds the credentials (measured
  # 2026-09-27: it was in the context before these patterns were added). The
  # three lists are already asserted byte-identical above, so one read suffices.
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

  for ref in "$builder" "$runtime"; do
    case "$ref" in
      *@sha256:*) ;;
      *) echo "check-pins: '$ref' is not digest-pinned (@sha256:<64 hex>): a mutable tag must not reach a build or a deploy file" >&2; fail=1 ;;
    esac
  done

  printf 'builder  %s\n' "$builder"
  printf 'runtime  %s\n' "$runtime"
  printf 'sdk      %s (global.json: the same SDK CI installs, so the image and the test build compile with one toolchain)\n' "$sdk"
  return "$fail"
}

# --check-registry: resolve the pinned tags at mcr.microsoft.com and compare.
# A moved tag is informational (upstream published a patch under the same tag;
# the pin still points at what was reviewed), a tag that no longer resolves is fatal.
check_registry() {
  python3 - "$(builder_pin api)" "$(runtime_pin api)" <<'PYEOF'
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

# Where the runtime base actually came from: the pinned layers must be the
# built image's first layers, because a stage can only add on top of its FROM.
#
# The comparison target is read from the *registry* (the pinned index digest ->
# the platform's manifest -> its config -> rootfs.diff_ids) rather than from the
# local store: with a multi-platform pin the local name resolves to one platform
# image (measured 2026-09-27: `docker image inspect` on the pinned index digest
# answered the arm64 variant while the image under test was amd64), so a name-based
# comparison silently compares the wrong platform. Offline (`--offline`, or a
# registry that cannot be reached) the layer proof degrades to the inherited
# config markers and says so.
check_runtime_base() {
  local reference="$1" platform="$2" runtime="$3"
  python3 - "$reference" "$platform" "$runtime" \
    "$(docker image inspect "$reference" --format '{{json .RootFS.Layers}}')" \
    "$(docker image inspect "$reference" --format '{{json .Config.Env}}')" \
    "$OFFLINE" <<'PYEOF'
import json, sys, urllib.request

reference, platform, runtime = sys.argv[1], sys.argv[2], sys.argv[3]
layers, environment, offline = json.loads(sys.argv[4]), json.loads(sys.argv[5]), sys.argv[6] == "1"
architecture = platform.split("/")[1]

ACCEPT = ", ".join([
    "application/vnd.docker.distribution.manifest.list.v2+json",
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.v2+json",
])


def fetch(url):
    request = urllib.request.Request(url)
    request.add_header("Accept", ACCEPT)
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.loads(response.read().decode())


def base_layers():
    registry, _, rest = runtime.partition("/")
    image, _, index_digest = rest.partition("@")
    repository, _, _tag = image.partition(":")
    index = fetch("https://%s/v2/%s/manifests/%s" % (registry, repository, index_digest))
    entries = index.get("manifests")
    if not entries:
        raise RuntimeError("the pinned reference is not a manifest list: %s" % runtime)
    entry = next((m for m in entries
                  if m.get("platform", {}).get("os") == "linux"
                  and m["platform"].get("architecture") == architecture), None)
    if entry is None:
        raise RuntimeError("the pinned index %s has no linux/%s manifest" % (runtime, architecture))
    manifest = fetch("https://%s/v2/%s/manifests/%s" % (registry, repository, entry["digest"]))
    config = fetch("https://%s/v2/%s/blobs/%s" % (registry, repository, manifest["config"]["digest"]))
    return config["rootfs"]["diff_ids"], entry["digest"]


def marker_fallback(reason):
    tag = runtime.partition("@")[0].rsplit(":", 1)[-1]
    required = ["APP_UID=1654", "DOTNET_VERSION=" + tag.split("-")[0]]
    missing = [marker for marker in required if marker not in environment]
    if missing:
        sys.exit("build-images: cannot verify the runtime base of %s (%s) and the inherited "
                 "config markers %s are missing too" % (reference, reason, ", ".join(missing)))
    print("   runtime base: WARNING cannot reach the registry (%s); fell back to the inherited "
          "config markers %s — the layer proof was NOT performed" % (reason, ", ".join(required)))


if offline:
    marker_fallback("--offline")
else:
    try:
        base, manifest_digest = base_layers()
    except Exception as error:
        marker_fallback(error)
    else:
        if not base:
            sys.exit("build-images: the pinned runtime base reports no layers: %s" % runtime)
        if layers[:len(base)] != base:
            sys.exit("build-images: %s does not start with the pinned runtime layers\n"
                     "  pinned %s (linux/%s manifest %s)\n  base   %s\n  image  %s"
                     % (reference, runtime, architecture, manifest_digest, base, layers[:len(base)]))
        print("   runtime base: the pinned linux/%s manifest's %d layer(s) (%s) are the image's "
              "first layers" % (architecture, len(base), manifest_digest))
PYEOF
}

check_user() {
  local reference="$1" user
  user="$(docker image inspect "$reference" --format '{{.Config.User}}')"
  [[ -n "$user" ]] || die "$reference has an empty Config.User: AC1 requires a non-root User"
  case "$user" in
    0 | 0:* | root | root:*) die "$reference runs as root (Config.User='$user'): AC1 requires non-root" ;;
  esac
  echo "   user: Config.User='$user' (non-root)"
}

# The container filesystem is read with `docker cp`: the chiseled runtime has no shell.
copy_from_image() {
  local reference="$1" path="$2" destination="$3" container
  container="$(docker create --platform "$CHECK_PLATFORM" "$reference")"
  docker cp "$container:$path" "$destination" >/dev/null
  docker rm -f "$container" >/dev/null
}

check_p7() {
  local reference="$1" name="$2" assembly scratch
  assembly="$(assembly_of "$name")"
  scratch="$(mktemp -d)"
  copy_from_image "$reference" "/app/$assembly" "$scratch/$assembly"
  copy_from_image "$reference" "/app/${assembly%.dll}.runtimeconfig.json" "$scratch/runtimeconfig.json"
  python3 - "$scratch/runtimeconfig.json" <<'PYEOF'
import json, sys

document = json.load(open(sys.argv[1], encoding="utf-8"))
properties = document.get("runtimeOptions", {}).get("configProperties", {})
expected = {"System.Globalization.Invariant": True, "System.GC.Server": True}
missing = [key for key, value in expected.items() if properties.get(key) is not value]
if missing:
    sys.exit("build-images: runtimeconfig.json misses the 00 §5.7 P7 process setting(s): %s"
             % ", ".join(missing))
frameworks = [entry.get("name") for entry in document.get("runtimeOptions", {}).get("frameworks", [])]
print("   P7: runtimeconfig.json has InvariantGlobalization + Server GC (frameworks=%s)"
      % ", ".join(str(name) for name in frameworks))
PYEOF
  if [[ "$(is_r2r "$name")" == "1" ]]; then
    python3 - "$scratch/$assembly" <<'PYEOF'
import sys

data = open(sys.argv[1], "rb").read(1 << 20)
if b"RTR\x00" not in data:
    sys.exit("build-images: %s carries no ReadyToRun header (00 §5.7 P7)" % sys.argv[1])
print("   P7: ReadyToRun header present in %s" % sys.argv[1].rsplit("/", 1)[-1])
PYEOF
  fi
  rm -rf "$scratch"
}

# 01.1.2-T1.3: BuildingBlocks.Persistence globs migrations/*.sql at build time and
# bakes the greatest file name into assembly metadata as the expected journal head.
# A build context without migrations/ does not fail — it bakes the "(none)"
# sentinel and every ready probe would then answer unready — so the baked value is
# checked against the repository's head.
check_migration_head() {
  local reference="$1" head scratch
  head="$(ls migrations/*.sql | sort | tail -1)"
  head="${head##*/}"
  scratch="$(mktemp -d)"
  copy_from_image "$reference" "/app/Evidgate.BuildingBlocks.Persistence.dll" "$scratch/persistence.dll"
  python3 - "$scratch/persistence.dll" "$head" <<'PYEOF'
import sys

dll, head = sys.argv[1], sys.argv[2]
data = open(dll, "rb").read()
if head.encode() not in data:
    sys.exit("build-images: %s does not carry the expected migration head '%s' — the build "
             "context is missing migrations/*.sql, so the head baked into the image is "
             "degraded (01.1.2-T1.3)" % (dll, head))
print("   expected migration head baked in: %s" % head)
PYEOF
  rm -rf "$scratch"
}

check_migrator_cli() {
  local reference="$1" output status
  set +e
  output="$(docker run --rm --platform "$CHECK_PLATFORM" "$reference" --help 2>&1)"
  status=$?
  set -e
  [[ "$status" -eq 0 ]] || die "'docker run $reference --help' exited $status, expected 0 (AC5)"
  printf '%s\n' "$output" | grep -q 'usage: Evidgate.Migrator' \
    || die "'docker run $reference --help' did not print the CLI contract"
  printf '%s\n' "$output" | grep -q -- '--status' \
    || die "the Migrator usage text no longer documents --status"
  echo "   entry point: 'docker run $reference --help' exits 0 and prints the usage text"
}

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

# --- --with-db ----------------------------------------------------------------

db_begin() {
  local deadline
  echo "== starting a throwaway $POSTGRES_IMAGE"
  docker network create "$network" >/dev/null
  docker run -d --name "$db_container" --network "$network" --network-alias db \
    -e POSTGRES_PASSWORD=imagecheck \
    -e POSTGRES_USER=postgres \
    -e POSTGRES_DB=postgres \
    -e POSTGRES_INITDB_ARGS='--auth-host=scram-sha-256' \
    "$POSTGRES_IMAGE" >/dev/null
  deadline=$((SECONDS + 120))
  until docker exec "$db_container" pg_isready -h 127.0.0.1 -p 5432 -U postgres -d postgres >/dev/null 2>&1; do
    [[ $SECONDS -lt $deadline ]] || die "the throwaway PostgreSQL did not become ready within 120s"
    sleep 1
  done
  docker exec -i "$db_container" psql -U postgres -d postgres -v ON_ERROR_STOP=1 -q <<'SQL' >/dev/null
create role evidgate_owner login createrole nosuperuser nocreatedb nobypassrls password 'imagecheck-owner';
create database evidgate owner evidgate_owner;
SQL
  echo "   ready: database evidgate owned by evidgate_owner"
}

db_connection_string() { echo "Host=db;Port=5432;Database=evidgate;Username=evidgate_owner;Password=$OWNER_PASSWORD"; }

check_migrator_status() {
  local reference="$1" output status
  set +e
  output="$(docker run --rm --platform "$CHECK_PLATFORM" --network "$network" \
    -e "EVIDGATE_MIGRATOR__CONNECTIONSTRING=$(db_connection_string)" \
    "$reference" --status 2>&1)"
  status=$?
  set -e
  printf '%s\n' "$output" | sed 's/^/   | /'
  [[ "$status" -eq 0 ]] || die "'--status' exited $status against the database, expected 0"
  printf '%s\n' "$output" | grep -qiE 'pending' \
    || die "'--status' printed no applied/pending summary"
  echo "   --status: exit 0"
}

check_migrator_apply() {
  local reference="$1"
  echo "-- applying the image's migration set to the throwaway database"
  docker run --rm --platform "$CHECK_PLATFORM" --network "$network" \
    -e "EVIDGATE_MIGRATOR__CONNECTIONSTRING=$(db_connection_string)" \
    "$reference" | sed 's/^/   | /'
}

check_api_health() {
  local reference="$1" host_port deadline live ready
  echo "-- starting the Api image against the migrated database"
  # 0001_foundation_roles.sql deliberately creates evidgate_app with nologin and
  # no password (a password in a migration would live in git history); the deploy
  # bootstrap grants them, and so does this check (scripts/dev/db-init.sh line 178).
  # The Api refuses to serve as any other role (00 §5.4.3), which its own log shows.
  docker exec "$db_container" psql -U postgres -d evidgate -v ON_ERROR_STOP=1 -q \
    -c "alter role evidgate_app login password '$APP_PASSWORD';" >/dev/null
  docker run -d --platform "$CHECK_PLATFORM" --name "$api_container" --network "$network" \
    -p 127.0.0.1::8080 \
    -e "EVIDGATE__DATABASE__CONNECTIONSTRING=Host=db;Port=5432;Database=evidgate;Username=evidgate_app;Password=$APP_PASSWORD" \
    "$reference" >/dev/null
  host_port="$(docker port "$api_container" 8080/tcp | head -1)"
  host_port="${host_port##*:}"
  [[ -n "$host_port" ]] || die "the Api container published no host port"
  deadline=$((SECONDS + 60))
  live=""
  until live="$(curl -fsS --max-time 2 "http://127.0.0.1:$host_port/internal/health/live" 2>/dev/null)"; do
    [[ $SECONDS -lt $deadline ]] || {
      docker logs "$api_container" 2>&1 | tail -20 | sed 's/^/   | /'
      die "the Api image did not answer /internal/health/live within 60s"
    }
    sleep 1
  done
  printf '%s\n' "$live" | sed 's/^/   live:  /'
  printf '%s\n' "$live" | grep -q '"status"' \
    || die "the live endpoint did not return the 00 §9.1 health envelope"
  ready="$(curl -fsS --max-time 5 "http://127.0.0.1:$host_port/internal/health/ready" 2>/dev/null || true)"
  printf '%s\n' "$ready" | sed 's/^/   ready: /'
  printf '%s\n' "$ready" | grep -q '"status"' \
    || die "the ready endpoint did not return the 00 §9.1 health envelope"
  echo "   /internal/health/live + /ready served by the built Api image"
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
    if [[ "$image" == "migrator" ]]; then
      check_migrator_cli "$reference"
      built_migrator="$reference"
    else
      check_p7 "$reference" "$image"
      check_migration_head "$reference"
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
