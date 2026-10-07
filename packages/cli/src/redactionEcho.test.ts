import { beforeEach, describe, expect, it } from 'vitest';

import { main } from './main.js';
import { resetSecrets, type Io } from './redact.js';

// 04.2.1-T2.2 / T78 boundary: an error message that echoes a value equal to a
// registered secret must still be redacted. `resolveConfig` registers every env
// secret before `validateApiUrl` throws, so an `invalid_config` message that
// embeds the offending `--api-url` value cannot leak a token that happens to be
// that value. This is the echo case the generic redaction specs do not cover.
describe('redaction of echoed input', () => {
  beforeEach(() => {
    resetSecrets();
  });

  it('redacts a secret echoed back in an invalid_config message', async () => {
    const token = 'evg_dev_FAKE_echo_value';
    const stdout: string[] = [];
    const stderr: string[] = [];
    const io: Io = {
      stdout: (text) => stdout.push(text),
      stderr: (text) => stderr.push(text),
    };

    const code = await main(
      ['config', 'check', '--api-url', token],
      {
        EVIDGATE_API_URL: 'https://api.evidgate.test',
        EVIDGATE_PROJECT: 'proj-1',
        EVIDGATE_ENV: 'dev',
        EVIDGATE_DEV_TOKEN: token,
      },
      io,
    );

    expect(code).toBe(3);
    expect(stderr.join('')).toContain('invalid_config');
    expect(stderr.join('')).not.toContain(token);
    expect(stdout.join('')).not.toContain(token);
  });
});
