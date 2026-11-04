import { beforeEach, describe, expect, it } from 'vitest';

import { redact, redacted, registerSecret, resetSecrets } from './redact.js';

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
