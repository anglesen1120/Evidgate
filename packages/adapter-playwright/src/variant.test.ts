// 04.3.2-T1.3 / AC3: `variant_hash` stability and cross-runtime parity. The corpus
// fixtures/variant-hash/vectors.json is asserted here and by the server's own suite
// (tests/Tenancy.IntegrationTests/VariantGoldenVectorTests.cs), so a regression on either
// side fails that side's suite.
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import { normalizeVariant } from './variant.js';

interface VariantProblem {
  readonly path: string;
  readonly reason: string;
}

interface VariantVector {
  readonly name: string;
  readonly params: unknown;
  readonly expect: { readonly hash: string } | { readonly problems: readonly VariantProblem[] };
}

const vectorsPath = resolve(
  dirname(fileURLToPath(import.meta.url)),
  '../../../fixtures/variant-hash/vectors.json',
);
const vectors = JSON.parse(readFileSync(vectorsPath, 'utf8')) as VariantVector[];

describe('variant_hash golden vectors', () => {
  it.each(vectors)('$name', ({ params, expect: expected }) => {
    const result = normalizeVariant(params);

    if ('hash' in expected) {
      expect(result.ok).toBe(true);
      if (result.ok) {
        expect(result.hash).toBe(expected.hash);
        expect(result.paramsJson).toBeTypeOf('string');
      }
    } else {
      expect(result).toEqual({ ok: false, problems: expected.problems });
    }
  });
});

describe('variant_hash properties', () => {
  it('is invariant under key order permutation', () => {
    const a = normalizeVariant({ browser: 'chromium', retries: 2, headless: true, locale: 'en' });
    const b = normalizeVariant({ locale: 'en', headless: true, retries: 2, browser: 'chromium' });

    expect(a.ok && b.ok).toBe(true);
    if (a.ok && b.ok) expect(a.hash).toBe(b.hash);
  });

  it('is idempotent: re-normalising paramsJson gives the same hash', () => {
    const first = normalizeVariant({
      browser: ' Chromium ',
      retries: 2,
      note: 'nightly',
      locale: ['EN', 'vi'],
    });

    expect(first.ok).toBe(true);
    if (!first.ok) return;

    const second = normalizeVariant(JSON.parse(first.paramsJson));
    expect(second.ok).toBe(true);
    if (second.ok) {
      expect(second.hash).toBe(first.hash);
      expect(second.paramsJson).toBe(first.paramsJson);
    }
  });

  it('does not pollute Object.prototype and drops a __proto__ key like the server', () => {
    const polluted = JSON.parse('{"__proto__":{"x":1},"a":2}') as Record<string, unknown>;

    const result = normalizeVariant(polluted);

    expect(({} as Record<string, unknown>).x).toBeUndefined();
    expect(Object.prototype).not.toHaveProperty('x');
    // `__proto__` starts with `_`, so the server drops it; only `a` identifies the variant.
    expect(result).toEqual(normalizeVariant({ a: 2 }));
  });

  it('keeps a constructor key as an ordinary own property', () => {
    const parsed = JSON.parse('{"constructor":"x"}') as Record<string, unknown>;

    expect(normalizeVariant(parsed)).toEqual(normalizeVariant({ constructor: 'x' }));
  });
});
