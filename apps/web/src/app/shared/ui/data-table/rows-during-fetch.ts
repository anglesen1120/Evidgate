import { linkedSignal, type Signal } from '@angular/core';

const emptyRows: readonly never[] = [];

/** A served page can remain visible during replacement, but its cursor and selection must not be reused. */
export function rowsDuringFetch<T>(
  scope: () => unknown,
  pending: () => boolean,
  rows: () => readonly T[],
): Signal<readonly T[]> {
  return linkedSignal({
    source: () => ({ scope: scope(), pending: pending(), rows: rows() }),
    computation: (current, previous): readonly T[] => {
      if (current.scope == null) return emptyRows;
      return current.pending && !current.rows.length && current.scope === previous?.source.scope
        ? (previous?.value ?? current.rows)
        : current.rows;
    },
  });
}
