#!/usr/bin/env bash
# 01.3.1 contract gate: spectral lint at warn severity (AC1) + conventions
# checks for 00 §9.1/§9.2 coverage, §9.3 operation coverage, no /internal
# paths, JSON Schema 2020-12 dialect, snake_case naming and the §9.4/§10
# validity boundaries (AC2-AC4, AC6). Exits 0 only when every stage passes.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

export SCARF_ANALYTICS=false

echo "== spectral lint (fail at warn) =="
npx spectral lint contracts/openapi.yaml --ruleset .spectral.yaml --fail-severity warn

echo "== contract conventions =="
node scripts/check-contract-conventions.mjs
