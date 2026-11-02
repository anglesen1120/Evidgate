#!/usr/bin/env node
// 09.1.1-T12 (plan 09.1.1 §2 AC10, §8 T12): "the production build contains no fixture code
// (bundle grep)". The dev-only fixture backend of 09.1.1-T12.1 is swapped in by the `fixtures`
// angular.json configuration (`fileReplacements`), so it must never reach `dist/browser`.
//
// Two independent leak classes are checked over every deployable file:
// 1. The fixture backend's own code. Three literal marker strings are required by design to
//    appear in that code (`X-Evidgate-Fixture` as the response header of a fixture answer,
//    `evidgate.fixture-backend` as the InjectionToken description, `fixtures/web-api` inside the
//    501 message) plus the export name `provideFixtureBackend` and the 501 error code
//    `fixture_not_found`; a production bundle can only contain them if the dev backend (or a
//    catalog/route import) reached the build. Two more markers, the `dev/ui-gallery` route path
//    and the `app-ui-gallery` selector, catch the review-only `shared/ui` gallery the same way.
//    The other review-only route paths of that same seam — `dev/settings-secrets`,
//    `dev/settings-policy` (09.7.1-T3/T4) and `dev/run-list` (09.3.1-T1), which mount a
//    phase-later product screen on its review address — are markers too: they exist only in
//    `dev-routes.fixtures.ts`, so any one of them in `dist/browser` means the `fileReplacements`
//    seam regressed and a phase-G2/G4 screen shipped in a G1 build. Their page selectors are NOT
//    markers: those pages are ordinary product code the production bundle carries.
// 2. The fixture corpus data: every string value of >= `MIN_FIXTURE_STRING_LENGTH` characters in
//    `fixtures/web-api/*/input.json`. A response body that survived into the bundle leaks the
//    fixtures just as plainly as the backend code does. A value holding a non-ASCII character is
//    also searched in its `\uXXXX`-escaped form, because a minifier that escapes non-ASCII text
//    emits the escape sequence rather than the character. The protocol slots of a fixture document
//    (`headers`, `operation_id`, `variant`, `code`, `resource`, `action`) are skipped with their
//    whole subtree: their values are vocabulary production code already carries —
//    `content-type`/`application/json` in the HTTP client, the operation ids in the SDK,
//    `precondition_failed`-style codes in `api-error.ts`, the permission literals of the navigation
//    registry — so scanning them would fail every build without telling anything about the
//    fixtures. The `provider` and `status` members are not skipped as slots: only the words the
//    contract enumerates for them (`github_actions`, `pending_verification`, `active`, `disabled`,
//    `rotated`, `expired`, `revoked`, which the step-2 page renders) are exempt, and the exemption
//    matches per value — a fixture that grows any other status or provider word keeps it as a
//    needle, so it cannot hide a new fixture string behind a common member name.
//
// The production invocation forbids all 18 markers. The review web image is the one caller that
// may carry a dev-only surface: `npm run build:review -w apps/web` adds the `review` angular.json
// configuration, which swaps `login-extension.ts` for its development replacement so the review
// stack shows the dev sign-in form. Its six markers are listed in `DEV_SIGN_IN_MARKERS` and are
// NOT forbidden under `--allow-dev-sign-in`; everything else stays forbidden, and the flag
// additionally requires one of `app-dev-sign-in` / `/dev/session` in the output, so a review
// build that lost the form fails here instead of serving a login screen with no way in.
//
// The check fails closed: a missing/empty dist directory, or a corpus with no `input.json`, is a
// failure, not a pass — otherwise a broken build or a moved corpus would make the gate vacuous.
//
// Dependency-free (node: fs/path/url only) like `check-bundle-budget.mjs`, so a CI job that ran
// `npm ci --ignore-scripts` needs nothing extra. The dist directory is resolved from this file's
// own location (`apps/web/dist/browser`), so the answer does not depend on the caller's cwd.
// `EVIDGATE_WEB_DIST_DIR` and `EVIDGATE_WEB_FIXTURE_DIR` exist ONLY to run the synthetic proofs of
// this gate (packages/cli/src/webFixtureLeakGate.test.ts); they are not build knobs.
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// `@angular/build:application` writes the deployable assets into `<outputPath>/browser`
// (angular.json: outputPath "dist"), exactly as `check-bundle-budget.mjs` reads them.
const DIST_DIR = process.env.EVIDGATE_WEB_DIST_DIR
  ? resolve(process.env.EVIDGATE_WEB_DIST_DIR)
  : resolve(fileURLToPath(new URL('../dist/browser/', import.meta.url)));
