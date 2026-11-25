import auditList from '../../../../fixtures/web-api/audit-list/input.json';
import auditListSupport from '../../../../fixtures/web-api/audit-list-support/input.json';

/** The fixed header row `audit-csv.ts` writes, in the column order every exported line follows. */
export const CSV_COLUMNS = [
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

/** The `metadata` key `support-access.ts` reads: the grant a support action was taken under. */
const SUPPORT_GRANT_KEY = 'support_grant_id';

/**
 * One audit row of the corpus, as the fixtures spell it. Declared here (rather than taken from the
 * generated SDK type) because the spec reads the JSON documents directly, and their `metadata` is a
 * different shape in each document.
 */
interface FixtureAuditRow {
  readonly id: string;
  readonly seq: number;
  readonly actor_type: string;
  readonly actor_id: string;
  readonly action: string;
  readonly created_at: string;
  readonly metadata: Readonly<Record<string, unknown>> | null;
}

/** The default page the tab opens with: six entries, no support-access row among them. */
export const DEFAULT_ROWS: readonly FixtureAuditRow[] = auditList.body.items;

/** The `support` page: the same log with two `user` rows carrying a support grant on top. */
export const SUPPORT_ROWS: readonly FixtureAuditRow[] = auditListSupport.body.items;

/**
 * The support-access marker of one corpus row, by the rule `support-access.ts` implements: the
 * `metadata.support_grant_id` marker alone flags a row here (no corpus row names the wire's
 * `platform_operator` actor — the fixture catalogue header records why), and a plain row is `null`.
 * Kept tri-state, because "operator without a recorded grant" and "ordinary row" render differently.
 */
export function supportMarker(row: FixtureAuditRow): { readonly grantId: string | null } | null {
  const marker = row.metadata?.[SUPPORT_GRANT_KEY];
  if (typeof marker === 'string' && marker !== '') {
    return { grantId: marker };
  }
  return row.actor_type === 'platform_operator' ? { grantId: null } : null;
}

/** The rows the chip must appear on, in corpus order (newest first, the server's order). */
export const FLAGGED_ROWS: readonly FixtureAuditRow[] = SUPPORT_ROWS.filter(
  (row) => supportMarker(row) !== null,
);

/** The rows that must render without a chip on the same page. */
export const PLAIN_ROWS: readonly FixtureAuditRow[] = SUPPORT_ROWS.filter(
  (row) => supportMarker(row) === null,
);

/** The shared table's real records enclose every cell; the actor marker is only one fact. */
export const AUDIT_ROWS = 'app-audit-table [data-row-id]';
export const SUPPORT_ROWS_SELECTOR = `${AUDIT_ROWS}:has([data-audit-support="true"])`;
export const PLAIN_ROWS_SELECTOR = `${AUDIT_ROWS}:has([data-audit-support="false"])`;

export const SUPPORT_CHIPS = `${AUDIT_ROWS} [data-audit-support-chip]`;
