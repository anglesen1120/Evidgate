#!/usr/bin/env bash
# 11.2.1-T1.2 (AC1): lint a customer GitHub Actions workflow against the five
# FR-11-2 fork-safe rules published in docs/security/customer-ci-recipe.md.
#
# usage: scripts/lint-ci-recipe.sh <workflow.yml>...
#        scripts/lint-ci-recipe.sh --self-test
#
# Violations go to stderr as `lint-ci-recipe: <file>: <rule-id>: <detail>`.
# Exit 0 clean, 1 any violation or unparseable/ambiguous YAML, 2 usage/IO error.
# The parsing and the rules live in scripts/lint-ci-recipe.mjs (YAML 1.2 core
# schema via the pinned `yaml` package, ADR-0002); its header lists the rules and
# the limits of the remaining shell-text heuristics (R4).
# --self-test lints fixtures/ci-recipes/github-fork-safe.yml (must pass) and every
# fixtures/ci-recipes/{negative,bypass}/*.yml (each must fail with the rule named
# on its `# expect:` first line) and prints one PASS/FAIL line per file.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"

if ! command -v node >/dev/null 2>&1; then
  echo "lint-ci-recipe: node is not on PATH" >&2
  exit 2
fi
if [[ ! -f "$here/../node_modules/yaml/package.json" ]]; then
  echo "lint-ci-recipe: the 'yaml' package is missing — run 'npm ci' first" >&2
  exit 2
fi

exec node "$here/lint-ci-recipe.mjs" "$@"
