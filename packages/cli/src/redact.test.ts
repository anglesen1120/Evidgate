import { beforeEach, describe, expect, it } from 'vitest';

import { createRedactingIo, redact, redacted, registerSecret, resetSecrets } from './redact.js';

// The redaction contract: a registered secret value can never reach stdout/stderr, and an
// `Authorization` header is masked even when its value was never registered.
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

  it('masks an Authorization header assigned with an equals sign', () => {
    expect(redact('Authorization=Bearer abc.def.ghi')).toBe(`Authorization=${redacted}`);
    expect(redact('authorization = Bearer abc.def.ghi')).toBe(`authorization = ${redacted}`);
  });

  it('masks a JSON Authorization member and keeps the line parseable', () => {
    const line = '{"Authorization":"Bearer abc.def.ghi","mode":"ci"}';

    expect(redact(line)).toBe('{"Authorization":"[REDACTED]","mode":"ci"}');
    // A reader of `gate --json` output parses the line, so the member must stay a string.
    expect(JSON.parse(redact(line))).toStrictEqual({
      Authorization: '[REDACTED]',
      mode: 'ci',
    });
    expect(redact('{ "authorization" : "Bearer abc" }')).toBe('{ "authorization" : "[REDACTED]" }');
  });

  it('masks a JSON Authorization member whose value carries an escape', () => {
    expect(redact('{"Authorization":"Bearer \\"abc\\""}')).toBe('{"Authorization":"[REDACTED]"}');
  });

  it('masks a JSON Authorization member whose closing quote was cut off', () => {
    // A truncated message must not print the value just because its quote is missing.
    expect(redact('{"Authorization":"Bearer abc')).toBe('{"Authorization":"[REDACTED]');
  });

  it('masks a JSON Authorization member inside an escaped JSON string', () => {
    expect(redact('{\\"Authorization\\":\\"Bearer abc\\"}')).toBe(
      '{\\"Authorization\\":\\"[REDACTED]',
    );
  });

  it('stays linear on a megabyte of header-shaped text', () => {
    const unclosed = `"Authorization":"${'a'.repeat(1_048_576)}`;
    const names = 'authorization '.repeat(65_536);
    const assigned = `Authorization=Bearer ${'c'.repeat(1_048_576)}`;
    const started = performance.now();

    expect(redact(unclosed)).toBe('"Authorization":"[REDACTED]');
    expect(redact(names)).toBe(names);
    expect(redact(assigned)).toBe(`Authorization=${redacted}`);
    // Linear in the input: a backtracking shape would need seconds for these two megabytes.
    expect(performance.now() - started).toBeLessThan(1_000);
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

  // `formatCliError`/`formatInternalError` escape Cc/Cf characters before the
  // redacting writer runs, so the escaped form must also be registered. `\u200b` (Cf)
  // is not escaped by `JSON.stringify`.
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
