#!/usr/bin/env bash
# Runs every scripts/review/tests/t-*.sh with plain bash and reports each file.
# No framework, no external service: the stubs in lib-fixture.sh answer ssh,
# docker and curl, so the whole review-deployment flow runs offline.
#
# usage: bash scripts/review/tests/run.sh
set -uo pipefail

TEST_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
failed=0
ran=0

for test in "$TEST_DIR"/t-*.sh; do
  [ -e "$test" ] || continue
  ran=$((ran + 1))
  printf '\n== %s\n' "${test##*/}"
  if bash "$test"; then
    printf 'PASS %s\n' "${test##*/}"
  else
    printf 'FAIL %s\n' "${test##*/}" >&2
    failed=$((failed + 1))
  fi
done

printf '\n%d test file(s), %d failed\n' "$ran" "$failed"
[ "$failed" -eq 0 ]