// apps/web/scripts -> repository root -> fixtures/web-api.
const FIXTURE_DIR = process.env.EVIDGATE_WEB_FIXTURE_DIR
  ? resolve(process.env.EVIDGATE_WEB_FIXTURE_DIR)
  : resolve(fileURLToPath(new URL('../../../fixtures/web-api/', import.meta.url)));

/**
 * Everything the browser can serve, including source maps, the copied assets and the
 * translation catalogs. `@angular/build` copies `public/` verbatim, so an `.svg` or a
 * `.webmanifest` can carry a fixture string just as a chunk can; `.xlf` covers a build that
 * emits its translation files next to the bundle. The textual types only: `.ico`, fonts and
 * images are binary, so `readFileSync(…, 'utf8')` never runs on them.
 */
const SCANNED_EXTENSIONS = [
  '.js',
  '.mjs',
  '.html',
  '.css',
  '.map',
  '.json',
  '.txt',
  '.svg',
  '.xlf',
  '.webmanifest',
];

/**
 * The markers of the Development-only sign-in: the login-extension seam of the `development` and
 * `fixtures` configurations, the route `/v1/dev/session` it calls, and the generated SDK operation
 * that spells the same route. The `review` configuration mounts the same form on purpose (the
 * review stack has no human login yet), so `--allow-dev-sign-in` exempts exactly these six and
 * requires one of the presence needles below to survive in the output.
 */
export const DEV_SIGN_IN_MARKERS = [
  'app-dev-sign-in',
  '/v1/dev/session',
  'Dev sign-in',
  'Đăng nhập phát triển',
  // The same dev sign-in seen through the generated SDK (`packages/sdk/gen/sdk.gen.ts`): its `url`
  // is the bare `/dev/session` (the app's client adds `/v1`) and its operation name survives in a
  // source map, so either needle catches a chunk that kept the SDK operation.
  'createDevSession',
  '/dev/session',
];

/**
 * The marker strings every build must keep out: the fixture backend, the review-only routes and
 * the UI gallery (see the header).
 */
const FIXTURE_MARKERS = [
  'X-Evidgate-Fixture',
  'evidgate.fixture-backend',
  'fixtures/web-api',
  'provideFixtureBackend',
  'fixture_not_found',
  // The review-only `shared/ui` gallery (09.1.1-T4.1): `dev-routes.fixtures.ts` mounts it at this
  // path and `UiGalleryComponent` renders this selector. The production `dev-routes.ts` adds no
  // route, so either string in `dist/browser` means the `fileReplacements` seam regressed. The
  // selector, not the class name, is the marker: a minified bundle renames classes but keeps
  // selectors.
  'dev/ui-gallery',
  'app-ui-gallery',
  // The phase-later review addresses of the same seam (09.7.1-T3/T4, 09.3.1-T1, F20): `fixtures`
  // and `development` mount the real Secrets (G2), Policy (G4) and Runs list (G2) pages under
  // `/dev/...`; production has no such route at all. The paths, not the page selectors, are the
  // markers — those pages are product code the production bundle carries.
  'dev/settings-secrets',
  'dev/settings-policy',
  'dev/run-list',
  'dev/run-detail',
  'dev/suite-composition',
];

/**
 * Every marker, in its historical order (fixture first, sign-in last): the production invocation
 * forbids this whole list, so its count and its report line stay what they were.
 */
const MARKERS = [...FIXTURE_MARKERS, ...DEV_SIGN_IN_MARKERS];

/**
 * `--allow-dev-sign-in` is the review build's mode: `npm run build:review -w apps/web` passes it
 * to this gate after `ng build --configuration review` and the bundle-budget gate. It exempts
 * `DEV_SIGN_IN_MARKERS` only, and then requires one of these two needles in the output — the form
 * component's selector or the route it posts to — so a review build that lost the sign-in fails
 * instead of passing vacuously.
 */
const allowDevSignIn = process.argv.slice(2).includes('--allow-dev-sign-in');
const SIGN_IN_PRESENCE_NEEDLES = ['app-dev-sign-in', '/dev/session'];
const forbiddenMarkers = allowDevSignIn ? FIXTURE_MARKERS : MARKERS;

/**
 * Shortest corpus string treated as a leak signal. Shorter values (`ok`, ids, `en`) collide with
 * ordinary bundle text and would report noise instead of fixtures.
 */
const MIN_FIXTURE_STRING_LENGTH = 12;

