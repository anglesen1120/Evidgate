import { InjectionToken } from '@angular/core';

/** The product limit: 10,000 rows of one keyset-paged list. */
export const DEFAULT_RETAINED_ROWS_CAP = 10_000;

/**
 * How many rows a keyset-paged list keeps. The bound caps DOM cost and memory on a walk the
 * server keeps extending; a build can replace the token where a smaller list proves the same
 * behaviour, so no caller reads the number.
 */
export const RETAINED_ROWS_CAP = new InjectionToken<number>('evidgate.retained-rows-cap', {
  providedIn: 'root',
  factory: () => DEFAULT_RETAINED_ROWS_CAP,
});

type RetainedKeysetRows<T> = {
  readonly rows: readonly T[];
} & (
  | { readonly capped: true; readonly nextCursor: null }
  | { readonly capped: false; readonly nextCursor: string | null }
);

/** A full terminal page is complete; a discarded row or forward cursor means browsing stopped early. */
export function retainKeysetRows<T>(
  rows: readonly T[],
  nextCursor: string | null,
  cap: number,
): RetainedKeysetRows<T> {
  if (rows.length > cap || (rows.length === cap && nextCursor !== null)) {
    return {
      rows: rows.length > cap ? rows.slice(0, cap) : rows,
      nextCursor: null,
      capped: true,
    };
  }
  return { rows, nextCursor, capped: false };
}
