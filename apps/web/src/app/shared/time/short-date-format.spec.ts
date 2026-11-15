import { formatDate, registerLocaleData } from '@angular/common';
import localeVi from '@angular/common/locales/vi';

import { shortDateFormat } from './short-date-format';

registerLocaleData(localeVi);

describe.each(['vi', 'vi-VN', 'vi_VN', 'VI-vn'])('Vietnamese short dates (%s)', (locale) => {
  it.each([
    ['2026-01-02T03:04:05Z', '02/01/2026', '03:04 02/01/2026'],
    ['2026-12-31T23:59:59Z', '31/12/2026', '23:59 31/12/2026'],
    ['2027-01-01T00:00:00Z', '01/01/2027', '00:00 01/01/2027'],
  ])('formats %s with a padded date and four-digit year', (instant, date, dateTime) => {
    expect(formatDate(instant, shortDateFormat(locale), locale, 'UTC')).toBe(date);
    expect(formatDate(instant, shortDateFormat(locale, true), locale, 'UTC')).toBe(dateTime);
  });

  it('keeps a date-only calendar value without introducing a timezone conversion', () => {
    expect(formatDate('2026-01-02', shortDateFormat(locale), locale)).toBe('02/01/2026');
  });

  it('retains the short clock and requested UTC conversion across a year boundary', () => {
    expect(
      formatDate('2027-01-01T00:15:00+07:00', shortDateFormat(locale, true), locale, 'UTC'),
    ).toBe('17:15 31/12/2026');
  });
});

describe('English short date compatibility', () => {
  const instant = '2026-01-02T03:04:05Z';

  it('preserves the original short date alias and exact output', () => {
    expect(shortDateFormat('en-US')).toBe('shortDate');
    expect(formatDate(instant, shortDateFormat('en-US'), 'en-US', 'UTC')).toBe('1/2/26');
  });

  it('preserves the original short date-time alias, clock and spacing', () => {
    expect(shortDateFormat('en-US', true)).toBe('short');
    expect(formatDate(instant, shortDateFormat('en-US', true), 'en-US', 'UTC')).toBe(
      '1/2/26, 3:04\u202fAM',
    );
  });
});
