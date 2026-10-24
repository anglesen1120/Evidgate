#!/usr/bin/env node
// 09.1.1-T1.2 — 00 §5.6 W7 / AC1: initial JS ≤ 300 kB gzip, every lazy chunk ≤ 150 kB gzip.
// 09.1.1-T6.1: the check is PER LOCALE, because `ng build` now emits one bundle per locale.
//
// Measurement finding (AC1 wording gap, read from the installed @angular/build 22.2.0):
// `angular.json` `budgets` are checked against RAW on-disk asset bytes, not transfer size.
// src/utils/bundle-calculator.js computes every budget from `asset.size` (lines 116-143) and
// never gzips (Angular's own `kB` unit is 1000 bytes, bundle-calculator.js:17). Angular's
// budget schema therefore cannot express W7 as written, so this script is the gzip gate and
// the `budgets` block in angular.json is a raw-size backstop calibrated at 3x the gzip
// target (initial 900 kB, anyScript 450 kB).
//
// Wired into `npm run build -w apps/web` after `ng build`; dependency-free on purpose (node:
// fs/zlib/url/path only) so `npm ci --ignore-scripts` on CI needs nothing extra. Budget
// numbers live in the two constants below and nowhere else.
//
// Layout: with `localize` (angular.json build configurations) `@angular/build:application`
// writes one directory per locale — `dist/browser/en/`, `dist/browser/vi/` — each holding its
// own index.html and chunks, and no root index.html. Every locale directory is checked on its
// own against the same two limits, and the report prefixes each row with the locale.
//
// The expected locale set is NOT read back from the output: `projects.web.i18n.sourceLocale`
// plus every key of `projects.web.i18n.locales` in apps/web/angular.json is the contract, and
// the build must satisfy all of it. Deriving it from the directories found instead would let a
// build that silently lost `en/` pass, because the surviving `vi/` directory alone looks like
// a complete localized build. So a missing expected locale directory (or its index.html), and
// an unexpected locale directory that the config does not declare, both fail the gate. The
// single unnamed layout (index.html directly in the dist directory) is checked only when
// `EVIDGATE_WEB_LOCALES` is set to the empty string, i.e. only for a synthetic proof build.
// A dist matching no layout fails closed instead of reporting "0 kB".
//
// Paths are resolved from this file's own location — the dist directory
// (`apps/web/dist/browser`, the same way `e2e/static-server.mjs` does it) and
// `apps/web/angular.json` — so the check reports the same thing whatever the caller's cwd is.
// `EVIDGATE_WEB_DIST_DIR` and `EVIDGATE_WEB_LOCALES` exist ONLY for the synthetic-output
// proofs of this gate (apps/web/scripts/check-bundle-budget.test.mjs); they are not build
// knobs. `EVIDGATE_WEB_LOCALES` takes a comma-separated locale list, or the empty string for
// the unnamed single-layout proof.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync } from 'node:zlib';

// `@angular/build:application` with the default static output mode writes the deployable
// assets into `<outputPath>/browser` (angular.json: outputPath "dist"), so the locale
// directories (or index.html, unlocalized) sit there.
const DIST_DIR = process.env.EVIDGATE_WEB_DIST_DIR
  ? resolve(process.env.EVIDGATE_WEB_DIST_DIR)
  : resolve(fileURLToPath(new URL('../dist/browser/', import.meta.url)));
// The locale contract: `projects.web.i18n` of the same project this script belongs to.
const ANGULAR_JSON_PATH = fileURLToPath(new URL('../angular.json', import.meta.url));
/** W7: initial JS budget, gzip, 1000 bytes per kB (the unit Angular's budgets use). */
const INITIAL_JS_GZIP_LIMIT_BYTES = 300 * 1000;
/** W7: per lazy-chunk budget, gzip. */
const LAZY_CHUNK_GZIP_LIMIT_BYTES = 150 * 1000;

function fail(message) {
  console.error(`bundle budget: ${message}`);
  process.exit(1);
}

/** `true` for a regular file; a missing path and a directory of that name both answer `false`. */
function isFile(path) {
  try {
    return statSync(path).isFile();
  } catch {
    return false;
  }
}

/** Every `.js` file below `dir`, as POSIX paths relative to `dir`. */
function listScripts(dir, prefix = '') {
  const entries = readdirSync(dir, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    const posixPath = prefix === '' ? entry.name : `${prefix}/${entry.name}`;
    if (entry.isDirectory()) {
      files.push(...listScripts(join(dir, entry.name), posixPath));
    } else if (entry.isFile() && entry.name.endsWith('.js')) {
      files.push(posixPath);
    }
  }
  return files;
}

