// 09.1.1-T6.1 — AC6 proofs for scripts/check-i18n-keys.mjs: one test per error class (a)-(h),
// the two-locale case ("either locale" fails), the CLI exit codes, and the real repo catalogs.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { after, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { SOURCE_CATALOG, checkI18nKeys } from './check-i18n-keys.mjs';

const WEB_ROOT = fileURLToPath(new URL('..', import.meta.url));
const SCRIPT = fileURLToPath(new URL('./check-i18n-keys.mjs', import.meta.url));
const VI_CATALOG = 'src/locale/messages.vi.xlf';
const FR_CATALOG = 'src/locale/messages.fr.xlf';
const DEFAULT_LOCALES = { vi: { translation: VI_CATALOG } };

const SOURCE_UNITS = [
  { id: 'app.title', source: 'Evidgate' },
  { id: 'app.greeting', source: 'Hello <x id="INTERPOLATION" equiv-text="{{ name }}"/>' },
];
const VI_UNITS = [
  { id: 'app.title', source: 'Evidgate', target: 'Evidgate' },
  {
    id: 'app.greeting',
    source: 'Hello <x id="INTERPOLATION" equiv-text="{{ name }}"/>',
    target: 'Xin chào <x id="INTERPOLATION"/>',
  },
];

const tempDirs = [];
after(() => {
  for (const dir of tempDirs) rmSync(dir, { recursive: true, force: true });
});

function tempDir() {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-i18n-keys-'));
  tempDirs.push(dir);
  return dir;
}

/** XLIFF 1.2 exactly as `ng extract-i18n` writes it; `target` is omitted when undefined. */
function transUnit({ id, source, target }) {
  const lines = [
    `      <trans-unit id="${id}" datatype="html">`,
    `        <source>${source}</source>`,
  ];
  if (target !== undefined) lines.push(`        <target>${target}</target>`);
  lines.push('      </trans-unit>');
  return lines.join('\n');
}

function catalog(units, { sourceLanguage = 'en' } = {}) {
  const body = units.map(transUnit).join('\n');
  return `<?xml version="1.0" encoding="UTF-8" ?>
<xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2">
  <file source-language="${sourceLanguage}" datatype="plaintext" original="ng2.template">
    <body>
${body}
    </body>
  </file>
</xliff>
`;
}

function writeCatalog(dir, path, units, options) {
  const file = join(dir, path);
  mkdirSync(dirname(file), { recursive: true });
  writeFileSync(file, catalog(units, options), 'utf8');
  return file;
}

/** A catalog written verbatim, for the malformed shapes `transUnit()` never produces. */
function rawCatalog(body) {
  return `<?xml version="1.0" encoding="UTF-8" ?>
<xliff version="1.2" xmlns="urn:oasis:names:tc:xliff:document:1.2">
  <file source-language="en" datatype="plaintext" original="ng2.template">
    <body>
${body}
    </body>
  </file>
</xliff>
`;
}

function writeRaw(dir, path, content) {
  const file = join(dir, path);
  mkdirSync(dirname(file), { recursive: true });
  writeFileSync(file, content, 'utf8');
  return file;
}

function writeAngularJson(dir, locales) {
  const i18n = { sourceLocale: 'en' };
  if (locales !== null) i18n.locales = locales;
  writeFileSync(
    join(dir, 'angular.json'),
    JSON.stringify({ projects: { web: { i18n } } }, null, 2),
    'utf8',
  );
}

/** A temporary apps/web: `source` (undefined = do not write it), `locales`, `files`. */
function fixture({ source, locales = DEFAULT_LOCALES, files = {} } = {}) {
  const dir = tempDir();
  writeAngularJson(dir, locales);
  if (source !== undefined) writeCatalog(dir, SOURCE_CATALOG, source);
  for (const [path, units] of Object.entries(files)) writeCatalog(dir, path, units);
  return dir;
}

function run(dir, options = {}) {
  return checkI18nKeys({ angularJsonPath: join(dir, 'angular.json'), ...options });
}

function text(result) {
  return result.errors.join('\n');
}

test('a complete en/vi pair passes and reports the source unit count', () => {
  const dir = fixture({ source: SOURCE_UNITS, files: { [VI_CATALOG]: VI_UNITS } });
  const result = run(dir);
  assert.deepEqual(result.errors, []);
  assert.deepEqual(result.locales, ['vi']);
  assert.equal(result.keyCount, 2);
});

