#!/usr/bin/env node
// 09.1.1-T1.2 — 00 §5.6 W7 / AC1: initial JS ≤ 300 kB gzip, every lazy chunk ≤ 150 kB gzip.
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
// The dist directory is resolved from this file's own location
// (`apps/web/dist/browser`, the same way `e2e/static-server.mjs` does it), so the check
// reports the same thing whatever the caller's cwd is. `EVIDGATE_WEB_DIST_DIR` overrides it
// ONLY to run the synthetic-output proofs of this gate; it is not a build knob.
import { readdirSync, readFileSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync } from 'node:zlib';

// `@angular/build:application` with the default static output mode writes the deployable
// assets into `<outputPath>/browser` (angular.json: outputPath "dist"), so index.html and
// every emitted script sit there.
const DIST_DIR = process.env.EVIDGATE_WEB_DIST_DIR
  ? resolve(process.env.EVIDGATE_WEB_DIST_DIR)
  : resolve(fileURLToPath(new URL('../dist/browser/', import.meta.url)));
/** W7: initial JS budget, gzip, 1000 bytes per kB (the unit Angular's budgets use). */
const INITIAL_JS_GZIP_LIMIT_BYTES = 300 * 1000;
/** W7: per lazy-chunk budget, gzip. */
const LAZY_CHUNK_GZIP_LIMIT_BYTES = 150 * 1000;

function fail(message) {
  console.error(`bundle budget: ${message}`);
  process.exit(1);
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

const indexPath = join(DIST_DIR, 'index.html');
const shownIndexPath = relative(process.cwd(), indexPath).startsWith('..')
  ? indexPath
  : relative(process.cwd(), indexPath);
let indexHtml;
try {
  indexHtml = readFileSync(indexPath, 'utf8');
} catch {
  fail(`no ${indexPath} — run \`ng build\` first (this check reads the production output).`);
}

const scripts = listScripts(DIST_DIR);
if (scripts.length === 0) {
  fail(`no JavaScript in ${DIST_DIR}/ — the production output is missing.`);
}

const referenced = initialScriptsFromIndex(indexHtml);
if (referenced.size === 0) {
  // Fail closed: an index.html that names no initial script means the parse or the build is
  // wrong, and silently measuring "0 kB initial" would pass the gate for the wrong reason.
  fail(
    `no initial <script src> or <link rel="modulepreload"> .js reference found in ${shownIndexPath} — ` +
      'this check cannot tell initial from lazy chunks without it.',
  );
}

const scriptSet = new Set(scripts);
const missing = [...referenced].filter(([path]) => !scriptSet.has(path));
if (missing.length > 0) {
  for (const [path, url] of missing) {
    console.error(
      `bundle budget: ${shownIndexPath} references ${url} (${path}), absent from ${DIST_DIR}`,
    );
  }
  fail(`${missing.length} referenced script(s) missing from the build output.`);
}

const initial = [];
const lazy = [];
for (const script of scripts) {
  const size = gzipBytes(join(DIST_DIR, ...script.split('/')));
  (referenced.has(script) ? initial : lazy).push({ script, size });
}

const initialTotal = initial.reduce((total, chunk) => total + chunk.size, 0);
const rows = [
  {
    label: `initial JS (${initial.length} chunk(s))`,
    size: initialTotal,
    limit: INITIAL_JS_GZIP_LIMIT_BYTES,
  },
  ...lazy.map((chunk) => ({
    label: `lazy chunk ${chunk.script}`,
    size: chunk.size,
    limit: LAZY_CHUNK_GZIP_LIMIT_BYTES,
  })),
];

console.log('bundle budget (gzip, 1000 bytes per kB) — 00 §5.6 W7');
for (const row of rows) {
  const verdict = row.size > row.limit ? 'FAIL' : 'ok';
  console.log(
    `  ${verdict.padEnd(4)} ${row.label.padEnd(44)} ${formatKiB(row.size).padStart(10)} / ${formatKiB(row.limit)}`,
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

console.log(`bundle budget: ${shownIndexPath} checked, all budgets met.`);
