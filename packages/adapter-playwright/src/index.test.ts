import { describe, expect, it } from 'vitest';

import * as entry from './index.js';

// 01.6.1-T1.2: placeholder smoke spec until the Playwright reporter lands (04.x/05.x). It
// fails for a broken package.json ("type"/"main"), a missing entry file, or a CommonJS
// entry.
describe('@evidgate/adapter-playwright entry point', () => {
  it('loads as an ES module namespace', () => {
    expect(Object.prototype.toString.call(entry)).toBe('[object Module]');
  });
});
