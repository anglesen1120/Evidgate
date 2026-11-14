import type { DataTableColumn } from './data-table.component';

/** Synthetic public demo data, not an API wire DTO or generated SDK replacement. */
export interface DataTableFixtureRow {
  readonly id: string;
  readonly hash: string;
  readonly note: string;
  readonly attempts: number;
  readonly mutation: string;
}

export const DATA_TABLE_FIXTURE_COLUMNS: readonly DataTableColumn<DataTableFixtureRow>[] = [
  {
    id: 'hash',
    header: 'Evidence identity',
    cell: (row) => row.hash,
    role: 'identity',
    kind: 'identifier',
    truncate: true,
  },
  { id: 'note', header: 'Context', cell: (row) => row.note, role: 'text' },
  { id: 'attempts', header: 'Attempts', cell: (row) => row.attempts, role: 'number', align: 'end' },
  {
    id: 'mutation',
    header: 'Mutation',
    cell: (row) => row.mutation,
    role: 'actions',
    mutates: true,
  },
];

export function dataTableFixtureRows(count: number): readonly DataTableFixtureRow[] {
  return Array.from({ length: count }, (_, index) => ({
    id: `demo-${index}`,
    hash: `sha256:demo-evidence-${index}-0123456789abcdef`,
    note: `Context ${index}: ${'Long supplied English / Tiếng Việt context. '.repeat(4)}`,
    attempts: index,
    mutation: 'Unavailable in this read-only specimen',
  }));
}
