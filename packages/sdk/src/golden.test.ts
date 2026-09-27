// 01.3.2-T1.4: golden-file check on one representative generated client method.
//
// `golden/exchangeCiToken.golden` is the byte-exact committed snapshot of the
// `exchangeCiToken` block inside `gen/sdk.gen.ts` — the first operation in the spec and
// the only Idempotency-Key-exempt one, which is why it is the representative sample.
// Any generator upgrade or spec edit that changes how that method is emitted fails here
// with a diff, instead of drifting silently until the byte-level drift gate catches it.
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

const thisDir = dirname(fileURLToPath(import.meta.url));

/**
 * Extracts the whole `export const <name> … = …` arrow-function block from a generated
 * source file — from `export const <name>` through the matching `});` at column 0 (the
 * emit shape of every operation in `gen/sdk.gen.ts`). Returns null when the method is
 * absent or its shape changed so much the block no longer parses, which the spec also
 * treats as drift.
 */
function extractExportedConstBlock(source: string, name: string): string | null {
  const match = new RegExp(`export const ${name} = [\\s\\S]*?^\\}\\);`, 'm').exec(source);
  return match?.[0] ?? null;
}

describe('golden: generated exchangeCiToken', () => {
  it('is byte-identical to the committed snapshot', () => {
    const generated = readFileSync(join(thisDir, '../gen/sdk.gen.ts'), 'utf8');
    // The golden file keeps exactly the exported block, without a trailing newline.
    const expected = readFileSync(join(thisDir, 'golden/exchangeCiToken.golden'), 'utf8').replace(
      /\n$/,
      '',
    );
    const actual = extractExportedConstBlock(generated, 'exchangeCiToken');
    expect(actual, 'extractExportedConstBlock must find the emitted method').not.toBeNull();
    expect(actual).toBe(expected);
  });
});