/**
 * Members of a fixture document whose values are protocol vocabulary rather than fixture data, so
 * their whole subtree is skipped: the response `headers`, the openapi `operation_id`, the toggle
 * `variant` name, the 00 §9.2 error `code` (which is also the permission `code` of a catalogue
 * row), and the permission row's `resource` and `action`. Production code carries the same strings
 * (`content-type`/`application/json`, the SDK operation ids, the `precondition_failed`-style codes
 * of `apps/web/src/app/core/http/api-error.ts`, and the navigation registry's permission literals
 * such as `organization:read`), so scanning them would report a leak on every build without saying
 * anything about the fixtures.
 *
 * The code markers of `MARKERS` are the primary leak detector: the fixture backend cannot
 * reach a production bundle without at least one of them, and they are matched in every scanned
 * file for free. Skipping these members is a deliberate trade-off — the corpus strings still
 * cover the fixture *data* (`body` values such as names and descriptions, which production code
 * never carries), so a leaked response body is caught without the noise the vocabulary would add
 * on every build.
 */
const PROTOCOL_MEMBERS = new Set(['headers', 'operation_id', 'variant', 'code']);

/**
 * The closed enums of `contracts/openapi.yaml` that a corpus document carries under an ordinary
 * data member, as `member -> the words the contract enumerates for it`. With 09.2.1-T2 the
 * step-2 page names the CI provider (`github_actions`) in its create request and renders the
 * binding/token status words, so those strings live in production code as well; skipping the
 * members outright would also exempt every *other* word a fixture ever puts there, so the
 * exemption is per value: `ENUM_MEMBERS.get(member).has(value)` (09.2.1-T7).
 */
const ENUM_MEMBERS = new Map([
  ['provider', new Set(['github_actions'])],
  [
    'status',
    new Set(['pending_verification', 'active', 'disabled', 'rotated', 'expired', 'revoked']),
  ],
]);

/**
 * `resource`/`action` are the halves of a permission `code` (`contracts/permissions.json`): a
 * catalogue row whose `resource` is `organization` would otherwise fail the build for a word
 * production needs. They are common field names, so they are skipped ONLY on a permission row —
 * an object whose `code` is exactly `<resource>:<action>` — and scanned everywhere else.
 */
const PERMISSION_HALVES = ['resource', 'action'];

function isPermissionRow(value) {
  return (
    typeof value.code === 'string' &&
    typeof value.resource === 'string' &&
    typeof value.action === 'string' &&
    value.code === `${value.resource}:${value.action}`
  );
}

function fail(message) {
  console.error(`fixture code: ${message}`);
  process.exit(1);
}

/** `path` relative to the caller's cwd when it is below it, absolute otherwise. */
function shown(path) {
  const relativePath = relative(process.cwd(), path);
  return relativePath.startsWith('..') ? path : relativePath;
}

/** Every scanned file below `dir`, as POSIX paths relative to `dir`. */
function listFiles(dir, prefix = '') {
  const entries = readdirSync(dir, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    const posixPath = prefix === '' ? entry.name : `${prefix}/${entry.name}`;
    if (entry.isDirectory()) {
      files.push(...listFiles(join(dir, entry.name), posixPath));
    } else if (
      entry.isFile() &&
      SCANNED_EXTENSIONS.some((extension) => entry.name.endsWith(extension))
    ) {
      files.push(posixPath);
    }
  }
  return files;
}

/** Every `input.json` below `dir`, as `{ name, path }` with the fixture directory as `name`. */
function listFixtureInputs(dir, prefix = '') {
  const entries = readdirSync(dir, { withFileTypes: true });
  const inputs = [];
  for (const entry of entries) {
    const posixPath = prefix === '' ? entry.name : `${prefix}/${entry.name}`;
    if (entry.isDirectory()) {
      inputs.push(...listFixtureInputs(join(dir, entry.name), posixPath));
    } else if (entry.isFile() && entry.name === 'input.json') {
      inputs.push({ name: prefix === '' ? '.' : prefix, path: join(dir, entry.name) });
    }
  }
  return inputs;
}

/**
 * Whether `child`, the value of `key` in a corpus document, is contract vocabulary rather than
 * fixture data. A `PROTOCOL_MEMBERS` slot always is. A member of `ENUM_MEMBERS` is vocabulary only
 * when its value is a string the contract enumerates for that member: any other value — a number,
 * or a word the fixture invented — is ordinary data and stays a needle (09.2.1-T7).
 */
function isProtocolValue(key, child) {
  if (PROTOCOL_MEMBERS.has(key)) {
    return true;
  }
  const enumerated = ENUM_MEMBERS.get(key);
  return enumerated !== undefined && typeof child === 'string' && enumerated.has(child);
}

/**
 * Adds every string leaf of `value` that is long enough to be a leak signal to `strings`
 * (value -> fixture name). A protocol slot is skipped together with its whole subtree, and an
 * enum member is skipped only for its enumerated words: their values are shared with production
 * code, not fixture data.
 */
