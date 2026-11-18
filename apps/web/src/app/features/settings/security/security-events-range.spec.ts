import { securityEventRange } from './security-events-range';

describe('Security-event range validation', () => {
  it.each([
    ['invalid from', 'not-a-date', '2026-11-18T10:00', true, false],
    ['invalid to', '2026-11-18T09:00', 'not-a-date', false, true],
    ['equal', '2026-11-18T09:00', '2026-11-18T09:00', true, true],
    ['inverted', '2026-11-18T10:00', '2026-11-18T09:00', true, true],
  ] as const)(
    'rejects %s without converting it to an ISO query',
    (_case, from, to, invalidFrom, invalidTo) => {
      expect(securityEventRange(from, to)).toMatchObject({ invalidFrom, invalidTo });
    },
  );
  it('accepts optional bounds and preserves the valid local instants', () => {
    expect(securityEventRange('', '')).toEqual({
      from: null,
      to: null,
      invalidFrom: false,
      invalidTo: false,
    });
    const range = securityEventRange('2026-11-18T09:00', '2026-11-18T10:00');
    expect(range.invalidFrom || range.invalidTo).toBe(false);
    expect(range.from?.toISOString()).toBe(new Date('2026-11-18T09:00').toISOString());
    expect(range.to?.toISOString()).toBe(new Date('2026-11-18T10:00').toISOString());
    expect(securityEventRange('', '2026-11-18T10:00').invalidTo).toBe(false);
  });
});
