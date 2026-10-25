// 09.7.1-T4 (plan 09.7.1 §2 AC4, §5 "activation mode" row; S83 P2): the mode chip's whole decision.
//
// The four cases are the four claims the chip can make, and the reason each one is spelled out is
// that a wrong one is a security claim rather than a cosmetic detail: `enforcing` must read as
// gating, `advisory` as advisory, a mode nobody declared must be shown as the raw server text in a
// neutral chip (never rounded to one of the two the UI knows), and an absent mode must read as
// unavailable rather than defaulting to either.

import { policyModeView } from './policy-mode';

describe('policyModeView (09.7.1-T4)', () => {
  it('renders enforcing as the gating chip, keeping the raw value', () => {
    expect(policyModeView('enforcing')).toEqual({ kind: 'gating', raw: 'enforcing' });
  });

  it('renders advisory as the advisory chip, keeping the raw value', () => {
    expect(policyModeView('advisory')).toEqual({ kind: 'advisory', raw: 'advisory' });
  });

  it.each(['permissive', 'ENFORCING', 'enforcing ', 'gating'])(
    'keeps an undeclared mode %p as unknown with the server’s own text',
    (mode) => {
      // Never rounded to gating/advisory: the UI does not know what this mode means, so the chip
      // renders the server's own text in a neutral style.
      expect(policyModeView(mode)).toEqual({ kind: 'unknown', raw: mode });
    },
  );

  it.each([
    ['null', null],
    ['undefined', undefined],
    ['the empty string', ''],
  ])('answers unavailable for %s rather than guessing a mode', (_label, mode) => {
    expect(policyModeView(mode)).toEqual({ kind: 'unavailable' });
  });
});
