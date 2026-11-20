// Edge cases of the shared variant normaliser that fixtures/variant-hash/vectors.json does not
// carry, because the corpus holds only what both sides can express in JSON text. Each refusal row
// records what this normaliser answers and what VariantNormalizer.Normalize (the server) answers
// for the same input, so a parity gap is visible in the table; the acceptance tests
// pin this normaliser only. Rows marked `ts-only` have no JSON spelling the server could be
// handed. Row prefixes: a secret-shaped values, b fold order, c numbers, d surrogates, e arrays,
// f values only a JavaScript caller can build.
import { describe, expect, it } from 'vitest';

import { normalizeVariant } from './normalize.js';
import { valueLooksSecret } from './secretShape.js';

type Problems = readonly { readonly path: string; readonly reason: string }[];

const TOKEN = 'ghp_abcdefghijklmnopqrstuvwxyz0123456789';
const PAIRED = '\u{1F9EA}';

interface RefusalRow {
  readonly name: string;
  readonly params: unknown;
  readonly problems: Problems;
  /** The server's answer for the same input; `same` or the difference. */
  readonly server: string;
  /** Text that must appear in no problem: the offending value or key. */
  readonly offending: readonly string[];
}

const rows: readonly RefusalRow[] = [
  {
    name: 'a: secret-shaped string as an array element names the element index',
    params: { list: ['ok', TOKEN] },
    problems: [{ path: '/params/list/1', reason: 'secret_value' }],
    server: 'same',
    offending: [TOKEN],
  },
  {
    name: 'a: secret-shaped string in an array under an enum-like key is refused before folding',
    params: { browser: ['chromium', `  ${TOKEN} `] },
    problems: [{ path: '/params/browser/1', reason: 'secret_value' }],
    server: 'same',
    offending: [TOKEN],
  },
  {
    name: 'a: every secret-shaped element is reported, clean ones are not',
    params: { list: [TOKEN, 'ok', TOKEN] },
    problems: [
      { path: '/params/list/0', reason: 'secret_value' },
      { path: '/params/list/2', reason: 'secret_value' },
    ],
    server: 'same',
    offending: [TOKEN],
  },
  {
    name: 'b: padded secret-shaped value under an enum-like key is checked before the trim',
    params: { mode: `  ${TOKEN}  ` },
    problems: [{ path: '/params/mode', reason: 'secret_value' }],
    server: 'same (StringValue scans before it folds)',
    offending: [TOKEN],
  },
  {
    name: 'b: upper-case 40-character token under an enum-like key is refused by the entropy rule',
    params: { mode: `  ${TOKEN.toUpperCase()} ` },
    problems: [{ path: '/params/mode', reason: 'secret_value' }],
    server: 'same',
    offending: [TOKEN, TOKEN.toUpperCase()],
  },
  {
    name: 'c: NaN scalar is invalid_value',
    params: { v: Number.NaN },
    problems: [{ path: '/params/v', reason: 'invalid_value' }],
    server: 'not expressible in JSON text; 1e999 is invalid_value',
    offending: ['NaN'],
  },
  {
    name: 'c: Infinity scalar is invalid_value',
    params: { v: Number.POSITIVE_INFINITY },
    problems: [{ path: '/params/v', reason: 'invalid_value' }],
    server: 'same as 1e999',
    offending: ['Infinity'],
  },
  {
    name: 'c: -Infinity scalar is invalid_value',
    params: { v: Number.NEGATIVE_INFINITY },
    problems: [{ path: '/params/v', reason: 'invalid_value' }],
    server: 'same as -1e999',
    offending: ['Infinity'],
  },
  {
    name: 'c: 2^53 scalar is invalid_value',
    params: { v: 2 ** 53 },
    problems: [{ path: '/params/v', reason: 'invalid_value' }],
    server: 'same for digits 9007199254740992',
    offending: ['9007199254740992'],
  },
  {
    name: 'c: -2^53 scalar is invalid_value',
    params: { v: -(2 ** 53) },
    problems: [{ path: '/params/v', reason: 'invalid_value' }],
    server: 'same for digits -9007199254740992',
    offending: ['9007199254740992'],
  },
  {
    name: 'c: NaN and Infinity as array elements are indexed invalid_value',
    params: { v: [1, Number.NaN, Number.POSITIVE_INFINITY] },
    problems: [
      { path: '/params/v/1', reason: 'invalid_value' },
      { path: '/params/v/2', reason: 'invalid_value' },
    ],
    server: 'same index paths for 1e999 elements',
    offending: ['NaN', 'Infinity'],
  },
  {
    name: 'c: 2^53 as an array element is indexed invalid_value',
    params: { v: [2 ** 53] },
    problems: [{ path: '/params/v/0', reason: 'invalid_value' }],
    server: 'same',
    offending: ['9007199254740992'],
  },
  {
    name: 'd: lone surrogate in a value is invalid_value',
    params: { v: 'a\ud800b' },
    problems: [{ path: '/params/v', reason: 'invalid_value' }],
    server: 'same (Scalar catches the GetString failure)',
    offending: ['\ud800'],
  },
  {
    name: 'd: lone low surrogate in an array element is indexed invalid_value',
    params: { v: ['a', '\udc00'] },
    problems: [{ path: '/params/v/1', reason: 'invalid_value' }],
    server: 'same',
    offending: ['\udc00'],
  },
  {
    name: 'd: lone surrogate in a key is invalid_key by position',
    params: { 'zzkey\ud800': 1 },
    problems: [{ path: '/params/#1', reason: 'invalid_key' }],
    server: 'same',
    offending: ['\ud800', 'zzkey'],
  },
  {
    name: 'e: 33 items is too_many_items at the array path',
    params: { v: Array.from({ length: 33 }, (_, i) => i) },
    problems: [{ path: '/params/v', reason: 'too_many_items' }],
    server: 'same',
    offending: [],
  },
  {
    name: 'e: nested array element is not_scalar',
    params: { v: [[TOKEN]] },
    problems: [{ path: '/params/v/0', reason: 'not_scalar' }],
    server: 'same',
    offending: [TOKEN],
  },
  {
    name: 'e: object element is not_scalar',
    params: { v: [{ a: TOKEN }] },
    problems: [{ path: '/params/v/0', reason: 'not_scalar' }],
    server: 'same',
    offending: [TOKEN],
  },
  {
    name: 'e: mixed array reports secret, containers and number problems in element order',
    params: { v: [TOKEN, [1], { a: 1 }, Number.NaN] },
    problems: [
      { path: '/params/v/0', reason: 'secret_value' },
      { path: '/params/v/1', reason: 'not_scalar' },
      { path: '/params/v/2', reason: 'not_scalar' },
      { path: '/params/v/3', reason: 'invalid_value' },
    ],
    server: 'same for the JSON-expressible elements',
    offending: [TOKEN],
  },
  {
    name: 'f (ts-only): sparse array hole is not_scalar at its index',
    params: { v: [1, , 2] },
    problems: [{ path: '/params/v/1', reason: 'not_scalar' }],
    server: 'no analogue: JSON has no holes',
    offending: [],
  },
  {
    name: 'f (ts-only): undefined array element is not_scalar at its index',
    params: { v: [undefined, 1] },
    problems: [{ path: '/params/v/0', reason: 'not_scalar' }],
    server: 'no analogue: JSON has no undefined',
    offending: [],
  },
  {
    name: 'b: secret-shaped prefix padded with whitespace under an enum-like key is refused, not folded into an accepted value',
    params: { browser: ['  AKIAaaaaaaaa  '] },
    problems: [{ path: '/params/browser/0', reason: 'secret_value' }],
    server: 'same',
    offending: ['AKIAaaaaaaaa'],
  },
  {
    name: 'b: 257 characters padded to be trimmed to 256 under an enum-like key is refused, length is checked before the trim',
    params: { mode: ` ${'x'.repeat(256)}` },
    problems: [{ path: '/params/mode', reason: 'invalid_value' }],
    server: 'same',
    offending: ['xxxxxxxx'],
  },
  {
    name: 'c: -Infinity as an array element is indexed invalid_value',
    params: { v: [Number.NEGATIVE_INFINITY] },
    problems: [{ path: '/params/v/0', reason: 'invalid_value' }],
    server: 'same as -1e999',
    offending: ['Infinity'],
  },
  {
    name: 'c: an integer well above 2^53 as an array element is indexed invalid_value',
    params: { v: [2 ** 60] },
    problems: [{ path: '/params/v/0', reason: 'invalid_value' }],
    server: 'same for the digits of 2^60',
    offending: ['1152921504606847000', '11529215046068'],
  },
];

