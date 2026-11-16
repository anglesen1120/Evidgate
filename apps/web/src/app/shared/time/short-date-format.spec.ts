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

describe.each(['en', 'en-US'])('English short dates (%s)', (locale) => {
  it.each([
    ['2026-01-02T03:04:05Z', '1/2/2026', '1/2/2026, 3:04\u202fAM'],
    ['2026-11-02T03:04:05Z', '11/2/2026', '11/2/2026, 3:04\u202fAM'],
    ['2026-12-31T23:59:59Z', '12/31/2026', '12/31/2026, 11:59\u202fPM'],
    ['2027-01-01T00:00:00Z', '1/1/2027', '1/1/2027, 12:00\u202fAM'],
  ])('widens only the year for %s', (instant, date, time) => {
    expect(formatDate(instant, shortDateFormat(locale), locale, 'UTC')).toBe(date);
    expect(formatDate(instant, shortDateFormat(locale, true), locale, 'UTC')).toBe(time);
  });

  it('keeps a date-only calendar value without introducing a timezone conversion', () => {
    expect(formatDate('2026-01-02', shortDateFormat(locale), locale)).toBe('1/2/2026');
  });

  it('retains the short clock and requested UTC conversion across a year boundary', () => {
    expect(
      formatDate('2027-01-01T00:15:00+07:00', shortDateFormat(locale, true), locale, 'UTC'),
    ).toBe('12/31/2026, 5:15\u202fPM');
  });
});

describe.each(['en', 'en-US', 'vi', 'vi-VN'])('Operational date-times (%s)', (locale) => {
  const vietnamese = locale.startsWith('vi');

  it('retains the locale clock including seconds with the shared numeric calendar', () => {
    const expected = vietnamese ? '06:22:59 15/11/2026' : '11/15/2026, 6:22:59\u202fAM';
    expect(
      formatDate('2026-11-15T06:22:59Z', shortDateFormat(locale, true, 'medium'), locale, 'UTC'),
    ).toBe(expected);
  });

  it('preserves the health panel 24-hour clock without adding a meridiem', () => {
    const expected = vietnamese ? '13:14:15 15/11/2026' : '11/15/2026, 13:14:15';
    expect(
      formatDate('2026-11-15T13:14:15Z', shortDateFormat(locale, true, '24h'), locale, 'UTC'),
    ).toBe(expected);
  });

  it('keeps the UTC calendar year and second precision across an offset boundary', () => {
    const expected = vietnamese ? '17:15:04 31/12/2026' : '12/31/2026, 5:15:04\u202fPM';
    expect(
      formatDate(
        '2027-01-01T00:15:04+07:00',
        shortDateFormat(locale, true, 'medium'),
        locale,
        'UTC',
      ),
    ).toBe(expected);
  });

  it('does not append or convert a clock for date-only values', () => {
    const expected = vietnamese ? '02/01/2026' : '1/2/2026';
    expect(formatDate('2026-01-02', shortDateFormat(locale, false, 'medium'), locale)).toBe(
      expected,
    );
  });
});
