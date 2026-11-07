import { rfc3339InstantMs } from './rfc3339-instant';

describe('rfc3339InstantMs', () => {
  it.each([
    '1',
    '2026',
    'Nov 7 2026',
    '2026-13-01T00:00:00Z',
    '2026-11-07T12:00:00',
    '',
    // Calendar impossibilities `Date.parse` would roll into a neighbouring day or second.
    '2026-02-29T00:00:00Z',
    '2026-02-30T00:00:00Z',
    '2026-04-31T00:00:00Z',
    '2026-11-07T24:00:00Z',
    '2026-11-07T23:59:60Z',
    // The shape is uppercase-only, and surrounding whitespace is not part of an instant.
    '2026-11-07t12:00:00z',
    '2026-11-07T12:00:00Z ',
  ])('refuses %j', (value) => {
    expect(rfc3339InstantMs(value)).toBeNull();
  });

  it('reads Z and numeric-offset instants to the same millisecond', () => {
    const z = rfc3339InstantMs('2026-11-07T12:00:00Z');
    expect(z).toBe(Date.UTC(2026, 10, 7, 12));
    expect(rfc3339InstantMs('2026-11-07T19:00:00.000+07:00')).toBe(z);
  });

  it('reads a leap day and keeps every accepted offset and fractional second', () => {
    expect(rfc3339InstantMs('2028-02-29T00:00:00Z')).toBe(Date.UTC(2028, 1, 29));
    for (const value of [
      '2026-11-07T12:00:00+14:00',
      '2026-11-07T12:00:00-12:00',
      '2026-11-07T12:00:00+05:30',
      '2026-11-07T12:00:00.123456Z',
    ]) {
      expect(rfc3339InstantMs(value)).not.toBeNull();
    }
    expect(rfc3339InstantMs('2026-11-07T12:00:00+05:30')).toBe(Date.UTC(2026, 10, 7, 6, 30));
  });
});
