// 04.3.1-T1.2: the 04 §6 `ext` allow-list (AC2/§8 row). Only declared keys survive; a
// secret-looking key is dropped and counted.
import { describe, expect, it } from 'vitest';

import { EXT_ALLOWED_KEYS, filterExt, SECRET_KEY_PATTERN } from './extFilter.js';

describe('ext allow-list', () => {
  it('keeps every declared key', () => {
    const candidate: Record<string, unknown> = {};
    for (const key of EXT_ALLOWED_KEYS) candidate[key] = `value-${key}`;

    const result = filterExt(candidate);
    expect(Object.keys(result.ext).sort()).toEqual([...EXT_ALLOWED_KEYS].sort());
    expect(result.dropped).toEqual([]);
    expect(result.secretDropped).toEqual([]);
  });

  it('drops keys that are not declared', () => {
    const result = filterExt({
      schema: 'playwright/1',
      title: 'adds two numbers',
      stdout: ['noise'],
      errors: [{ message: 'boom' }],
    });

    expect(result.ext).toEqual({ schema: 'playwright/1', title: 'adds two numbers' });
    expect(result.dropped).toEqual(['stdout', 'errors']);
    expect(result.secretDropped).toEqual([]);
  });

  it('drops and counts secret-looking keys, case-insensitively', () => {
    const result = filterExt({
      schema: 'playwright/1',
      accessToken: 'leak',
      client_secret: 'leak',
      PASSWORD: 'leak',
      tokenCount: 3,
    });

    expect(result.ext).toEqual({ schema: 'playwright/1' });
    expect(result.dropped).toEqual([]);
    expect(result.secretDropped).toEqual([
      'accessToken',
      'client_secret',
      'PASSWORD',
      'tokenCount',
    ]);
  });

  it('skips undefined values without counting them', () => {
    const result = filterExt({ schema: 'playwright/1', project: undefined, unknown: undefined });

    expect(result.ext).toEqual({ schema: 'playwright/1' });
    expect(result.dropped).toEqual([]);
    expect(result.secretDropped).toEqual([]);
  });

  it('matches the documented secret pattern', () => {
    expect(SECRET_KEY_PATTERN.test('apiToken')).toBe(true);
    expect(SECRET_KEY_PATTERN.test('client_secret')).toBe(true);
    expect(SECRET_KEY_PATTERN.test('dbPassword')).toBe(true);
    expect(SECRET_KEY_PATTERN.test('title')).toBe(false);
  });
});
