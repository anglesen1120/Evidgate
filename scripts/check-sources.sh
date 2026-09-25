#!/usr/bin/env bash
# Supply-chain check (01.1.1 T2.4, T40): exactly one package source per ecosystem —
# nuget.org for NuGet (nuget.config <clear/>) and registry.npmjs.org for npm (.npmrc).
# Prints both configurations; exits 1 unless each resolves to exactly one source.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"


if ! command -v dotnet >/dev/null 2>&1; then
  if [ -x "$HOME/.dotnet/dotnet" ]; then
    export PATH="$HOME/.dotnet:$PATH"
  else
    echo "check-sources: dotnet not found on PATH or ~/.dotnet" >&2
    exit 1
  fi
fi

fail=0

echo "== npm =="
npm_registry="$(npm config get registry)"
echo "npm registry: $npm_registry"
if [ "$npm_registry" != "https://registry.npmjs.org/" ]; then
  echo "check-sources: npm registry is '$npm_registry', expected exactly https://registry.npmjs.org/" >&2
  fail=1
fi

echo "== nuget =="
dotnet nuget list source
# Count Enabled sources; the repo nuget.config must reduce the machine list to one.
enabled="$(dotnet nuget list source | grep -cE '^[[:space:]]*[0-9]+\.[[:space:]]+.*\[Enabled\]' || true)"
if [ "$enabled" -ne 1 ]; then
  echo "check-sources: expected exactly 1 enabled NuGet source, found $enabled" >&2
  fail=1
elif ! dotnet nuget list source | grep -q 'https://api.nuget.org/v3/index.json'; then
  echo "check-sources: the enabled NuGet source is not nuget.org" >&2
  fail=1
fi

exit "$fail"
