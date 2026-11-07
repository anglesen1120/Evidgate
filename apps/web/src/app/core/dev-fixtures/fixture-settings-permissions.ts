// The Settings tab strip's admin review session, in TypeScript rather than the
// `fixtures/web-api` corpus: the strip's G1 tabs include Quota & retention, whose read is
// `GET /orgs/{o}/usage/summary` and whose declared code is `usage:read`, while
// `permissions-list-settings-admin` predates that tab and grants every *other* tab code.
//
// Editing that corpus would change a document the .NET fixture loader also walks, so the extra
// grant is a computed specimen here: the admin rows plus the catalog's own `usage:read` row, in
// code order. Nothing is restated — both halves are the corpus documents themselves.
//
// A review selects it with `?fixture.variant.listPermissions=settings-admin-usage`.

import type { FixtureDocument } from './fixture-document';
import { findFixtureEntry } from './fixture-catalog';
import type { FixtureEntry } from './fixture-catalog-entries';

/** The toggle value that selects this session; no corpus directory answers it. */
export const SETTINGS_ADMIN_USAGE_VARIANT = 'settings-admin-usage';

/** One row of a `listPermissions` page, exactly as the corpus stores it. */
interface PermissionRow {
  readonly code: string;
  readonly [member: string]: unknown;
}

/** The `listPermissions` page a corpus document holds, narrowed to the rows this specimen reads. */
function pageOf(variant: string): { items: readonly PermissionRow[]; next_cursor: unknown } {
  const document = findFixtureEntry('listPermissions', variant)?.document;
  const body = document?.body as { items?: unknown; next_cursor?: unknown } | undefined;
  if (document?.status !== 200 || !Array.isArray(body?.['items'])) {
    throw new Error(`the listPermissions corpus lost its ${variant} page`);
  }
  return { items: body['items'] as readonly PermissionRow[], next_cursor: body['next_cursor'] };
}

/** The `usage:read` row: the corpus's own copy, so the specimen cannot drift from the contract. */
function usageReadRow(): PermissionRow {
  const row = pageOf('usage-read').items.find((item) => item['code'] === 'usage:read');
  if (row === undefined) {
    throw new Error('the listPermissions corpus lost its usage:read row');
  }
  return row;
}

/**
 * The fixture entry answering `variant`, or `undefined` so the corpus answers as usual — a typo in
 * the toggle must surface as `fixture_not_found` or the corpus page, never as a merged grant set.
 */
export function findSettingsAdminUsageFixture(variant: string): FixtureEntry | undefined {
  if (variant !== SETTINGS_ADMIN_USAGE_VARIANT) {
    return undefined;
  }
  const admin = pageOf('settings-admin');
  const items = [...admin.items, usageReadRow()].sort((left, right) =>
    left['code'] < right['code'] ? -1 : left['code'] > right['code'] ? 1 : 0,
  );
  const document: FixtureDocument = {
    operation_id: 'listPermissions',
    variant: SETTINGS_ADMIN_USAGE_VARIANT,
    status: 200,
    headers: {},
    latency_ms: 0,
    body: { items, next_cursor: admin.next_cursor },
  };
  return { name: `permissions-list-${SETTINGS_ADMIN_USAGE_VARIANT}`, document };
}
