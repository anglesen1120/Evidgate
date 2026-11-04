import { beforeEach, describe, expect, it } from 'vitest';

import { redact, redacted, registerSecret, resetSecrets } from './redact.js';

// A token is often echoed already encoded — in a header value or a URL parameter — so the
// registry carries the percent-encoded and base64 spellings of every secret.
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
    // A caller that re-encodes a secret picks its own hex case, so the encoded spelling is
    // matched per escape rather than as one literal.
    {
      shape: 'a percent encoding with mixed-case escapes',
      secret: 'qa/a:b',
      encoded: 'qa%2Fa%3ab',
    },
    {
      shape: 'a percent encoding with the other mixed case',
      secret: 'qa/a:b',
      encoded: 'qa%2fa%3Ab',
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

  // A short secret still has a value form worth registering, but its encoding is a few letters
  // of ordinary text; stripping that would rewrite output that never held the secret.
  it('leaves a short derived form of a short secret unregistered', () => {
    registerSecret('x');
    expect(redact('hello eA')).toBe('hello eA');
    expect(redact('value x here')).toBe(`value ${redacted} here`);
  });
});