function collectFixtureStrings(value, name, strings) {
  if (typeof value === 'string') {
    if (value.length >= MIN_FIXTURE_STRING_LENGTH && !strings.has(value)) {
      strings.set(value, name);
    }
    return;
  }
  if (Array.isArray(value)) {
    for (const item of value) {
      collectFixtureStrings(item, name, strings);
    }
    return;
  }
  if (value !== null && typeof value === 'object') {
    const permissionRow = isPermissionRow(value);
    for (const [key, child] of Object.entries(value)) {
      if (isProtocolValue(key, child) || (permissionRow && PERMISSION_HALVES.includes(key))) {
        continue;
      }
      collectFixtureStrings(child, name, strings);
    }
  }
}

/**
 * `value` with every non-ASCII code unit written as a lowercase `\uXXXX` escape, the form a
 * bundler or minifier emits when it escapes non-ASCII text (`đ` becomes `\u0111`, and an astral
 * character becomes its two surrogate escapes, exactly as `JSON.stringify` + such an escape pass
 * produces). A corpus string that survives into a bundle that way still leaks the fixture, so the
 * escaped form is searched as well — it never matches the raw string and vice versa. ASCII text
 * is returned unchanged, which is how a caller tells whether escaping applies.
 */
function escapeNonAscii(value) {
  let escaped = '';
  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index);
    escaped += code > 0x7f ? `\\u${code.toString(16).padStart(4, '0')}` : value[index];
  }
  return escaped;
}

/** The corpus string values a bundle must not contain, keyed by the fixture they came from. */
function fixtureStrings(dir) {
  if (!existsSync(dir)) {
    fail(`no ${dir} — the fixture corpus this gate cross-checks is missing.`);
  }
  const inputs = listFixtureInputs(dir);
  if (inputs.length === 0) {
    fail(
      `no input.json below ${dir}/ — the fixture corpus is empty, so this gate would pass ` +
        'vacuously.',
    );
  }
  const strings = new Map();
  for (const { name, path } of inputs) {
    let document;
    try {
      document = JSON.parse(readFileSync(path, 'utf8'));
    } catch (error) {
      fail(`cannot read ${shown(path)}: ${error instanceof Error ? error.message : String(error)}`);
    }
    collectFixtureStrings(document, name, strings);
  }
  return strings;
}

// The corpus is resolved first: a broken corpus is a gate failure in its own right, independent
// of whether a production build exists yet.
const fixtureValues = fixtureStrings(FIXTURE_DIR);

if (!existsSync(DIST_DIR)) {
  fail(`no ${DIST_DIR} — run \`ng build\` first (this check reads the production output).`);
}
const files = listFiles(DIST_DIR);
if (files.length === 0) {
  fail(`no scannable file in ${shown(DIST_DIR)}/ — the production output is missing.`);
}

const needles = [
  ...forbiddenMarkers.map((marker) => ({ needle: marker, source: 'the fixture backend code' })),
  ...[...fixtureValues].map(([value, name]) => ({
    needle: value,
    source: `fixtures/web-api/${name}/input.json`,
  })),
  // A second needle per non-ASCII fixture string: the escaped form a bundler emits (see
  // `escapeNonAscii`). ASCII-only values have no distinct escaped form and are skipped.
  ...[...fixtureValues]
    .filter(([value]) => escapeNonAscii(value) !== value)
    .map(([value, name]) => ({
      needle: escapeNonAscii(value),
      source: `fixtures/web-api/${name}/input.json (JSON-escaped)`,
    })),
];

const leaks = [];
let signInPresent = false;
for (const file of files) {
  const text = readFileSync(join(DIST_DIR, file), 'utf8');
  if (allowDevSignIn && SIGN_IN_PRESENCE_NEEDLES.some((needle) => text.includes(needle))) {
    signInPresent = true;
  }
  for (const { needle, source } of needles) {
    if (text.includes(needle)) {
      leaks.push(`${shown(join(DIST_DIR, file))}: ${JSON.stringify(needle)} (${source})`);
    }
  }
}

if (leaks.length > 0) {
  fail(
    `${leaks.length} fixture leak(s) in the production output:\n  ${leaks.join('\n  ')}\n` +
      'The dev fixture backend or its corpus data reached the build; check the `fixtures` ' +
      '`fileReplacements` in angular.json.',
  );
}

if (allowDevSignIn && !signInPresent) {
  fail(
    'the review output has no dev sign-in: neither "app-dev-sign-in" nor "/dev/session" is in ' +
      `${shown(DIST_DIR)}/. --allow-dev-sign-in expects the review build (angular.json ` +
      '`review`, which replaces login-extension.ts); the form is missing, so a reviewer could ' +
      'not sign in.',
  );
}

console.log(
  `fixture code: ${files.length} file(s) in ${shown(DIST_DIR)}/ scanned for ` +
    `${forbiddenMarkers.length} marker(s) and ${fixtureValues.size} fixture string(s)` +
    `${allowDevSignIn ? ' (dev sign-in allowed and present)' : ''}.`,
);
