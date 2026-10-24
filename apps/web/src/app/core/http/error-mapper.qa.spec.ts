import { parseRetryAfter } from './error-mapper';

// Fixed clock: Sat, 24 Oct 2026 12:00:00 GMT.
const NOW = Date.UTC(2026, 9, 24, 12, 0, 0);
const ONE_DAY = 86_400;

describe('parseRetryAfter QA boundaries', () => {
  it('reads zero delta-seconds as an immediate retry, not as a missing value', () => {
    expect(parseRetryAfter('0', NOW)).toBe(0);
  });

  it('trims surrounding spaces and tabs before reading delta-seconds', () => {
    expect(parseRetryAfter('  120 \t', NOW)).toBe(120);
  });

  it.each(['-5', '1.5', '+5', '1e3', 'Infinity', 'NaN', '12s'])(
    'rejects the non-delta-seconds value %s as no Retry-After',
    (value) => {
      expect(parseRetryAfter(value, NOW)).toBeNull();
    },
  );

  it.each([null, undefined, '', '   '])('treats %j as no Retry-After', (value) => {
    expect(parseRetryAfter(value, NOW)).toBeNull();
  });

  it('clamps an oversized delta-seconds value to one day', () => {
    expect(parseRetryAfter('99999999', NOW)).toBe(ONE_DAY);
  });

  it('clamps a digit string that overflows Number to one day, not to null', () => {
    expect(parseRetryAfter('9'.repeat(400), NOW)).toBe(ONE_DAY);
  });

  it('reads an HTTP-date exactly one hour ahead as 3600 seconds', () => {
    expect(parseRetryAfter('Sat, 24 Oct 2026 13:00:00 GMT', NOW)).toBe(3600);
  });

  it('reads an HTTP-date equal to now as 0, not as a negative wait', () => {
    expect(parseRetryAfter('Sat, 24 Oct 2026 12:00:00 GMT', NOW)).toBe(0);
  });

  it('reads an HTTP-date in the past as retry now', () => {
    expect(parseRetryAfter('Sat, 24 Oct 2026 11:00:00 GMT', NOW)).toBe(0);
  });

  it('clamps a far-future HTTP-date to one day', () => {
    expect(parseRetryAfter('Fri, 01 Jan 2100 00:00:00 GMT', NOW)).toBe(ONE_DAY);
  });

  it('reads an RFC 850 HTTP-date ending in GMT', () => {
    expect(parseRetryAfter('Saturday, 24-Oct-26 13:00:00 GMT', NOW)).toBe(3600);
  });

  it.each([
    '2026-10-24T13:00:00Z',
    '2026-10-24',
    'Sat, 24 Oct 2026 13:00:00',
    'Sat, 24 Oct 2026 13:00:00 +0000',
    'Sat, 24 Oct 2026',
  ])('rejects %s, which is not an HTTP-date, as no Retry-After', (value) => {
    expect(parseRetryAfter(value, NOW)).toBeNull();
  });
});
