import { rfc3339InstantMs } from './rfc3339-instant';

describe('rfc3339InstantMs', () => {
  it.each(['1', '2026', 'Nov 7 2026', '2026-13-01T00:00:00Z', '2026-11-07T12:00:00', ''])(
    'refuses %j',
    (value) => {
      expect(rfc3339InstantMs(value)).toBeNull();
    },
  );

  it('reads Z and numeric-offset instants to the same millisecond', () => {
    const z = rfc3339InstantMs('2026-11-07T12:00:00Z');
    expect(z).toBe(Date.UTC(2026, 10, 7, 12));
    expect(rfc3339InstantMs('2026-11-07T19:00:00.000+07:00')).toBe(z);
  });
});
