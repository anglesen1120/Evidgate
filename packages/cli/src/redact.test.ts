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
