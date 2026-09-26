import { describe, expect, it } from 'vitest';

import * as entry from './index.js';

// 01.6.1-T1.2: placeholder smoke spec for the CLI workspace; the canonical-hashing specs
// arrive with 01.3.2-T4 as separate `src/*.test.ts` files. It fails for a broken
// package.json ("type"/"main"), a missing entry file, or a CommonJS entry.
describe('@evidgate/cli entry point', () => {
  it('loads as an ES module namespace', () => {
    expect(Object.prototype.toString.call(entry)).toBe('[object Module]');
  });
});