test('(a) a source id missing from the locale catalog fails, naming file and id', () => {
  const dir = fixture({ source: SOURCE_UNITS, files: { [VI_CATALOG]: [VI_UNITS[0]] } });
  const result = run(dir);
  assert.equal(result.errors.length, 1, text(result));
  assert.match(text(result), /messages\.vi\.xlf/);
  assert.match(text(result), /missing id "app\.greeting"/);
  assert.match(text(result), /locale "vi"/);
});

test('(b) a locale id absent from the source catalog fails as a stale key', () => {
  const stale = { id: 'app.legacy', source: 'Old', target: 'Cũ' };
  const dir = fixture({ source: SOURCE_UNITS, files: { [VI_CATALOG]: [...VI_UNITS, stale] } });
  const result = run(dir);
  assert.equal(result.errors.length, 1, text(result));
  assert.match(text(result), /messages\.vi\.xlf: id "app\.legacy" is not in the source catalog/);
  assert.match(text(result), /stale key/);
});

test('(c) an empty target and a catalog without any target both fail', () => {
  const empty = fixture({
    source: SOURCE_UNITS,
    files: { [VI_CATALOG]: [{ id: 'app.title', source: 'Evidgate', target: '   ' }, VI_UNITS[1]] },
  });
  const emptyResult = run(empty);
  assert.match(text(emptyResult), /messages\.vi\.xlf: id "app\.title" has an empty <target>/);

  const absent = fixture({
    source: SOURCE_UNITS,
    files: { [VI_CATALOG]: [{ id: 'app.title', source: 'Evidgate' }, VI_UNITS[1]] },
  });
  const absentResult = run(absent);
  assert.match(text(absentResult), /messages\.vi\.xlf: id "app\.title" has no <target> element/);
});

test('(d) a duplicate id inside one catalog fails', () => {
  const dir = fixture({
    source: SOURCE_UNITS,
    files: { [VI_CATALOG]: [...VI_UNITS, { ...VI_UNITS[0], target: 'Evidgate lần hai' }] },
  });
  const result = run(dir);
  assert.match(text(result), /messages\.vi\.xlf: duplicate id "app\.title"/);
});

test('(e) an id outside the @@area.name convention fails', () => {
  const dir = fixture({
    source: SOURCE_UNITS,
    files: {
      [VI_CATALOG]: [...VI_UNITS, { id: 'App_Title', source: 'X', target: 'Y' }],
    },
  });
  const result = run(dir);
  assert.match(text(result), /id "App_Title" does not follow the @@area\.name convention/);
  assert.match(text(result), /messages\.vi\.xlf/);
});

test('(f) placeholder sets that differ from the source fail', () => {
  const dir = fixture({
    source: SOURCE_UNITS,
    files: {
      [VI_CATALOG]: [
        VI_UNITS[0],
        {
          id: 'app.greeting',
          source: 'Hello <x id="INTERPOLATION" equiv-text="{{ name }}"/>',
          target: 'Xin chào <x id="INTERPOLATION"/><x id="PH"/>',
        },
      ],
    },
  });
  const result = run(dir);
  assert.match(text(result), /id "app\.greeting" placeholder sets differ from the source/);
  assert.match(text(result), /source: INTERPOLATION; target: INTERPOLATION, PH/);
});

test('(g) a missing translation file, a missing and an empty source catalog all fail', () => {
  const noTranslation = run(fixture({ source: SOURCE_UNITS }));
  assert.match(
    text(noTranslation),
    /translation file src\/locale\/messages\.vi\.xlf \(locale "vi"\) does not exist/,
  );

  const noSource = run(fixture({ files: { [VI_CATALOG]: VI_UNITS } }));
  assert.match(text(noSource), /source catalog src\/locale\/messages\.xlf does not exist/);
  assert.equal(noSource.keyCount, 0);

  const emptySource = run(fixture({ source: [], files: { [VI_CATALOG]: VI_UNITS } }));
  assert.match(text(emptySource), /has no <trans-unit> entries/);
  assert.match(text(emptySource), /an empty check must never pass/);
  assert.equal(emptySource.keyCount, 0);

  const notConfigured = run(fixture({ source: SOURCE_UNITS, locales: { vi: {} } }));
  assert.match(text(notConfigured), /locales\.vi\.translation is missing/);
});

