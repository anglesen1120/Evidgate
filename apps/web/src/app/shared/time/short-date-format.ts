const VI_LOCALE = /^vi(?:[-_]|$)/i;

/** Keep Vietnamese compact dates unambiguous while retaining the existing short clock. */
export function shortDateFormat(locale: string, dateTime = false): string {
  if (VI_LOCALE.test(locale)) {
    return dateTime ? 'HH:mm dd/MM/yyyy' : 'dd/MM/yyyy';
  }
  return dateTime ? 'short' : 'shortDate';
}
