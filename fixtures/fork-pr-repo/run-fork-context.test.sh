#!/usr/bin/env bash
# Negative controls for run-fork-context.sh: a credential in the caller's shell
# must not reach the suite, a suite that reports one must be refused, and an
# all-skipped suite must be refused. Run it from this directory after `npm ci`.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
cleanup() {
  rm -rf "$tmp"
}
trap cleanup EXIT

fail() {
  echo "run-fork-context.test: $*" >&2
  exit 1
}

copy_suite() {
  local target="$1"
  mkdir -p "$target"
  cp "$here/package.json" "$here/playwright.config.ts" \
    "$here/suite-env.global-setup.ts" "$here/run-fork-context.sh" "$target/"
  cp -R "$here/src" "$target/src"
  chmod +x "$target/run-fork-context.sh"
  ln -s "$here/node_modules" "$target/node_modules"
}

# 1. A credential-shaped variable in the caller's shell is not inherited, and the
#    suite still produces a report the runner accepts.
if ! (cd "$here" && EVIDGATE_API_TOKEN='not-a-credential' ./run-fork-context.sh); then
  fail 'the runner refused a clean suite because the caller held EVIDGATE_API_TOKEN'
fi
if grep -q 'EVIDGATE_' "$here/test-results/suite-env.json"; then
  fail 'the suite environment inherited an EVIDGATE_* variable'
fi

# 2. The check itself fails when the suite does report an Evidgate variable: a
#    runner that accepts this run would accept anything.
leaky="$tmp/leaky"
copy_suite "$leaky"
mkdir -p "$leaky/tests"
cp "$here/tests/summarize.spec.ts" "$leaky/tests/summarize.spec.ts"
cat >"$leaky/suite-env.global-setup.ts" <<'TS'
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';

export default function recordSuiteEnvironment(config: { configFile?: string }): void {
  const resultsDir = join(dirname(config.configFile ?? ''), 'test-results');
  mkdirSync(resultsDir, { recursive: true });
  const names = [...Object.keys(process.env), 'EVIDGATE_API_TOKEN'].sort();
  writeFileSync(join(resultsDir, 'suite-env.json'), `${JSON.stringify({ names }, null, 2)}\n`);
}
TS
status=0
(cd "$leaky" && ./run-fork-context.sh) >"$tmp/leaky.log" 2>&1 || status=$?
if [[ "$status" -eq 0 ]]; then
  fail 'the runner accepted a suite whose environment reported EVIDGATE_API_TOKEN'
fi
grep -q 'EVIDGATE_API_TOKEN' "$tmp/leaky.log" ||
  fail "the runner refused for another reason: $(cat "$tmp/leaky.log")"

# 3. A report with only skips proves nothing, so an all-skipped suite is refused.
skipped="$tmp/skipped"
copy_suite "$skipped"
mkdir -p "$skipped/tests"
cat >"$skipped/tests/all-skipped.spec.ts" <<'TS'
import { test } from '@playwright/test';

test.describe.skip('a suite that proves nothing', () => {
  test('never runs', () => {});
});
TS
status=0
(cd "$skipped" && ./run-fork-context.sh) >"$tmp/skipped.log" 2>&1 || status=$?
if [[ "$status" -eq 0 ]]; then
  fail 'the runner accepted an all-skipped suite'
fi
grep -q 'skipped' "$tmp/skipped.log" ||
  fail "the runner refused for another reason: $(cat "$tmp/skipped.log")"

echo 'run-fork-context.test: all negative controls hold'