test('(h) an angular.json that declares no locales fails', () => {
  const dir = fixture({ source: SOURCE_UNITS, locales: null });
  const result = run(dir);
  assert.match(text(result), /angular\.json: projects\.web\.i18n\.locales is missing/);
  assert.deepEqual(result.locales, []);
});

test('a self-closing <trans-unit/> is reported and never swallows the next unit', () => {
  const source = [
    { id: 'a.one', source: 'One' },
    { id: 'a.two', source: 'Two' },
    { id: 'a.three', source: 'Three' },
  ];
  const units = (third) =>
    rawCatalog(
      [
        transUnit({ id: 'a.one', source: 'One', target: 'Một' }),
        '      <trans-unit id="a.two"/>',
        third,
      ].join('\n'),
    );

  const dir = fixture({ source });
  writeRaw(dir, VI_CATALOG, units(transUnit({ id: 'a.three', source: 'Three', target: 'Ba' })));
  const result = run(dir);
  assert.match(text(result), /messages\.vi\.xlf: <trans-unit id="a\.two"\/> is self-closing/);
  assert.doesNotMatch(text(result), /missing id "a\.three"/);
  // The self-closing unit declares no content, so the key it names cannot be claimed as present.
  assert.match(text(result), /\(src\/locale\/messages\.vi\.xlf\): missing id "a\.two"/);
  assert.equal(result.keyCount, 3);

  const blankNext = fixture({ source });
  const blankThird = transUnit({ id: 'a.three', source: 'Three', target: ' ' });
  writeRaw(blankNext, VI_CATALOG, units(blankThird));
  assert.match(text(run(blankNext)), /id "a\.three" has an empty <target>/);
});

test('<target> inside <alt-trans> is not the unit target, and a real one still counts', () => {
  const withBody = (lines) => {
    const body = ['      <trans-unit id="app.greeting">', ...lines, '      </trans-unit>'];
    return rawCatalog([transUnit(VI_UNITS[0]), body.join('\n')].join('\n'));
  };

  const altOnly = fixture({ source: SOURCE_UNITS });
  writeRaw(
    altOnly,
    VI_CATALOG,
    withBody([
      '        <source>Hello <x id="INTERPOLATION" equiv-text="{{ name }}"/></source>',
      '        <alt-trans>',
      '          <target>Xin chào <x id="INTERPOLATION"/></target>',
      '        </alt-trans>',
    ]),
  );
  const altOnlyResult = run(altOnly);
  assert.equal(altOnlyResult.errors.length, 1, text(altOnlyResult));
  assert.match(
    text(altOnlyResult),
    /messages\.vi\.xlf: id "app\.greeting" has no <target> element/,
  );

  const withReal = fixture({ source: SOURCE_UNITS });
  writeRaw(
    withReal,
    VI_CATALOG,
    withBody([
      '        <source>Hello <x id="INTERPOLATION" equiv-text="{{ name }}"/></source>',
      '        <alt-trans>',
      '          <target>Chào <x id="PH"/></target>',
      '        </alt-trans>',
      '        <target>Xin chào <x id="INTERPOLATION"/></target>',
    ]),
  );
  assert.deepEqual(run(withReal).errors, [], 'a real target after an <alt-trans> still counts');
});

test('an unclosed <trans-unit> is reported instead of under-counting keys', () => {
  const brokenUnit = rawCatalog(
    [
      '      <trans-unit id="app.title" datatype="html">',
      '        <source>Evidgate</source>',
      '        <target>Evidgate</target>',
    ].join('\n'),
  );

  const locale = fixture({ source: SOURCE_UNITS });
  writeRaw(locale, VI_CATALOG, brokenUnit);
  const localeResult = run(locale);
  assert.match(
    text(localeResult),
    /messages\.vi\.xlf: <trans-unit id="app\.title"> is never closed/,
  );
  assert.match(text(localeResult), /missing <\/trans-unit>/);

  const sourceCatalog = fixture();
  writeRaw(sourceCatalog, SOURCE_CATALOG, brokenUnit);
  assert.match(
    text(run(sourceCatalog)),
    /messages\.xlf: <trans-unit id="app\.title"> is never closed/,
  );
});

