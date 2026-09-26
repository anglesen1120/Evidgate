import { describe, expect, it } from 'vitest';

import * as entry from './index.js';

// 01.6.1-T1.2: placeholder smoke spec until the Angular application lands (09.1.1-T2). It
// fails for a broken package.json ("type"/"main"), a missing entry file, or an entry Vite
// transpiled to CommonJS instead of ESM.
describe('@evidgate/web entry point', () => {
  it('loads as an ES module namespace', () => {
    expect(Object.prototype.toString.call(entry)).toBe('[object Module]');
  });
});
