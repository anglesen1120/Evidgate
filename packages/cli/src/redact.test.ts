import { beforeEach, describe, expect, it } from 'vitest';

import { createRedactingIo, redact, redacted, registerSecret, resetSecrets } from './redact.js';

// 04.2.1-T2.1/T2.2: the redaction contract (T78, 04.2.1 §8 S3 analogue) — a registered
// secret value can never reach stdout/stderr, and an `Authorization` header is masked
// even when its value was never registered.
describe('redact', () => {
  beforeEach(() => {
    resetSecrets();
  });

  it('replaces a registered secret', () => {
    registerSecret('super-secret-token');
    expect(redact('bearer super-secret-token here')).toBe(`bearer ${redacted} here`);
  });

  it('replaces several registered secrets', () => {
    registerSecret('alpha');
    registerSecret('beta');
    expect(redact('alpha and beta')).toBe(`${redacted} and ${redacted}`);
  });

  it('ignores empty and undefined values', () => {
    registerSecret('');
    registerSecret(undefined);
    expect(redact('nothing to hide')).toBe('nothing to hide');
  });

  it('never lets an Authorization header through', () => {
    expect(redact('Authorization: Bearer abc.def.ghi')).toBe(`Authorization: ${redacted}`);
  });

  it('clears the registry', () => {
    registerSecret('secret');
    resetSecrets();
    expect(redact('secret')).toBe('secret');
  });

  it('replaces a longer secret before a shorter prefix of it (CR5/SEC1)', () => {
    registerSecret('tok');
    registerSecret('tok-long-value');
    expect(redact('tok-long-value')).toBe(redacted);
  });

  it('replaces a longer secret regardless of registration order (CR5/SEC1)', () => {
    registerSecret('tok-long-value');
    registerSecret('tok');
    expect(redact('tok-long-value')).toBe(redacted);
  });

  it('registers the trimmed form of a secret (SEC6)', () => {
    registerSecret('  padded-secret  ');
    expect(redact('padded-secret')).toBe(redacted);
  });

  it('registers the percent-encoded form of a secret (SEC6)', () => {
    registerSecret('a b');
    expect(redact('a%20b')).toBe(redacted);
  });

  it('registers the JSON-escaped form of a secret (SEC6)', () => {
    registerSecret('a"b');
    expect(redact('a\\"b')).toBe(redacted);
  });

  // D-2: `formatCliError`/`formatInternalError` escape Cc/Cf characters before the
  // redacting writer runs, so the escaped form must also be registered. `\u200b` (Cf)
  // is not escaped by `JSON.stringify`, so this form is only registered by the fix.
  it('registers the control-character-escaped form of a secret (D-2)', () => {
    registerSecret('evg_dev_\u200btoken');
    expect(redact('evg_dev_\\u200btoken')).toBe(redacted);
  });

  it('still redacts the raw form of a secret with a format character (D-2)', () => {
    registerSecret('evg_dev_\u200btoken');
    expect(redact('evg_dev_\u200btoken')).toBe(redacted);
  });

  it('wraps both writers', () => {
    registerSecret('tok');
    const stdout: string[] = [];
    const stderr: string[] = [];
    const io = createRedactingIo({
      stdout: (text) => stdout.push(text),
      stderr: (text) => stderr.push(text),
    });
    io.stdout('out tok');
    io.stderr('err tok');
    expect(stdout).toEqual([`out ${redacted}`]);
    expect(stderr).toEqual([`err ${redacted}`]);
  });
});
