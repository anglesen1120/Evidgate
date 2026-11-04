// The keyset page step the run-detail walks share: the two copies differed only in their row key
// and in the codes their region renders, while the loop rules were one rule. Pure — it reads no
// state, calls no API and throws nothing, so the caller keeps its own state and error mapping.

import type { ApiError } from '../core/http/api-error';

/** A region's own typed refusals: the code and the copy belong to the panel that renders them. */
export interface KeysetRefusals {
  readonly cursorRepeated: ApiError;
  readonly pageWithoutNewRows: ApiError;
}

/** One step of a keyset walk: the fresh rows and the next cursor, or the typed refusal. */
export type KeysetStep<Row, Key> =
  | { readonly outcome: 'refused'; readonly error: ApiError }
  | {
      readonly outcome: 'advanced';
      readonly fresh: Row[];
      readonly seen: Set<Key>;
      readonly nextCursor: string | null;
    };

export interface KeysetStepInput<Row, Key> {
  /** The cursor the rows were requested with; `null` asks for the first page. */
  readonly cursor: string | null;
  readonly items: readonly Row[];
  /** The cursor the server answered with; `null` ends the walk after these rows. */
  readonly nextCursor: string | null;
  /** Keys this walk already showed. A first page starts the walk over, so it ignores them. */
  readonly seen: ReadonlySet<Key>;
  readonly keyOf: (row: Row) => Key;
  readonly refusals: KeysetRefusals;
}

/**
 * One step of a keyset walk. A row whose key was already shown is dropped in page order, first
 * copy kept: a write between two pages shifts the window, so a row can arrive twice.
 *
 * Two shapes cannot advance a walk and are refused typed, never retried — the server handing back
 * the cursor it was given, and a follow-up page carrying no fresh row while promising another.
 * An empty or repeated-row page whose next cursor is absent is the ordinary end of the walk.
 */
export function takeKeysetPage<Row, Key>(input: KeysetStepInput<Row, Key>): KeysetStep<Row, Key> {
  const { cursor, items, nextCursor, keyOf, refusals } = input;
  if (nextCursor !== null && nextCursor === cursor) {
    return { outcome: 'refused', error: refusals.cursorRepeated };
  }
  const first = cursor === null;
  const seen = first ? new Set<Key>() : new Set(input.seen);
  const fresh: Row[] = [];
  for (const row of items) {
    const key = keyOf(row);
    if (seen.has(key)) continue;
    seen.add(key);
    fresh.push(row);
  }
  if (!first && fresh.length === 0 && nextCursor !== null) {
    return { outcome: 'refused', error: refusals.pageWithoutNewRows };
  }
  return { outcome: 'advanced', fresh, seen, nextCursor };
}
