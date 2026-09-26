import { describe, expect, it } from 'vitest';

import * as entry from './index.js';

// 01.6.1-T1.2: placeholder smoke spec so the workspace runs under the shared Vitest
// config; the generated client and its real specs arrive with 03. It still fails for a
// broken package.json ("type"/"main"), a missing entry file, or an entry that Vite
// transpiled to CommonJS instead of ESM.
describe('@evidgate/sdk entry point', () => {
  it('loads as an ES module namespace', () => {
    expect(Object.prototype.toString.call(entry)).toBe('[object Module]');
  });
});