describe('variant_hash edge refusals', () => {
  it.each(rows)('$name (server: $server)', ({ params, problems }) => {
    expect(normalizeVariant(params)).toEqual({ ok: false, problems });
  });

  it('never puts the offending value or key text in any problem path or reason', () => {
    const checked = rows.flatMap((row) => {
      const result = normalizeVariant(row.params);
      expect(result.ok).toBe(false);
      const text = result.ok ? '' : result.problems.map((p) => `${p.path}\n${p.reason}`).join('\n');
      for (const offending of row.offending) {
        expect(text, row.name).not.toContain(offending);
      }
      return row.offending;
    });

    expect(checked.length).toBeGreaterThan(0);
    for (const row of rows) {
      if (row.problems.some((p) => p.reason === 'secret_value')) {
        expect(row.offending.length, row.name).toBeGreaterThan(0);
      }
    }
  });
});

describe('variant_hash edge acceptances', () => {
  it('c: -0 scalar and array element hash as 0 (reporter cannot see -0.0, normalize.ts header lines 28-30)', () => {
    const scalar = normalizeVariant({ v: -0 });
    const element = normalizeVariant({ v: [-0] });

    expect(scalar.ok && scalar.paramsJson).toBe('{"v":0}');
    expect(element.ok && element.paramsJson).toBe('{"v":[0]}');
  });

  it('c: largest safe integers are accepted as scalar and array element', () => {
    const max = 2 ** 53 - 1;

    const scalar = normalizeVariant({ v: max });
    const element = normalizeVariant({ v: [-max] });

    expect(scalar.ok && scalar.paramsJson).toBe('{"v":9007199254740991}');
    expect(element.ok && element.paramsJson).toBe('{"v":[-9007199254740991]}');
  });

  it('c: 1e21 is accepted as 1e+21 (reporter cannot see full digits, normalize.ts header lines 26-27)', () => {
    const result = normalizeVariant({ v: 1e21 });

    expect(result.ok && result.paramsJson).toBe('{"v":1e+21}');
  });

  it('d: a well-formed surrogate pair in a value and in an array element is accepted', () => {
    const result = normalizeVariant({ name: `a${PAIRED}`, list: [PAIRED] });

    expect(result.ok).toBe(true);
    expect(result.ok && result.params).toEqual({ name: `a${PAIRED}`, list: [PAIRED] });
  });

  it('e: an array of exactly 32 items is accepted', () => {
    const items = Array.from({ length: 32 }, (_, i) => i);

    const result = normalizeVariant({ v: items });

    expect(result.ok && result.paramsJson).toBe(`{"v":${JSON.stringify(items)}}`);
  });

  it('e: an empty array is kept as an empty array', () => {
    const result = normalizeVariant({ v: [] });

    expect(result.ok && result.paramsJson).toBe('{"v":[]}');
  });

  it('e: an array of nulls normalises to an empty array and nulls are dropped between items', () => {
    const nulls = normalizeVariant({ v: [null, null] });
    const between = normalizeVariant({ v: [1, null, 2] });

    expect(nulls.ok && nulls.paramsJson).toBe('{"v":[]}');
    expect(between.ok && between.paramsJson).toBe('{"v":[1,2]}');
  });

  it('f (ts-only): extra own properties on an array leave no trace and the result array is fresh', () => {
    const array = Object.assign([1], { extraname: TOKEN });

    const result = normalizeVariant({ v: array });

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    const returned = result.params['v'] as unknown[];
    expect(returned).not.toBe(array);
    expect(Object.keys(returned)).toEqual(['0']);
    expect(result.paramsJson).toBe('{"v":[1]}');
    for (const text of [result.paramsJson, result.hash, JSON.stringify(result.params)]) {
      expect(text).not.toContain('extraname');
      expect(text).not.toContain(TOKEN);
    }
  });
});

