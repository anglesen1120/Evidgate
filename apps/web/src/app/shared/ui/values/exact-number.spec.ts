import { ExactNumberFormatter } from './exact-number';

describe.each([
  ['en-US', '1,234,567,890,123,456.123456', '-0.001200', '9,007,199,254,740,993'],
  ['vi', '1.234.567.890.123.456,123456', '-0,001200', '9.007.199.254.740.993'],
])('ExactNumberFormatter (%s)', (locale, large, negative, integer) => {
  const formatter = new ExactNumberFormatter(locale);

  it('preserves every decimal string digit, including beyond Number precision', () => {
    expect(formatter.format('1234567890123456.123456')).toBe(large);
    expect(formatter.format('-0.001200')).toBe(negative);
    expect(formatter.format(9007199254740993n)).toBe(integer);
  });

  it('keeps zero and finite numeric magnitudes without fixed fractional rounding', () => {
    expect(formatter.format(0)).toBe('0');
    expect(formatter.format(0.0000001)).not.toBe('0');
    expect(formatter.format(1e24)).not.toContain('E');
  });

  it.each([
    null,
    undefined,
    Number.NaN,
    Infinity,
    -Infinity,
    '',
    ' ',
    '12,345',
    '0x10',
    'not-a-number',
  ])('refuses an absent or invalid magnitude without inventing zero: %j', (value) =>
    expect(formatter.format(value)).toBeNull(),
  );
});
