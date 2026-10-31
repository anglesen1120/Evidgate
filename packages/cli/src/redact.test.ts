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

// Redaction runs on the way out more than once (a JSON member, then the writer), so a second
// pass must not append a second placeholder next to the first.
describe('redaction is idempotent', () => {
  beforeEach(() => {
    resetSecrets();
  });

  it('leaves_an_already_redacted_query_alone', () => {
    const url = 'https://store.test/b/k?X-Amz-Credential=AKIA%2F2026&X-Amz-Expires=900';
    const once = redact(url);
    expect(once).toBe('https://store.test/b/k?[REDACTED]');
    expect(redact(once)).toBe(once);
  });

  it('leaves_an_already_redacted_url_with_userinfo_alone', () => {
    const userinfo = ['user', 'secret'].join(':');
    const once = redact(`https://${userinfo}@store.test/object?x=1`);
    expect(once).toBe('https://store.test/object?[REDACTED]');
    expect(redact(once)).toBe(once);
  });

  it('leaves_an_already_redacted_header_alone', () => {
    const once = redact('Authorization: Bearer abc.def.ghi');
    expect(once).toBe(`Authorization: ${redacted}`);
    expect(redact(once)).toBe(once);
  });

  it('leaves_an_already_redacted_secret_alone', () => {
    registerSecret('super-secret-token');
    const once = redact('bearer super-secret-token here');
    expect(once).toBe(`bearer ${redacted} here`);
    expect(redact(once)).toBe(once);
  });
});

// A presigned URL IS a bearer credential, so any http(s) URL that carries a query string
// loses it — naming the signature parameters would leak the ones a future provider names
// differently. A URL with userinfo before the host loses that userinfo too.
describe('presigned URL redaction', () => {
  beforeEach(() => {
    resetSecrets();
  });

  it('strips_the_query_of_any_url', () => {
    expect(redact('see http://127.0.0.1:9/pending/x?X-Amz-Signature=abc now')).toBe(
      'see http://127.0.0.1:9/pending/x?[REDACTED] now',
    );
    expect(redact('https://store.test/b/k?X-Amz-Credential=AKIA%2F2026&X-Amz-Expires=900')).toBe(
      'https://store.test/b/k?[REDACTED]',
    );
    expect(redact('https://store.test/b/k?x-amz-security-token=tok')).toBe(
      'https://store.test/b/k?[REDACTED]',
    );
    // No query: nothing to strip, so a plain docs URL stays readable.
    expect(redact('see https://docs.evidgate.dev/cli')).toBe('see https://docs.evidgate.dev/cli');
  });

  it('strips_userinfo', () => {
    const userinfo = ['user', 'secret'].join(':');
    expect(redact(`https://${userinfo}@store.test/object`)).toBe('https://store.test/object');
  });

  it('keeps_wrapping_punctuation_outside_the_redaction', () => {
    expect(redact('(http://127.0.0.1:9/p/x?sig=1).')).toBe('(http://127.0.0.1:9/p/x?[REDACTED]).');
  });

  it('strips_the_fragment_of_any_url', () => {
    expect(redact('https://id.test/cb#access_token=abc&state=1')).toBe(
      'https://id.test/cb#[REDACTED]',
    );
    // An empty or already-redacted fragment carries nothing to strip.
    expect(redact('https://docs.test/page#')).toBe('https://docs.test/page#');
    expect(redact('https://id.test/cb#[REDACTED]')).toBe('https://id.test/cb#[REDACTED]');
  });

  it('strips_userinfo_that_contains_a_separator', () => {
    const userinfo = ['u', 'p@ss'].join(':');
    expect(redact(`https://${userinfo}@store.test/object`)).toBe('https://store.test/object');
  });

  it('redacts_a_query_after_a_bracketed_host', () => {
    expect(redact('http://[::1]:8080/p?sig=1')).toBe('http://[::1]:8080/p?[REDACTED]');
    expect(redact(redact('http://[::1]:8080/p?sig=1'))).toBe('http://[::1]:8080/p?[REDACTED]');
  });

  it('does_not_stop_at_a_placeholder_inside_a_larger_query', () => {
    // A registered secret replaced inside the query must not end the match at its `]`,
    // which would leave the rest of the query readable.
    registerSecret('super-secret-token');
    const once = redact('https://store.test/b/k?token=super-secret-token&sig=leaked');
    expect(once).toBe('https://store.test/b/k?[REDACTED]');
    expect(redact(once)).toBe(once);
  });
});
