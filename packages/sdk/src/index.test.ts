import { describe, expect, it } from 'vitest';

import * as entry from './index.js';

// 01.6.1-T1.2 + 01.3.2-T1: entry-point smoke spec — still fails for a broken
// package.json ("type"/"main"), a missing entry file, or an entry transpiled to
// CommonJS; and now pins that the generated operations, the client and the handwritten
// 01.3.2-T1 surface are reachable through `./index.js` (they are re-exports, so a wrong
// relative specifier fails the whole import).
describe('@evidgate/sdk entry point', () => {
  it('loads as an ES module namespace', () => {
    expect(Object.prototype.toString.call(entry)).toBe('[object Module]');
  });

  it('re-exports the generated operations, client and SDK surface', () => {
    expect(typeof entry.exchangeCiToken).toBe('function');
    expect(typeof entry.createClient).toBe('function');
    expect(typeof entry.createConfig).toBe('function');
    expect(entry.client).toBeDefined();
    expect(typeof entry.createHeaderHookInterceptor).toBe('function');
    expect(typeof entry.isErrorEnvelope).toBe('function');
  });
});
