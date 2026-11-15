import { FormatWidth, getLocaleTimeFormat } from '@angular/common';

const VI_LOCALE = /^vi(?:[-_]|$)/i;

/** Share one calendar policy while retaining each surface's existing clock precision. */
export function shortDateFormat(
  locale: string,
  dateTime = false,
  clock: 'short' | 'medium' | '24h' = 'short',
): string {
  const vietnamese = VI_LOCALE.test(locale);
  const date = vietnamese ? 'dd/MM/yyyy' : 'M/d/yyyy';
  if (!dateTime) return date;
  const time =
    clock === '24h'
      ? 'HH:mm:ss'
      : getLocaleTimeFormat(locale, clock === 'medium' ? FormatWidth.Medium : FormatWidth.Short);
  return vietnamese ? `${time} ${date}` : `${date}, ${time}`;
}
