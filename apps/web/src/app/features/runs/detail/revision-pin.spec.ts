import { isCanonicalRevisionNo, MAX_PINNED_REVISION, parsePinnedRevision } from './revision-pin';

describe('evidence revision pin', () => {
  it.each(['1', '4', '42', '999999999'])('accepts the canonical decimal %s', (value) => {
    expect(parsePinnedRevision(value)).toBe(Number(value));
  });

  it.each([
    '',
    '0',
    '00',
    '01',
    '-3',
    '+3',
    ' 5 ',
    '5 ',
    ' 5',
    '1e3',
    '1E3',
    '0x10',
    '1e21',
    '3.0',
    '1000000000',
    'ev-3',
    '018f0000-0000-7000-8000-0000000c1007',
    '٥',
  ])('rejects %s instead of coercing it to a number', (value) => {
    expect(parsePinnedRevision(value)).toBeNull();
  });

  it('treats an absent query value as no pin', () => {
    expect(parsePinnedRevision(null)).toBeNull();
  });

  it('accepts only integers inside the canonical range, never an exponent or hex form', () => {
    expect(isCanonicalRevisionNo(1)).toBe(true);
    expect(isCanonicalRevisionNo(MAX_PINNED_REVISION)).toBe(true);
    // `0x10` and `1e3` are the numbers 16 and 1000 once parsed: the string form is what a URL
    // value must reject, so a typed call site accepts them exactly like any other integer.
    expect(isCanonicalRevisionNo(0x10)).toBe(true);
    expect(isCanonicalRevisionNo(1e3)).toBe(true);
    expect(isCanonicalRevisionNo(0)).toBe(false);
    expect(isCanonicalRevisionNo(-1)).toBe(false);
    expect(isCanonicalRevisionNo(1.5)).toBe(false);
    expect(isCanonicalRevisionNo(1e21)).toBe(false);
    expect(isCanonicalRevisionNo('5')).toBe(false);
    expect(isCanonicalRevisionNo(null)).toBe(false);
  });
});
