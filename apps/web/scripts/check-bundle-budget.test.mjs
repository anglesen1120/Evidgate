// 09.1.1-T6.1 (D1): proofs for apps/web/scripts/check-bundle-budget.mjs. Every case builds a
// synthetic dist under the OS temp directory and runs the real script against it, so the gate's
// two failure modes that a real build cannot rehearse cheaply — a build that LOST a locale, and
// a `vi` bundle over the W7 initial-JS budget — are proven here instead of reasoned about.
//
// The expected locale set comes from apps/web/angular.json (sourceLocale `en` plus the `vi`
// translation), which is why the fixture dialects below are `en` and `vi` and not arbitrary
// names: a proof that only ever writes both locales cannot show that a missing one fails.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const SCRIPT = fileURLToPath(new URL('./check-bundle-budget.mjs', import.meta.url));

/** One index.html naming a single initial script, the shape the builder emits. */
const INDEX_HTML = [
  '<!doctype html>',
  '<html><head><title>probe</title></head>',
  '<body><app-root></app-root>',
  '<script src="main.js" type="module"></script>',
  '</body></html>',
].join('\n');

/** A tiny, highly compressible script — far below both W7 budgets. */
const SMALL_SCRIPT = "console.log('bundle budget probe');\n";

/** Incompressible bytes well over the 300 kB gzip initial budget (~320 kB after gzip). */
function overBudgetScript() {
  return randomBytes(320 * 1024);
}

async function tempRoot(t) {
  const root = await mkdtemp(join(tmpdir(), 'evidgate-budget-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  return root;
}

/** Write one locale layout: `<root>/<name>/index.html` plus its `main.js`. */
async function writeLayout(dir, script = SMALL_SCRIPT) {
  await mkdir(dir, { recursive: true });
  await writeFile(join(dir, 'index.html'), INDEX_HTML, 'utf8');
  await writeFile(join(dir, 'main.js'), script);
}

async function writeLocalizedDist(root, { en = true, vi = true } = {}) {
  if (en) {
    await writeLayout(join(root, 'en'));
  }
  if (vi) {
    await writeLayout(join(root, 'vi'));
  }
  return root;
}

/**
 * Run the gate against `distDir`. `locales` maps to `EVIDGATE_WEB_LOCALES` and is removed from
 * the child environment when `undefined`, which is the production path (angular.json decides).
 */
function runGate(distDir, { locales } = {}) {
  const env = { ...process.env, EVIDGATE_WEB_DIST_DIR: distDir };
  delete env.EVIDGATE_WEB_LOCALES;
  if (locales !== undefined) {
    env.EVIDGATE_WEB_LOCALES = locales;
  }
  const result = spawnSync(process.execPath, [SCRIPT], { encoding: 'utf8', env });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

test('both declared locales present: every budget met', async (t) => {
  const root = await tempRoot(t);
  const dist = await writeLocalizedDist(join(root, 'browser'));

  const { status, stdout, stderr } = runGate(dist);

  assert.equal(status, 0, stderr);
  assert.match(stdout, /all budgets met/);
  assert.match(stdout, /initial JS \(1 chunk\(s\)\)/);
});

test('a build that lost the source locale (en) fails, naming en and its directory', async (t) => {
  const root = await tempRoot(t);
  const dist = await writeLocalizedDist(join(root, 'browser'), { en: false });

  const { status, stderr } = runGate(dist);

  assert.equal(status, 1);
  assert.match(stderr, /missing locale "en"/);
  assert.ok(stderr.includes(join(dist, 'en')), stderr);
  assert.match(stderr, /expected locale\(s\) missing/);
});

test('a build that lost a translated locale (vi) fails, naming vi', async (t) => {
  const root = await tempRoot(t);
  const dist = await writeLocalizedDist(join(root, 'browser'), { vi: false });

  const { status, stderr } = runGate(dist);

  assert.equal(status, 1);
  assert.match(stderr, /missing locale "vi"/);
  assert.ok(stderr.includes(join(dist, 'vi')), stderr);
});

test('a localized build whose en/ lost its index.html fails, naming en', async (t) => {
  const root = await tempRoot(t);
  const dist = await writeLocalizedDist(join(root, 'browser'));
  await rm(join(dist, 'en', 'index.html'));

  const { status, stderr } = runGate(dist);

  assert.equal(status, 1);
  assert.match(stderr, /missing locale "en"/);
  assert.ok(stderr.includes(join(dist, 'en', 'index.html')), stderr);
});

test('a vi initial bundle over the 300 kB gzip budget fails, naming vi', async (t) => {
  const root = await tempRoot(t);
  const dist = await writeLocalizedDist(join(root, 'browser'));
  await writeFile(join(dist, 'vi', 'main.js'), overBudgetScript());

  const { status, stderr } = runGate(dist);

  assert.equal(status, 1);
  assert.match(stderr, /vi initial JS/);
  assert.match(stderr, /over 300\.0 kB/);
});

test('a locale directory the configuration does not declare fails', async (t) => {
  const root = await tempRoot(t);
  const dist = await writeLocalizedDist(join(root, 'browser'));
  await writeLayout(join(dist, 'fr'));

  const { status, stderr } = runGate(dist);

  assert.equal(status, 1);
  assert.match(stderr, /unexpected locale directory/);
  assert.ok(stderr.includes(join(dist, 'fr')), stderr);
});

test('an empty dist directory fails instead of reporting 0 kB', async (t) => {
  const root = await tempRoot(t);
  const dist = join(root, 'browser');
  await mkdir(dist, { recursive: true });

  const { status, stderr } = runGate(dist);

  assert.equal(status, 1);
  assert.match(stderr, /missing locale "en"/);
  assert.match(stderr, /missing locale "vi"/);
});

test('a missing dist directory fails', async (t) => {
  const root = await tempRoot(t);

  const { status, stderr } = runGate(join(root, 'absent'));

  assert.equal(status, 1);
  assert.match(stderr, /run `ng build` first/);
});

// The single unnamed layout is a synthetic-proof-only mode: an empty EVIDGATE_WEB_LOCALES is
// what turns off the angular.json locale contract, so no real build can drift into it.
test('the unnamed single layout is checked only for EVIDGATE_WEB_LOCALES=""', async (t) => {
  const root = await tempRoot(t);
  const dist = join(root, 'browser');
  await writeLayout(dist);

  const withoutOverride = runGate(dist);
  assert.equal(withoutOverride.status, 1);
  assert.match(withoutOverride.stderr, /missing locale "en"/);

  const { status, stdout, stderr } = runGate(dist, { locales: '' });
  assert.equal(status, 0, stderr);
  assert.match(stdout, /all budgets met/);
});

// The other half of the override: a proof dist with a locale set of its own.
test('EVIDGATE_WEB_LOCALES replaces the expected locale set', async (t) => {
  const root = await tempRoot(t);
  const dist = join(root, 'browser');
  await writeLayout(join(dist, 'fr'));

  const { status, stdout, stderr } = runGate(dist, { locales: 'fr' });

  assert.equal(status, 0, stderr);
  assert.match(stdout, /fr initial JS/);
});
