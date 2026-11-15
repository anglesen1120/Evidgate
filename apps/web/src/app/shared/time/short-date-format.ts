const VI_LOCALE = /^vi(?:[-_]|$)/i;

/** Keep short dates unambiguous while retaining the existing locale order and clock. */
export function shortDateFormat(locale: string, dateTime = false): string {
  if (VI_LOCALE.test(locale)) {
    return dateTime ? 'HH:mm dd/MM/yyyy' : 'dd/MM/yyyy';
  }
  return dateTime ? 'M/d/yyyy, h:mm\u202fa' : 'M/d/yyyy';
}
