import { beforeEach, describe, expect, it } from 'vitest';

import { redact, redacted, registerSecret, resetSecrets } from './redact.js';

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
