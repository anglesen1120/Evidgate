// The CLI's variant normaliser against the shared corpus: `fixtures/variant-hash/vectors.json`
// is the same file the adapter's own suite and the server's variant suite assert, so a copy
// that drifts from `packages/adapter-playwright/src/variant.ts` fails here first.
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { describe, expect, it } from 'vitest';

import { manifestParams, normalizeVariant, type VariantProblem } from './variantHash.js';

interface Vector {
  readonly name: string;
  readonly params: unknown;
  readonly expect: { readonly hash?: string; readonly problems?: readonly VariantProblem[] };
}

const vectors = JSON.parse(
  readFileSync(resolve(import.meta.dirname, '../../../fixtures/variant-hash/vectors.json'), 'utf8'),
) as Vector[];

describe('variant normalisation parity', () => {
  it('the corpus covers accepted and refused parameter sets', () => {
    expect(vectors.length).toBeGreaterThan(30);
    expect(vectors.filter((vector) => vector.expect.hash !== undefined).length).toBeGreaterThan(10);
    expect(vectors.filter((vector) => vector.expect.problems !== undefined).length).toBeGreaterThan(
      10,
    );
  });

  it.each(vectors)('$name', ({ params, expect: expected }) => {
    const result = normalizeVariant(params);
    if (expected.hash !== undefined) {
      expect(result).toMatchObject({ ok: true, hash: expected.hash });
    } else {
      expect(result.ok).toBe(false);
      expect(result.ok ? [] : result.problems).toEqual(expected.problems);
    }
  });

  it('mixed_scalar_params_normalize_but_are_not_manifest_representable', () => {
    const result = normalizeVariant({ browser: 'Chromium ', retries: 2, headless: true });
    expect(result.ok).toBe(true);
    if (!result.ok) {
      return;
    }
    // Enum-like values are lower-cased, so the manifest value is the hashed one.
    expect({ ...result.params }).toEqual({ browser: 'chromium', retries: 2, headless: true });
    expect(manifestParams(result.params)).toBeUndefined();
  });

  it('refuses_values_the_manifest_format_cannot_carry', () => {
    expect(manifestParams({ browser: 'chromium' })).toEqual({ browser: 'chromium' });
    expect(manifestParams({ retries: 2 })).toBeUndefined();
    expect(manifestParams({ 'Browser.Layout': 'x' })).toBeUndefined();
    expect(manifestParams({ browser: 'x'.repeat(257) })).toBeUndefined();
  });
});
