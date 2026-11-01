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

  it('leaves_an_already_redacted_assigned_header_alone', () => {
    const once = redact('authorization = Bearer abc.def.ghi');
    expect(once).toBe(`authorization = ${redacted}`);
    expect(redact(once)).toBe(once);
  });

  it('leaves_an_already_redacted_json_member_alone', () => {
    const once = redact('{"Authorization":"Bearer abc.def.ghi"}');
    expect(once).toBe('{"Authorization":"[REDACTED]"}');
    expect(redact(once)).toBe(once);
    expect(JSON.parse(redact(once))).toStrictEqual({ Authorization: '[REDACTED]' });
  });

  it('leaves_an_already_redacted_unterminated_json_member_alone', () => {
    const once = redact('{"Authorization":"Bearer abc');
    expect(once).toBe('{"Authorization":"[REDACTED]');
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

  it('redacts_past_an_unbalanced_bracket', () => {
    expect(redact('https://store.test/x?meta=[&sig=SECRET')).toBe(
      'https://store.test/x?[REDACTED]',
    );
    expect(redact('https://store.test/x?[REDACTED]')).toBe('https://store.test/x?[REDACTED]');
  });

  it('redacts_a_fragment_that_precedes_a_question_mark', () => {
    expect(redact('https://id.test/cb#access_token=SECRET?state=1')).toBe(
      'https://id.test/cb#[REDACTED]',
    );
    expect(redact('https://id.test/cb#[REDACTED]')).toBe('https://id.test/cb#[REDACTED]');
  });

  it('redacts_past_punctuation_that_cannot_be_in_a_url', () => {
    expect(redact('https://store.test/x?meta=[[1]]&sig=SECRET')).toBe(
      'https://store.test/x?[REDACTED]',
    );
    expect(redact('https://store.test/x?a=1)&sig=SECRET')).toBe('https://store.test/x?[REDACTED]');
    expect(redact('https://store.test/x?f={a}&sig=SECRET')).toBe('https://store.test/x?[REDACTED]');
    expect(redact('https://s.test/f(1).png?sig=SECRET')).toBe('https://s.test/f(1).png?[REDACTED]');
    expect(redact('https://id.test/cb#[REDACTED]]&x=SECRET')).toBe('https://id.test/cb#[REDACTED]');
  });

  it('keeps_a_json_escape_after_the_redacted_credential', () => {
    expect(redact('"url":"https://a/b?x=1\\""')).toBe('"url":"https://a/b?[REDACTED]\\""');
  });

  it('stays_stable_when_a_short_secret_is_a_fragment_of_the_placeholder', () => {
    registerSecret('RED');
    const once = redact('value RED here');
    expect(once).toBe(`value ${redacted} here`);
    expect(redact(once)).toBe(once);
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

// A URL credential travels in more spellings than `https://user:pass@`: a scheme-relative
// reference, the percent-encoded form a query round-trip produces, a database URL, and a
// password that kept a raw space. Each row keeps the host and the path readable.
describe('URL credential spellings', () => {
  beforeEach(() => {
    resetSecrets();
  });

  const credentialRows = [
    {
      shape: 'a scheme-relative URL',
      input: 'see //user:secret@store.test/path now',
      expected: 'see //store.test/path now',
    },
    {
      shape: 'a percent-encoded URL in upper case',
      input: 'https%3A%2F%2Fuser%3Asecret%40store.test/object',
      expected: 'https%3A%2F%2Fstore.test/object',
    },
    {
      shape: 'a percent-encoded URL in lower case',
      input: 'https%3a%2f%2fuser%3asecret%40store.test/object',
      expected: 'https%3a%2f%2fstore.test/object',
    },
    {
      shape: 'a percent-encoded scheme-relative reference',
      input: '%2f%2fuser%3Asecret%40store.test/object',
      expected: '%2f%2fstore.test/object',
    },
    {
      shape: 'a postgres scheme',
      input: 'connect postgres://user:secret@db.test:5432/app now',
      expected: 'connect postgres://db.test:5432/app now',
    },
    {
      shape: 'any other scheme',
      input: 'redis://user:secret@cache.test/0',
      expected: 'redis://cache.test/0',
    },
    {
      shape: 'a raw space inside the password',
      input: 'https://user:pa ss@store.test/object',
      expected: 'https://store.test/object',
    },
    {
      shape: 'a raw space inside the password of a signed URL',
      input: 'https://user:pa ss@store.test/object?X-Amz-Signature=abc',
      expected: 'https://store.test/object?[REDACTED]',
    },
  ];

  it.each(credentialRows)('strips the credential from $shape', ({ input, expected }) => {
    expect(redact(input)).toBe(expected);
  });

  it.each(credentialRows)(
    'strips the credential twice without changing the second result',
    ({ input, expected }) => {
      expect(redact(redact(input))).toBe(redact(expected));
    },
  );

  // The scan runs over every line, so ordinary text must survive it unchanged.
  const untouchedRows = [
    { shape: 'a scheme-relative URL without userinfo', text: 'see //example.com/path' },
    { shape: 'percent-encoded ordinary text', text: 'a%20b' },
    { shape: 'an email address', text: 'write to user@host' },
    { shape: 'a URL without userinfo', text: 'see https://docs.evidgate.dev/cli' },
    { shape: 'prose that follows a host and port', text: 'https://host:8080 and user@mail' },
    { shape: 'an authority that a line break ends', text: 'see //host\nuser:pw@mail' },
    { shape: 'a base64 string that is not registered', text: 'cGFzc3dvcmQ=' },
  ];

  it.each(untouchedRows)('keeps $shape intact', ({ text }) => {
    expect(redact(text)).toBe(text);
  });

  it('stays linear on 200 KB of authority-shaped text', () => {
    const credentialed = '//user:pa ss@host/path '.repeat(8_000);
    const slashes = '//'.repeat(100_000);
    const started = performance.now();

    expect(redact(credentialed)).toBe('//host/path '.repeat(8_000));
    expect(redact(slashes)).toBe(slashes);
    // Linear in the input: a backtracking shape would need seconds for either string.
    expect(performance.now() - started).toBeLessThan(1_000);
  });
});

// A token is often echoed already encoded — in a header value or a URL parameter — so the
// registry carries the base64 and the lowercase-escape spellings of every secret.
describe('registered secret encodings', () => {
  beforeEach(() => {
    resetSecrets();
  });

  const encodedRows = [
    { shape: 'standard base64', secret: 'evg_dev_token', encoded: 'ZXZnX2Rldl90b2tlbg==' },
    {
      shape: 'standard base64 without padding',
      secret: 'evg_dev_token',
      encoded: 'ZXZnX2Rldl90b2tlbg',
    },
    { shape: 'url-safe base64', secret: 'a?>b~', encoded: 'YT8-Yn4=' },
    { shape: 'url-safe base64 without padding', secret: 'a?>b~', encoded: 'YT8-Yn4' },
    {
      shape: 'a percent encoding with lower-case escapes',
      secret: 'a:b',
      encoded: 'a%3ab',
    },
  ];

  it.each(encodedRows)('replaces the $shape of a registered secret', ({ secret, encoded }) => {
    registerSecret(secret);
    expect(redact(`value ${encoded} here`)).toBe(`value ${redacted} here`);
  });

  it('replaces the padded base64 form without leaving its padding behind', () => {
    registerSecret('evg_dev_token');
    expect(redact('ZXZnX2Rldl90b2tlbg==')).toBe(redacted);
  });
});
