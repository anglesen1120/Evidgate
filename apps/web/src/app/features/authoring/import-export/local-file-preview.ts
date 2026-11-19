import type { ImportAuthoringData } from '@evidgate/sdk';

export type AuthoringFormat = ImportAuthoringData['query']['format'];
export const IMPORT_BYTE_CAP = 5 * 1024 * 1024;
export const IMPORT_ROW_CAP = 5000;
export const IMPORT_COLUMNS = [
  'kind',
  'key',
  'parent_key',
  'item_key',
  'node_kind',
  'title',
  'description',
  'precondition',
  'steps',
  'priority',
  'state',
  'code',
  'position',
] as const;
export type ImportColumn = (typeof IMPORT_COLUMNS)[number];
export interface LocalPreviewRow {
  readonly index: number;
  readonly cells: Readonly<Partial<Record<ImportColumn, string>>>;
}
export interface LocalPreview {
  readonly rows: readonly LocalPreviewRow[];
  readonly issue: 'unreadable' | 'header' | 'display-cap' | 'xlsx' | null;
}

function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
function display(value: unknown): string {
  if (value === undefined || value === null) return '';
  return typeof value === 'string' ? value : JSON.stringify(value);
}
function jsonRows(text: string): LocalPreview {
  const document: unknown = JSON.parse(text);
  if (!record(document) || !Array.isArray(document['rows']))
    return { rows: [], issue: 'unreadable' };
  const source: unknown[] = document['rows'];
  const rows = source.slice(0, IMPORT_ROW_CAP).map((value, offset) => ({
    index: offset + 1,
    cells: Object.fromEntries(
      IMPORT_COLUMNS.map((column) => [column, record(value) ? display(value[column]) : '']),
    ),
  }));
  return { rows, issue: source.length > IMPORT_ROW_CAP ? 'display-cap' : null };
}

// Display-only scanner: quoted newlines and doubled quotes are data, not row boundaries. A blank
// line carries no record, so it neither gets an index nor shifts the ones after it (the server's
// CSV index skips it the same way).
function csvRows(text: string): string[][] {
  const rows: string[][] = [];
  let row: string[] = [],
    cell = '',
    quoted = false,
    closed = false,
    sawQuote = false;
  for (let index = 0; index < text.length; index++) {
    const character = text[index];
    if (quoted) {
      if (character !== '"') cell += character;
      else if (text[index + 1] === '"') {
        cell += '"';
        index++;
      } else {
        quoted = false;
        closed = true;
      }
    } else if (character === '"' && cell === '' && !closed) {
      quoted = true;
      sawQuote = true;
    } else if (character === ',' || character === '\r' || character === '\n') {
      row.push(cell);
      cell = '';
      closed = false;
      if (character !== ',') {
        if (row.length > 1 || row[0] !== '' || sawQuote) rows.push(row);
        row = [];
        sawQuote = false;
        if (character === '\r' && text[index + 1] === '\n') index++;
        if (rows.length > IMPORT_ROW_CAP + 1) return rows;
      }
    } else {
      if (closed || character === '"') throw new Error('Unreadable CSV preview');
      cell += character;
    }
  }
  if (quoted) throw new Error('Unreadable CSV preview');
  if (cell !== '' || row.length > 0 || closed) {
    row.push(cell);
    rows.push(row);
  }
  return rows;
}
function csvPreview(text: string): LocalPreview {
  const source = csvRows(text.replace(/^\uFEFF/, ''));
  const header = source.shift();
  if (!header || header.join(',') !== IMPORT_COLUMNS.join(','))
    return { rows: [], issue: 'header' };
  const rows = source.slice(0, IMPORT_ROW_CAP).map((values, offset) => ({
    index: offset + 1,
    cells: Object.fromEntries(IMPORT_COLUMNS.map((column, index) => [column, values[index] ?? ''])),
  }));
  return { rows, issue: source.length > IMPORT_ROW_CAP ? 'display-cap' : null };
}
export function parseLocalPreview(text: string, format: AuthoringFormat): LocalPreview {
  if (format === 'xlsx') return { rows: [], issue: 'xlsx' };
  try {
    return format === 'json' ? jsonRows(text) : csvPreview(text);
  } catch {
    return { rows: [], issue: 'unreadable' };
  }
}

/**
 * Data rows in a serialized export, or `null` when the text cannot be read. An empty scope is
 * still a non-empty file (a `rows: []` envelope, a CSV header), so the download guards on this
 * count rather than on the file's byte length.
 */
export function exportedJsonRows(text: string): number | null {
  try {
    const document: unknown = JSON.parse(text);
    return record(document) && Array.isArray(document['rows']) ? document['rows'].length : null;
  } catch {
    return null;
  }
}

/** Logical CSV records after the header; blank lines are not records; `null` when unreadable. */
export function exportedCsvRows(text: string): number | null {
  try {
    const [, ...records] = csvRows(text.replace(/^\uFEFF/, ''));
    return records.length;
  } catch {
    return null;
  }
}
