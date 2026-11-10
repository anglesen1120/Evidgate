#!/usr/bin/env bash
# Why: a fork pull request must be able to run tests with no Evidgate credential.
# The suite starts under `env -i` with PATH and HOME only, so the caller's
# variables are not inherited; the suite's own global setup records the names it
# receives, and this runner refuses a run whose recorded environment holds a
# credential-shaped name, a skipped test, or no test at all.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
out="$here/test-results"
report="$out/report.json"
suite_env="$out/suite-env.json"
html="$here/playwright-report/index.html"

home="$(mktemp -d)"
cleanup() {
  rm -rf "$home"
}
trap cleanup EXIT

rm -rf "$out" "$here/playwright-report"

if ! cli="$(cd "$here" && node -e 'process.stdout.write(require.resolve("@playwright/test/cli"))' 2>/dev/null)"; then
  echo "run-fork-context: @playwright/test is not installed; run npm ci" >&2
  exit 2
fi

# PATH finds node inside the suite process; HOME keeps any temporary file out of a
# shared directory. No other variable is passed, so nothing else can be inherited.
status=0
env -i PATH="$PATH" HOME="$home" \
  bash -c 'cd "$1" && exec node "$2" test --config playwright.config.ts' _ "$here" "$cli" ||
  status=$?
if [[ "$status" -ne 0 ]]; then
  echo "run-fork-context: the example suite exited $status" >&2
  exit "$status"
fi

node - "$here" "$suite_env" "$report" <<'JS'
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const [here, suiteEnvPath, reportPath] = process.argv.slice(2);
const out = path.join(here, 'test-results');
const htmlPath = path.join(here, 'playwright-report', 'index.html');

function fail(message) {
  console.error(`run-fork-context: ${message}`);
  process.exit(1);
}

// The suite records its own process.env in global setup, so this reads what the
// tests saw rather than the environment of a helper process started next to them.
if (!fs.existsSync(suiteEnvPath)) fail('the suite recorded no environment (test-results/suite-env.json)');
const recorded = JSON.parse(fs.readFileSync(suiteEnvPath, 'utf8'));
const names = Array.isArray(recorded.names)
  ? recorded.names.filter((name) => typeof name === 'string')
  : [];
if (names.length === 0) fail('the suite recorded an empty environment');

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
const skipped = stats.skipped || 0;
if (skipped !== 0) fail(`the suite skipped ${skipped} tests; a skipped test proves nothing`);
if (stats.expected < 1) fail('the suite ran no tests');
if (stats.unexpected !== 0) fail(`the suite reported ${stats.unexpected} unexpected results`);

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
  suite: { expected: stats.expected, unexpected: stats.unexpected, skipped, flaky: stats.flaky || 0 },
  runtime: { node: process.version, playwright },
  environment: {
    recorded_by: 'suite global setup (tests see these names)',
    names,
    evidgate_vars: evidgateVars,
    secret_vars: secretVars,
  },
  artifacts: {
    report: { path: 'test-results/report.json', ...digest(reportPath) },
    html_report: { path: 'playwright-report/index.html', ...digest(htmlPath) },
  },
};
fs.writeFileSync(path.join(out, 'run-context.json'), `${JSON.stringify(record, null, 2)}\n`);
console.log(
  `run-fork-context: ${stats.expected} passed with ${names.length} environment names and no EVIDGATE_* variable; report ${record.artifacts.report.sha256}`,
);
JS
