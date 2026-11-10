#!/usr/bin/env bash
# Why: a fork pull request must be able to run tests with no Evidgate credential.
# This runner starts the example suite under `env -i`, so no EVIDGATE_* variable
# and no secret from the caller's shell can reach it, then records the observed
# environment and the produced report. It fails when the report is missing.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
out="$here/test-results"
report="$out/report.json"
html="$here/playwright-report/index.html"

home="$(mktemp -d)"
cleanup() {
  rm -rf "$home"
}
trap cleanup EXIT

rm -rf "$out" "$here/playwright-report"

if ! cli="$(cd "$here" && node -e 'process.stdout.write(require.resolve("@playwright/test/cli"))' 2>/dev/null)"; then
  echo "run-fork-context: @playwright/test is not installed; run npm install" >&2
  exit 2
fi

# The allow-list is the proof: PATH finds node, HOME keeps any temporary file out
# of a shared directory, and no other variable is passed to the suite.
env -i PATH="$PATH" HOME="$home" bash -c 'env' >"$home/environment.txt"

status=0
env -i PATH="$PATH" HOME="$home" \
  bash -c 'cd "$1" && exec node "$2" test --config playwright.config.ts' _ "$here" "$cli" ||
  status=$?
if [[ "$status" -ne 0 ]]; then
  echo "run-fork-context: the example suite exited $status" >&2
  exit "$status"
fi

node - "$here" "$home/environment.txt" <<'JS'
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const [here, environmentFile] = process.argv.slice(2);
const out = path.join(here, 'test-results');
const reportPath = path.join(out, 'report.json');
const htmlPath = path.join(here, 'playwright-report', 'index.html');

function fail(message) {
  console.error(`run-fork-context: ${message}`);
  process.exit(1);
}

const names = fs
  .readFileSync(environmentFile, 'utf8')
  .split('\n')
  .filter((line) => line.length > 0)
  .map((line) => line.slice(0, line.indexOf('=')));
const evidgateVars = names.filter((name) => name.startsWith('EVIDGATE_'));
const secretVars = names.filter((name) =>
  /TOKEN|SECRET|PASSWORD|CREDENTIAL|API_?KEY|PRIVATE_?KEY/i.test(name),
);
if (evidgateVars.length > 0) fail(`the suite environment holds ${evidgateVars.join(', ')}`);
if (secretVars.length > 0) fail(`the suite environment holds secret-like names: ${secretVars.join(', ')}`);
if (!fs.existsSync(reportPath)) fail('the suite produced no report (test-results/report.json)');
if (!fs.existsSync(htmlPath)) fail('the suite produced no HTML report (playwright-report/index.html)');

const stats = JSON.parse(fs.readFileSync(reportPath, 'utf8')).stats;
if (!stats || typeof stats.expected !== 'number') fail('the report has no stats block');
if (stats.unexpected !== 0) fail(`the suite reported ${stats.unexpected} unexpected results`);
if (stats.expected + (stats.skipped || 0) < 1) fail('the suite ran no tests');

function digest(file) {
  const bytes = fs.readFileSync(file);
  const sha256 = crypto.createHash('sha256').update(bytes).digest('hex');
  return { sha256: `sha256:${sha256}`, bytes: bytes.length };
}

const playwright = JSON.parse(
  fs.readFileSync(require.resolve('@playwright/test/package.json', { paths: [here] }), 'utf8'),
).version;
const record = {
  schema: 'fork-pr-fixture/1',
  recorded_at: new Date().toISOString(),
  suite: {
    expected: stats.expected,
    unexpected: stats.unexpected,
    skipped: stats.skipped || 0,
    flaky: stats.flaky || 0,
  },
  runtime: { node: process.version, playwright },
  environment: { allow_list: names.sort(), evidgate_vars: 0, secret_vars: 0 },
  artifacts: {
    report: { path: 'test-results/report.json', ...digest(reportPath) },
    html_report: { path: 'playwright-report/index.html', ...digest(htmlPath) },
  },
};
fs.writeFileSync(path.join(out, 'run-context.json'), `${JSON.stringify(record, null, 2)}\n`);
console.log(
  `run-fork-context: ${stats.expected} passed with no EVIDGATE_* variable; report ${record.artifacts.report.sha256}`,
);
JS
