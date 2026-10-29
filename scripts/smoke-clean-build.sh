#!/usr/bin/env bash
# AC1 (01.1.1): a clean copy of the repo builds both ecosystems with nothing but
# the .NET 10 SDK and Node 26 installed. Runs inside Docker so the host toolchain
# cannot leak into the result. The SDK image is pinned by multi-arch digest
# (tag: mcr.microsoft.com/dotnet/sdk:10.0) and the Node tarball is verified
# against the published SHASUMS256.txt before extraction.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# mcr.microsoft.com/dotnet/sdk:10.0 — multi-arch index digest
IMAGE="mcr.microsoft.com/dotnet/sdk:10.0@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29"
NODE_VERSION="26.10.0"

# Stage under $HOME: container runtimes (colima/docker-desktop) mount $HOME into
# the VM but not /var/folders, so an mktemp default would appear empty inside.
WORKDIR="$(mktemp -d "$HOME/.evidgate-smoke.XXXXXX")"
# The inner script lives OUTSIDE the copied tree: inside it would show up in
# the container as an extra root file and trip check-layout.sh.
INNER_SCRIPT="$(mktemp "$HOME/.evidgate-smoke-inner.XXXXXX.sh")"
trap 'rm -rf "$WORKDIR"; rm -f "$INNER_SCRIPT"' EXIT

# Copy tracked + untracked-not-ignored files (a clean-clone equivalent for the
# working tree: bin/, obj/, node_modules/, dist/, .env* are excluded by .gitignore).
echo "smoke-clean-build: copying working tree to $WORKDIR"
cd "$ROOT"
git ls-files --cached --others --exclude-standard -z \
  | tar --null -T - -cf - \
  | tar -xf - -C "$WORKDIR"

# Inner phase run as the host uid:gid via setpriv (below); a real mounted file
# so the container's bash -c stays free of nested quoting.
cat > "$INNER_SCRIPT" <<'INNER'
#!/usr/bin/env bash
set -euo pipefail
set -x
arch="$(uname -m)"
case "$arch" in
  x86_64)  node_arch=x64 ;;
  aarch64) node_arch=arm64 ;;
  *) echo "unsupported arch $arch" >&2; exit 1 ;;
esac
tarball="node-v${NODE_VERSION}-linux-${node_arch}.tar.gz"
cd /tmp
curl -fsSL "https://nodejs.org/dist/v${NODE_VERSION}/${tarball}" -o "$tarball"
curl -fsSL "https://nodejs.org/dist/v${NODE_VERSION}/SHASUMS256.txt" -o SHASUMS256.txt
grep " ${tarball}\$" SHASUMS256.txt | sha256sum -c -
tar -xzf "$tarball" -C /tmp/node --strip-components=1
export PATH=/tmp/node/bin:$PATH
cd /src
node -v && npm -v && dotnet --version
dotnet restore --locked-mode
dotnet build -c Release Evidgate.slnx
npm ci
# AC6 (01.2.1): the clean clone also passes the full local gate. verify.sh
# itself does tsc -b via the root tsconfig and never runs npm ci, so it runs
# after npm ci here. The tar copy has no .git; check-layout.sh needs
# `git ls-files`, so the copy becomes a fresh repo with one commit holding every
# file the clean clone would hold. The commit is also the `develop` ref: the
# change-set stages (check-dor-security) scope themselves to "changed against
# develop", so a clone with no change set checks nothing, instead of every
# pre-existing plan being "new" because it is untracked.
git init -q
git add -A
git -c user.name=smoke -c user.email=smoke@localhost commit -q -m "clean clone"
git branch develop
# The tier is pinned to `full`: this copy's single commit is its own base, so a
# bare run would ask the classifier about an empty change set instead of running
# the gate the clone exists to prove.
EVIDGATE_VERIFY_SKIP_DOCKER=1 EVIDGATE_GATE_SCOPE=full scripts/verify.sh
INNER

# The container entry runs as root only for one apt step: verify.sh calls
# check-adr-matrix.sh and check-links.sh, which need python3 — not shipped in
# the SDK image and not installable as non-root. python3 comes unpinned from the
# image's signed Debian repo (the image itself stays digest-pinned; apt is
# authenticated by the repo signatures). The actual build/npm/verify work then
# runs as the host uid:gid via setpriv, so nothing repo-side executes as root.
docker run --rm \
  -e NODE_VERSION="$NODE_VERSION" \
  -e HOME=/tmp/home \
  -e DOTNET_CLI_HOME=/tmp/home \
  -e NUGET_PACKAGES=/tmp/nuget/packages \
  -e NPM_CONFIG_CACHE=/tmp/npm-cache \
  -e HOST_UID="$(id -u)" -e HOST_GID="$(id -g)" \
  -v "$WORKDIR":/src -v "$INNER_SCRIPT":/tmp/smoke-inner.sh:ro -w /src \
  "$IMAGE" bash -euo pipefail -c '
    command -v setpriv >/dev/null || { echo "setpriv missing from image" >&2; exit 1; }
    apt-get update -qq && apt-get install -y -qq --no-install-recommends python3
    mkdir -p /tmp/home /tmp/nuget/packages /tmp/npm-cache /tmp/node
    chown -R "$HOST_UID:$HOST_GID" /tmp/home /tmp/nuget /tmp/npm-cache /tmp/node
    exec setpriv --reuid "$HOST_UID" --regid "$HOST_GID" --clear-groups \
      env HOME=/tmp/home DOTNET_CLI_HOME=/tmp/home \
          NUGET_PACKAGES=/tmp/nuget/packages NPM_CONFIG_CACHE=/tmp/npm-cache \
      bash /tmp/smoke-inner.sh
  '

echo "smoke-clean-build: clean-clone build passed (AC1)"
