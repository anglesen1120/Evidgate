// The reporter's `variant_hash` entry point against the shared corpus
// `fixtures/variant-hash/vectors.json`, which the CLI and the server's variant suite assert too.
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import { normalizeVariant } from './variant.js';

interface VariantVector {
  readonly name: string;
  readonly params: unknown;
  readonly expect:
    { readonly hash: string } | { readonly problems: readonly { path: string; reason: string }[] };
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
      }
    } else {
      expect(result).toEqual({ ok: false, problems: expected.problems });
    }
  });

  it('keeps the public result free of the normalised values the CLI reads', () => {
    const result = normalizeVariant({ browser: 'chromium' });

    expect(Object.keys(result).sort()).toEqual(['hash', 'ok', 'paramsJson']);
  });

  it('refuses an integer above 2^53 spelled as a JavaScript number, as the server refuses a long', () => {
    expect(normalizeVariant({ v: 1e20 })).toEqual({
      ok: false,
      problems: [{ path: '/params/v', reason: 'invalid_value' }],
    });
  });
});
