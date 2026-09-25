#!/usr/bin/env bash
# FR-01-1 (plans/01-foundation.md §2): the full local gate — the same checks CI
# runs (01.7.1), in the same order. Stops at the first failure.
# Prerequisites only: .NET 10 SDK + Node 26 on PATH, dependencies restored once
# (dotnet restore/npm ci — this script never installs anything itself).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

stage() { echo "== $1"; }

stage "restore (locked mode)"
dotnet restore --locked-mode Evidgate.slnx

stage "build (Release, warnings as errors)"
dotnet build -c Release --no-restore Evidgate.slnx

stage "dotnet format (verify only)"
dotnet format Evidgate.slnx --verify-no-changes --no-restore

stage "dotnet test (unit + architecture)"
dotnet test --no-build -c Release Evidgate.slnx

# tsc -b of the root tsconfig.json references: packages/* AND apps/web — the
# --workspaces variant skips apps/web (no build script) and would let it escape
# the strict gate.
stage "tsc -b (solution references)"
npm run build

stage "eslint"
npm run lint

stage "prettier"
npm run format:check

stage "check-layout"
scripts/check-layout.sh

stage "check-sources"
scripts/check-sources.sh

stage "check-adr-matrix"
scripts/check-adr-matrix.sh

stage "check-adr"
scripts/check-adr.sh

stage "check-links"
scripts/check-links.sh

stage "check-contract-conventions"
scripts/check-contract-conventions.sh

echo "== verify: all stages passed"
