// 09.7.1-T6.2 (plan 09.7.1 §2 AC7, §5 audit row, §7 "CSV export is client-side", §9 "Audit export
// cap", §10 T6.2; COORDINATOR-DECISION S76): one page of audit rows as an RFC 4180 CSV document.
//
// Pure by design: the export's bound (10,000 rows, repeated cursors) is the store's, and this
// module's only job is that a cell can never become a spreadsheet formula or break the row layout.
// The two defences are the server export's own (`/orgs/{o}/audit/export`, which this screen does
// not call, so the rule is repeated here rather than shared across the wire boundary):
//
// 1. **Formula guarding.** A cell starting with `=`, `+`, `-`, `@`, TAB or CR gets a leading
//    apostrophe, so `=HYPERLINK(...)` in an `action` or a `metadata` value opens as text. The
//    guard runs before quoting, so the apostrophe is inside the quoted cell.
// 2. **RFC 4180 quoting.** A cell containing a quote, a comma, CR or LF is wrapped in double
//    quotes and its own quotes are doubled. `\r\n` ends every line — including the last, so a
//    trailing line is unambiguous.
//
// Columns are fixed and named: `seq, created_at, actor_type, actor_id, action, object_type,
// object_id, request_id, support_grant_id, metadata`. `metadata` is the row's map as a JSON string
// (the only lossless cell for a map), `support_grant_id` is the AC8 marker
// (`support-access.ts`), and every absent value is an empty cell — never the string `"null"`.

import type { AuditEntryDto } from '@evidgate/sdk';

import { supportAccessOf } from './support-access';

/** The header row, in the column order every exported line follows. */
const COLUMNS = [
  'seq',
  'created_at',
  'actor_type',
  'actor_id',
  'action',
  'object_type',
  'object_id',
  'request_id',
  'support_grant_id',
  'metadata',
] as const;

/** First characters that make a spreadsheet treat the cell as a formula (server export rule). */
const FORMULA_PREFIXES = '=+-@\t\r';

/**
 * One cell: guarded against formula evaluation, then quoted for RFC 4180 when it needs to be.
 * `null`/`undefined` become the empty cell; everything else is its string form.
 */
function csvCell(value: string | number | null | undefined): string {
  if (value === null || value === undefined) {
    return '';
  }
  const text = String(value);
  const guarded = text.length > 0 && FORMULA_PREFIXES.includes(text.charAt(0)) ? `'${text}` : text;
  return /[",\r\n]/.test(guarded) ? `"${guarded.replace(/"/g, '""')}"` : guarded;
}

/** One audit row as a CSV line, in {@link COLUMNS} order. */
function csvLine(entry: AuditEntryDto): string {
  const metadata = entry.metadata == null ? '' : JSON.stringify(entry.metadata);
  const grantId = supportAccessOf(entry)?.grantId ?? '';
  return [
    csvCell(entry.seq),
    csvCell(entry.created_at),
    csvCell(entry.actor_type),
    csvCell(entry.actor_id),
    csvCell(entry.action),
    csvCell(entry.object_type),
    csvCell(entry.object_id),
    csvCell(entry.request_id),
    csvCell(grantId),
    csvCell(metadata),
  ].join(',');
}

/**
 * The exported document: the header row and one row per entry, `\r\n` line ends, trailing newline.
 * An empty list is a header-only document, which is a valid (and honest) export of no rows.
 */
export function toAuditCsv(rows: readonly AuditEntryDto[]): string {
  const lines = [COLUMNS.join(',')];
  for (const entry of rows) {
    lines.push(csvLine(entry));
  }
  return `${lines.join('\r\n')}\r\n`;
}
