// QA 04.3.1-T1 (tier T2): version-guard boundary probes the existing `guard.test.ts`
// does not cover — build metadata, a prerelease combined with build metadata, a numeric
// prerelease identifier, surrounding whitespace, a two-part version, and the exact
// minor/major boundaries of the hand-written `^1.63` range.
import { describe, expect, it } from 'vitest';

import { parseVersion, satisfiesSupportedEngine } from './versionGuard.js';

describe('version guard boundaries', () => {
  it('accepts build metadata (npm semver ignores it for range satisfaction)', () => {
    expect(satisfiesSupportedEngine('1.63.0+build.7')).toBe(true);
    expect(satisfiesSupportedEngine('1.63.5+sha.abcdef')).toBe(true);
  });

  it('rejects a prerelease even when build metadata is attached', () => {
    expect(satisfiesSupportedEngine('1.63.0-beta.1+build.7')).toBe(false);
    expect(satisfiesSupportedEngine('1.63.0-0')).toBe(false);
  });

  it('trims surrounding whitespace before parsing', () => {
    expect(satisfiesSupportedEngine(' 1.63.0 ')).toBe(true);
    expect(parseVersion('\t1.63.0\n')).not.toBeNull();
  });

  it('rejects a two-part version (the hand-written parser requires major.minor.patch)', () => {
    expect(satisfiesSupportedEngine('1.63')).toBe(false);
    expect(satisfiesSupportedEngine('1')).toBe(false);
  });

  it('accepts the next minor and rejects the next major', () => {
    expect(satisfiesSupportedEngine('1.64.0')).toBe(true);
    expect(satisfiesSupportedEngine('1.999.999')).toBe(true);
    expect(satisfiesSupportedEngine('2.0.0')).toBe(false);
    expect(satisfiesSupportedEngine('2.0.0-alpha.1')).toBe(false);
  });
});