test('40,000 unclosed <trans-unit> entries are scanned in under a second', () => {
  const body = Array.from(
    { length: 40_000 },
    (_unused, index) =>
      `      <trans-unit id="perf.key${index}" datatype="html">\n` +
      `        <source>Key ${index}</source>`,
  ).join('\n');
  const dir = fixture({ source: SOURCE_UNITS });
  writeRaw(dir, VI_CATALOG, rawCatalog(body));

  const started = performance.now();
  const result = run(dir);
  const elapsed = performance.now() - started;
  assert.match(text(result), /is never closed/);
  assert.ok(elapsed < 1000, `parsing took ${elapsed.toFixed(0)} ms, expected under 1000 ms`);
});

test('a second locale missing one key fails naming that locale', () => {
  const dir = fixture({
    source: SOURCE_UNITS,
    locales: {
      fr: { translation: FR_CATALOG },
      vi: { translation: VI_CATALOG },
    },
    files: {
      [FR_CATALOG]: [{ id: 'app.title', source: 'Evidgate', target: 'Evidgate' }],
      [VI_CATALOG]: VI_UNITS,
    },
  });
  const result = run(dir);
  assert.deepEqual(result.locales, ['fr', 'vi']);
  assert.equal(result.errors.length, 1, text(result));
  assert.match(text(result), /locale "fr" \(src\/locale\/messages\.fr\.xlf\): missing id/);
  assert.match(text(result), /"app\.greeting"/);
});

test('XML entities decode the same on both sides', () => {
  const markup = 'Fish &amp; Chips &lt;b&gt;bold&lt;/b&gt; &quot;q&quot; &apos;s&apos;';
  const dir = fixture({
    source: [{ id: 'app.entities', source: markup }],
    files: { [VI_CATALOG]: [{ id: 'app.entities', source: markup, target: markup }] },
  });
  const result = run(dir);
  assert.deepEqual(result.errors, []);
  assert.equal(result.keyCount, 1);
});

test('a locale whose translation is an array checks every file', () => {
  const extra = 'src/locale/messages.vi-extra.xlf';
  const split = fixture({
    source: SOURCE_UNITS,
    locales: { vi: { translation: [VI_CATALOG, extra] } },
    files: { [VI_CATALOG]: [VI_UNITS[0]], [extra]: [VI_UNITS[1]] },
  });
  const splitResult = run(split);
  assert.deepEqual(splitResult.errors, []);

  const missingOne = fixture({
    source: SOURCE_UNITS,
    locales: { vi: { translation: [VI_CATALOG, extra] } },
    files: { [VI_CATALOG]: VI_UNITS },
  });
  assert.match(
    text(run(missingOne)),
    /translation file src\/locale\/messages\.vi-extra\.xlf \(locale "vi"\) does not exist/,
  );
});

test('the CLI exits 0 on a clean fixture and 1 on a broken one', () => {
  const clean = fixture({ source: SOURCE_UNITS, files: { [VI_CATALOG]: VI_UNITS } });
  const cleanRun = spawnSync(
    process.execPath,
    [SCRIPT, '--angular-json', join(clean, 'angular.json')],
    { encoding: 'utf8' },
  );
  assert.equal(cleanRun.status, 0, cleanRun.stderr);
  assert.match(cleanRun.stdout, /2 source key\(s\), 1 locale\(s\): vi/);
  assert.match(cleanRun.stdout, /every catalog is complete/);
  assert.equal(cleanRun.stderr, '');

  const relativeRun = spawnSync(
    process.execPath,
    [SCRIPT, '--angular-json', 'angular.json', '--root', clean],
    { encoding: 'utf8' },
  );
  assert.equal(relativeRun.status, 0, relativeRun.stderr);

  const broken = fixture({ source: SOURCE_UNITS, files: { [VI_CATALOG]: [VI_UNITS[0]] } });
  const brokenRun = spawnSync(
    process.execPath,
    [SCRIPT, '--angular-json', join(broken, 'angular.json')],
    { encoding: 'utf8' },
  );
  assert.equal(brokenRun.status, 1);
  assert.equal(brokenRun.stderr.trim().split('\n').length, 1, brokenRun.stderr);
  assert.match(brokenRun.stderr, /missing id "app\.greeting"/);
  assert.match(brokenRun.stdout, /1 error\(s\)/);
});

test('the real repo catalogs are complete', () => {
  const result = checkI18nKeys({ angularJsonPath: join(WEB_ROOT, 'angular.json') });
  assert.deepEqual(result.errors, [], text(result));
  assert.ok(result.keyCount >= 1, `expected at least one key, got ${result.keyCount}`);
  assert.ok(result.locales.includes('vi'), `expected the vi locale, got ${result.locales}`);
});
