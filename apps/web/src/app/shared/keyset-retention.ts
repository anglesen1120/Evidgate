export const CASE_ROWS_CAP = 10_000;

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
): RetainedKeysetRows<T> {
  if (rows.length > CASE_ROWS_CAP || (rows.length === CASE_ROWS_CAP && nextCursor !== null)) {
    return {
      rows: rows.length > CASE_ROWS_CAP ? rows.slice(0, CASE_ROWS_CAP) : rows,
      nextCursor: null,
      capped: true,
    };
  }
  return { rows, nextCursor, capped: false };
}
