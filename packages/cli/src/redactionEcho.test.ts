import { beforeEach, describe, expect, it } from 'vitest';

import { main } from './main.js';
import { resetSecrets, type Io } from './redact.js';

// 04.2.1-T2.2 / T78 boundary: an error message that echoes a value equal to a
// registered secret must still be redacted. `resolveConfig` registers every env secret
// before `resolveDevToken` throws, so a `dev_token_refused` message that embeds the
// offending `EVIDGATE_ENV` value cannot leak a token that happens to be that value.
describe('redaction of echoed input', () => {
  beforeEach(() => {
    resetSecrets();
  });

  it('redacts a secret echoed back in a dev_token_refused message', async () => {
    const token = 'evg_dev_fake_echo_value';
    const stdout: string[] = [];
    const stderr: string[] = [];
    const io: Io = {
      stdout: (text) => stdout.push(text),
      stderr: (text) => stderr.push(text),
    };

    const code = await main(
      ['config', 'check'],
      {
        EVIDGATE_API_URL: 'http://127.0.0.1:8080',
        EVIDGATE_PROJECT: 'proj-1',
        EVIDGATE_ENV: token,
        EVIDGATE_DEV_TOKEN: token,
      },
      io,
    );

    expect(code).toBe(3);
    expect(stderr.join('')).toContain('dev_token_refused');
    expect(stderr.join('')).not.toContain(token);
    expect(stdout.join('')).not.toContain(token);
  });

  it('does not echo the raw API URL on the invalid_config path (SEC3)', async () => {
    const secret = 'https://ci:hunter2@host:99999';
    const stdout: string[] = [];
    const stderr: string[] = [];
    const io: Io = {
      stdout: (text) => stdout.push(text),
      stderr: (text) => stderr.push(text),
    };

    const code = await main(
      ['config', 'check', '--api-url', secret],
      { EVIDGATE_PROJECT: 'proj-1', EVIDGATE_INGEST_TOKEN: 'ingest-token' },
      io,
    );

    expect(code).toBe(3);
    expect(stderr.join('')).toContain('invalid_config');
    expect(stderr.join('')).not.toContain('hunter2');
    expect(stdout.join('')).not.toContain('hunter2');
  });

  // D-2: the error formatter escapes Cc/Cf characters before the redacting writer
  // runs, so a secret containing one is echoed in its escaped form. Both the raw and
  // the escaped form must be redacted end to end. `\u200b` (Cf) is not escaped by
  // `JSON.stringify`, so the escaped form is only registered by the fix.
  it('redacts a secret whose control characters were escaped before the writer (D-2)', async () => {
    const secret = 'evg_dev_\u200becho_value';
    const stdout: string[] = [];
    const stderr: string[] = [];
    const io: Io = {
      stdout: (text) => stdout.push(text),
      stderr: (text) => stderr.push(text),
    };

    const code = await main([secret], { EVIDGATE_INGEST_TOKEN: secret }, io);

    expect(code).toBe(3);
    expect(stderr.join('')).toContain('unknown command');
    expect(stderr.join('')).toContain('[REDACTED]');
    expect(stderr.join('')).not.toContain(secret);
    expect(stderr.join('')).not.toContain('\\u200b');
    expect(stdout.join('')).not.toContain(secret);
  });
});