/** The value of `name` on a tag, quoted with either `"` or `'`. */
function attribute(tag, name) {
  const match = new RegExp(`\\b${name}\\s*=\\s*(?:"([^"]*)"|'([^']*)')`, 'i').exec(tag);
  return match?.[1] ?? match?.[2];
}

/**
 * The scripts the browser loads before the app is interactive, taken from the generated
 * index.html: `<script src>` tags and the `<link rel="modulepreload">` hints the builder
 * emits for initial chunks. Everything else is a lazy chunk.
 *
 * Returns dist-relative paths (`./` and `/` prefixes and any query/hash removed), and the
 * raw urls alongside them so the caller can name an unresolvable reference in its error.
 */
function initialScriptsFromIndex(html) {
  const referenced = new Map();
  const tags = html.match(/<(?:script|link)\b[^>]*>/gi) ?? [];
  for (const tag of tags) {
    const isModulePreload = /rel\s*=\s*["']?modulepreload/i.test(tag);
    const src = attribute(tag, 'src');
    const href = attribute(tag, 'href');
    const url = src ?? (isModulePreload ? href : undefined);
    if (url === undefined || !url.endsWith('.js')) {
      continue;
    }
    const path = decodeURIComponent(url.split(/[?#]/)[0]).replace(/^\.?\/+/, '');
    referenced.set(path, url);
  }
  return referenced;
}

function gzipBytes(path) {
  // Level 9 is the W7 definition: the smallest gzip a static server can be told to emit,
  // so it is the kindest reading of the budget and any other level only adds bytes.
  return gzipSync(readFileSync(path), { level: 9 }).length;
}

function formatKiB(bytes) {
  return `${(bytes / 1000).toFixed(1)} kB`;
}

function showPath(path) {
  const shown = relative(process.cwd(), path);
  return shown.startsWith('..') ? path : shown;
}

/**
 * The locales the production build must emit, and where that set came from: `sourceLocale` (a
 * string, or `{ code }` in the newer schema) followed by every key of `projects.web.i18n.locales`
 * in apps/web/angular.json. An empty locale list means "one unnamed layout", which is reachable
 * only through `EVIDGATE_WEB_LOCALES=''` — the synthetic single-layout proof.
 */
function expectedLocales() {
  const override = process.env.EVIDGATE_WEB_LOCALES;
  if (override !== undefined) {
    return {
      locales: override
        .split(',')
        .map((locale) => locale.trim())
        .filter((locale) => locale !== ''),
      source: 'EVIDGATE_WEB_LOCALES',
    };
  }

  let config;
  try {
    config = JSON.parse(readFileSync(ANGULAR_JSON_PATH, 'utf8'));
  } catch (error) {
    fail(`cannot read ${ANGULAR_JSON_PATH} for the expected locales (${error.message}).`);
  }
  const i18n = config?.projects?.web?.i18n;
  const sourceLocale =
    typeof i18n?.sourceLocale === 'string' ? i18n.sourceLocale : i18n?.sourceLocale?.code;
  if (typeof sourceLocale !== 'string' || sourceLocale === '') {
    fail(`projects.web.i18n.sourceLocale is missing from ${ANGULAR_JSON_PATH}.`);
  }
  return {
    locales: [sourceLocale, ...Object.keys(i18n.locales ?? {})],
    source: ANGULAR_JSON_PATH,
  };
}

/**
 * The bundle layouts to check, as `{ label, dir }`: one per expected locale, each of which must
 * exist with its own index.html. A leftover root index.html is ignored in that mode, because a
 * localized build does not write one and counting it would double-count the chunks.
 *
 * With an empty locale list (`EVIDGATE_WEB_LOCALES=''`) the single unnamed layout —
 * index.html directly in the dist directory — is the only layout, which is what an unlocalized
 * proof build produces. The expected-locale checks are what make a build that lost a locale
 * fail: reading the locales back from the output would call the surviving directories complete.
 */
function findLayouts(distDir, { locales, source }) {
  let entries;
  try {
    entries = readdirSync(distDir, { withFileTypes: true });
  } catch {
    fail(`no ${distDir}/ — run \`ng build\` first (this check reads the production output).`);
  }
  const directoryNames = entries.filter((entry) => entry.isDirectory()).map((entry) => entry.name);

  if (locales.length === 0) {
    if (!isFile(join(distDir, 'index.html'))) {
      // Fail closed: nothing to measure, and reporting "0 kB initial" would pass for the wrong
      // reason.
      fail(`no ${join(distDir, 'index.html')} — run \`ng build\` first.`);
    }
    return [{ label: '', dir: distDir }];
  }

  const missingDirectory = locales.filter((locale) => !directoryNames.includes(locale));
  for (const locale of missingDirectory) {
    console.error(
      `bundle budget: missing locale "${locale}" — no ${join(distDir, locale)}/ directory, ` +
        `although ${source} declares it.`,
    );
  }
  if (missingDirectory.length > 0) {
    fail(`${missingDirectory.length} expected locale(s) missing from the build output.`);
  }

  const missingIndex = locales.filter((locale) => !isFile(join(distDir, locale, 'index.html')));
  for (const locale of missingIndex) {
    console.error(
      `bundle budget: missing locale "${locale}" — ${join(distDir, locale, 'index.html')} ` +
        `is absent, although ${source} declares the locale.`,
    );
  }
  if (missingIndex.length > 0) {
    fail(`${missingIndex.length} expected locale(s) without an index.html.`);
  }

  const unexpected = directoryNames
    .filter((name) => isFile(join(distDir, name, 'index.html')))
    .filter((name) => !locales.includes(name))
    .sort();
  if (unexpected.length > 0) {
    for (const name of unexpected) {
      console.error(
        `bundle budget: unexpected locale directory ${join(distDir, name)}/ — ` +
          `${source} declares only ${locales.join(', ')}.`,
      );
    }
    fail(`${unexpected.length} locale directory(ies) the locale configuration does not declare.`);
  }

  return locales.map((locale) => ({ label: locale, dir: join(distDir, locale) }));
}

/** The W7 rows for one layout: the gzip size of every script, split into initial and lazy. */
function measureLayout({ label, dir }) {
  const rowPrefix = label === '' ? '' : `${label} `;
  const indexPath = join(dir, 'index.html');
  const shownIndexPath = showPath(indexPath);
  const indexHtml = readFileSync(indexPath, 'utf8');

  const scripts = listScripts(dir);
  if (scripts.length === 0) {
    fail(`${rowPrefix}no JavaScript in ${dir}/ — the production output is missing.`);
  }

  const referenced = initialScriptsFromIndex(indexHtml);
  if (referenced.size === 0) {
    // Fail closed: an index.html that names no initial script means the parse or the build is
    // wrong, and silently measuring "0 kB initial" would pass the gate for the wrong reason.
    fail(
      `${rowPrefix}no initial <script src> or <link rel="modulepreload"> .js reference found in ` +
        `${shownIndexPath} — this check cannot tell initial from lazy chunks without it.`,
    );
  }

  const scriptSet = new Set(scripts);
  const missing = [...referenced].filter(([path]) => !scriptSet.has(path));
  if (missing.length > 0) {
    for (const [path, url] of missing) {
      console.error(
        `bundle budget: ${shownIndexPath} references ${url} (${path}), absent from ${dir}`,
      );
    }
    fail(`${rowPrefix}${missing.length} referenced script(s) missing from the build output.`);
  }

  const initial = [];
  const lazy = [];
  for (const script of scripts) {
    const size = gzipBytes(join(dir, ...script.split('/')));
    (referenced.has(script) ? initial : lazy).push({ script, size });
  }

  const initialTotal = initial.reduce((total, chunk) => total + chunk.size, 0);
  return {
    shownIndexPath,
    rows: [
      {
        label: `${rowPrefix}initial JS (${initial.length} chunk(s))`,
        size: initialTotal,
        limit: INITIAL_JS_GZIP_LIMIT_BYTES,
      },
      ...lazy.map((chunk) => ({
        label: `${rowPrefix}lazy chunk ${chunk.script}`,
        size: chunk.size,
        limit: LAZY_CHUNK_GZIP_LIMIT_BYTES,
      })),
    ],
  };
}

const layouts = findLayouts(DIST_DIR, expectedLocales());
const measured = layouts.map(measureLayout);

console.log('bundle budget (gzip, 1000 bytes per kB) — 00 §5.6 W7');
const rows = measured.flatMap((result) => result.rows);
for (const row of rows) {
  const verdict = row.size > row.limit ? 'FAIL' : 'ok';
  console.log(
    `  ${verdict.padEnd(4)} ${row.label.padEnd(52)} ${formatKiB(row.size).padStart(10)} / ${formatKiB(row.limit)}`,
  );
}

const overBudget = rows.filter((row) => row.size > row.limit);
if (overBudget.length > 0) {
  for (const row of overBudget) {
    console.error(
      `bundle budget: ${row.label} is ${formatKiB(row.size)} gzip, over ${formatKiB(row.limit)}`,
    );
  }
  fail(`${overBudget.length} budget(s) exceeded.`);
}

const checked = measured.map((result) => result.shownIndexPath).join(', ');
console.log(`bundle budget: ${checked} checked, all budgets met.`);