describe('variant_hash: the stored form never looks like a secret', () => {
  // The secret scan covers the text as sent and the text as stored (enum-like keys fold it), so an
  // upper-cased token prefix cannot reach storage as a lower-cased, token-looking value.
  it.each([
    'GHP_AAAAAAAA',
    'GITHUB_PAT_AAAAAAAA',
    'XOXB-AAAAAAAA',
    '  GHP_AAAAAAAA  ',
    'SK-LIVE-CHANNEL',
    'NPM_REGISTRY',
  ])('refuses %j under an enum-like key', (input) => {
    expect(normalizeVariant({ mode: input })).toEqual({
      ok: false,
      problems: [{ path: '/params/mode', reason: 'secret_value' }],
    });
  });

  it('refuses a folded token prefix inside an enum-like array', () => {
    expect(normalizeVariant({ locale: ['en', 'GHP_AAAAAAAA'] })).toEqual({
      ok: false,
      problems: [{ path: '/params/locale/1', reason: 'secret_value' }],
    });
  });

  it('names neither the value nor the key in a refusal', () => {
    const result = normalizeVariant({ mode: 'GHP_ZZECHOZZ' });
    const text = result.ok ? '' : result.problems.map((p) => `${p.path} ${p.reason}`).join(' ');

    expect(result.ok).toBe(false);
    expect(text.toLowerCase()).not.toContain('echo');
  });

  it.each([
    ['GHP_MODE', 'ghp_mode'],
    ['ghost_town_mode', 'ghost_town_mode'],
    ['PYPI-TEST', 'pypi-test'],
    ['EVG_STAGING', 'evg_staging'],
  ])('accepts %s under an enum-like key and stores %s', (input, stored) => {
    const result = normalizeVariant({ env: input });

    expect(result.ok && result.params).toEqual({ env: stored });
    expect(valueLooksSecret(stored)).toBe(false);
  });

  it.each(['GHP_AAAAAAAA', 'GITHUB_PAT_AAAAAAAA', 'XOXB-AAAAAAAA'])(
    'accepts %s unchanged under a key that is not enum-like (stored as sent)',
    (input) => {
      const result = normalizeVariant({ other: input });

      expect(result.ok && result.params).toEqual({ other: input });
    },
  );

  it('keeps refusing the same upper-cased token at 24+ characters', () => {
    const result = normalizeVariant({ mode: TOKEN.toUpperCase() });

    expect(result).toEqual({
      ok: false,
      problems: [{ path: '/params/mode', reason: 'secret_value' }],
    });
  });

  it.each([
    ['NBSP padding is trimmed before the scan', '\u00a0GHP_AAAAAAAA\u00a0', 'secret_value'],
    ['U+0085 is refused before any fold', '\u0085GHP_AAAAAAAA', 'invalid_value'],
    ['U+FEFF is refused before any fold', '\ufeffGHP_AAAAAAAA', 'invalid_value'],
    ['the Kelvin sign folds to k', 'S\u212a-AAAAAAAA', 'secret_value'],
  ])('%s', (_name, input, reason) => {
    expect(normalizeVariant({ mode: input })).toEqual({
      ok: false,
      problems: [{ path: '/params/mode', reason }],
    });
  });
});
