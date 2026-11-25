import { formatNumber } from '@angular/common';

export function formatDuration(
  milliseconds: number | null | undefined,
  locale: string,
): string | null {
  if (milliseconds == null || !Number.isFinite(milliseconds) || milliseconds < 0) return null;
  return milliseconds < 1000
    ? `${formatNumber(milliseconds, locale, '1.0-20')} ms`
    : `${formatNumber(milliseconds / 1000, locale, '1.0-2')} s`;
}
