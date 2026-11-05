// target_env resolution: the Playwright project name as the default, the ctor-validated
// EVIDGATE_TARGET_ENV override, and the shape validTargetEnv accepts.
import { describe, expect, it } from 'vitest';

import { fakeTest, useIdentityResolveSpec } from './identity-resolve.testing.js';
import { resolveIdentity, validTargetEnv } from './identity.js';

describe('target_env', () => {
  useIdentityResolveSpec();

  it('defaults to the Playwright project name', () => {
    expect(resolveIdentity(fakeTest(), {}).target_env).toBe('chromium');
  });

  it('omits an invalid project name', () => {
    expect(
      resolveIdentity(fakeTest({ project: { name: 'Chromium Desktop' } }), {}).target_env,
    ).toBe(undefined);
  });

  it('prefers the ctor-validated EVIDGATE_TARGET_ENV', () => {
    const identity = resolveIdentity(fakeTest(), { targetEnv: 'staging-eu' });
    expect(identity.target_env).toBe('staging-eu');
  });

  it('validates the manifest targetEnv shape', () => {
    expect(validTargetEnv('prod')).toBe('prod');
    expect(validTargetEnv('staging-eu_2')).toBe('staging-eu_2');
    expect(validTargetEnv('')).toBeUndefined();
    expect(validTargetEnv('Prod')).toBeUndefined();
    expect(validTargetEnv('-leading')).toBeUndefined();
    expect(validTargetEnv('a'.repeat(65))).toBeUndefined();
  });
});
