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
trap 'rm -rf "$WORKDIR"' EXIT

# Copy tracked + untracked-not-ignored files (a clean-clone equivalent for the
# working tree: bin/, obj/, node_modules/, dist/, .env* are excluded by .gitignore).
echo "smoke-clean-build: copying working tree to $WORKDIR"
cd "$ROOT"
git ls-files --cached --others --exclude-standard -z \
  | tar --null -T - -cf - \
  | tar -xf - -C "$WORKDIR"

# Run as the host user so build output written to the bind-mounted tree gets the
# caller's ownership. HOME and all caches therefore live under /tmp, and Node is
# installed to /tmp/node (the container's /usr/local is not writable by uid).
docker run --rm \
  --user "$(id -u):$(id -g)" \
  -e HOME=/tmp/home \
  -e DOTNET_CLI_HOME=/tmp/home \
  -e NUGET_PACKAGES=/tmp/nuget/packages \
  -e NPM_CONFIG_CACHE=/tmp/npm-cache \
  -v "$WORKDIR":/src -w /src \
  "$IMAGE" bash -euo pipefail -c '
    set -x
    mkdir -p /tmp/home /tmp/nuget/packages /tmp/npm-cache /tmp/node
    arch="$(uname -m)"
    case "$arch" in
      x86_64)  node_arch=x64 ;;
      aarch64) node_arch=arm64 ;;
      *) echo "unsupported arch $arch" >&2; exit 1 ;;
    esac
    tarball="node-v'"$NODE_VERSION"'-linux-${node_arch}.tar.gz"
    cd /tmp
    curl -fsSL "https://nodejs.org/dist/v'"$NODE_VERSION"'/${tarball}" -o "$tarball"
    curl -fsSL "https://nodejs.org/dist/v'"$NODE_VERSION"'/SHASUMS256.txt" -o SHASUMS256.txt
    grep " ${tarball}\$" SHASUMS256.txt | sha256sum -c -
    tar -xzf "$tarball" -C /tmp/node --strip-components=1
    export PATH=/tmp/node/bin:$PATH
    cd /src
    node -v && npm -v && dotnet --version
    dotnet restore --locked-mode
    dotnet build -c Release Evidgate.slnx
    npm ci
    npm run build --workspaces --if-present
  '

echo "smoke-clean-build: clean-clone build passed (AC1)"
